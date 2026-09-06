package com.remoteassist.unattended

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import com.remoteassist.protocol.Protocol
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

enum class Scope { VIEW, CONTROL, FILES, CLIPBOARD }

/**
 * A standing grant from THIS host to a specific paired controller identity,
 * created by the device owner and revocable at any time. [controllerId] is the
 * peer's public-key id — the same identity the trust store keys on.
 */
data class UnattendedGrant(
    val grantId: String,
    val controllerId: String,
    val controllerName: String,
    val createdAt: Long,
    val expiresAt: Long?,
    val scope: Set<Scope>,
    val requireUnlock: Boolean,
    val active: Boolean,
    val lastUsedAt: Long? = null,
) {
    /**
     * Delegates to [Protocol.grantUsable] rather than re-deriving the rule, so the
     * store and the cross-language contract cannot drift. `expiresAt = 0` is the
     * epoch and therefore expired, not "never expires".
     */
    fun isUsable(now: Long = System.currentTimeMillis()) = Protocol.grantUsable(active, expiresAt, now)

    fun downgradeToViewOnly() = copy(scope = setOf(Scope.VIEW))

    fun toJson() = JSONObject().apply {
        put("grantId", grantId); put("controllerId", controllerId); put("controllerName", controllerName)
        put("createdAt", createdAt); putOpt("expiresAt", expiresAt)
        put("scope", JSONArray(scope.map { it.name }))
        put("requireUnlock", requireUnlock); put("active", active); putOpt("lastUsedAt", lastUsedAt)
    }

    companion object {
        /**
         * Rebuild a grant from its persisted form.
         *
         * Unknown scope names are **dropped, not guessed at and not thrown on** —
         * the rule `protocol/fixtures/capabilities.json` states for legacy scopes.
         * `Scope.valueOf` threw here, so a single grant written by a newer build
         * took down [GrantStore.load] for every grant on the device, and an
         * unattended host would fail to arm with no way to recover but a reinstall.
         * Dropping the token fails closed: the grant survives with less power.
         */
        fun fromJson(o: JSONObject): UnattendedGrant {
            val scopes = mutableSetOf<Scope>()
            val arr = o.optJSONArray("scope")
            for (i in 0 until (arr?.length() ?: 0)) {
                val name = arr!!.opt(i) as? String ?: continue
                runCatching { Scope.valueOf(name) }.getOrNull()?.let { scopes.add(it) }
            }
            return UnattendedGrant(
                grantId = o.getString("grantId"),
                controllerId = o.getString("controllerId"),
                controllerName = o.getString("controllerName"),
                createdAt = o.getLong("createdAt"),
                expiresAt = if (o.isNull("expiresAt")) null else o.getLong("expiresAt"),
                scope = scopes,
                requireUnlock = o.getBoolean("requireUnlock"),
                active = o.getBoolean("active"),
                lastUsedAt = if (o.isNull("lastUsedAt")) null else o.getLong("lastUsedAt"),
            )
        }

        fun newId(): String = UUID.randomUUID().toString()
    }
}

class GrantStore private constructor(private val prefs: SharedPreferences) {
    private val cache = linkedMapOf<String, UnattendedGrant>()

    init { load() }

    /**
     * Read the persisted grants.
     *
     * A single unreadable entry is skipped rather than aborting the load. This runs
     * from `init`, so a throw here propagated out of [create] and left an
     * unattended host unable to start at all — the most fragile possible response
     * to one corrupt record. Skipping fails closed: an unreadable grant confers
     * nothing, and the rest still load.
     */
    private fun load() {
        cache.clear()
        val raw = prefs.getString(KEY, null) ?: return
        val arr = runCatching { JSONArray(raw) }.getOrNull() ?: return
        for (i in 0 until arr.length()) {
            val g = runCatching { UnattendedGrant.fromJson(arr.getJSONObject(i)) }.getOrNull() ?: continue
            cache[g.grantId] = g
        }
    }

    private fun persist() {
        val arr = JSONArray()
        cache.values.forEach { arr.put(it.toJson()) }
        prefs.edit().putString(KEY, arr.toString()).apply()
    }

    fun all(): List<UnattendedGrant> = cache.values.toList()
    fun activeCount(): Int = cache.values.count { it.isUsable() }

    fun save(g: UnattendedGrant) { cache[g.grantId] = g; persist() }

    fun findActive(controllerId: String): UnattendedGrant? =
        cache.values.firstOrNull { it.controllerId == controllerId && it.isUsable() }

    fun hasGrantFor(controllerId: String): Boolean =
        cache.values.any { it.controllerId == controllerId }

    fun markUsed(grantId: String) {
        cache[grantId]?.let { cache[grantId] = it.copy(lastUsedAt = System.currentTimeMillis()); persist() }
    }

    fun deactivate(grantId: String) {
        cache[grantId]?.let { cache[grantId] = it.copy(active = false); persist() }
    }

    fun revokeAllFor(controllerId: String) {
        cache.values.filter { it.controllerId == controllerId }
            .forEach { cache[it.grantId] = it.copy(active = false) }
        persist()
    }

    companion object {
        private const val KEY = "grants"

        fun create(context: Context): GrantStore {
            val master = MasterKey.Builder(context)
                .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                .build()
            val prefs = EncryptedSharedPreferences.create(
                context, "grant_store", master,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
            )
            return GrantStore(prefs)
        }
    }
}
