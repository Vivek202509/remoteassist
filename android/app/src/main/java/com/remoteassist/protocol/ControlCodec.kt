package com.remoteassist.protocol

import org.json.JSONArray
import org.json.JSONObject
import kotlin.math.floor

/**
 * A decoded control message, platform-neutral.
 *
 * Every command an Android or Windows endpoint can express appears here exactly
 * once, regardless of which dialect carried it. Downstream code never sees the
 * wire form, so the legacy/v1 split does not leak into input dispatch, UI, or
 * authorization.
 */
sealed interface Control {
    /** The v1 type name. Also the key used for permission lookup. */
    val kind: String

    data class PointerTap(val x: Double, val y: Double) : Control {
        override val kind get() = "pointer.tap"
    }

    data class PointerMove(val x: Double, val y: Double) : Control {
        override val kind get() = "pointer.move"
    }

    data class PointerDown(val x: Double, val y: Double, val button: String) : Control {
        override val kind get() = "pointer.down"
    }

    data class PointerUp(val x: Double, val y: Double, val button: String) : Control {
        override val kind get() = "pointer.up"
    }

    data class PointerWheel(val x: Double, val y: Double, val dx: Double, val dy: Double) : Control {
        override val kind get() = "pointer.wheel"
    }

    data class PointerSwipe(
        val x1: Double, val y1: Double, val x2: Double, val y2: Double, val ms: Long,
    ) : Control {
        override val kind get() = "pointer.swipe"
    }

    data class NavKey(val key: String) : Control {
        override val kind get() = "nav.key"
    }

    data class KeyDown(val code: String, val mods: List<String>?) : Control {
        override val kind get() = "keyboard.keyDown"
    }

    data class KeyUp(val code: String, val mods: List<String>?) : Control {
        override val kind get() = "keyboard.keyUp"
    }

    data class KeyText(val text: String) : Control {
        override val kind get() = "keyboard.text"
    }

    data class ClipboardSet(val mime: String, val text: String) : Control {
        override val kind get() = "clipboard.set"
    }

    data class ClipboardData(val mime: String, val text: String) : Control {
        override val kind get() = "clipboard.data"
    }

    data object ClipboardRequest : Control {
        override val kind get() = "clipboard.request"
    }

    data class System(override val kind: String) : Control

    data object DisplayList : Control {
        override val kind get() = "display.list"
    }

    data class DisplaySelect(val id: String) : Control {
        override val kind get() = "display.select"
    }

    data class HostCallState(val state: String) : Control {
        override val kind get() = "host.callState"
    }

    data class Hello(val meta: Protocol.EndpointMeta, val ack: Boolean) : Control {
        override val kind get() = if (ack) "hello.ack" else "hello"
    }

    /** Recognised but not modelled by this build. Ignored safely, not treated as hostile. */
    data class Opaque(override val kind: String) : Control
}

/**
 * Encodes and decodes Techee control frames.
 *
 * Two dialects share one DataChannel:
 *  - **v0 (legacy)** — what every shipped Android build sends: `{"t":"tap",…}`.
 *  - **v1** — versioned and platform-neutral: `{"v":1,"t":"pointer.tap",…}`.
 *
 * Both decode to the same [Control], so a v1 Windows host and a v0 Android
 * controller interoperate without either knowing which dialect the other speaks.
 *
 * Nothing here throws. A frame is attacker-influenced input arriving on a WebRTC
 * network thread; on an unattended host, an exception there takes down the
 * handset that nobody is standing next to. Every rejection is a `null` return.
 */
object ControlCodec {

    private val NAV_KEYS = setOf("BACK", "HOME", "RECENTS")
    private val POINTER_BUTTONS = setOf("left", "right", "middle", "x1", "x2")
    private val CLIPBOARD_MIMES = setOf("text/plain")

    /** v0 type name -> v1 type name. */
    private val V0_TO_V1 = mapOf(
        "tap" to "pointer.tap",
        "swipe" to "pointer.swipe",
        "key" to "nav.key",
        "text" to "keyboard.text",
        "callstate" to "host.callState",
    )

    /** v1 type name -> v0 type name, for the messages the legacy dialect can express. */
    private val V1_TO_V0 = V0_TO_V1.entries.associate { (k, v) -> v to k }

    /** True when a command can be expressed to a peer that only speaks the legacy dialect. */
    fun hasLegacyForm(kind: String): Boolean = V1_TO_V0.containsKey(kind)

    // ---- decoding -------------------------------------------------------

    /**
     * Parse a raw DataChannel payload.
     *
     * The size ceiling is enforced before parsing, so an oversized payload costs
     * a length check rather than a full JSON parse.
     */
    fun decodeRaw(bytes: ByteArray): Control? {
        if (bytes.size > Protocol.MAX_FRAME_BYTES) return null
        val obj = try {
            JSONObject(String(bytes, Charsets.UTF_8))
        } catch (_: Exception) {
            // Not JSON, or JSON that is not an object. Both are simply dropped.
            return null
        }
        return decode(obj)
    }

