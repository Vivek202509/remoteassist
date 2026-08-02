package com.remoteassist.webrtc

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import com.remoteassist.identity.DeviceIdentity
import com.remoteassist.crypto.Crypto
import com.remoteassist.signaling.SignalingClient
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import org.webrtc.*
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets

enum class LinkState { CONNECTING, CONNECTED, RECOVERING, CLOSED }

/**
 * Wraps a single WebRTC PeerConnection for one remote session, in either role.
 * Handles offer/answer/ICE, the control DataChannel, SDP-fingerprint signing for
 * MITM protection, and network-aware ICE-restart recovery armed on first connect.
 */
class RtcSession(
    private val context: Context,
    private val eglBase: EglBase,
    private val factory: PeerConnectionFactory,
    private val signaling: SignalingClient,
    private val scope: CoroutineScope,
    @Volatile private var iceServers: List<PeerConnection.IceServer>,
) {
    @Volatile var link = LinkState.CONNECTING; private set
    var onRemoteVideo: ((VideoTrack) -> Unit)? = null
    var onControlMessage: ((JSONObject) -> Unit)? = null
    var onLinkState: ((LinkState) -> Unit)? = null
    /** Verify a peer's identity owns the DER pub for the given peerId (from TrustStore). */
    var peerPublicKeyProvider: ((peerId: String) -> ByteArray?)? = null

    private var pc: PeerConnection? = null
    private var dataChannel: DataChannel? = null
    private var peerId: String = ""
    private var isCaller = false
    private var everConnected = false
    private var recoveryJob: Job? = null
    private var networkCallback: ConnectivityManager.NetworkCallback? = null

    // ---------- lifecycle ----------
    private fun createPc() {
        val cfg = PeerConnection.RTCConfiguration(iceServers).apply {
            sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
            bundlePolicy = PeerConnection.BundlePolicy.MAXBUNDLE
            rtcpMuxPolicy = PeerConnection.RtcpMuxPolicy.REQUIRE
            continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
        }
        pc = factory.createPeerConnection(cfg, object : PeerConnection.Observer {
            override fun onIceCandidate(c: IceCandidate) =
                signaling.sendIce(peerId, c.sdpMid, c.sdpMLineIndex, c.sdp)
            override fun onTrack(t: RtpTransceiver) {
                (t.receiver.track() as? VideoTrack)?.let { onRemoteVideo?.invoke(it) }
            }
            override fun onDataChannel(dc: DataChannel) = bindData(dc)
            override fun onIceConnectionChange(s: PeerConnection.IceConnectionState) = handleIceState(s)
            override fun onSignalingChange(s: PeerConnection.SignalingState) {}
            override fun onIceGatheringChange(s: PeerConnection.IceGatheringState) {}
            override fun onIceCandidatesRemoved(c: Array<out IceCandidate>) {}
            override fun onAddStream(s: MediaStream) {}
            override fun onRemoveStream(s: MediaStream) {}
            override fun onRenegotiationNeeded() {}
            override fun onIceConnectionReceivingChange(b: Boolean) {}
        })
    }

    // ---------- HOST ----------
    fun startAsHost(controllerId: String, screenTrack: VideoTrack) {
        peerId = controllerId; isCaller = true
        createPc()
        pc!!.addTrack(screenTrack, listOf("screen"))
        bindData(pc!!.createDataChannel("control", DataChannel.Init().apply { ordered = true }))
        createAndSendOffer(iceRestart = false)
    }

    // ---------- CONTROLLER ----------
    fun startAsController(hostId: String) {
        peerId = hostId; isCaller = false
        createPc()
        pc!!.addTransceiver(
            MediaStreamTrack.MediaType.MEDIA_TYPE_VIDEO,
            RtpTransceiver.RtpTransceiverInit(RtpTransceiver.RtpTransceiverDirection.RECV_ONLY)
        )
    }

    // ---------- SDP handling ----------
    fun onRemoteOffer(from: String, sdp: String, fpSig: String?) {
        if (!verifyRemoteSdp(from, sdp, fpSig)) return failLink("offer fingerprint verification failed")
        peerId = from
        pc!!.setRemoteDescription(observer {
            pc!!.createAnswer(observer { ans ->
                pc!!.setLocalDescription(observer {}, ans)
                signaling.sendAnswer(from, ans.description, signFingerprint(ans.description))
            }, MediaConstraints())
        }, SessionDescription(SessionDescription.Type.OFFER, sdp))
    }

    fun onRemoteAnswer(from: String, sdp: String, fpSig: String?) {
        if (!verifyRemoteSdp(from, sdp, fpSig)) return failLink("answer fingerprint verification failed")
        pc!!.setRemoteDescription(observer {}, SessionDescription(SessionDescription.Type.ANSWER, sdp))
    }

    fun onRemoteIce(mid: String, idx: Int, cand: String) =
        pc?.addIceCandidate(IceCandidate(mid, idx, cand)) ?: Unit

    fun onRestartRequested() { if (isCaller) createAndSendOffer(iceRestart = true) }

    private fun createAndSendOffer(iceRestart: Boolean) {
        val conn = pc ?: return
        val constraints = MediaConstraints().apply {
            if (iceRestart) mandatory.add(MediaConstraints.KeyValuePair("IceRestart", "true"))
        }
        conn.createOffer(observer { sdp ->
            conn.setLocalDescription(observer {}, sdp)
            signaling.sendOffer(peerId, sdp.description, signFingerprint(sdp.description))
        }, constraints)
    }

    // ---------- control channel ----------
    fun sendControl(msg: JSONObject) {
        val dc = dataChannel ?: return
        dc.send(DataChannel.Buffer(ByteBuffer.wrap(msg.toString().toByteArray()), false))
    }

    private fun bindData(dc: DataChannel) {
        dataChannel = dc
        dc.registerObserver(object : DataChannel.Observer {
            override fun onMessage(b: DataChannel.Buffer) {
                val bytes = ByteArray(b.data.remaining()).also { b.data.get(it) }
                onControlMessage?.invoke(JSONObject(String(bytes, StandardCharsets.UTF_8)))
            }
            override fun onStateChange() {}
            override fun onBufferedAmountChange(l: Long) {}
        })
    }

    // ---------- MITM protection: sign/verify the DTLS fingerprint ----------
    private fun signFingerprint(sdp: String): String =
        Crypto.b64(DeviceIdentity.sign(extractFingerprint(sdp).toByteArray()))

    private fun verifyRemoteSdp(from: String, sdp: String, fpSig: String?): Boolean {
        // For a PAIRED peer we have a shared identity to verify against, so the
        // signed fingerprint must check out (MITM protection). For an unpaired,
        // ad-hoc code-based session there is no pre-shared identity — trust there
        // comes from the one-time 6-digit code plus explicit host consent — so we
        // accept it rather than hard-failing the whole flow.
        val pub = peerPublicKeyProvider?.invoke(from) ?: return true
        if (fpSig == null) return false
        return Crypto.verify(pub, extractFingerprint(sdp).toByteArray(), Crypto.unb64(fpSig))
    }

    private fun extractFingerprint(sdp: String): String =
        sdp.lineSequence().firstOrNull { it.startsWith("a=fingerprint:") }?.trim().orEmpty()

    // ---------- recovery ----------
    private fun handleIceState(s: PeerConnection.IceConnectionState) {
        when (s) {
            PeerConnection.IceConnectionState.CONNECTED,
            PeerConnection.IceConnectionState.COMPLETED -> onConnected()
            PeerConnection.IceConnectionState.DISCONNECTED -> if (everConnected) scheduleRecovery(2_000)
            PeerConnection.IceConnectionState.FAILED -> if (everConnected) scheduleRecovery(0)
            PeerConnection.IceConnectionState.CLOSED -> setLink(LinkState.CLOSED)
            else -> {}
        }
    }

    private fun onConnected() {
        recoveryJob?.cancel(); recoveryJob = null
        val first = !everConnected
        everConnected = true
        setLink(LinkState.CONNECTED)
        if (first) armRecoveryWatchdogs()
    }

    private fun armRecoveryWatchdogs() {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val cb = object : ConnectivityManager.NetworkCallback() {
            private var last: Network? = null
            override fun onAvailable(network: Network) {
                if (last != null && network != last && everConnected) scheduleRecovery(0)
                last = network
            }
        }
        networkCallback = cb
        cm.registerDefaultNetworkCallback(cb)
        signaling.onReconnected = { if (link == LinkState.RECOVERING) doIceRestart() }
    }

    private fun scheduleRecovery(graceMs: Long) {
        if (link == LinkState.CLOSED) return
        if (recoveryJob?.isActive == true) return
        setLink(LinkState.RECOVERING)
        recoveryJob = scope.launch {
            delay(graceMs)
            if (link == LinkState.CONNECTED) return@launch
            var attempt = 0
            val maxAttempts = 6
            while (isActive && link == LinkState.RECOVERING && attempt < maxAttempts) {
                attempt++
                if (!signaling.isConnected) signaling.reconnectAndAwait(5_000)
                if (signaling.isConnected) {
                    doIceRestart()
                    delay(backoff(attempt))
                    if (link == LinkState.CONNECTED) return@launch
                } else {
                    delay(backoff(attempt))
                }
            }
            if (link != LinkState.CONNECTED) { setLink(LinkState.CLOSED); pc?.close() }
        }
    }

    private fun backoff(attempt: Int): Long = minOf(1000L * (1 shl (attempt - 1)), 15_000L)

    fun doIceRestart() {
        if (pc == null) return
        if (isCaller) createAndSendOffer(iceRestart = true)
        else signaling.requestRestart(peerId)  // answerer nudges the offerer
    }

    fun updateIceServers(servers: List<PeerConnection.IceServer>) {
        iceServers = servers
        pc?.setConfiguration(PeerConnection.RTCConfiguration(servers).apply {
            sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
            bundlePolicy = PeerConnection.BundlePolicy.MAXBUNDLE
            continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
        })
    }

    fun hangup() {
        recoveryJob?.cancel()
        networkCallback?.let { cb ->
            try {
                context.getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(cb)
            } catch (_: Exception) { /* already unregistered */ }
        }
        networkCallback = null
        signaling.onReconnected = null
        signaling.send(JSONObject().put("type", "hangup").put("to", peerId))
        pc?.close(); pc = null
        setLink(LinkState.CLOSED)
    }

    private fun setLink(s: LinkState) { link = s; onLinkState?.invoke(s) }
    private fun failLink(reason: String) { android.util.Log.w("RtcSession", reason); hangup() }

    private fun observer(onCreate: (SessionDescription) -> Unit) = object : SdpObserver {
        override fun onCreateSuccess(s: SessionDescription) = onCreate(s)
        override fun onSetSuccess() {}
        override fun onCreateFailure(e: String?) { android.util.Log.w("RtcSession", "createSdp: $e") }
        override fun onSetFailure(e: String?) { android.util.Log.w("RtcSession", "setSdp: $e") }
    }
}
