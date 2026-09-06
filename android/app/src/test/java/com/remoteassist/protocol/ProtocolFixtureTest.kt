package com.remoteassist.protocol

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Holds the Kotlin protocol implementation to the shared golden vectors in
 * `protocol/fixtures/` — the exact files `server/test/protocol.js` reads, mounted
 * onto the unit-test classpath by `app/build.gradle.kts`.
 *
 * The point is not that Kotlin agrees with itself. It is that Kotlin, Node and
 * (from W2) .NET are each independently held to one artifact, so a divergence
 * shows up as a red test in whichever language drifted rather than as a device
 * that mysteriously cannot connect.
 */
class ProtocolFixtureTest {

    private fun fixture(name: String): JSONObject {
        val stream = javaClass.getResourceAsStream("/$name")
            ?: error(
                "Missing protocol fixture '$name'. It should be mounted from " +
                    "<repo>/protocol/fixtures by the test sourceSet in app/build.gradle.kts."
            )
        return JSONObject(stream.bufferedReader().use { it.readText() })
    }

    private fun JSONArray.strings(): List<String> = (0 until length()).map { getString(it) }

    /** Fixtures name oversized payloads rather than inlining 70 KB of text. */
    private fun materialize(v: Any?): Any? = when {
        v is String && v.startsWith("GENERATE:") -> "a".repeat(v.removePrefix("GENERATE:").toInt())
        else -> v
    }

    private fun materializeFrame(o: JSONObject): JSONObject {
        val out = JSONObject()
        for (k in o.keys()) out.put(k, materialize(o.opt(k)))
        return out
    }

    // ---- vocabulary -----------------------------------------------------

    @Test
    fun `vocabulary matches the shared fixture`() {
        val fx = fixture("capabilities.json")

        assertEquals(fx.getJSONObject("protocolVersion").getInt("current"), Protocol.PROTOCOL_VERSION)
        assertEquals(fx.getJSONObject("protocolVersion").getInt("minSupported"), Protocol.MIN_PROTOCOL_VERSION)
        assertEquals(fx.getJSONArray("platforms").strings().sorted(), Protocol.PLATFORMS.sorted())
        assertEquals(fx.getJSONArray("knownCapabilities").strings().sorted(), Protocol.CAPABILITIES.sorted())
        assertEquals(fx.getJSONArray("knownPermissions").strings().sorted(), Protocol.PERMISSIONS.sorted())

        val limits = fx.getJSONObject("limits")
        assertEquals(limits.getInt("maxCapabilities"), Protocol.MAX_CAPABILITIES)
        assertEquals(limits.getInt("maxCapabilityLength"), Protocol.MAX_CAPABILITY_LENGTH)
        assertEquals(limits.getInt("maxVersionLength"), Protocol.MAX_VERSION_LENGTH)
        assertEquals(limits.getInt("maxPermissions"), Protocol.MAX_PERMISSIONS)
    }

    @Test
    fun `control limits match the shared fixture`() {
        val limits = fixture("control-v1.json").getJSONObject("limits")
        assertEquals(limits.getInt("maxFrameBytes"), Protocol.MAX_FRAME_BYTES)
        assertEquals(limits.getInt("maxTextBytes"), Protocol.MAX_TEXT_BYTES)
        assertEquals(limits.getInt("maxClipboardBytes"), Protocol.MAX_CLIPBOARD_BYTES)
        assertEquals(limits.getLong("maxSwipeMs"), Protocol.MAX_SWIPE_MS)
        assertEquals(limits.getLong("defaultSwipeMs"), Protocol.DEFAULT_SWIPE_MS)
    }

    // ---- endpoint metadata ---------------------------------------------

