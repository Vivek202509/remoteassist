package com.remoteassist.crypto

import android.util.Base64
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PublicKey
import java.security.SecureRandom
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.KeyAgreement

/**
 * Crypto primitives shared by identity, pairing, and per-session auth.
 *
 * We standardize on the NIST P-256 (secp256r1) curve for BOTH signing (ECDSA)
 * and key agreement (ECDH). P-256 is available since API 26, unlike X25519/XDH
 * which only arrived at API 33 — this keeps minSdk at 26 without extra libraries.
 */
object Crypto {
    private const val CURVE = "secp256r1"
    private const val SIG_ALG = "SHA256withECDSA"

    private val rng = SecureRandom()

    fun randomBytes(n: Int): ByteArray = ByteArray(n).also { rng.nextBytes(it) }

    fun sha256(vararg parts: ByteArray): ByteArray {
        val md = MessageDigest.getInstance("SHA-256")
        parts.forEach { md.update(it) }
        return md.digest()
    }

    /** Verify an ECDSA/P-256 signature made by [publicKeyDer] over [data]. */
    fun verify(publicKeyDer: ByteArray, data: ByteArray, sig: ByteArray): Boolean = try {
        val pub = decodePublicKey(publicKeyDer)
        Signature.getInstance(SIG_ALG).run {
            initVerify(pub); update(data); verify(sig)
        }
    } catch (e: Exception) {
        false
    }

    fun decodePublicKey(der: ByteArray): PublicKey =
        KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(der))

    // ---- ephemeral ECDH (used during pairing for a forward-secret shared secret) ----

    fun generateEphemeral(): KeyPair =
        KeyPairGenerator.getInstance("EC").apply {
            initialize(ECGenParameterSpec(CURVE))
        }.generateKeyPair()

    /** Derive the raw shared secret between our ephemeral private key and a peer's ephemeral public key. */
    fun agree(ephPrivate: java.security.PrivateKey, peerEphPublicDer: ByteArray): ByteArray {
        val ka = KeyAgreement.getInstance("ECDH")
        ka.init(ephPrivate)
        ka.doPhase(decodePublicKey(peerEphPublicDer), true)
        return ka.generateSecret()
    }

    // ---- encoders ----

    fun b64(bytes: ByteArray): String = Base64.encodeToString(bytes, Base64.NO_WRAP)
    fun unb64(s: String): ByteArray = Base64.decode(s, Base64.NO_WRAP)
    fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02x".format(it) }

    /**
     * Stable, human-verifiable identity id: SHA-256 of the DER public key,
     * grouped into 4-char hex blocks, e.g. "4F2A-9C81-...". This is what the UI
     * shows as a fingerprint and what the backend routes on.
     */
    fun publicKeyId(publicKeyDer: ByteArray): String = hex(sha256(publicKeyDer))

    fun shortFingerprint(publicKeyDer: ByteArray): String =
        publicKeyId(publicKeyDer).take(16).chunked(4).joinToString("-").uppercase()

    /**
     * Safety number: identical on both peers iff the ECDH exchange wasn't tampered
     * with. Order-independent in the two public keys. Users compare it out-of-band.
     */
    fun safetyNumber(shared: ByteArray, aPubDer: ByteArray, bPubDer: ByteArray): String {
        val ordered = listOf(aPubDer, bPubDer).sortedBy { hex(it) }
        val digest = sha256(shared, ordered[0], ordered[1])
        return digest.take(6).joinToString("-") { "%03d".format(it.toInt() and 0xFF) }
    }
}
