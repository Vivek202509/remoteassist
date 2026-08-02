package com.remoteassist.fcm

import android.content.Context
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import com.remoteassist.ServiceLocator
import com.remoteassist.host.Notifications
import com.remoteassist.host.SignalingService
import com.remoteassist.host.UnattendedHostService

/**
 * Receives high-priority, data-only wake pushes. Because it's data-only,
 * onMessageReceived runs even in the background / Doze, giving us a brief window
 * to reconnect signaling and alert the owner (or auto-accept if unattended).
 */
class RemoteFcmService : FirebaseMessagingService() {

    override fun onNewToken(token: String) {
        // Tokens rotate — always re-report or wakes silently fail.
        try { ServiceLocator.signaling.reportToken(token) } catch (_: Exception) {}
    }

    override fun onMessageReceived(message: RemoteMessage) {
        if (message.data["type"] != "wake") return
        WakeCoordinator.onWakePush(
            applicationContext,
            controllerId = message.data["controllerId"],
            sessionId = message.data["sessionId"],
            unattended = message.data["unattended"] == "true" || message.data["unattended"] == "1",
        )
    }
}

object WakeCoordinator {
    fun onWakePush(ctx: Context, controllerId: String?, sessionId: String?, unattended: Boolean) {
        // 1) Bring signaling back up (socket was killed during Doze).
        SignalingService.ensureConnected(ctx)

        // 2) Route by grant: unattended -> let the persistent host service decide
        //    (it auto-accepts if the grant is valid, else posts the incoming notif);
        //    attended -> call-style incoming notification for the owner to approve.
        if (unattended && controllerId != null) {
            UnattendedHostService.notifyIncoming(ctx, controllerId)
        } else {
            Notifications.incoming(ctx, controllerId, sessionId)
        }
    }
}