    /** Decode a parsed frame into a command, or null for anything malformed or unknown. */
    fun decode(frame: JSONObject): Control? {
        // Version gate. Absent means the legacy dialect; present must be a number
        // we support. A future version is refused, never guessed at.
        //
        // The test is on the VALUE, not the spelling. JSON has no integer type, so
        // 1 and 1.0 are the same number and a parser may hand back either as the
        // other — org.json gives an Int for `1` and a Double for `1.0`, while a JS
        // parser cannot tell them apart at all. Keying off the Kotlin type would
        // therefore drop a frame from a perfectly ordinary v1 peer purely because
        // its encoder wrote a decimal point.
        val version: Int
        if (!frame.has("v")) {
            version = Protocol.LEGACY_VERSION
        } else {
            val v = frame.opt("v") as? Number ?: return null
            val d = v.toDouble()
            // Integral and in range. A fractional version is not one we speak.
            if (!d.isFinite() || d != floor(d)) return null
            if (d < Protocol.MIN_PROTOCOL_VERSION.toDouble() || d > Protocol.PROTOCOL_VERSION.toDouble()) return null
            version = d.toInt()
        }

        val rawType = (frame.opt("t") as? String)?.takeIf { it.isNotEmpty() } ?: return null

        // Legacy names are lifted into their v1 equivalents so everything
        // downstream reasons about one vocabulary. The two vocabularies stay
        // disjoint: a legacy frame may only use legacy names, and vice versa, so
        // a frame's dialect is never ambiguous.
        val kind = if (version == Protocol.LEGACY_VERSION) {
            V0_TO_V1[rawType] ?: return null
        } else {
            rawType.takeIf { Protocol.isKnownCommand(it) } ?: return null
        }

        return when (kind) {
            "pointer.tap" -> xy(frame)?.let { Control.PointerTap(it.first, it.second) }
            "pointer.move" -> xy(frame)?.let { Control.PointerMove(it.first, it.second) }

            "pointer.down", "pointer.up" -> {
                val p = xy(frame) ?: return null
                val button = if (frame.has("b")) frame.opt("b") as? String ?: return null else "left"
                if (button !in POINTER_BUTTONS) return null
                if (kind == "pointer.down") Control.PointerDown(p.first, p.second, button)
                else Control.PointerUp(p.first, p.second, button)
            }

            "pointer.wheel" -> {
                val p = xy(frame) ?: return null
                val dx = finite(frame, "dx") ?: return null
                val dy = finite(frame, "dy") ?: return null
                Control.PointerWheel(p.first, p.second, dx, dy)
            }

            "pointer.swipe" -> {
                val x1 = coord(frame, "x1") ?: return null
                val y1 = coord(frame, "y1") ?: return null
                val x2 = coord(frame, "x2") ?: return null
                val y2 = coord(frame, "y2") ?: return null
                val ms = if (!frame.has("ms")) Protocol.DEFAULT_SWIPE_MS
                else (finite(frame, "ms") ?: return null).toLong()
                // Clamped rather than rejected, preserving the shipped Android
                // behaviour: a hostile duration cannot wedge the gesture queue,
                // but an ordinary rounding artefact still produces a gesture.
                Control.PointerSwipe(x1, y1, x2, y2, ms.coerceIn(1L, Protocol.MAX_SWIPE_MS))
            }

            "nav.key" -> (frame.opt("k") as? String)?.takeIf { it in NAV_KEYS }?.let { Control.NavKey(it) }

            "keyboard.keyDown", "keyboard.keyUp" -> {
                val code = (frame.opt("code") as? String)?.takeIf { it.isNotEmpty() && it.length <= 32 } ?: return null
                val mods = if (frame.has("mods")) modifiers(frame.optJSONArray("mods")) ?: return null else null
                if (kind == "keyboard.keyDown") Control.KeyDown(code, mods) else Control.KeyUp(code, mods)
            }

            "keyboard.text" -> {
                val s = frame.opt("s") as? String ?: return null
                if (s.toByteArray(Charsets.UTF_8).size > Protocol.MAX_TEXT_BYTES) return null
                Control.KeyText(s)
            }

            "clipboard.set", "clipboard.data" -> {
                val s = frame.opt("s") as? String ?: return null
                if (s.toByteArray(Charsets.UTF_8).size > Protocol.MAX_CLIPBOARD_BYTES) return null
                val mime = if (frame.has("mime")) frame.opt("mime") as? String ?: return null else "text/plain"
                // v1 is text/plain only. Refusing unknown formats outright is
                // deliberate: HTML and file payloads are how a clipboard channel
                // becomes a delivery mechanism, and adding them needs its own
                // threat-model pass rather than a permissive default.
                if (mime !in CLIPBOARD_MIMES) return null
                if (kind == "clipboard.set") Control.ClipboardSet(mime, s) else Control.ClipboardData(mime, s)
            }

            "clipboard.request" -> Control.ClipboardRequest
            "display.list" -> Control.DisplayList

            "display.select" ->
                (frame.opt("id") as? String)?.takeIf { it.isNotEmpty() && it.length <= 64 }
                    ?.let { Control.DisplaySelect(it) }

            "system.lock", "system.sleep", "system.hibernate", "system.restart", "system.shutdown" ->
                Control.System(kind)

            "host.callState" ->
                (frame.opt("state") as? String)?.takeIf { it.isNotEmpty() && it.length <= 32 }
                    ?.let { Control.HostCallState(it) }

            "hello", "hello.ack" ->
                Protocol.parseEndpointMeta(frame)?.let { Control.Hello(it, ack = kind == "hello.ack") }

            "display.info", "host.status", "error" -> Control.Opaque(kind)

            else -> null
        }
    }

