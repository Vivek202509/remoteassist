package com.remoteassist

import android.app.Application
import com.google.firebase.messaging.FirebaseMessaging
import com.remoteassist.host.Notifications

class RemoteApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Notifications.ensureChannels(this)
        ServiceLocator.init(this, BuildConfig.SIGNALING_URL)
        registerFcmToken()
    }

    private fun registerFcmToken() {
        // Wrapped in try/catch so the app still runs without a google-services.json.
        try {
            FirebaseMessaging.getInstance().token.addOnCompleteListener { task ->
                if (task.isSuccessful) ServiceLocator.signaling.reportToken(task.result)
            }
        } catch (e: Exception) {
            android.util.Log.w("RemoteApp", "FCM unavailable: ${e.message}")
        }
    }
}
