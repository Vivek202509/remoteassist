package com.remoteassist.host

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationCompat
import com.remoteassist.MainActivity
import com.remoteassist.R

/**
 * All user-visible session notifications. Every capture/unattended path MUST post
 * one of these — visibility is policy-mandated, not optional.
 */
object Notifications {
    const val CH_SESSION = "remote_session"
    const val CH_UNATTENDED = "unattended"
    const val CH_INCOMING = "incoming"

    const val ID_CAPTURE = 1001
    const val ID_UNATTENDED = 1002
    const val ID_INCOMING = 1003
    const val ID_SIGNALING = 1004

    fun ensureChannels(ctx: Context) {
        val nm = ctx.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CH_SESSION, "Remote session", NotificationManager.IMPORTANCE_HIGH))
        nm.createNotificationChannel(NotificationChannel(CH_UNATTENDED, "Unattended access", NotificationManager.IMPORTANCE_DEFAULT))
        nm.createNotificationChannel(NotificationChannel(CH_INCOMING, "Incoming remote session", NotificationManager.IMPORTANCE_HIGH))
    }

    private fun stopIntent(ctx: Context, cls: Class<*>): PendingIntent =
        PendingIntent.getService(
            ctx, 0, Intent(ctx, cls).setAction("STOP"),
            PendingIntent.FLAG_IMMUTABLE
        )

    /** Non-dismissible notification shown the entire time the screen is shared. */
    fun capture(ctx: Context): Notification =
        NotificationCompat.Builder(ctx, CH_SESSION)
            .setContentTitle("Your screen is being shared")
            .setContentText("Someone can currently view and control this device")
            .setSmallIcon(R.drawable.ic_cast)
            .setOngoing(true)
            .addAction(R.drawable.ic_stop, "End now", stopIntent(ctx, ScreenCaptureService::class.java))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .build()

    /** Persistent indicator shown while unattended access is armed (even idle). */
    fun unattendedArmed(ctx: Context): Notification =
        NotificationCompat.Builder(ctx, CH_UNATTENDED)
            .setContentTitle("Unattended access is ON")
            .setContentText("A trusted device can connect to this phone")
            .setSmallIcon(R.drawable.ic_shield)
            .setOngoing(true)
            .addAction(R.drawable.ic_stop, "Turn off", stopIntent(ctx, UnattendedHostService::class.java))
            .build()

    fun signalingConnecting(ctx: Context): Notification =
        NotificationCompat.Builder(ctx, CH_UNATTENDED)
            .setContentTitle("Connecting…")
            .setContentText("Re-establishing connection for an incoming session")
            .setSmallIcon(R.drawable.ic_cast)
            .setOngoing(true)
            .build()

    /** Call-style incoming-session notification posted on wake. */
    fun incoming(ctx: Context, controllerId: String?, sessionId: String?) {
        val nm = ctx.getSystemService(NotificationManager::class.java)
        val open = PendingIntent.getActivity(
            ctx, 0,
            Intent(ctx, MainActivity::class.java)
                .putExtra("route", "consent")
                .putExtra("controllerId", controllerId)
                .putExtra("sessionId", sessionId)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val n = NotificationCompat.Builder(ctx, CH_INCOMING)
            .setSmallIcon(R.drawable.ic_cast)
            .setContentTitle("Incoming remote session")
            .setContentText("A device is requesting to connect. Tap to review.")
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_CALL)
            .setFullScreenIntent(open, true)
            .setAutoCancel(true)
            .setContentIntent(open)
            .build()
        nm.notify(ID_INCOMING, n)
    }
}
