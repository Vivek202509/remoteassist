package com.remoteassist.trust

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import com.remoteassist.crypto.Crypto
import org.json.JSONArray
import org.json.JSONObject

enum class TrustState { PENDING_CONFIRM, TRUSTED, REVOKED }
enum class PeerRole { CONTROLLER, HOST, BOTH }

/**
 * A paired peer. [peerPubKeyB64] (the DER public key) is the real identity and the
 * primary key; everything downstream (unattended grants, per-session auth) references it.
 */
data class PeerIdentity(
    val peerPubKeyB64: String,
    val peerName: String,
    val sharedSecretB64: String,
    val role: PeerRole,
    val state: TrustState,
    val pairedAt: Long,
    val lastSeenAt: Long? = null,
) {
    val peerId: String get() = Crypto.publicKeyId(Crypto.unb64(peerPubKeyB64))
    val shortFingerprint: String get() = Crypto.shortFingerprint(Crypto.unb64(peerPubKeyB64))

    fun toJson() = JSONObject().apply {
        put("pub", peerPubKeyB64); put("name", peerName); put("secret", sharedSecretB64)
        put("role", role.name); put("state", state.name)
        put("pairedAt", pairedAt); putOpt("lastSeenAt", lastSeenAt)
    }

    companion object {
        fun fromJson(o: JSONObject) = PeerIdentity(
            peerPubKeyB64 = o.getString("pub"),
            peerName = o.getString("name"),
            sharedSecretB64 = o.getString("secret"),
            role = PeerRole.valueOf(o.getString("role")),
            state = TrustState.valueOf(o.getString("state")),
            pairedAt = o.getLong("pairedAt"),
            lastSeenAt = if (o.isNull("lastSeenAt")) null else o.getLong("lastSeenAt"),
        )
    }
}

/**
 * Encrypted, on-device store of paired peers. Backed by EncryptedSharedPreferences
 * with a hardware-backed master key, so trust records can't be lifted off the device.
 */
class TrustStore private constructor(private val prefs: SharedPreferences) {

    private val cache = linkedMapOf<String, PeerIdentity>()

    init { load() }

    private fun load() {
        cache.clear()
        val raw = prefs.getString(KEY, null) ?: return
        val arr = JSONArray(raw)
        for (i in 0 until arr.length()) {
            val p = PeerIdentity.fromJson(arr.getJSONObject(i))
            cache[p.peerPubKeyB64] = p
        }
    }

    private fun persist() {
        val arr = JSONArray()
        cache.values.forEach { arr.put(it.toJson()) }
        prefs.edit().putString(KEY, arr.toString()).apply()
    }

    fun all(): List<PeerIdentity> = cache.values.toList()

    fun find(peerPubB64: String): PeerIdentity? = cache[peerPubB64]

    fun isTrusted(peerPubB64: String): Boolean =
        cache[peerPubB64]?.state == TrustState.TRUSTED

    fun sharedSecret(peerPubB64: String): ByteArray? =
        cache[peerPubB64]?.sharedSecretB64?.let { Crypto.unb64(it) }

    fun savePending(p: PeerIdentity) { cache[p.peerPubKeyB64] = p.copy(state = TrustState.PENDING_CONFIRM); persist() }

    fun promote(peerPubB64: String, state: TrustState) {
        cache[peerPubB64]?.let { cache[peerPubB64] = it.copy(state = state); persist() }
    }

    fun markSeen(peerPubB64: String) {
        cache[peerPubB64]?.let { cache[peerPubB64] = it.copy(lastSeenAt = System.currentTimeMillis()); persist() }
    }

    fun revoke(peerPubB64: String) { promote(peerPubB64, TrustState.REVOKED) }

    fun remove(peerPubB64: String) { cache.remove(peerPubB64); persist() }

    companion object {
        private const val KEY = "peers"

        fun create(context: Context): TrustStore {
            val master = MasterKey.Builder(context)
                .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                .build()
            val prefs = EncryptedSharedPreferences.create(
                context, "trust_store", master,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
            )
            return TrustStore(prefs)
        }
    }
}
