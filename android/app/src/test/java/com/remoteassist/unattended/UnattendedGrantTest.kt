package com.remoteassist.unattended

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Persisted grants are attacker-influenced only indirectly, but they are read on
 * an unattended host at startup with nobody standing next to it. The rule these
 * tests pin is that a record this build cannot fully understand costs the device
 * *less power*, never *availability*: unreadable input must fail closed, not throw.
 */
class UnattendedGrantTest {

    private fun grantJson(scope: String) = JSONObject(
        """
        {"grantId":"g1","controllerId":"c1","controllerName":"Phone","createdAt":1,
         "expiresAt":null,"scope":$scope,"requireUnlock":false,"active":true}
        """.trimIndent()
    )

    @Test
    fun `an unknown scope name is dropped, not thrown on`() {
        // A grant written by a newer build. Scope.valueOf threw here, and because
        // the read happens in GrantStore's init that took down every grant on the
        // device — an unattended host that could not arm at all.
        val g = UnattendedGrant.fromJson(grantJson("""["VIEW","SHUTDOWN"]"""))
        assertEquals(setOf(Scope.VIEW), g.scope)
    }

    @Test
    fun `non-string and absent scope entries are dropped`() {
        assertEquals(setOf(Scope.CONTROL), UnattendedGrant.fromJson(grantJson("""["CONTROL",42,null]""")).scope)
        assertEquals(emptySet<Scope>(), UnattendedGrant.fromJson(grantJson("""[]""")).scope)
        assertEquals(emptySet<Scope>(), UnattendedGrant.fromJson(grantJson("""null""")).scope)
    }

    @Test
    fun `a dropped scope narrows the grant rather than widening it`() {
        // Fail-closed: the surviving grant must confer no more than the tokens
        // this build actually understood.
        val g = UnattendedGrant.fromJson(grantJson("""["VIEW","SHUTDOWN"]"""))
        assertFalse("an unrecognised scope must not become CONTROL", g.scope.contains(Scope.CONTROL))
        assertEquals(setOf(Scope.VIEW), g.scope)
    }

    @Test
    fun `usability treats the epoch as expired`() {
        val base = UnattendedGrant.fromJson(grantJson("""["VIEW"]"""))

        assertTrue("no expiry never expires", base.isUsable(Long.MAX_VALUE))
        assertFalse("expiresAt 0 is the epoch, not 'never expires'",
            base.copy(expiresAt = 0L).isUsable(now = 2000L))
        assertTrue("expiry exactly at now is still valid",
            base.copy(expiresAt = 2000L).isUsable(now = 2000L))
        assertFalse("a past expiry is expired",
            base.copy(expiresAt = 1999L).isUsable(now = 2000L))
        assertFalse("an inactive grant is unusable",
            base.copy(active = false).isUsable(now = 0L))
    }

    @Test
    fun `a malformed record is rejected rather than half-built`() {
        // GrantStore.load skips whatever this cannot produce; the point here is
        // that it surfaces as a failure to build one grant, not a partial grant.
        val missingId = JSONObject("""{"controllerId":"c1","active":true,"scope":["VIEW"]}""")
        assertNull(runCatching { UnattendedGrant.fromJson(missingId) }.getOrNull())
    }
}
