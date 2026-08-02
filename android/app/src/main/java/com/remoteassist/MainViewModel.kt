package com.remoteassist

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.remoteassist.host.RemoteInputService
import com.remoteassist.session.SessionCodes
import com.remoteassist.trust.PeerIdentity
import com.remoteassist.webrtc.LinkState
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject
import org.webrtc.EglBase
import org.webrtc.VideoTrack

/** Screens the single-activity UI can show. */
enum class Screen { HOME, HOST_CODE, JOIN, SCAN, PAIRED, SAFETY_NUMBER, SESSION }

private const val JOIN_TIMEOUT_MS = 45_000L

/**
 * UI state + intent handling. Bridges Compose to ServiceLocator; keeps only
 * view-facing state, delegating real work to SessionManager / PairingManager.
 */
class MainViewModel(app: Application) : AndroidViewModel(app) {

    var screen by mutableStateOf(Screen.HOME); private set
    var sessionCode by mutableStateOf<String?>(null); private set
    var joinError by mutableStateOf<String?>(null); private set
    var linkState by mutableStateOf(LinkState.CONNECTING); private set

    // pending incoming controller awaiting host consent
    var pendingControllerId by mutableStateOf<String?>(null); private set
    var pendingUnattended by mutableStateOf(false); private set
    var pendingGrantId by mutableStateOf<String?>(null); private set

    // pairing confirmation
    var safetyNumber by mutableStateOf<String?>(null); private set
    var safetyPeerPub by mutableStateOf<String?>(null); private set

    var remoteVideo by mutableStateOf<VideoTrack?>(null); private set

    // controller-side connection progress
    var connecting by mutableStateOf(false); private set   // dialed, awaiting host consent
    private var lastCode: String? = null
    private var joinTimeoutJob: Job? = null

    val eglBase: EglBase get() = ServiceLocator.core.eglBase
    val accessibilityEnabled: Boolean get() = RemoteInputService.isEnabled(getApplication())
    fun pairedPeers(): List<PeerIdentity> = ServiceLocator.trust.all()

    init {
        ServiceLocator.uiOnSessionCode = { code -> sessionCode = code; screen = Screen.HOST_CODE }
        ServiceLocator.uiOnJoinRequest = { controllerId, unattended, grantId ->
            pendingControllerId = controllerId
            pendingUnattended = unattended
            pendingGrantId = grantId
        }
        ServiceLocator.uiOnConsentResult = { accepted, _ ->
            cancelJoinTimeout()
            connecting = false
            if (accepted) {
                screen = Screen.SESSION
            } else {
                joinError = "declined"
                screen = Screen.JOIN
            }
        }
        ServiceLocator.uiOnJoinFailed = { reason ->
            cancelJoinTimeout()
            connecting = false
            joinError = reason
            if (screen == Screen.SCAN) screen = Screen.JOIN
        }
        ServiceLocator.uiOnSafetyNumber = { peerPub, number ->
            safetyPeerPub = peerPub; safetyNumber = number; screen = Screen.SAFETY_NUMBER
        }
        ServiceLocator.session.onRemoteVideo = { remoteVideo = it }
        ServiceLocator.session.onLinkState = { linkState = it }
    }

    // ---- navigation ----
    fun goHome() { cancelJoinTimeout(); connecting = false; screen = Screen.HOME }
    fun goJoin() { joinError = null; screen = Screen.JOIN }
    fun goScan() { joinError = null; screen = Screen.SCAN }
    fun goPaired() { screen = Screen.PAIRED }

    /** QR payload for the current session code, or null if none yet. */
    fun sessionQrPayload(): String? = sessionCode?.let { SessionCodes.toQrPayload(it) }

    // ---- host role ----
    fun becomeHost() { ServiceLocator.signaling.hostOpen() }
    fun cancelHost() { sessionCode = null; goHome() }

    // ---- controller role ----
    fun connectWithCode(code: String) {
        if (!SessionCodes.isValid(code)) { joinError = "invalid-code"; return }
        joinError = null
        lastCode = code
        connecting = true
        ServiceLocator.signaling.join(code)
        startJoinTimeout()
    }

    /** Handle a scanned QR: parse to a code, then dial. Returns false if unparseable. */
    fun onScanned(raw: String): Boolean {
        val code = SessionCodes.parseScanned(raw) ?: run { joinError = "unreadable-qr"; screen = Screen.JOIN; return false }
        screen = Screen.JOIN
        connectWithCode(code)
        return true
    }

    fun connectToPaired(hostId: String) {
        connecting = true
        ServiceLocator.signaling.joinPaired(hostId)
        startJoinTimeout()
    }

    fun cancelConnecting() {
        cancelJoinTimeout()
        connecting = false
        screen = Screen.JOIN
    }

    /** Retry after a closed/failed session by re-dialing the last code. */
    fun retryConnect() {
        val code = lastCode
        if (code != null) { screen = Screen.JOIN; connectWithCode(code) } else goJoin()
    }

    private fun startJoinTimeout() {
        cancelJoinTimeout()
        joinTimeoutJob = viewModelScope.launch {
            delay(JOIN_TIMEOUT_MS)
            if (connecting) {
                connecting = false
                joinError = "timeout"
                screen = Screen.JOIN
            }
        }
    }
    private fun cancelJoinTimeout() { joinTimeoutJob?.cancel(); joinTimeoutJob = null }

    // ---- consent (host side) ----
    /** Called after the host taps Allow AND the MediaProjection dialog returns OK. */
    fun approveIncoming(resultCode: Int, data: android.content.Intent) {
        val controllerId = pendingControllerId ?: return
        ServiceLocator.signaling.consent(controllerId, true)
        com.remoteassist.host.ScreenCaptureService.start(
            getApplication(), resultCode, data, controllerId, pendingGrantId
        )
        screen = Screen.SESSION
        clearPending()
    }
    fun declineIncoming() {
        pendingControllerId?.let { ServiceLocator.signaling.consent(it, false) }
        clearPending()
    }
    private fun clearPending() { pendingControllerId = null; pendingUnattended = false; pendingGrantId = null }

    // ---- pairing confirmation ----
    fun confirmPairing() {
        safetyPeerPub?.let { ServiceLocator.pairing.confirm(it) }
        safetyNumber = null; safetyPeerPub = null; goPaired()
    }
    fun rejectPairing() { safetyNumber = null; safetyPeerPub = null; goHome() }

    fun revokePeer(peer: PeerIdentity) {
        ServiceLocator.trust.remove(peer.peerPubKeyB64)
        ServiceLocator.grants.revokeAllFor(peer.peerId)
        ServiceLocator.session.endIfPeer(peer.peerId)
        ServiceLocator.signaling.send(JSONObject().apply {
            put("type", "revoke-pairing")
            put("myPub", ServiceLocator.deviceId)
            put("peerPub", peer.peerId)
        })
    }

    // ---- controller sends a tap ----
    fun sendTap(nx: Float, ny: Float) {
        ServiceLocator.session.sendControl(JSONObject().put("t", "tap").put("x", nx).put("y", ny))
    }
    fun sendSwipe(x1: Float, y1: Float, x2: Float, y2: Float, ms: Long) {
        ServiceLocator.session.sendControl(
            JSONObject().put("t", "swipe").put("x1", x1).put("y1", y1)
                .put("x2", x2).put("y2", y2).put("ms", ms)
        )
    }
    fun sendKey(key: String) { ServiceLocator.session.sendControl(JSONObject().put("t", "key").put("k", key)) }

    fun endSession() { ServiceLocator.session.endNow(); goHome() }
}
