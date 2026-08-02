package com.remoteassist.host

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.content.ContextCompat
import com.remoteassist.ServiceLocator
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Short-lived foreground service used on FCM wake: re-establishes the signaling
 * socket (killed during Doze) so the controller's join-request can reach this
 * device, then stops once a session starts or the request times out.
 */
class SignalingService : Service() {

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForegroundCompat()

        if (!ServiceLocator.signaling.isConnected) ServiceLocator.signaling.connect()
        ServiceLocator.signaling.hostOpen() // re-advertise so a pending join resolves

        // Give the incoming request ~60s to arrive; then stand down.
        ServiceLocator.scope.launch60sThenStop { stopSelf() }
        return START_NOT_STICKY
    }

    private fun startForegroundCompat() {
        val n = Notifications.signalingConnecting(this)
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(Notifications.ID_SIGNALING, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        } else {
            startForeground(Notifications.ID_SIGNALING, n)
        }
    }

    override fun onDestroy() { stopForeground(STOP_FOREGROUND_REMOVE); super.onDestroy() }

    companion object {
        fun ensureConnected(ctx: Context) =
            ContextCompat.startForegroundService(ctx, Intent(ctx, SignalingService::class.java))
    }
}

private fun CoroutineScope.launch60sThenStop(stop: () -> Unit) {
    launch {
        delay(60_000)
        stop()
    }
}
