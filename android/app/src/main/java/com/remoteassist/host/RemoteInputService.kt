package com.remoteassist.host

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.graphics.Path
import android.os.Bundle
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import org.json.JSONObject

/**
 * Performs remote input on the controlled device. This is the ONLY use of the
 * AccessibilityService: dispatching gestures / global actions / text during an
 * approved session. It never reads or transmits screen content.
 */
class RemoteInputService : AccessibilityService() {

    override fun onServiceConnected() { instance = this }
    override fun onAccessibilityEvent(event: AccessibilityEvent) {}
    override fun onInterrupt() {}
    override fun onDestroy() { if (instance === this) instance = null; super.onDestroy() }

    fun tap(px: Float, py: Float) {
        val path = Path().apply { moveTo(px, py) }
        dispatchGesture(
            GestureDescription.Builder()
                .addStroke(GestureDescription.StrokeDescription(path, 0, 50)).build(),
            null, null
        )
    }

    fun swipe(x1: Float, y1: Float, x2: Float, y2: Float, ms: Long) {
        val path = Path().apply { moveTo(x1, y1); lineTo(x2, y2) }
        dispatchGesture(
            GestureDescription.Builder()
                .addStroke(GestureDescription.StrokeDescription(path, 0, ms.coerceAtLeast(1))).build(),
            null, null
        )
    }

    fun global(action: String): Boolean = when (action) {
        "BACK" -> performGlobalAction(GLOBAL_ACTION_BACK)
        "HOME" -> performGlobalAction(GLOBAL_ACTION_HOME)
        "RECENTS" -> performGlobalAction(GLOBAL_ACTION_RECENTS)
        else -> false
    }

    fun typeText(text: String) {
        val node = findFocus(AccessibilityNodeInfo.FOCUS_INPUT) ?: return
        val args = Bundle().apply {
            putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, text)
        }
        node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)
    }

    companion object {
        @Volatile var instance: RemoteInputService? = null

        fun isEnabled(context: android.content.Context): Boolean {
            val expected = "${context.packageName}/${RemoteInputService::class.java.name}"
            val enabled = android.provider.Settings.Secure.getString(
                context.contentResolver,
                android.provider.Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES
            ) ?: return false
            return enabled.split(':').any { it.equals(expected, ignoreCase = true) }
        }
    }
}

/**
 * Translates a control message from the DataChannel into an input action,
 * mapping normalized 0..1 coordinates onto the current screen size.
 */
object InputRouter {
    @Volatile var screenWidth: Int = 1080
    @Volatile var screenHeight: Int = 2400

    /** If true, control messages are ignored (e.g. view-only / locked). */
    @Volatile var controlEnabled: Boolean = true

    fun dispatch(msg: JSONObject) {
        if (!controlEnabled) return
        val svc = RemoteInputService.instance ?: return
        val sw = screenWidth.toFloat(); val sh = screenHeight.toFloat()
        when (msg.getString("t")) {
            "tap" -> svc.tap((msg.getDouble("x") * sw).toFloat(), (msg.getDouble("y") * sh).toFloat())
            "swipe" -> svc.swipe(
                (msg.getDouble("x1") * sw).toFloat(), (msg.getDouble("y1") * sh).toFloat(),
                (msg.getDouble("x2") * sw).toFloat(), (msg.getDouble("y2") * sh).toFloat(),
                msg.optLong("ms", 200)
            )
            "key" -> svc.global(msg.getString("k"))
            "text" -> svc.typeText(msg.getString("s"))
        }
    }
}
