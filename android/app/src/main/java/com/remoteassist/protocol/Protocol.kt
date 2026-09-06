package com.remoteassist.protocol

import org.json.JSONArray
import org.json.JSONObject

/**
 * The Techee protocol contract, in Kotlin.
 *
 * This is one of three implementations of the same specification — the others
 * are `server/src/protocol.js` and (from W2) `Techee.Protocol` on Windows. None
 * is derived from the others; all three are held to the shared golden vectors in
 * `protocol/fixtures/`, which is what stops them drifting apart. See
 * docs/PROTOCOL.md for the normative prose.
 *
 * Deliberately free of Android types so the whole thing is exercisable as an
 * ordinary JVM unit test, the same reasoning that keeps [com.remoteassist.webrtc.SdpAuth]
 * testable.
 */
object Protocol {

    /**
     * Bumping this means adding messages or fields. It never means changing what
     * a v1 message already means: a peer that only speaks v1 must be able to go
     * on speaking v1 to a newer peer indefinitely.
     */
    const val PROTOCOL_VERSION = 1
    const val MIN_PROTOCOL_VERSION = 1

    /**
     * Frames with no `v` are the pre-versioning dialect that every shipped
     * Android build speaks. It is a supported dialect, not an error tolerated.
     */
    const val LEGACY_VERSION = 0

    val PLATFORMS = listOf("android", "windows")

    val CAPABILITIES = listOf(
        "screen.share", "screen.receive",
        "input.send", "input.receive",
        "audio.microphone.send", "audio.desktop.send", "audio.receive",
        "clipboard",
        "display.multi",
        "nav.android",
        "power.lock", "power.sleep", "power.hibernate", "power.restart", "power.shutdown",
    )

    val PERMISSIONS = listOf(
        "screen.view",
        "input.control",
        "clipboard.read", "clipboard.write",
        "system.lock", "system.sleep", "system.hibernate", "system.restart", "system.shutdown",
        "files.transfer",
    )

    const val MAX_CAPABILITIES = 32
    const val MAX_CAPABILITY_LENGTH = 40
    const val MAX_VERSION_LENGTH = 32
    const val MAX_PERMISSIONS = 32
    const val MAX_FRAME_BYTES = 65536
    const val MAX_TEXT_BYTES = 4096
    const val MAX_CLIPBOARD_BYTES = 65536
    const val MAX_SWIPE_MS = 10_000L
    const val DEFAULT_SWIPE_MS = 200L

    private val capabilitySet = CAPABILITIES.toSet()
    private val permissionSet = PERMISSIONS.toSet()

    /**
     * How the shipped [com.remoteassist.unattended.Scope] enum maps onto v1
     * permission tokens.
     *
     * Note what is absent: nothing maps to any `system.*` permission. Every grant
     * created before power control existed therefore confers no power over the
     * machine, so an app update cannot silently hand an old controller the
     * ability to shut down a PC. That is the reason this is a table with a
     * fixture behind it rather than an inline `when`.
     */
    val LEGACY_SCOPE_MAP: Map<String, List<String>> = mapOf(
        "VIEW" to listOf("screen.view"),
        // Controlling a screen you may not see is not a coherent grant.
        "CONTROL" to listOf("input.control", "screen.view"),
        "CLIPBOARD" to listOf("clipboard.read", "clipboard.write"),
        "FILES" to listOf("files.transfer"),
    )

    /** What a peer says it is. Everything here is unverified self-description. */
    data class EndpointMeta(
        val platform: String,
        val version: String,
        val capabilities: List<String>,
    ) {
        fun toJson(): JSONObject = JSONObject().apply {
            put("platform", platform)
            put("version", version)
            put("capabilities", JSONArray(capabilities))
        }

        /**
         * Whether the peer claims to support something.
         *
         * Use this to decide whether to *show* an affordance, never whether to
         * *allow* an action. A peer advertising `power.shutdown` has told you it
         * owns a shutdown button, not that you may press it.
         */
        fun has(capability: String): Boolean = capabilities.contains(capability)
    }

    /**
     * Validate a peer's self-description.
     *
     * Returns null when the metadata is absent or unusable. Null is not a
     * failure to escalate — it means "this peer did not say what it is", which is
     * exactly what every Android build shipped so far does. Treat it as unknown
     * and hide capability-gated UI; never refuse the peer over it.
     *
     * Unknown capability tokens are dropped rather than rejected, so a newer peer
     * advertising something this build has never heard of still connects.
     */
    fun parseEndpointMeta(raw: JSONObject?): EndpointMeta? {
        if (raw == null) return null

        val platform = raw.optString("platform").takeIf { it.isNotEmpty() } ?: return null
        if (platform !in PLATFORMS) return null

        val version = if (raw.has("version") && !raw.isNull("version")) {
            if (raw.opt("version") !is String) return null
            raw.getString("version")
        } else {
            "unknown"
        }
        if (version.isEmpty() || version.length > MAX_VERSION_LENGTH) return null

        val capabilities = linkedSetOf<String>()
        val arr = raw.optJSONArray("capabilities")
        if (arr != null) {
            for (i in 0 until arr.length()) {
                if (capabilities.size >= MAX_CAPABILITIES) break
                val c = arr.opt(i) as? String ?: continue
                if (c.length > MAX_CAPABILITY_LENGTH) continue
                if (c !in capabilitySet) continue
                capabilities.add(c)
            }
        }

        // Sorted so two implementations that build the list in different orders
        // still compare equal — the fixtures depend on this being deterministic.
        return EndpointMeta(platform, version, capabilities.sorted())
    }