    @Test
    fun `endpoint metadata vectors`() {
        val vectors = fixture("capabilities.json").getJSONArray("metaVectors")
        for (i in 0 until vectors.length()) {
            val v = vectors.getJSONObject(i)
            val name = v.getString("name")

            var input = if (v.isNull("input")) null else v.getJSONObject("input")
            // One vector asks for 64 synthetic tokens rather than inlining them.
            if (input != null && input.opt("capabilities") == "GENERATE:64") {
                input = JSONObject(input.toString())
                    .put("capabilities", JSONArray((0 until 64).map { "synthetic.cap.$it" }))
            }

            val got = Protocol.parseEndpointMeta(input)

            if (v.isNull("expect")) {
                assertNull("$name: expected rejection", got)
            } else {
                val want = v.getJSONObject("expect")
                assertNotNull("$name: expected acceptance", got)
                assertEquals("$name: platform", want.getString("platform"), got!!.platform)
                assertEquals("$name: version", want.getString("version"), got.version)
                assertEquals("$name: capabilities", want.getJSONArray("capabilities").strings(), got.capabilities)
            }
        }
    }

    @Test
    fun `a capability advertisement can never authorize anything`() {
        // The invariant the descriptive/authoritative split exists to protect.
        val boastful = Protocol.parseEndpointMeta(
            JSONObject()
                .put("platform", "windows")
                .put("version", "1.0")
                .put("capabilities", JSONArray(listOf("power.shutdown", "power.restart")))
        )
        assertNotNull(boastful)
        assertTrue("advertisement is recorded", boastful!!.has("power.shutdown"))

        // ...and confers nothing.
        assertFalse(Protocol.authorize("system.shutdown", emptyList(), grantUsable = true))
        // A capability token is not interchangeable with a permission token.
        assertFalse(Protocol.authorize("system.shutdown", listOf("power.shutdown"), grantUsable = true))
    }

    // ---- permissions ----------------------------------------------------

    @Test
    fun `legacy scope mapping vectors`() {
        val vectors = fixture("capabilities.json")
            .getJSONObject("legacyScopeMapping").getJSONArray("vectors")
        for (i in 0 until vectors.length()) {
            val v = vectors.getJSONObject(i)
            val scope = v.getJSONArray("scope").strings()
            val want = v.getJSONArray("permissions").strings()
            assertEquals(
                "legacy scope $scope",
                want,
                Protocol.normalizePermissions(permissions = null, legacyScope = scope),
            )
        }
    }

    @Test
    fun `no legacy scope confers system power`() {
        // The upgrade-safety property, asserted directly rather than left as a
        // consequence of the table: an existing grant must not gain the ability
        // to shut down a PC merely because the app learned how.
        for (scope in listOf("VIEW", "CONTROL", "CLIPBOARD", "FILES")) {
            val perms = Protocol.normalizePermissions(null, listOf(scope))
            assertTrue(
                "legacy scope $scope leaked a system permission: $perms",
                perms.none { it.startsWith("system.") },
            )
        }
    }

    @Test
    fun `grant evaluation vectors`() {
        // B8 in docs/CROSS_PLATFORM_TEST_MATRIX.md. JS and C# have always driven
        // these vectors; Kotlin was listed as passing them without reading the
        // file, which is exactly the drift the fixtures exist to prevent.
        val vectors = fixture("capabilities.json").getJSONArray("grantVectors")
        assertTrue("fixture must carry grant vectors", vectors.length() > 0)

        for (i in 0 until vectors.length()) {
            val v = vectors.getJSONObject(i)
            val name = v.getString("name")
            val grant = v.getJSONObject("grant")

            val now = if (v.has("nowMs")) v.getLong("nowMs") else System.currentTimeMillis()
            val active = grant.optBoolean("active", false)
            val expiresAt = if (grant.has("expiresAt")) grant.getLong("expiresAt") else null
            val permissions = grant.optJSONArray("permissions")?.strings()
            val legacyScope = grant.optJSONArray("scope")?.strings()

            for (p in v.getJSONArray("permits").strings()) {
                assertTrue(
                    "$name: must permit $p",
                    Protocol.grantPermits(p, active, expiresAt, permissions, legacyScope, now),
                )
            }
            for (p in v.getJSONArray("denies").strings()) {
                assertFalse(
                    "$name: must deny $p",
                    Protocol.grantPermits(p, active, expiresAt, permissions, legacyScope, now),
                )
            }
        }
    }

