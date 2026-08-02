package com.remoteassist.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.QrCode2
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.remoteassist.MainViewModel
import com.remoteassist.Screen
import com.remoteassist.webrtc.LinkState

/**
 * Single-activity Compose root. Renders the current [Screen] and overlays the
 * consent dialog whenever an incoming controller is awaiting host approval.
 */
@Composable
fun AppRoot(
    vm: MainViewModel,
    onRequestNotifications: () -> Unit,
    onAllowIncoming: () -> Unit,
    onOpenAccessibilitySettings: () -> Unit,
) {
    Surface(Modifier.fillMaxSize()) {
        when (vm.screen) {
            Screen.HOME -> HomeScreen(vm, onRequestNotifications, onOpenAccessibilitySettings)
            Screen.HOST_CODE -> HostCodeScreen(vm)
            Screen.JOIN -> JoinScreen(vm)
            Screen.SCAN -> QrScannerScreen(
                onCode = { raw -> vm.onScanned(raw) },
                onManual = { vm.goJoin() },
                onCancel = { vm.goJoin() },
            )
            Screen.PAIRED -> PairedDevicesScreen(vm)
            Screen.SAFETY_NUMBER -> SafetyNumberScreen(vm)
            Screen.SESSION -> SessionScreen(vm)
        }
    }

    // Consent overlay (host side) — the compliance-critical gate.
    vm.pendingControllerId?.let { controllerId ->
        ConsentDialog(
            controllerId = controllerId,
            unattended = vm.pendingUnattended,
            onAllow = onAllowIncoming,     // launches MediaProjection dialog, then approveIncoming()
            onDeny = { vm.declineIncoming() }
        )
    }
}

@Composable
private fun HomeScreen(
    vm: MainViewModel,
    onRequestNotifications: () -> Unit,
    onOpenAccessibilitySettings: () -> Unit,
) {
    Column(
        Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        Text("RemoteAssist", style = MaterialTheme.typography.headlineMedium)

        ElevatedCard(Modifier.fillMaxWidth().clickable { vm.becomeHost() }) {
            Column(Modifier.padding(20.dp)) {
                Text("Share my screen", style = MaterialTheme.typography.titleMedium)
                Text(
                    "Generate a code so another device can view & control this phone",
                    style = MaterialTheme.typography.bodyMedium
                )
            }
        }

        ElevatedCard(Modifier.fillMaxWidth().clickable { vm.goJoin() }) {
            Column(Modifier.padding(20.dp)) {
                Text("Connect to a device", style = MaterialTheme.typography.titleMedium)
                Text("Enter a 6-digit code to control another phone", style = MaterialTheme.typography.bodyMedium)
            }
        }

        OutlinedButton(onClick = { vm.goPaired() }) { Text("Paired devices") }

        Spacer(Modifier.height(8.dp))
        SetupStatusRow(
            accessibilityEnabled = vm.accessibilityEnabled,
            onEnableAccessibility = onOpenAccessibilitySettings,
            onRequestNotifications = onRequestNotifications,
        )
    }
}

@Composable
private fun SetupStatusRow(
    accessibilityEnabled: Boolean,
    onEnableAccessibility: () -> Unit,
    onRequestNotifications: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("Setup", style = MaterialTheme.typography.titleSmall)
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                if (accessibilityEnabled) "✓ Accessibility enabled" else "• Accessibility required for control",
                Modifier.weight(1f), style = MaterialTheme.typography.bodySmall
            )
            if (!accessibilityEnabled) TextButton(onClick = onEnableAccessibility) { Text("Enable") }
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("• Notifications", Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = onRequestNotifications) { Text("Allow") }
        }
    }
}

