package com.remoteassist.host

import android.app.KeyguardManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import com.remoteassist.ServiceLocator
import com.remoteassist.unattended.Scope
import com.remoteassist.util.parcelable
import org.webrtc.VideoCapturer

/**
 * Persistent unattended host. Set up once by the device owner; holds the
 * MediaProjection open so future sessions from trusted controllers start with no
 * system dialog. Auto-accepts sessions that have a valid standing grant, otherwise
 * falls back to the attended consent notification.
 */
class UnattendedHostService : Service() {

    // The projection permission Intent captured during owner setup. Held so future
    // trusted sessions start without a new system dialog. See README caveat: on
    // Android 11+ a permission Intent is single-use, so a production build keeps the
    // capturer/track alive across sessions rather than rebuilding from the Intent.
    private var resultCode: Int = 0
    private var resultData: Intent? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var capturer: VideoCapturer? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // START_STICKY means the OS may restart us with a null Intent after a Doze
        // kill. We can't restore the (single-use) projection grant without the
        // original Intent, so stand down cleanly instead of dereferencing null.
        if (intent == null) { stopSelf(); return START_NOT_STICKY }

        when (intent.action) {
            "STOP" -> { teardown(); return START_NOT_STICKY }
            "INCOMING" -> { onIncomingSession(intent.getStringExtra("controllerId")); return START_STICKY }
        }

        startForegroundCompat()

        resultCode = intent.getIntExtra("code", 0)
        resultData = intent.parcelable<Intent>("data")

        // Bounded wakelock so the OS reclaims it if the service is ever orphaned;
        // long enough to cover a realistic unattended session.
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "remoteassist:unattended")
            .also { it.acquire(WAKELOCK_TIMEOUT_MS) }

        return START_STICKY
    }

    private fun onIncomingSession(controllerId: String?) {
        controllerId ?: return
        val grant = ServiceLocator.grants.findActive(controllerId)
        if (grant == null || !grant.isUsable()) {
            // No standing grant -> attended consent path.
            Notifications.incoming(this, controllerId, null)
            return
        }
        val locked = getSystemService(KeyguardManager::class.java).isKeyguardLocked
        val effective = if (grant.requireUnlock && locked) grant.downgradeToViewOnly() else grant
        InputRouter.controlEnabled = effective.scope.contains(Scope.CONTROL)

        val data = resultData ?: run { Notifications.incoming(this, controllerId, null); return }
        val (w, h) = ScreenCaptureService.screenSize(this)
        InputRouter.screenWidth = w; InputRouter.screenHeight = h

        val (track, cap) = ServiceLocator.core.buildScreenTrack(
            appContext = applicationContext, widthPx = w, heightPx = h, permissionData = data
        )
        capturer = cap
        ServiceLocator.session.startHost(controllerId, track, grant.grantId)
        ServiceLocator.grants.markUsed(grant.grantId)
        // A distinct "session live" notification replaces the idle one.
        startForeground(Notifications.ID_CAPTURE, Notifications.capture(this))
    }

    private fun startForegroundCompat() {
        val n = Notifications.unattendedArmed(this)
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(Notifications.ID_UNATTENDED, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        } else {
            startForeground(Notifications.ID_UNATTENDED, n)
        }
    }

    private fun teardown() {
        try { capturer?.stopCapture() } catch (_: Exception) {}
        capturer?.dispose(); capturer = null
        wakeLock?.let { if (it.isHeld) it.release() }; wakeLock = null
        resultData = null
        ServiceLocator.session.endNow()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() { teardown(); super.onDestroy() }

    companion object {
        private const val WAKELOCK_TIMEOUT_MS = 2 * 60 * 60 * 1000L // 2h cap

        fun arm(ctx: Context, resultCode: Int, data: Intent) {
            val i = Intent(ctx, UnattendedHostService::class.java)
                .putExtra("code", resultCode).putExtra("data", data)
            androidx.core.content.ContextCompat.startForegroundService(ctx, i)
        }
        fun notifyIncoming(ctx: Context, controllerId: String) {
            val i = Intent(ctx, UnattendedHostService::class.java)
                .setAction("INCOMING").putExtra("controllerId", controllerId)
            androidx.core.content.ContextCompat.startForegroundService(ctx, i)
        }
        fun disarm(ctx: Context) {
            ctx.startService(Intent(ctx, UnattendedHostService::class.java).setAction("STOP"))
        }
    }
}