    // ---- encoding -------------------------------------------------------

    /**
     * Render a command into the dialect a peer understands.
     *
     * Pass [Protocol.LEGACY_VERSION] for a peer that never sent `hello`. Returns
     * null when the command has no representation in that dialect — a Windows
     * power action simply cannot be expressed to a peer that predates it, and
     * inventing an encoding it would misread is worse than not sending.
     *
     * This is a presentation-layer downgrade and nothing more. It does not, and
     * must not, weaken authentication: SDP identity binding is enforced
     * separately, is never negotiated, and has no legacy fallback.
     */
    fun encode(cmd: Control, version: Int = Protocol.PROTOCOL_VERSION): JSONObject? {
        // [Control.System] and [Control.Opaque] carry their kind as a plain string,
        // so an unknown one is representable and would otherwise be serialised and
        // sent. The peer would reject it, but the frame would still have left this
        // device. Refuse it here instead, as the JS and C# encoders do.
        if (!Protocol.isKnownCommand(cmd.kind)) return null

        val o = JSONObject().put("v", Protocol.PROTOCOL_VERSION).put("t", cmd.kind)
        when (cmd) {
            is Control.PointerTap -> o.put("x", cmd.x).put("y", cmd.y)
            is Control.PointerMove -> o.put("x", cmd.x).put("y", cmd.y)
            is Control.PointerDown -> o.put("x", cmd.x).put("y", cmd.y).put("b", cmd.button)
            is Control.PointerUp -> o.put("x", cmd.x).put("y", cmd.y).put("b", cmd.button)
            is Control.PointerWheel -> o.put("x", cmd.x).put("y", cmd.y).put("dx", cmd.dx).put("dy", cmd.dy)
            is Control.PointerSwipe ->
                o.put("x1", cmd.x1).put("y1", cmd.y1).put("x2", cmd.x2).put("y2", cmd.y2).put("ms", cmd.ms)
            is Control.NavKey -> o.put("k", cmd.key)
            is Control.KeyDown -> o.put("code", cmd.code).also { j -> cmd.mods?.let { j.put("mods", JSONArray(it)) } }
            is Control.KeyUp -> o.put("code", cmd.code).also { j -> cmd.mods?.let { j.put("mods", JSONArray(it)) } }
            is Control.KeyText -> o.put("s", cmd.text)
            is Control.ClipboardSet -> o.put("mime", cmd.mime).put("s", cmd.text)
            is Control.ClipboardData -> o.put("mime", cmd.mime).put("s", cmd.text)
            is Control.DisplaySelect -> o.put("id", cmd.id)
            is Control.HostCallState -> o.put("state", cmd.state)
            is Control.Hello -> {
                o.put("platform", cmd.meta.platform)
                o.put("version", cmd.meta.version)
                o.put("capabilities", JSONArray(cmd.meta.capabilities))
            }
            else -> Unit // payload-free commands
        }

        if (version != Protocol.LEGACY_VERSION) return o

        val legacyType = V1_TO_V0[cmd.kind] ?: return null
        o.remove("v")
        o.put("t", legacyType)
        return o
    }

    // ---- field readers --------------------------------------------------

    private fun xy(frame: JSONObject): Pair<Double, Double>? {
        val x = coord(frame, "x") ?: return null
        val y = coord(frame, "y") ?: return null
        return x to y
    }

    /**
     * A normalized 0..1 surface coordinate.
     *
     * Non-numeric and non-finite values are rejected; finite values are clamped,
     * so a rounding artefact at the edge still lands on the edge instead of
     * discarding the whole frame.
     */
    private fun coord(frame: JSONObject, key: String): Double? =
        finite(frame, key)?.coerceIn(0.0, 1.0)

    /** A finite JSON number. Rejects strings, booleans, nulls, NaN and infinities. */
    private fun finite(frame: JSONObject, key: String): Double? {
        if (!frame.has(key)) return null
        val v = frame.opt(key)
        if (v !is Number) return null
        val d = v.toDouble()
        if (d.isNaN() || d.isInfinite()) return null
        return d
    }

    /** All-or-nothing: one bad modifier invalidates the frame rather than being dropped silently. */
    private fun modifiers(arr: JSONArray?): List<String>? {
        if (arr == null) return null
        val out = ArrayList<String>(arr.length())
        for (i in 0 until arr.length()) {
            val m = arr.opt(i) as? String ?: return null
            if (m.isEmpty() || m.length > 32) return null
            out.add(m)
        }
        return out
    }
}
