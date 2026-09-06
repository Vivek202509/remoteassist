# Techee protocol

The wire contract between Techee endpoints and the signaling broker, and between
two endpoints over their peer connection.

This document is normative. Where it and an implementation disagree, the
implementation is wrong — but the *fixtures* outrank both. Every rule stated here
that can be expressed as data is expressed as data in `protocol/fixtures/`, and
three implementations are held to those files:

| Implementation | Location | Test |
|---|---|---|
| JavaScript (broker + reference) | `server/src/protocol.js` | `server/test/protocol.js` |
| Kotlin (Android) | `com.remoteassist.protocol` | `ProtocolFixtureTest` |
| C# (Windows) | `Techee.Protocol` — **W2, not yet written** | `Techee.Protocol.Tests` |

None is derived from the others. That is the point: a divergence shows up as a red
test in whichever language drifted, rather than as a device that mysteriously
cannot connect.

---

## 0. Status

| Area | State |
|---|---|
| Registration handshake + transcripts | Shipped, pinned by fixtures |
| SDP identity binding | Shipped (Android), pinned by fixtures |
| Endpoint metadata / capabilities | **New in W1** — implemented in broker + Android |
| Permission-scoped grants | **New in W1** — implemented in broker + Android |
| Control protocol v1 | **New in W1** — decoder/encoder implemented in broker-reference + Android |
| Windows endpoint | **Not implemented.** W2 onward |
| Clipboard, power, display, audio messages | **Specified and validated, not yet executed by any host** |

Specified-but-not-executed matters: a host that receives `system.restart` today
will validate it, check the permission, and then do nothing, because no host
implements the action yet. That is deliberate — the contract lands before the
capability so all three languages can agree on it first.

---

## 1. Layers

```
┌──────────────────────────────────────────────────────────┐
│  Control protocol  (§5)   DataChannel "control"          │  ← E2E, broker cannot read
├──────────────────────────────────────────────────────────┤
│  Media             WebRTC DTLS-SRTP                      │  ← E2E
├──────────────────────────────────────────────────────────┤
│  SDP authentication (§4)  identity-signed fingerprints   │  ← binds media to identity
├──────────────────────────────────────────────────────────┤
│  Signaling         (§3)   WebSocket to the broker        │  ← broker CAN read
├──────────────────────────────────────────────────────────┤
│  Identity          (§2)   P-256, challenge-response      │
└──────────────────────────────────────────────────────────┘
```

**The invariant that everything else defends:** a compromised broker must be able
to disrupt sessions but never to become a media or control endpoint. It relays
SDP it cannot forge, because each peer signs its own DTLS fingerprint with its
identity key (§4). Every design decision below that looks paranoid exists to keep
that true.

---

## 2. Identity

### 2.1 Key

ECDSA on **P-256** (secp256r1) — used for both signing and, via ephemeral keys,
ECDH during pairing. P-256 rather than Ed25519/X25519 because it is available
from Android API 26; raising the floor to API 33 for X25519 was not worth it.

Private keys are non-exportable and hardware-backed where the platform allows:

| Platform | Store |
|---|---|
| Android | `AndroidKeyStore`, alias `remoteassist_device_identity` |
| Windows | CNG — Platform Crypto Provider (TPM) preferred, Software KSP fallback |

### 2.2 Encodings

These are the encodings that cause silent interop failures, so they are stated
exactly and pinned in `protocol/fixtures/identity.json`.

| Item | Encoding |
|---|---|
| Public key | X.509 **SubjectPublicKeyInfo DER**, then standard base64 (padded, unwrapped) |
| Device ID | lowercase hex **SHA-256 over the SPKI DER**, exactly `/^[0-9a-f]{64}$/` |
| Signature | ECDSA-SHA256, **ASN.1 DER** `SEQUENCE {INTEGER r, INTEGER s}`, then base64 |

Traps, each of which produces a working-looking client that cannot register:

- The device ID hashes the **SPKI DER**, not the raw EC point and not PEM.
  .NET: `ExportSubjectPublicKeyInfo()`. Java: `PublicKey.getEncoded()`.
- The signature is **DER**, not IEEE-P1363 (`r‖s`). Java `SHA256withECDSA` and
  Node `crypto.sign` produce DER by default; **.NET does not** and must be told:
  `SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)`.
- Base64 is the **standard** alphabet with padding, not base64url.

### 2.3 Transcripts

Every signature Techee produces covers a transcript with this shape:

```
<domain separator> LF <field> LF <field> …
```