    /** This device's own advertisement. */
    fun localMeta(appVersion: String, capabilities: List<String>): EndpointMeta =
        EndpointMeta("android", appVersion, capabilities.filter { it in capabilitySet }.distinct().sorted())

    /**
     * The permissions a grant actually confers, sorted and de-duplicated.
     *
     * Prefers an explicit `permissions` array; otherwise widens a legacy `scope`
     * array. Unknown tokens in either are dropped — a permission this build does
     * not understand is one it cannot enforce, so honouring it would be worse
     * than ignoring it.
     */
    fun normalizePermissions(permissions: Collection<String>?, legacyScope: Collection<String>?): List<String> {
        val out = linkedSetOf<String>()
        fun add(p: String) {
            if (out.size >= MAX_PERMISSIONS) return
            if (p in permissionSet) out.add(p)
        }
        if (permissions != null) {
            permissions.forEach { add(it) }
        } else if (legacyScope != null) {
            legacyScope.forEach { s -> LEGACY_SCOPE_MAP[s]?.forEach { add(it) } }
        }
        return out.sorted()
    }

    /**
     * Whether a grant is usable at [now].
     *
     * [expiresAt] null means no expiry. Any value present is honoured, **including
     * 0** — 0 is the Unix epoch, the most expired a grant can possibly be, so a
     * truthiness guard on this field fails open. Pinned by the `grantVectors` in
     * `protocol/fixtures/capabilities.json`, which is why this is a function on
     * the contract rather than a condition inlined at each call site.
     */
    fun grantUsable(active: Boolean, expiresAt: Long?, now: Long = System.currentTimeMillis()): Boolean =
        active && (expiresAt == null || expiresAt >= now)

    /**
     * Fail-closed authorization: the one question a host asks before acting.
     *
     * [permissions] and [legacyScope] are the grant's raw fields, widened through
     * [normalizePermissions] here rather than by the caller, so a legacy
     * scope-only grant and a v1 grant are judged by the identical rule.
     */
    fun grantPermits(
        permission: String,
        active: Boolean,
        expiresAt: Long?,
        permissions: Collection<String>?,
        legacyScope: Collection<String>?,
        now: Long = System.currentTimeMillis(),
    ): Boolean {
        if (permission !in permissionSet) return false
        if (!grantUsable(active, expiresAt, now)) return false
        return permission in normalizePermissions(permissions, legacyScope)
    }

    /** Which grant permission each command requires. Null means none. */
    private val REQUIRED_PERMISSION: Map<String, String?> = mapOf(
        "pointer.tap" to "input.control",
        "pointer.swipe" to "input.control",
        "pointer.move" to "input.control",
        "pointer.down" to "input.control",
        "pointer.up" to "input.control",
        "pointer.wheel" to "input.control",
        "keyboard.keyDown" to "input.control",
        "keyboard.keyUp" to "input.control",
        "keyboard.text" to "input.control",
        "nav.key" to "input.control",
        "clipboard.set" to "clipboard.write",
        "clipboard.request" to "clipboard.read",
        "clipboard.data" to null,
        "system.lock" to "system.lock",
        "system.sleep" to "system.sleep",
        "system.hibernate" to "system.hibernate",
        "system.restart" to "system.restart",
        "system.shutdown" to "system.shutdown",
        "display.list" to "screen.view",
        "display.select" to "screen.view",
        "display.info" to null,
        "host.status" to null,
        "host.callState" to null,
        "hello" to null,
        "hello.ack" to null,
        "error" to null,
    )

    fun isKnownCommand(kind: String): Boolean = REQUIRED_PERMISSION.containsKey(kind)

    /** The permission a command requires, or null if it needs none. */
    fun requiredPermission(kind: String): String? = REQUIRED_PERMISSION[kind]

    /**
     * The one question a host asks before executing a decoded command.
     *
     * Fails closed on every path: an unknown command, an unusable grant, or a
     * missing permission all return false.
     */
    fun authorize(kind: String, granted: Collection<String>?, grantUsable: Boolean): Boolean {
        if (!isKnownCommand(kind)) return false
        val needed = REQUIRED_PERMISSION[kind] ?: return true
        if (!grantUsable) return false
        return granted != null && needed in granted
    }
}
