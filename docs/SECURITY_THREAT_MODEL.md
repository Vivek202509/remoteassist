# Techee — Security Threat Model

Scope of this document: Techee Remote Phone, in which a controller device
operates a remote handset holding a live SIM. Compromise means an attacker can
place calls on someone else's number, read their messages, and see and control
their screen. It is treated accordingly.

Status: **Milestone 1 Security Gate.** Two HIGH findings closed. This document
records the attacks, the mitigations, and the executable evidence for each. It
is not yet a complete threat model — Milestone 9 performs the full review.

---

## 1. Assets

| Asset | Why it matters |
|---|---|
| Device identity private key | Sole proof of who a device is. Non-exportable, Android Keystore, hardware-backed. |
| Cellular SIM / call origination | An attacker who reaches it makes calls billed to, and attributed to, the owner. |
| Screen contents | May include OTPs, messages, banking apps. |
| Remote input capability | Full control of the handset. |
| Pairing trust store | Defines which controller may drive which host. |
| TURN credentials | Relay capacity; abusable if long-lived. |

## 2. Trust boundaries

```
Vivo Controller ──┐
                  │  WSS signaling (broker is UNTRUSTED for content)
                  ├── Signaling broker + TURN  (public internet)
                  │
Galaxy A03 Host ──┘  DTLS-SRTP media, end-to-end
```

The broker is explicitly **not** trusted with session content. It routes
messages and can drop or reorder them; it must not be able to read or forge
media, and it must not be able to impersonate a device to another device. That
property is what the SDP transcript signature enforces.

---

## 3. HIGH-1 — Unauthenticated device registration / deviceId takeover

**Status: FIXED.**

### Attack

The broker accepted `register {deviceId}` on trust:

```js
ws.deviceId = m.deviceId;
S.devices.set(m.deviceId, ws);
```

A deviceId is `SHA-256(public key)` — a public value, printed in the UI as a
fingerprint and exchanged during pairing. It is an identifier, never a secret.

An attacker who could reach the socket could therefore:

1. register as the A03's deviceId;
2. displace the real handset in the routing table, silently;
3. receive its `join-request` messages, including unattended-grant traffic;
4. deny service to the real device indefinitely, because the last writer won.

No key material was required. This is a one-message attack, and it is the reason
the server was not exposed publicly.

### Mitigation

Registration is now a challenge-response proving possession of the identity
private key:

```
client → register        { deviceId, publicKey }
server                    verify deviceId == SHA-256(publicKey)   ← binds id to key
server → register-challenge { challenge }                          ← 32 random bytes
client → register-proof  { deviceId, signature }                   ← ECDSA P-256 / SHA-256
server                    verify signature over the transcript
server → registered      { deviceId, iceServers }
```

Signed transcript (`server/src/auth.js`, `signaling/RegistrationAuth.kt`):

```
techee-register-v1
<deviceId>
<challenge>
```

Controls, and what each one stops:

| Control | Stops |
|---|---|
| deviceId must equal SHA-256(supplied public key) | Claiming a victim's id while signing with your own key |
| Challenge is 32 bytes from a CSPRNG | Prediction |
| Challenge is bound to the connection | Using a challenge issued to someone else |
| Challenge is consumed before verification | Retrying a wrong signature against the same nonce |
| Challenge expires (default 30 s) | Long-lived proof harvesting |
| Transcript binds context + deviceId + nonce | Replay across identities, nonces, or Techee protocols |
| Private key never leaves the Keystore | Key exfiltration; only signatures cross the wire |
| Every non-registration message requires `ws.authenticated` | Gating registration but leaving the rest of the protocol open |

### Legitimate reconnect vs takeover

Both are "a second socket claiming a registered id". They are separated by
capability, not by policy: **only the holder of the private key can complete the
handshake.**