    @Test
    fun `grant expiry fails closed at the epoch`() {
        // Stated separately from the vector table because it is the trap: 0 is
        // falsy, so any truthiness guard on expiresAt reads "maximally expired"
        // as "never expires" and hands out an unattended session.
        assertFalse("epoch expiry must not read as never-expiring",
            Protocol.grantUsable(active = true, expiresAt = 0L, now = 2000L))
        assertFalse("a past expiry is expired",
            Protocol.grantUsable(active = true, expiresAt = 1999L, now = 2000L))
        assertTrue("expiry exactly at now is still valid",
            Protocol.grantUsable(active = true, expiresAt = 2000L, now = 2000L))
        assertTrue("absent expiry never expires",
            Protocol.grantUsable(active = true, expiresAt = null, now = Long.MAX_VALUE))
        assertFalse("an inactive grant is unusable however it expires",
            Protocol.grantUsable(active = false, expiresAt = null, now = 0L))
    }

    @Test
    fun `permission requirements match the shared fixture`() {
        val required = fixture("capabilities.json").getJSONObject("permissionRequiredFor")
        for (kind in required.keys()) {
            if (kind.startsWith("_")) continue
            val want = if (required.isNull(kind)) null else required.getString(kind)
            assertEquals("permission for $kind", want, Protocol.requiredPermission(kind))
            assertTrue("$kind should be a known command", Protocol.isKnownCommand(kind))
        }
    }

    @Test
    fun `authorization fails closed`() {
        val control = listOf("screen.view", "input.control")

        assertTrue(Protocol.authorize("pointer.tap", control, grantUsable = true))
        assertFalse("a control grant must not imply restart",
            Protocol.authorize("system.restart", control, grantUsable = true))
        assertFalse("an unusable grant permits nothing",
            Protocol.authorize("pointer.tap", control, grantUsable = false))
        assertFalse("a null permission set permits nothing",
            Protocol.authorize("pointer.tap", null, grantUsable = true))
        assertFalse("an unknown command is refused even with every permission",
            Protocol.authorize("system.selfDestruct", Protocol.PERMISSIONS, grantUsable = true))
        assertTrue("a permission-free command needs no grant",
            Protocol.authorize("host.status", null, grantUsable = false))
    }

    // ---- control frames -------------------------------------------------

    @Test
    fun `decode vectors`() {
        val vectors = fixture("control-v1.json").getJSONArray("decodeVectors")
        for (i in 0 until vectors.length()) {
            val v = vectors.getJSONObject(i)
            val name = v.getString("name")
            val got = ControlCodec.decode(materializeFrame(v.getJSONObject("frame")))
            val want = v.getJSONObject("expect")

            assertNotNull("$name: should decode", got)
            assertEquals("$name: kind", want.getString("kind"), got!!.kind)
            assertControlMatches(name, want, got)
        }
    }

    @Test
    fun `reject vectors`() {
        val vectors = fixture("control-v1.json").getJSONArray("rejectVectors")
        for (i in 0 until vectors.length()) {
            val v = vectors.getJSONObject(i)
            val got = ControlCodec.decode(materializeFrame(v.getJSONObject("frame")))
            assertNull("${v.getString("name")}: should be rejected, got $got", got)
        }
    }

    @Test
    fun `raw reject vectors are dropped, never thrown`() {
        val vectors = fixture("control-v1.json").getJSONArray("rawRejectVectors")
        for (i in 0 until vectors.length()) {
            val v = vectors.getJSONObject(i)
            val name = v.getString("name")
            var raw = v.getString("raw")
            if (raw.startsWith("GENERATE_NESTED:")) {
                val depth = raw.removePrefix("GENERATE_NESTED:").toInt()
                raw = "[".repeat(depth) + "]".repeat(depth)
            }

            // Two separate properties. It must reject — and it must reject by
            // returning rather than throwing, because this runs on the WebRTC
            // network thread of an unattended host.
            val got = try {
                ControlCodec.decodeRaw(raw.toByteArray(Charsets.UTF_8))
            } catch (e: Throwable) {
                throw AssertionError("$name: decodeRaw threw instead of returning null", e)
            }
            assertNull("$name: should be rejected, got $got", got)
        }
    }

