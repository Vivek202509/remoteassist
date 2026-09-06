'use strict';

const crypto = require('crypto');

// Device registration authentication.
//
// Threat this closes: the broker used to take `register {deviceId}` at face
// value. Anyone who could reach the socket could claim the A03's deviceId,
// displace it in the routing table, and absorb its join-requests. Nothing about
// the deviceId was secret — it is a public hash — so this was a one-line attack.
//
// The fix is proof of possession of the device identity private key that the
// Android Keystore holds and cannot export. The deviceId is *derived* from the
// public key, so claiming an identity now requires the corresponding private
// key rather than merely knowing a string.

/** Must match Crypto.publicKeyId on Android: lowercase hex SHA-256 of the DER key. */
function deviceIdFor(publicKeyDer) {
  return crypto.createHash('sha256').update(publicKeyDer).digest('hex');
}

/**
 * The exact bytes a client must sign. Binding all three elements matters:
 *  - the context string stops a signature made for another Techee protocol
 *    (e.g. the peer-to-peer auth-challenge relay) being replayed as a login;
 *  - the deviceId stops a valid signature being presented for a different id;
 *  - the server-issued nonce stops replay of any previously observed proof.
 */
function registrationTranscript(deviceId, challengeB64) {
  return Buffer.from(['techee-register-v1', deviceId, challengeB64].join('\n'), 'utf8');
}

function newChallenge() {
  return crypto.randomBytes(32).toString('base64');
}

/** Decode an X.509/SPKI DER public key. Returns null for anything unusable. */
function parsePublicKey(publicKeyB64) {
  try {
    const der = Buffer.from(publicKeyB64, 'base64');
    if (der.length === 0) return null;
    return { der, key: crypto.createPublicKey({ key: der, format: 'der', type: 'spki' }) };
  } catch {
    return null;
  }
}

/**
 * Verify an ECDSA P-256 / SHA-256 signature produced by Android's
 * `Signature.getInstance("SHA256withECDSA")`, which emits DER — Node's default
 * dsaEncoding, so the two interoperate without re-encoding.
 */
function verifySignature(publicKey, transcript, signatureB64) {
  try {
    const sig = Buffer.from(signatureB64, 'base64');
    if (sig.length === 0) return false;
    return crypto.verify('sha256', transcript, publicKey, sig);
  } catch {
    return false;
  }
}

/**
 * Validate a `register` request. The deviceId is only accepted when it is the
 * hash of the key presented alongside it, which is what makes the later
 * signature check meaningful — otherwise a caller could pair a victim's
 * deviceId with their own key and sign for it happily.
 */
function beginRegistration(deviceId, publicKeyB64, ttlMs) {
  if (typeof deviceId !== 'string' || !/^[0-9a-f]{64}$/.test(deviceId)) {
    return { ok: false, reason: 'malformed-device-id' };
  }
  if (typeof publicKeyB64 !== 'string') {
    return { ok: false, reason: 'missing-public-key' };
  }
  const parsed = parsePublicKey(publicKeyB64);
  if (!parsed) return { ok: false, reason: 'malformed-public-key' };
  if (deviceIdFor(parsed.der) !== deviceId) {
    return { ok: false, reason: 'identity-mismatch' };
  }
  const challenge = newChallenge();
  return {
    ok: true,
    challenge,
    pending: {
      deviceId,
      publicKey: parsed.key,
      challenge,
      expiresAt: Date.now() + ttlMs,
    },
  };
}

/**
 * Check a `register-proof` against the pending challenge.
 *
 * The caller must consume (null out) the pending challenge before calling this,
 * so a challenge is single-use regardless of the outcome — a failed attempt
 * cannot be retried against the same nonce.
 */
function completeRegistration(pending, deviceId, signatureB64, now = Date.now()) {
  if (!pending) return { ok: false, reason: 'no-challenge' };
  if (now > pending.expiresAt) return { ok: false, reason: 'challenge-expired' };
  if (deviceId !== pending.deviceId) return { ok: false, reason: 'identity-mismatch' };
  if (typeof signatureB64 !== 'string') return { ok: false, reason: 'missing-signature' };

  const transcript = registrationTranscript(pending.deviceId, pending.challenge);
  if (!verifySignature(pending.publicKey, transcript, signatureB64)) {
    return { ok: false, reason: 'bad-signature' };
  }
  return { ok: true, deviceId: pending.deviceId };
}

module.exports = {
  deviceIdFor,
  registrationTranscript,
  newChallenge,
  parsePublicKey,
  verifySignature,
  beginRegistration,
  completeRegistration,
};