UTF-8, LF (`0x0A`) separators, **no trailing newline**, no length prefixes.

The first line is a domain separator, and it is load-bearing. The same identity
key signs registration proofs, peer challenges, and SDP; without distinct
contexts, a signature harvested under one protocol could be replayed as another.
That is not hypothetical — the peer-challenge relay was previously a blind
signing oracle that could be used to mint registration proofs, and domain
separation is what closed it.

| Context | Transcript | Used for |
|---|---|---|
| `techee-register-v1` | `context ⏎ deviceId ⏎ challengeB64` | broker registration |
| `techee-peer-auth-v1` | `context ⏎ challengerId ⏎ nonceB64` | peer identity challenge |
| `techee-sdp-v1` | `context ⏎ type ⏎ fromId ⏎ toId ⏎ fingerprintLine ⏎ hex(sha256(sdp))` | SDP binding |

> **Known weakness, unchanged in W1.** The pairing proofs in `PairingManager`
> are the one protocol here that uses raw concatenation with no domain separator
> and no length framing. They are not currently exploitable because the fields
> are fixed-length P-256 SPKI DERs, but they should be migrated to the shape
> above. Tracked in `docs/THREAT_MODEL.md`; deliberately out of W1 scope because
> changing them is a coordinated cross-platform break.

### 2.4 Registration handshake

```
client → broker   register        { deviceId, publicKey, meta? }
broker → client   register-challenge { challenge, expiresInMs, protocol }
client → broker   register-proof  { deviceId, signature }
broker → client   registered      { deviceId, iceServers, protocol }
                  register-failed { reason }
```

Rules the broker enforces:

- `deviceId` must be the SHA-256 of the presented `publicKey`, checked **before**
  the challenge is issued. Without this, a caller could pair a victim's ID with
  their own key and sign for it happily.
- The challenge is 32 CSPRNG bytes, base64, held **on the socket** (so it is
  structurally unusable on another connection), and **consumed before
  verification** — single-use whether the proof succeeds or fails.
- An unauthenticated socket may send only `register` / `register-proof`.
  Everything else gets `not-authenticated`.
- A successful registration for an already-registered ID **displaces** the old
  socket with an explicit `session-replaced`. Both sockets proved possession of
  the same non-exportable key, so the newcomer *is* the device — this is a
  reconnect, not a takeover.

`meta` (§6) is optional, is held aside until the proof succeeds, and is discarded
on failure.

---

## 3. Signaling messages

The broker is a relay and a rendezvous point. It does not terminate media, store
frames, or execute commands.

**Client → broker.** `register`, `register-proof`, `report-token`,
`turn-credentials`, `host-open`, `join`, `consent`, `offer`, `answer`, `ice`,
`restart`, `hangup`, `register-grant`, `revoke-grant`, `register-pairing`,
`revoke-pairing`, `pair-complete`, `pair-ack`, `auth-challenge`, `auth-response`.

**Broker → client.** `register-challenge`, `registered`, `register-failed`,
`session-replaced`, `not-authenticated`, `session-code`, `join-request`,
`join-pending`, `join-failed`, `grant-registered`, `grant-failed`,
`pairing-registered`, `pairing-failed`, `turn-credentials`, plus relayed forms of
the handshake messages with a broker-stamped `from`.

### 3.1 Compatibility rules

- **Unknown message types are ignored**, on both the broker and every client.
- **Unknown fields are ignored.** Adding a field is always safe; renaming or
  removing one is a breaking change.
- Relay messages are forwarded with `{...m, from: <sender's proven deviceId>}`.
  `from` is stamped by the broker and cannot be spoofed by the sender.

### 3.2 Authorization at the broker

The broker authenticates thoroughly and authorizes narrowly. Two rules are
enforced today:

- `join` by `hostId` requires an existing pairing edge (`arep aired`).
- `register-pairing` / `revoke-pairing` require `myPub === <the sender's own
  proven deviceId>`. **Added in W1.** Previously any authenticated device could
  insert or delete a pairing edge between two identities it had nothing to do
  with; because `join` honours those edges, a third party could unilaterally make
  A dialable by B, or tear down someone else's pairing. Both fields are named
  `*Pub` but actually carry device IDs, which is what made the omission easy to
  miss.

