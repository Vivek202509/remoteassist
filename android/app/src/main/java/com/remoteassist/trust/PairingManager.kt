package com.remoteassist.trust

import com.remoteassist.crypto.Crypto
import com.remoteassist.identity.DeviceIdentity
import com.remoteassist.signaling.SignalingClient
import org.json.JSONObject
import java.security.KeyPair

/**
 * Drives the authenticated pairing exchange (QR / numeric-code path).
 *
 * Flow:
 *  HOST  builds a PairingOffer (identity pub + ephemeral pub + nonce) -> QR.
 *  CTRL  scans, runs ECDH, signs a proof, sends `pair-complete`.
 *  HOST  verifies proof, derives same secret, signs its own proof, `pair-ack`.
 *  BOTH  compute an identical safety number and confirm it out-of-band, then
 *        promote the stored PeerIdentity to TRUSTED and register the pairing.
 */
class PairingManager(
    private val signaling: SignalingClient,
    private val trust: TrustStore,
) {
    data class PairingOffer(
        val hostPubB64: String,
        val ephPubB64: String,
        val nonceB64: String,
        val hostName: String,
        val relay: String,
        val ttlEpoch: Long,
    ) {
        fun toJson() = JSONObject().apply {
            put("hostPub", hostPubB64); put("ephPub", ephPubB64); put("nonce", nonceB64)
            put("hostName", hostName); put("relay", relay); put("ttl", ttlEpoch)
        }.toString()

        companion object {
            fun parse(s: String): PairingOffer {
                val o = JSONObject(s)
                return PairingOffer(
                    o.getString("hostPub"), o.getString("ephPub"), o.getString("nonce"),
                    o.getString("hostName"), o.getString("relay"), o.getLong("ttl"),
                )
            }
        }
    }

    // Transient per-pairing state.
    private var hostEph: KeyPair? = null
    private var controllerEphPub: ByteArray? = null
    private var pendingSecret: ByteArray? = null
    private var pendingPeerPub: ByteArray? = null

    // ---------- HOST: build the QR payload ----------
    fun buildOffer(hostName: String, relayUrl: String): PairingOffer {
        val eph = Crypto.generateEphemeral().also { hostEph = it }
        return PairingOffer(
            hostPubB64 = DeviceIdentity.publicKeyB64(),
            ephPubB64 = Crypto.b64(eph.public.encoded),
            nonceB64 = Crypto.b64(Crypto.randomBytes(32)),
            hostName = hostName,
            relay = relayUrl,
            ttlEpoch = System.currentTimeMillis() + 120_000,
        )
    }

    // ---------- CONTROLLER: complete after scanning ----------
    fun completeFromScan(offer: PairingOffer): Result<Unit> {
        if (offer.ttlEpoch < System.currentTimeMillis()) return Result.failure(IllegalStateException("QR expired"))

        val eph = Crypto.generateEphemeral()
        val hostEphPub = Crypto.unb64(offer.ephPubB64)
        val shared = Crypto.agree(eph.private, hostEphPub)

        // Prove we own our identity key over (nonce ‖ hostPub ‖ ourEphPub)
        val transcript = Crypto.unb64(offer.nonceB64) + Crypto.unb64(offer.hostPubB64) + eph.public.encoded
        val proof = DeviceIdentity.sign(transcript)

        controllerEphPub = eph.public.encoded
        pendingSecret = shared
        pendingPeerPub = Crypto.unb64(offer.hostPubB64)

        signaling.send(JSONObject().apply {
            put("type", "pair-complete")
            put("to", Crypto.publicKeyId(Crypto.unb64(offer.hostPubB64)))
            put("controllerPub", DeviceIdentity.publicKeyB64())
            put("controllerEph", Crypto.b64(eph.public.encoded))
            put("proof", Crypto.b64(proof))
            put("nonce", offer.nonceB64)
        })
        return Result.success(Unit)
    }

    // ---------- HOST: handle pair-complete ----------
    fun onPairComplete(m: JSONObject): Result<String> {
        val ctrlPub = Crypto.unb64(m.getString("controllerPub"))
        val ctrlEph = Crypto.unb64(m.getString("controllerEph"))
        val proof = Crypto.unb64(m.getString("proof"))

        val transcript = Crypto.unb64(m.getString("nonce")) + DeviceIdentity.publicKeyDer() + ctrlEph
        if (!Crypto.verify(ctrlPub, transcript, proof)) return Result.failure(SecurityException("bad controller proof"))

        val eph = hostEph ?: return Result.failure(IllegalStateException("no pending offer"))
        val shared = Crypto.agree(eph.private, ctrlEph)

        // Prove our identity back over (ctrlEph ‖ ctrlPub)
        val hostProof = DeviceIdentity.sign(ctrlEph + ctrlPub)
        signaling.send(JSONObject().apply {
            put("type", "pair-ack")
            put("to", Crypto.publicKeyId(ctrlPub))
            put("hostProof", Crypto.b64(hostProof))
        })

        val peerPubB64 = m.getString("controllerPub")
        trust.savePending(
            PeerIdentity(
                peerPubKeyB64 = peerPubB64,
                peerName = m.optString("controllerName", "Paired device"),
                sharedSecretB64 = Crypto.b64(shared),
                role = PeerRole.CONTROLLER,
                state = TrustState.PENDING_CONFIRM,
                pairedAt = System.currentTimeMillis(),
            )
        )
        pendingSecret = shared
        pendingPeerPub = ctrlPub
        return Result.success(safetyNumber(peerPubB64))
    }

    // ---------- CONTROLLER: handle pair-ack ----------
    fun onPairAck(m: JSONObject, hostName: String): Result<String> {
        val secret = pendingSecret ?: return Result.failure(IllegalStateException("no pending secret"))
        val peerPub = pendingPeerPub ?: return Result.failure(IllegalStateException("no pending peer"))
        val ctrlEph = controllerEphPub ?: return Result.failure(IllegalStateException("no pending ephemeral"))

        // Verify the host actually owns the scanned identity key: it signed
        // (ourEph ‖ ourIdentityPub) with that key. This is the responder-side proof;
        // the out-of-band safety number remains the final human check.
        val hostProof = Crypto.unb64(m.optString("hostProof", ""))
        val transcript = ctrlEph + DeviceIdentity.publicKeyDer()
        if (hostProof.isEmpty() || !Crypto.verify(peerPub, transcript, hostProof)) {
            return Result.failure(SecurityException("bad host proof"))
        }

        val peerPubB64 = Crypto.b64(peerPub)

        trust.savePending(
            PeerIdentity(
                peerPubKeyB64 = peerPubB64,
                peerName = hostName,
                sharedSecretB64 = Crypto.b64(secret),
                role = PeerRole.HOST,
                state = TrustState.PENDING_CONFIRM,
                pairedAt = System.currentTimeMillis(),
            )
        )
        return Result.success(safetyNumber(peerPubB64))
    }

    /** The peer public key (base64) currently being paired, if any. */
    fun pendingPeerPubB64(): String? = pendingPeerPub?.let { Crypto.b64(it) }

    /** Both sides derive this identically from the shared secret + both identity keys. */
    fun safetyNumber(peerPubB64: String): String {
        val secret = trust.sharedSecret(peerPubB64) ?: pendingSecret ?: ByteArray(0)
        return Crypto.safetyNumber(secret, DeviceIdentity.publicKeyDer(), Crypto.unb64(peerPubB64))
    }

    /** User confirmed the safety number matches: finalize trust + register with backend. */
    fun confirm(peerPubB64: String) {
        trust.promote(peerPubB64, TrustState.TRUSTED)
        signaling.send(JSONObject().apply {
            put("type", "register-pairing")
            put("myPub", DeviceIdentity.deviceId())
            put("peerPub", Crypto.publicKeyId(Crypto.unb64(peerPubB64)))
        })
    }
}