    @Test
    fun `an oversized frame is refused on length alone`() {
        val huge = JSONObject()
            .put("v", 1).put("t", "keyboard.text")
            .put("s", "a".repeat(Protocol.MAX_FRAME_BYTES))
            .toString()
        assertNull(ControlCodec.decodeRaw(huge.toByteArray(Charsets.UTF_8)))
    }

    @Test
    fun `the two dialects stay disjoint`() {
        // A v1-only name must not be reachable from a legacy frame...
        for (t in listOf("pointer.tap", "system.restart", "clipboard.set", "keyboard.keyDown")) {
            val frame = JSONObject().put("t", t).put("x", 0.5).put("y", 0.5)
                .put("code", "KeyA").put("s", "x")
            assertNull("legacy frame must not accept v1 name $t", ControlCodec.decode(frame))
        }
        // ...and a legacy name must not be reachable from a v1 frame, so a
        // frame's dialect is never ambiguous.
        for (t in listOf("tap", "swipe", "key", "text", "callstate")) {
            val frame = JSONObject().put("v", 1).put("t", t).put("x", 0.5).put("y", 0.5)
                .put("k", "BACK").put("s", "x").put("state", "IDLE")
            assertNull("v1 frame must not accept legacy name $t", ControlCodec.decode(frame))
        }
    }

    @Test
    fun `encode vectors and dialect downgrade`() {
        val vectors = fixture("control-v1.json").getJSONArray("encodeVectors")
        for (i in 0 until vectors.length()) {
            val v = vectors.getJSONObject(i)
            val name = v.getString("name")
            val cmd = commandFrom(v.getJSONObject("command"))

            val gotV1 = ControlCodec.encode(cmd, Protocol.PROTOCOL_VERSION)
            assertNotNull("$name: v1 encoding", gotV1)
            assertJsonEquals(name, v.getJSONObject("v1"), gotV1!!)

            val gotV0 = ControlCodec.encode(cmd, Protocol.LEGACY_VERSION)
            if (v.isNull("v0")) {
                assertNull("$name: must have no legacy form", gotV0)
            } else {
                assertNotNull("$name: v0 encoding", gotV0)
                assertJsonEquals(name, v.getJSONObject("v0"), gotV0!!)
                // Round trip proves the dialects are equivalent, not merely similar.
                assertEquals("$name: v0 round trip", cmd, ControlCodec.decode(gotV0))
            }
            assertEquals("$name: v1 round trip", cmd, ControlCodec.decode(gotV1))
        }
    }

    @Test
    fun `the version is judged by value, not by spelling`() {
        // JSON has no integer type: 1 and 1.0 are the same number, and org.json hands
        // back an Int for one and a Double for the other. Keying off the Kotlin type
        // dropped frames from ordinary v1 peers whose encoder wrote a decimal point —
        // a rule JS could not implement anyway, since JSON.parse collapses both
        // spellings before the decoder sees them.
        val want = Control.PointerTap(0.5, 0.25)
        for (v in listOf(1, 1.0, 1.0f, 1L)) {
            val frame = JSONObject().put("v", v).put("t", "pointer.tap").put("x", 0.5).put("y", 0.25)
            assertEquals("version spelled as $v (${v.javaClass.simpleName})", want, ControlCodec.decode(frame))
        }

        // A value that is not integral, not in range, or not a number is still refused.
        for (v in listOf<Any>(1.5, 0, 2, -1, 0.5, "1", true, JSONObject.NULL)) {
            val frame = JSONObject().put("v", v).put("t", "pointer.tap").put("x", 0.5).put("y", 0.25)
            assertNull("version $v must be refused", ControlCodec.decode(frame))
        }
    }

    @Test
    fun `encoding refuses an unknown command`() {
        // Control.System and Control.Opaque carry their kind as a string, so an
        // unknown one is representable. It must not reach the wire.
        assertNull(ControlCodec.encode(Control.System("system.selfDestruct")))
        assertNull(ControlCodec.encode(Control.Opaque("not.a.command")))
        assertNotNull("a known command still encodes", ControlCodec.encode(Control.System("system.restart")))
    }

    // ---- helpers --------------------------------------------------------