> **Still unenforced, documented honestly:** the ten relay message types
> (`offer`, `answer`, `ice`, `restart`, `hangup`, `pair-complete`, `pair-ack`,
> `auth-challenge`, `auth-response`, `consent`) are forwarded to any registered
> device without a pairing check. Endpoint-side defences (§4) are what actually
> stop this from mattering, but the broker should check too. Tracked in
> `docs/THREAT_MODEL.md`.

---

## 4. SDP authentication

Each peer signs its **own** SDP with its identity key; the other verifies against
the public key it holds from pairing. A broker that rewrites SDP invalidates the
signature.

The transcript binds, in order: protocol context, offer-vs-answer role, sender,
recipient, the DTLS fingerprint line, and a SHA-256 of the entire SDP. The
full-SDP digest is what makes tampering detectable beyond the fingerprint —
swapping a codec, an ICE ufrag, or the fingerprint itself all invalidate it.

Verification is **fail-closed**, and the reason is distinguishable:

| Verdict | Meaning |
|---|---|
| `OK` | accepted |
| `NO_EXPECTED_PEER` | no session is expecting anyone |
| `PEER_MISMATCH` | `from` is not the session's peer |
| `UNKNOWN_PEER_KEY` | no trusted key — unpaired or revoked |
| `KEY_IDENTITY_MISMATCH` | the key does not hash to the expected device ID |
| `MISSING_SIGNATURE` | no signature supplied |
| `NO_FINGERPRINT` | SDP has no DTLS fingerprint, so nothing to bind |
| `BAD_SIGNATURE` | signature did not verify |

Absence of evidence is a rejection, never a pass. A missing key is
`UNKNOWN_PEER_KEY`, not a skipped check.

`PEER_MISMATCH` and `NO_EXPECTED_PEER` discard the SDP **without touching the
session**; every other failure tears the link down. Tearing down on a third
party's SDP would trade a takeover bug for a denial-of-service one — anyone could
kill anyone's session by sending them an offer.

> **Known weakness:** the transcript contains no session ID, nonce, or
> timestamp, so the authentication layer offers no replay resistance of its own.
> A replayed SDP fails at DTLS/ICE rather than at signature check. Adding a
> session nonce is a candidate for `techee-sdp-v2`.

---

## 5. Control protocol

One reliable, ordered DataChannel labelled **`control`**, created by the host.
Payloads are UTF-8 JSON.

### 5.1 Two dialects

| Dialect | Shape | Who speaks it |
|---|---|---|
| **v0** (legacy) | `{"t":"tap","x":0.5,"y":0.5}` | every shipped Android build |
| **v1** | `{"v":1,"t":"pointer.tap","x":0.5,"y":0.5}` | Techee ≥ W1 |

v0 is a **supported dialect, not a deprecated one**. Both decode to the same
internal command, so nothing downstream of the codec knows which arrived.

The two vocabularies are **disjoint**: a v0 frame may only use v0 names, a v1
frame only v1 names. `{"t":"pointer.tap"}` and `{"v":1,"t":"tap"}` are both
rejected. This keeps a frame's dialect unambiguous rather than inferred.

Equivalences:

| v0 | v1 |
|---|---|
| `{"t":"tap","x","y"}` | `{"v":1,"t":"pointer.tap","x","y"}` |
| `{"t":"swipe","x1","y1","x2","y2","ms"}` | `{"v":1,"t":"pointer.swipe",…}` |
| `{"t":"key","k"}` | `{"v":1,"t":"nav.key","k"}` |
| `{"t":"text","s"}` | `{"v":1,"t":"keyboard.text","s"}` |
| `{"t":"callstate","state"}` | `{"v":1,"t":"host.callState","state"}` |

### 5.2 Negotiation and downgrade

A v1 endpoint announces itself with `hello` as soon as the channel opens. A peer
that never sends `hello` is a v0 peer, and the sender must render commands in the
v0 dialect. A command with no v0 form (`system.*`, `pointer.wheel`,
`keyboard.keyDown`, `clipboard.*`, `display.*`) is **not sent at all** — inventing
an encoding the old peer would misread is worse than silence.

> **This downgrade is presentation-layer only.** It changes how a command is
> spelled, never whether it is authenticated. SDP identity binding (§4) is not
> negotiated, has no legacy fallback, and cannot be downgraded by anything on
> this channel. A protocol-version downgrade must never become an authentication
> downgrade.

### 5.3 Message catalogue

Coordinates are **normalized doubles in `[0,1]`** in the *captured surface*
coordinate space — the shared display or the whole virtual desktop, **not** the
renderer's view. Out-of-range finite values clamp; non-finite values reject.

