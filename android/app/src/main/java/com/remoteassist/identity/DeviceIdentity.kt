package com.remoteassist.identity

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import com.remoteassist.crypto.Crypto
import java.security.KeyStore
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec

/**
 * The device's long-lived cryptographic identity, held in the hardware-backed
 * Android Keystore. The public key IS the device's stable identity; the private
 * key never leaves the secure element and cannot be exported.
 */
object DeviceIdentity {
    private const val ALIAS = "remoteassist_device_identity"
    private const val SIG_ALG = "SHA256withECDSA"

    private fun keystore() = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    fun ensureCreated() {
        val ks = keystore()
        if (!ks.containsAlias(ALIAS)) {
            KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
                initialize(
                    KeyGenParameterSpec.Builder(
                        ALIAS,
                        KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY
                    )
                        .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                        .setDigests(KeyProperties.DIGEST_SHA256)
                        // Flip to true for a high-security mode requiring unlock to sign.
                        .setUserAuthenticationRequired(false)
                        .build()
                )
            }.generateKeyPair()
        }
    }

    /** DER-encoded public key bytes (X.509 SubjectPublicKeyInfo). */
    fun publicKeyDer(): ByteArray {
        ensureCreated()
        return keystore().getCertificate(ALIAS).publicKey.encoded
    }

    fun publicKeyB64(): String = Crypto.b64(publicKeyDer())

    /** Stable identity id used as deviceId in signaling and as the trust-store key. */
    fun deviceId(): String = Crypto.publicKeyId(publicKeyDer())

    fun shortFingerprint(): String = Crypto.shortFingerprint(publicKeyDer())

    fun sign(data: ByteArray): ByteArray {
        val entry = keystore().getEntry(ALIAS, null) as KeyStore.PrivateKeyEntry
        return Signature.getInstance(SIG_ALG).run {
            initSign(entry.privateKey); update(data); sign()
        }
    }
}