@Composable
private fun HostCodeScreen(vm: MainViewModel) {
    Column(
        Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Text("Scan this code, or enter it manually")
        Spacer(Modifier.height(16.dp))
        val payload = vm.sessionQrPayload()
        if (payload != null) {
            QrImage(text = payload, sizePx = 560, modifier = Modifier.size(220.dp))
            Spacer(Modifier.height(16.dp))
        }
        Text(
            vm.sessionCode ?: "······",
            style = MaterialTheme.typography.displayMedium,
            letterSpacing = 8.sp
        )
        Spacer(Modifier.height(8.dp))
        Text("Expires in 5 minutes", style = MaterialTheme.typography.bodySmall)
        Spacer(Modifier.height(32.dp))
        OutlinedButton(onClick = { vm.cancelHost() }) { Text("Cancel") }
    }
}

@Composable
private fun JoinScreen(vm: MainViewModel) {
    var code by remember { mutableStateOf("") }
    Column(
        Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Spacer(Modifier.height(48.dp))
        Text("Enter connection code", style = MaterialTheme.typography.titleLarge)
        Spacer(Modifier.height(24.dp))
        OutlinedTextField(
            value = code,
            onValueChange = { if (it.length <= 6 && it.all(Char::isDigit)) code = it },
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
            textStyle = TextStyle(fontSize = 32.sp, letterSpacing = 8.sp, textAlign = TextAlign.Center),
            singleLine = true,
            enabled = !vm.connecting
        )
        vm.joinError?.let {
            Spacer(Modifier.height(8.dp))
            Text(joinErrorMessage(it), color = MaterialTheme.colorScheme.error, textAlign = TextAlign.Center)
        }
        Spacer(Modifier.height(24.dp))

        if (vm.connecting) {
            CircularProgressIndicator()
            Spacer(Modifier.height(8.dp))
            Text("Waiting for the host to accept…", style = MaterialTheme.typography.bodyMedium)
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = { vm.cancelConnecting() }) { Text("Cancel") }
        } else {
            Button(onClick = { vm.connectWithCode(code) }, enabled = code.length == 6) { Text("Connect") }
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = { vm.goScan() }) {
                Icon(Icons.Default.QrCodeScanner, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text("Scan QR code")
            }
            TextButton(onClick = { vm.goHome() }) { Text("Back") }
        }
    }
}

private fun joinErrorMessage(reason: String): String = when (reason) {
    "invalid-code" -> "That code isn't valid. Enter the 6 digits shown on the host."
    "not-paired" -> "This device isn't paired with that host."
    "host-offline" -> "The host is offline. Ask them to open Share Screen again."
    "declined" -> "The host declined the connection."
    "timeout" -> "No response — the host may have declined or gone offline."
    "unreadable-qr" -> "That QR code didn't contain a valid session code."
    "no-target", "invalid" -> "The session code has expired. Ask the host for a new one."
    else -> "Connection failed ($reason)."
}

@Composable
private fun PairedDevicesScreen(vm: MainViewModel) {
    Column(Modifier.fillMaxSize().padding(24.dp)) {
        Text("Paired devices", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(16.dp))

        val peers = vm.pairedPeers()
        if (peers.isEmpty()) {
            Text("No paired devices yet.", style = MaterialTheme.typography.bodyMedium)
        }
        peers.forEach { peer ->
            ListItem(
                headlineContent = { Text(peer.peerName) },
                supportingContent = {
                    Column {
                        Text("Fingerprint: ${peer.shortFingerprint}", style = MaterialTheme.typography.bodySmall)
                        Text("State: ${peer.state}", style = MaterialTheme.typography.bodySmall)
                    }
                },
                trailingContent = {
                    Row {
                        TextButton(onClick = { vm.connectToPaired(peer.peerId) }) { Text("Connect") }
                        TextButton(onClick = { vm.revokePeer(peer) }) { Text("Remove") }
                    }
                }
            )
            HorizontalDivider()
        }

        Spacer(Modifier.height(16.dp))
        FilledTonalButton(onClick = { /* launch QR pairing flow (scanner) */ }) {
            Icon(Icons.Default.QrCode2, contentDescription = null)
            Spacer(Modifier.width(8.dp))
            Text("Pair a new device")
        }
        Spacer(Modifier.height(8.dp))
        TextButton(onClick = { vm.goHome() }) { Text("Back") }
    }
}

