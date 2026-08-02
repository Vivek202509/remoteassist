package com.remoteassist.host

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.DisplayMetrics
import android.view.WindowManager
import com.remoteassist.ServiceLocator
import com.remoteassist.util.parcelable
import org.webrtc.VideoCapturer

/**
 * Attended-host screen capture. Started after the user approves the MediaProjection
 * system dialog. Posts the mandatory capture notification, builds a WebRTC screen
 * track, and hands it to the SessionManager to start hosting for a controller.
 */
class ScreenCaptureService : Service() {

    private var capturer: VideoCapturer? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == "STOP") { stopEverything(); return START_NOT_STICKY }

        // Notification MUST be posted before capture starts (FGS rule + policy).
        startForegroundCompat()

        val resultCode = intent!!.getIntExtra("code", 0)
        val data = intent.parcelable<Intent>("data")!!
        val controllerId = intent.getStringExtra("controllerId")!!
        val grantId = intent.getStringExtra("grantId")

        val (w, h) = screenSize(this)
        InputRouter.screenWidth = w; InputRouter.screenHeight = h
        // Attended host: the user explicitly approved view AND control. Reset the
        // process-global flag in case a prior view-only unattended session left it off.
        InputRouter.controlEnabled = true

        val (track, cap) = ServiceLocator.core.buildScreenTrack(
            appContext = applicationContext,
            widthPx = w, heightPx = h,
            permissionData = data,
        )
        capturer = cap
        ServiceLocator.session.startHost(controllerId, track, grantId)
        return START_NOT_STICKY
    }

    private fun startForegroundCompat() {
        val n = Notifications.capture(this)
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(Notifications.ID_CAPTURE, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        } else {
            startForeground(Notifications.ID_CAPTURE, n)
        }
    }

    private fun stopEverything() {
        ServiceLocator.session.endNow()
        try { capturer?.stopCapture() } catch (_: Exception) {}
        capturer?.dispose(); capturer = null
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() { stopEverything(); super.onDestroy() }

    companion object {
        fun start(ctx: Context, resultCode: Int, data: Intent, controllerId: String, grantId: String? = null) {
            val i = Intent(ctx, ScreenCaptureService::class.java)
                .putExtra("code", resultCode)
                .putExtra("data", data)
                .putExtra("controllerId", controllerId)
                .putExtra("grantId", grantId)
            androidx.core.content.ContextCompat.startForegroundService(ctx, i)
        }

        fun screenSize(ctx: Context): Pair<Int, Int> {
            val metrics = DisplayMetrics()
            @Suppress("DEPRECATION")
            ctx.getSystemService(WindowManager::class.java).defaultDisplay.getRealMetrics(metrics)
            return metrics.widthPixels to metrics.heightPixels
        }
    }
}
