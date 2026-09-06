# Techee architecture

Platform-neutral view of the system. Android specifics live in the source;
Windows specifics will live in `docs/WINDOWS_ARCHITECTURE.md` (W2+).

## 1. Shape

```
                        ┌──────────────────────┐
                        │  Signaling broker    │   Node + ws
                        │  (rendezvous only)   │   server/
                        └──────────┬───────────┘
                                   │  WSS: identity, pairing, SDP relay
                 ┌─────────────────┼─────────────────┐
                 │                                   │
        ┌────────┴────────┐                 ┌────────┴────────┐
        │ Techee Android  │                 │ Techee Windows  │
        │  host ∧ ctrl    │                 │  host ∧ ctrl    │   ← W2+
        └────────┬────────┘                 └────────┬────────┘
                 │                                   │
                 └───── WebRTC: DTLS-SRTP media ─────┘
                        + "control" DataChannel
                          (broker cannot read either)

                        ┌──────────────────────┐
                        │  coturn  (STUN/TURN) │   infra/
                        │  relays opaque bytes │
                        └──────────────────────┘
```

Every endpoint is **both roles**. Whichever side shares its screen is the host for
that session; A→B and B→A are two independent sessions.

## 2. What each component may know

| Component | Sees | Cannot see |
|---|---|---|
| Broker | device IDs, public keys, pairing graph, grant metadata, SDP | media, control frames, clipboard, screen |
| coturn | encrypted packets, IP endpoints | anything inside DTLS-SRTP |
| Host | its own screen, decoded control frames | the controller's screen |
| Controller | the host's screen | anything else on the host |

**The load-bearing invariant:** a compromised broker can disrupt sessions but
cannot become a media or control endpoint. It relays SDP it cannot forge, because
each peer signs its own DTLS fingerprint with its identity key. Everything in the
security design defends this one property.

## 3. Layering rule

```
   protocol / crypto        ← platform-neutral, no OS types, unit-testable anywhere
        ↑
   session / signaling      ← platform-neutral logic, thin platform seams
        ↑
   platform adapters        ← MediaProjection · AccessibilityService
                              WGC/DDA · SendInput · CNG · Windows Service
```

Nothing in the protocol layer may reference an OS type. That is what makes the
cross-language fixtures meaningful and the adversarial tests runnable on a plain
JVM or CLR — the SDP authenticator, the control codec, and the permission model
are all tested with real P-256 keys and no device.

Correspondence across platforms:

| Concern | Android | Windows (planned) |
|---|---|---|
| Identity key | `AndroidKeyStore` P-256 | CNG, TPM-preferred, non-exportable |
| Trust store | `EncryptedSharedPreferences` | DPAPI-protected per-machine store |
| Screen capture | `MediaProjection` | Windows Graphics Capture / Desktop Duplication |
| Input injection | `AccessibilityService` | `SendInput` |
| Unattended host | foreground service | Windows Service + interactive user agent |
| Wake | FCM data push | wake timers / Wake-on-LAN *(hardware permitting)* |
| Protocol | `com.remoteassist.protocol` | `Techee.Protocol` |

## 4. Session lifecycle

```
Host advertises  ──host-open──▶ broker ──▶ 6-digit code
   or: controller dials a paired host directly by device ID

Controller ──join──▶ broker ──join-request──▶ Host
                                                │
                    ┌───────────────────────────┴───────────────────┐
                    │                                               │
             attended: consent UI                  unattended: local grant check
                    │                                               │
                    └───────────────────────────┬───────────────────┘
                                                ▼
Host builds screen track ──offer + fpSig──▶ broker ──▶ Controller
Controller verifies signature against the paired key   ← fail-closed
Controller ──answer + fpSig──▶ Host, which verifies likewise
ICE via STUN/TURN ──▶ P2P or relayed
"control" DataChannel opens; both sides exchange `hello`
```

The unattended decision is **local**. The broker's `unattended` flag is advisory;
the host consults its own grant store, which is the only authority.

## 5. Reconnection

Recovery arms the moment the first connection succeeds:

- network-change callback → ICE restart on Wi-Fi↔cellular switch
- `DISCONNECTED` (2 s grace) / `FAILED` (immediate) → reconnect signaling if
  needed, refresh TURN credentials, `createOffer(iceRestart: true)` on the *same*
  PeerConnection so the video track and DataChannel survive — no black screen, no
  re-consent
- exponential backoff capped at 15 s, 6 attempts, then `CLOSED`

## 6. What W1 changed

W1 is protocol foundation only. No remote desktop, no Windows code.

- `protocol/fixtures/` — the cross-language contract
- `server/src/protocol.js`, `com.remoteassist.protocol` — two independent
  implementations of it
- endpoint metadata and capabilities, relayed as explicitly-untrusted hints
- permission-scoped grants with fail-closed legacy migration
- control protocol v1, decoding both dialects to one command set
- broker fix: pairing edges may only be created or removed by a device that is
  party to them

## 7. Known weaknesses

Recorded here so they are decisions rather than surprises. Detail in
`docs/THREAT_MODEL.md`.

| Area | Issue |
|---|---|
| Pairing proofs | no domain separation or length framing; host never verifies the nonce it issued or the QR TTL |
| Trust state | `resolvePeerPub` searches all peers regardless of `TrustState`, so a **revoked** peer's SDP still authenticates |
| Safety number | 48 bits, no domain separation; fails **open** to an empty secret if both lookups miss |
| SDP replay | transcript has no session ID or nonce |
| Broker | relay messages skip pairing checks; no rate limiting; 6-digit codes are enumerable |
| Broker state | in-memory only — pairings and grants are lost on restart, silently reverting unattended access to attended |
| ICE | `from` is discarded on `ice` and `restart`, so any registered device can inject candidates or force a renegotiation |
| Android | `InputRouter.controlEnabled` is process-global, defaults `true`, and is not reset at session end |