@Composable
private fun SafetyNumberScreen(vm: MainViewModel) {
    Column(
        Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Text("Verify this code matches on BOTH devices", textAlign = TextAlign.Center)
        Spacer(Modifier.height(24.dp))
        Text(
            vm.safetyNumber ?: "",
            style = MaterialTheme.typography.headlineMedium,
            letterSpacing = 4.sp
        )
        Spacer(Modifier.height(16.dp))
        Text(
            "Read it aloud to the other person. Only confirm if they match exactly.",
            style = MaterialTheme.typography.bodySmall,
            textAlign = TextAlign.Center
        )
        Spacer(Modifier.height(32.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedButton(onClick = { vm.rejectPairing() }) { Text("They don't match") }
            Button(onClick = { vm.confirmPairing() }) { Text("They match") }
        }
    }
}

@Composable
private fun SessionScreen(vm: MainViewModel) {
    Column(Modifier.fillMaxSize().padding(16.dp)) {
        // Connection-state banner.
        val (statusText, statusColor) = when (vm.linkState) {
            LinkState.CONNECTING -> "Connecting…" to MaterialTheme.colorScheme.onSurfaceVariant
            LinkState.CONNECTED -> "Connected" to MaterialTheme.colorScheme.primary
            LinkState.RECOVERING -> "Reconnecting…" to MaterialTheme.colorScheme.error
            LinkState.CLOSED -> "Session ended" to MaterialTheme.colorScheme.error
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (vm.linkState == LinkState.RECOVERING || vm.linkState == LinkState.CONNECTING) {
                CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(8.dp))
            }
            Text(statusText, style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold, color = statusColor)
        }
        Spacer(Modifier.height(8.dp))

        // Live remote screen + touch-to-control.
        RemoteVideoSurface(
            eglBase = vm.eglBase,
            track = vm.remoteVideo,
            onTap = { nx, ny -> vm.sendTap(nx, ny) },
            onSwipe = { x1, y1, x2, y2, ms -> vm.sendSwipe(x1, y1, x2, y2, ms) },
            modifier = Modifier.weight(1f).fillMaxWidth()
        )
        Spacer(Modifier.height(8.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = { vm.sendKey("BACK") }) { Text("Back") }
            OutlinedButton(onClick = { vm.sendKey("HOME") }) { Text("Home") }
            OutlinedButton(onClick = { vm.sendKey("RECENTS") }) { Text("Recents") }
        }
        Spacer(Modifier.height(8.dp))
        Button(
            onClick = { vm.endSession() },
            colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)
        ) { Text("End session") }
    }

    // Retry dialog when recovery exhausts and the link closes unexpectedly.
    if (vm.linkState == LinkState.CLOSED) {
        AlertDialog(
            onDismissRequest = { },
            title = { Text("Connection lost") },
            text = { Text("The session ended. You can try to reconnect or return home.") },
            confirmButton = { Button(onClick = { vm.retryConnect() }) { Text("Reconnect") } },
            dismissButton = { TextButton(onClick = { vm.endSession() }) { Text("Home") } }
        )
    }
}

@Composable
private fun ConsentDialog(
    controllerId: String,
    unattended: Boolean,
    onAllow: () -> Unit,
    onDeny: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDeny,
        icon = { Icon(Icons.Default.Warning, contentDescription = null) },
        title = { Text(if (unattended) "Trusted device connecting" else "Allow remote control?") },
        text = {
            Column {
                Text("Device \"${controllerId.take(12)}…\" is requesting to VIEW and CONTROL this phone.")
                Spacer(Modifier.height(12.dp))
                Text("• They will see your screen live", style = MaterialTheme.typography.bodySmall)
                Text("• They can tap, swipe, and type", style = MaterialTheme.typography.bodySmall)
                Text("• A notification stays visible while active", style = MaterialTheme.typography.bodySmall)
                Text("• You can end the session anytime", style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = { Button(onClick = onAllow) { Text("Allow") } },
        dismissButton = { TextButton(onClick = onDeny) { Text("Deny") } }
    )
}