A socket that authenticates for an already-registered deviceId performs an
*authenticated replacement*: the previous socket is sent `session-replaced`,
then closed with code 4001, and the event is logged. This is required for real
operation — network switches, process death, FCM wake — and the displaced client
stops reconnecting rather than fighting for the slot. An attacker without the
private key never reaches this path. Socket close only retracts a routing entry
if it still points at that socket, so a displaced socket closing later cannot
delete its replacement.

### Evidence

`server/test/smoke.js`, cases 1–7 — real P-256 keys, real signatures:

| Case | Assertion |
|---|---|
| 1 | legitimate registration succeeds |
| 2 | signature from the wrong private key → `bad-signature` |
| 3 | challenge from another connection replayed → `bad-signature` |
| 4 | expired challenge → `challenge-expired` |
| 5 | reused challenge after a success → `no-challenge` |
| 6a | victim id + attacker key → `identity-mismatch` |
| 6b | victim id + victim's *public* key, attacker signature → `bad-signature` |
| 6c | any message before authenticating → `not-authenticated` |
| 6d | transcript bytes match the Android client's format |
| 7 | legitimate reconnect replaces the prior registration explicitly |

---

## 4. HIGH-2 — SDP accepted for an unknown or unverified peer

**Status: FIXED.**

### Attack

`RtcSession.verifyRemoteSdp` failed **open**:

```kotlin
val pub = peerPublicKeyProvider?.invoke(from) ?: return true   // unknown peer ⇒ accepted
```

and `onRemoteOffer` then adopted the sender's claimed identity:

```kotlin
peerId = from
```

So an attacker needed only to send an offer under an identity the victim had
never paired with. No key was known for it, verification was skipped as a
"convenience" for ad-hoc sessions, the session's peer was reassigned to the
attacker, and the answer — with the victim's DTLS fingerprint — went to them.
That is a session takeover and a media MITM against a *paired* session.

The signature also covered only the `a=fingerprint:` line, leaving the rest of
the SDP unauthenticated.

### Mitigation

Verification is fail-closed and bound to the session, in `webrtc/SdpAuth.kt`:

```
techee-sdp-v1
<offer|answer>
<sender deviceId>
<recipient deviceId>
<a=fingerprint: line>
<SHA-256 of the entire SDP>
```

| Rule | Verdict when violated |
|---|---|
| A session must already expect a peer | `NO_EXPECTED_PEER` |
| `from` must equal the session's expected peer | `PEER_MISMATCH` |
| A trusted key for that peer must exist | `UNKNOWN_PEER_KEY` |
| That key must hash to the expected deviceId | `KEY_IDENTITY_MISMATCH` |
| A signature must be present | `MISSING_SIGNATURE` |
| SDP must carry a DTLS fingerprint | `NO_FINGERPRINT` |
| Signature must verify over the transcript | `BAD_SIGNATURE` |

`peerId` is **never** assigned from an incoming message. The expected peer is
fixed when the session starts (`startAsHost` / `startAsController`), and the
outbound answer is addressed to that peer rather than to `from`.

Binding the offer/answer type prevents replaying an offer signature as an
answer. Binding the recipient prevents relaying an offer intended for one device
at another. Digesting the whole SDP makes any post-signature edit detectable,
not just fingerprint substitution.

### Deliberate design decision: rejection is not always teardown

An SDP failing with `PEER_MISMATCH` or `NO_EXPECTED_PEER` came from a third
party. It is **discarded without touching the session**. Tearing the link down
there would let any registered device kill any other device's session by sending
one junk offer — trading a takeover vulnerability for a denial-of-service one.

A failure from the *expected* peer (bad signature, missing key, no fingerprint)
does tear the link down: that peer cannot be authenticated, so the session must
not continue.

### Evidence

`android/app/src/test/java/com/remoteassist/webrtc/SdpAuthTest.kt`, 16 tests
against real P-256 keys:

