package com.remoteassist

import android.Manifest
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.lifecycle.viewmodel.compose.viewModel
import com.remoteassist.ui.AppRoot
import com.remoteassist.ui.theme.RemoteAssistTheme

class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        setContent {
            RemoteAssistTheme {
                val vm: MainViewModel = viewModel()

                // Notification permission (Android 13+)
                val notifLauncher = rememberLauncherForActivityResult(
                    ActivityResultContracts.RequestPermission()
                ) { /* result handled implicitly */ }

                // MediaProjection consent -> on OK, start hosting for the pending controller
                val projectionLauncher = rememberLauncherForActivityResult(
                    ActivityResultContracts.StartActivityForResult()
                ) { result ->
                    if (result.resultCode == RESULT_OK && result.data != null) {
                        vm.approveIncoming(result.resultCode, result.data!!)
                    } else {
                        vm.declineIncoming()
                    }
                }

                AppRoot(
                    vm = vm,
                    onRequestNotifications = {
                        if (Build.VERSION.SDK_INT >= 33) notifLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                    },
                    onAllowIncoming = {
                        val mpm = getSystemService(MediaProjectionManager::class.java)
                        projectionLauncher.launch(mpm.createScreenCaptureIntent())
                    },
                    onOpenAccessibilitySettings = {
                        startActivity(
                            Intent(android.provider.Settings.ACTION_ACCESSIBILITY_SETTINGS)
                                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                        )
                    }
                )
            }
        }
    }
}