> **Known bug, unfixed in W1:** the shipped Android controller normalizes against
> the renderer view *including* aspect-fit letterbox bars, so a tap in a bar maps
> onto real pixels. Fixing this is a controller-side change scheduled with the
> W5 UI work.

| Type | Fields | Permission |
|---|---|---|
| `pointer.tap` | `x`, `y` | `input.control` |
| `pointer.move` | `x`, `y` | `input.control` |
| `pointer.down` / `pointer.up` | `x`, `y`, `b`? (`left`\|`right`\|`middle`\|`x1`\|`x2`, default `left`) | `input.control` |
| `pointer.wheel` | `x`, `y`, `dx`, `dy` (notches) | `input.control` |
| `pointer.swipe` | `x1`,`y1`,`x2`,`y2`, `ms`? (default 200, clamped 1–10000) | `input.control` |
| `nav.key` | `k` ∈ `BACK`\|`HOME`\|`RECENTS` | `input.control` |
| `keyboard.keyDown` / `keyboard.keyUp` | `code` (W3C `KeyboardEvent.code`), `mods`? | `input.control` |
| `keyboard.text` | `s` (≤ 4096 bytes UTF-8) | `input.control` |
| `clipboard.set` | `mime`? (`text/plain` only), `s` (≤ 64 KiB) | `clipboard.write` |
| `clipboard.request` | — | `clipboard.read` |
| `clipboard.data` | `mime`, `s` | — |
| `system.lock` / `sleep` / `hibernate` / `restart` / `shutdown` | — | matching `system.*` |
| `display.list` / `display.select` | `display.select` takes `id` | `screen.view` |
| `display.info` / `host.status` / `host.callState` | | — |
| `hello` / `hello.ack` | `platform`, `version`, `capabilities` | — |
| `error` | `code`, `ref`? | — |

**Key codes are W3C `KeyboardEvent.code` strings** (`KeyA`, `Enter`,
`ControlLeft`), not platform virtual-key numbers. They are well-specified,
layout-independent, and mean the same thing on both platforms.

### 5.4 Limits

| Limit | Value |
|---|---|
| Frame size | 65536 bytes (checked **before** JSON parse) |
| `keyboard.text` payload | 4096 bytes UTF-8 |
| Clipboard payload | 65536 bytes UTF-8 |
| Swipe duration | 1–10000 ms |
| Capabilities per endpoint | 32, each ≤ 40 chars |

Advisory per-session rate ceilings a host **should** enforce: `pointer.move` 250/s,
other commands 100/s, `system.*` 1 per 5s. Exceeding a ceiling drops frames — it
does not tear down the session. A controller on a bad network must not be able to
disconnect itself, and a hostile one must not be able to wedge the input queue.

> **Enforcement status.** The Windows host enforces these as of W4
> (`ControlRateLimiter`, applied per session in `PeerControlHandler`). Android does not
> yet, and the broker never will — it cannot read control frames. Two readings are
> settled there and should be matched by any other implementation: "other commands
> 100/s" is an **aggregate** budget rather than 100/s per kind, and all `system.*` share
> **one** bucket. Both are the stricter reading, chosen so that a hostile peer cannot
> multiply its allowance by rotating through command names.

### 5.5 Handling rules

These are requirements, not advice, and each has a fixture behind it:

1. **Never throw.** A control frame is attacker-influenced input arriving on a
   media callback. On an unattended host, an exception there kills the machine
   nobody is standing next to. Every rejection is a return.
2. **Unknown messages are ignored**, so a newer peer can send things this build
   has not learned about.
3. **Validate before executing**, and **authorize after validating** (§6.2).
4. **Reject, then drop, silently.** A rejection may be logged and audited as
   `PROTOCOL_REJECTION`; it is not reported to the sender by default.
5. **Never log payloads** — not clipboard text, not typed text.

---

## 6. Capabilities and permissions

> ### The rule this section exists for
> **Capabilities are descriptive. Permissions are authoritative.**
>
> A peer advertising `power.shutdown` has told you it owns a shutdown button. It
> has **not** told you it may press yours.
>
> The two use deliberately different vocabularies — `power.*` for capabilities,
> `system.*` for permissions — so one can never be silently accepted in place of
> the other. `grantPermits(grant, "power.shutdown")` returns false by
> construction, and there is a test asserting exactly that.

### 6.1 Endpoint metadata (descriptive)

Sent as `meta` on `register`, and as `hello` on the control channel:

```json
{ "platform": "windows", "version": "0.1.0",
  "capabilities": ["screen.share", "input.receive", "clipboard", "power.sleep"] }
```

Platforms: `android`, `windows`. Capabilities: `screen.share`, `screen.receive`,
`input.send`, `input.receive`, `audio.microphone.send`, `audio.desktop.send`,
`audio.receive`, `clipboard`, `display.multi`, `nav.android`, `power.lock`,
`power.sleep`, `power.hibernate`, `power.restart`, `power.shutdown`.

Parsing rules:

- **Absent metadata is valid** and means *unknown*. Every Android build shipped
  so far sends none. Hide capability-gated UI; never refuse the peer.
- **Unknown capability tokens are dropped, not rejected** — forward compatibility.
- An unknown `platform` rejects the whole metadata (→ *unknown*), because a
  platform we cannot reason about should not have half its claims believed.
- Capabilities are returned **sorted and de-duplicated**, so implementations that
  build the list in different orders still compare equal.

**Two trust levels.** Metadata relayed by the broker (`join-request.peerMeta`) is
flagged `peerMetaTrusted: false` — the broker could fabricate it. Metadata in a
`hello` frame arrives over the E2E-authenticated peer connection and is
trustworthy *as the peer's own claim*. Neither is authorization.

### 6.2 Grant permissions (authoritative)

```json
{ "grantId": "…", "controllerId": "<deviceId>", "active": true,
  "expiresAt": 1767225600000,
  "permissions": ["screen.view", "input.control", "system.sleep"] }
```

Permissions: `screen.view`, `input.control`, `clipboard.read`, `clipboard.write`,
`system.lock`, `system.sleep`, `system.hibernate`, `system.restart`,
`system.shutdown`, `files.transfer` *(reserved; no implementation acts on it)*.

**A screen-control grant does not imply power control.** `system.*` permissions
are individually granted. `system.restart` does not imply `system.shutdown`.

Evaluation, fail-closed at every step:

```
usable  = active === true
          && (expiresAt is absent/null  OR  expiresAt >= now)
permits(p) = usable && p ∈ normalize(grant) && p is a known permission
```

`expiresAt: 0` is the Unix epoch — maximally expired — and is treated as such.
*(A truthiness guard on this field reads 0 as "never expires" and fails open. The
broker's `findGrant` once did exactly that and gated unattended auto-join on the
result; it now calls `protocol.grantUsable`, and the fixture pins the behaviour in
all three languages.)*

### 6.3 Legacy grant migration

Grants created by the shipped Android app carry `scope`, not `permissions`:

| Legacy `Scope` | Permissions |
|---|---|
| `VIEW` | `screen.view` |
| `CONTROL` | `input.control`, `screen.view` |
| `CLIPBOARD` | `clipboard.read`, `clipboard.write` |
| `FILES` | `files.transfer` |

A grant with `permissions` uses them and ignores `scope`.

**Nothing maps to `system.*`.** Every grant created before power control existed
therefore confers no power over the machine, and an app update cannot silently
hand an old controller the ability to shut down a PC. This is asserted directly
in all three test suites rather than left as a consequence of the table.

---

## 7. Versioning policy

- `PROTOCOL_VERSION = 1`, `MIN_PROTOCOL_VERSION = 1`.
- Bumping the version means **adding** messages or fields. It never means
  changing what a v1 message already means.
- A frame with an unsupported `v` is **rejected, not guessed at**.
- `v` is judged **by value, not by spelling**. JSON has no integer type, so `1`,
  `1.0` and `1e0` are the same number and all name version 1. A receiver checks
  that the value is integral and in range; it must not inspect how the number was
  written. This is not a stylistic preference — a JS receiver *cannot* implement
  the stricter rule, because `JSON.parse` collapses every spelling to one value
  before the decoder runs, so a spelling-sensitive check is one that two of the
  three implementations would enforce and the third would silently ignore. A
  fractional version such as `1.5` is not a version we speak and is rejected.
- A peer that only speaks v1 must be able to keep speaking v1 to a newer peer
  indefinitely.
- **Version negotiation never affects authentication.** There is no
  "unauthenticated legacy mode", and there must never be one.

---

## 8. Changing this protocol

1. Change `protocol/fixtures/` first. That is the specification.
2. Watch all three test suites go red.
3. Make them green, one language at a time.
4. Update this document.
5. If the change is not backwards-compatible, it needs a version bump and a
   migration note in `docs/CROSS_PLATFORM_TEST_MATRIX.md`.
