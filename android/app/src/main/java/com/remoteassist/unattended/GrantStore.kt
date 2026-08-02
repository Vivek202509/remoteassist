package com.remoteassist.unattended

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
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
    fun isExpired(now: Long = System.currentTimeMillis()) = expiresAt != null && expiresAt < now
    fun isUsable(now: Long = System.currentTimeMillis()) = active && !isExpired(now)

    fun downgradeToViewOnly() = copy(scope = setOf(Scope.VIEW))

    fun toJson() = JSONObject().apply {
        put("grantId", grantId); put("controllerId", controllerId); put("controllerName", controllerName)
        put("createdAt", createdAt); putOpt("expiresAt", expiresAt)
        put("scope", JSONArray(scope.map { it.name }))
        put("requireUnlock", requireUnlock); put("active", active); putOpt("lastUsedAt", lastUsedAt)
    }

    companion object {
        fun fromJson(o: JSONObject): UnattendedGrant {
            val scopes = mutableSetOf<Scope>()
            val arr = o.getJSONArray("scope")
            for (i in 0 until arr.length()) scopes.add(Scope.valueOf(arr.getString(i)))
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

    private fun load() {
        cache.clear()
        val raw = prefs.getString(KEY, null) ?: return
        val arr = JSONArray(raw)
        for (i in 0 until arr.length()) {
            val g = UnattendedGrant.fromJson(arr.getJSONObject(i))
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