| Case | Assertion |
|---|---|
| 8, 8b | valid paired offer and answer accepted |
| 9 | unknown peer key rejected — the regression that mattered |
| 9b | key not matching the expected identity rejected |
| 10 | signature from the wrong key rejected |
| 10b | missing signature rejected |
| 11 | SDP modified after signing rejected |
| 12 | offer claiming a different sender rejected |
| 12b | signature bound to a different recipient rejected |
| 12c | offer signature replayed as an answer rejected |
| 13 | fingerprint substitution rejected |
| 13b | SDP with no fingerprint unsignable and unverifiable |
| 14 | unsolicited third-party offer during an active session rejected |
| 14b | offer before any session established rejected |

---

## 5. Signing oracle removed (required by HIGH-1)

**Status: FIXED.** Found while implementing HIGH-1; fixing it was mandatory,
because leaving it would have defeated the registration fix entirely.

`ServiceLocator.onAuthChallenge` signed a peer's **raw bytes** with the identity
key:

```kotlin
val sig = DeviceIdentity.sign(Crypto.unb64(nonceB64))   // signs anything
```

Attack chain:

1. attacker begins registration as the victim's deviceId and receives a challenge;
2. attacker sends the victim an `auth-challenge` whose "nonce" is the
   registration transcript for that challenge;
3. victim blind-signs it and returns the signature;
4. attacker submits it as the victim's `register-proof`.

Identity takeover without ever holding the private key. Every element of HIGH-1
would have remained satisfied while being bypassed.

**Mitigation:** domain separation. Peer challenges now sign

```
techee-peer-auth-v1
<challenger deviceId>
<nonce>
```

so the bytes signed on this path can never form a valid transcript for another
Techee protocol. The challenger is bound in as well, so a response collected by
one peer cannot be forwarded by another.

**Evidence:** `RegistrationAuthTest` — `peer challenge can never produce a
registration proof`, `peer auth context is distinct from registration and SDP`,
`peer auth transcript binds the challenger`.

---

## 6. Preserved mechanisms

Unchanged and not weakened by this gate:

- hardware-backed P-256 device identity (`DeviceIdentity`);
- ECDH pairing with an out-of-band safety number (`PairingManager`, `Crypto`);
- DTLS-SRTP for all media (inherent to WebRTC), now with an authenticated
  fingerprint on both ends;
- ephemeral HMAC-SHA1 TURN credentials (`turn.js`);
- minimum-permission telephony posture — `READ_CALL_LOG` still not requested;
- AccessibilityService used only to dispatch input, never to read screen content.

---

## 7. Known open findings

Carried forward to Milestone 9 unless stated otherwise.

| # | Severity | Finding |
|---|---|---|
| M-1 | MEDIUM | No rate limiting or brute-force protection. The 6-digit join code is CSPRNG-generated and single-use, but guesses are unlimited. Registration attempts are likewise uncapped. |
| M-2 | MEDIUM | Signaling is plaintext `ws://` by default; production requires TLS termination and `wss://`. |
| M-3 | MEDIUM | Production secrets are placeholders (`TURN_SECRET=dev-only-change-me`; `infra/turnserver.conf` unfilled). |
| M-4 | MEDIUM | Broker state is in-process memory; multi-instance deployment needs shared storage, and a restart drops all registrations. |
| L-1 | LOW | `register-pairing` accepts caller-supplied peer identifiers; an authenticated device can add edges to the pairing graph. Session-level SDP authentication limits the impact. |
| L-2 | LOW | `consent` is relayed without checking the sender is party to that session. |
| L-3 | LOW | No audit log or remote revoke/kill mechanism yet (§25 requires both). |
| L-4 | LOW | `myPub` / `peerPub` field names actually carry deviceIds; client and server agree, but the naming invites future drift. |

### Not yet verified

The Android↔server registration handshake has **not** been exercised end to end
against a live device. Both halves are tested independently, and the transcript
format is pinned on both sides by a shared literal, but the first real-device
connection is the confirming test. It is a manual test, recorded in the
[real-device matrix](GALAXY_A03_REAL_DEVICE_TEST_MATRIX.md).