    /** Rebuild a Control from the fixture's neutral description. */
    private fun commandFrom(o: JSONObject): Control = when (val kind = o.getString("kind")) {
        "pointer.tap" -> Control.PointerTap(o.getDouble("x"), o.getDouble("y"))
        "pointer.swipe" -> Control.PointerSwipe(
            o.getDouble("x1"), o.getDouble("y1"), o.getDouble("x2"), o.getDouble("y2"), o.getLong("ms"),
        )
        "pointer.wheel" -> Control.PointerWheel(
            o.getDouble("x"), o.getDouble("y"), o.getDouble("dx"), o.getDouble("dy"),
        )
        "nav.key" -> Control.NavKey(o.getString("key"))
        "keyboard.text" -> Control.KeyText(o.getString("text"))
        "host.callState" -> Control.HostCallState(o.getString("state"))
        "system.lock", "system.sleep", "system.hibernate", "system.restart", "system.shutdown" ->
            Control.System(kind)
        else -> error("fixture uses a command this test does not build: $kind")
    }

    private fun assertControlMatches(name: String, want: JSONObject, got: Control) {
        for (key in want.keys()) {
            if (key == "kind") continue
            val expected = want.opt(key)
            val actual: Any? = when (key) {
                "x" -> (got as? Control.PointerTap)?.x ?: (got as? Control.PointerMove)?.x
                    ?: (got as? Control.PointerDown)?.x ?: (got as? Control.PointerUp)?.x
                    ?: (got as? Control.PointerWheel)?.x
                "y" -> (got as? Control.PointerTap)?.y ?: (got as? Control.PointerMove)?.y
                    ?: (got as? Control.PointerDown)?.y ?: (got as? Control.PointerUp)?.y
                    ?: (got as? Control.PointerWheel)?.y
                "x1" -> (got as Control.PointerSwipe).x1
                "y1" -> (got as Control.PointerSwipe).y1
                "x2" -> (got as Control.PointerSwipe).x2
                "y2" -> (got as Control.PointerSwipe).y2
                "ms" -> (got as Control.PointerSwipe).ms
                "dx" -> (got as Control.PointerWheel).dx
                "dy" -> (got as Control.PointerWheel).dy
                "button" -> (got as? Control.PointerDown)?.button ?: (got as Control.PointerUp).button
                "key" -> (got as Control.NavKey).key
                "code" -> (got as? Control.KeyDown)?.code ?: (got as Control.KeyUp).code
                "mods" -> (got as? Control.KeyDown)?.mods ?: (got as Control.KeyUp).mods
                "text" -> (got as? Control.KeyText)?.text ?: (got as? Control.ClipboardSet)?.text
                    ?: (got as Control.ClipboardData).text
                "mime" -> (got as? Control.ClipboardSet)?.mime ?: (got as Control.ClipboardData).mime
                "id" -> (got as Control.DisplaySelect).id
                "state" -> (got as Control.HostCallState).state
                "platform" -> (got as Control.Hello).meta.platform
                "version" -> (got as Control.Hello).meta.version
                "capabilities" -> (got as Control.Hello).meta.capabilities
                else -> error("$name: fixture field '$key' has no accessor in this test")
            }
            when (expected) {
                is JSONArray -> assertEquals("$name: $key", expected.strings(), actual)
                is Number -> assertEquals("$name: $key", expected.toDouble(), (actual as Number).toDouble(), 1e-9)
                else -> assertEquals("$name: $key", expected, actual)
            }
        }
    }

    private fun assertJsonEquals(name: String, want: JSONObject, got: JSONObject) {
        assertEquals("$name: field set", want.keys().asSequence().toSortedSet(), got.keys().asSequence().toSortedSet())
        for (k in want.keys()) {
            val w = want.opt(k)
            val g = got.opt(k)
            if (w is Number && g is Number) {
                assertEquals("$name: $k", w.toDouble(), g.toDouble(), 1e-9)
            } else if (w is JSONArray && g is JSONArray) {
                assertEquals("$name: $k", w.strings(), g.strings())
            } else {
                assertEquals("$name: $k", w, g)
            }
        }
    }
}
