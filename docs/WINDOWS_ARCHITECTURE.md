# Techee for Windows — architecture

How a Windows PC becomes a Techee endpoint. Stack rationale is in
[ADR 0001](adr/0001-windows-technology-stack.md); the wire contract is in
[PROTOCOL.md](PROTOCOL.md).

## Status

**W2 complete.** A Windows endpoint has a cryptographic identity, registers with the
real broker, pairs, and persists trust and grants. W3 adds screen capture, VP8 encoding,
the frame pump and host session orchestration. W4 adds mouse and keyboard injection, so
a control frame now reaches the desktop. There is still **no service and no UI** — those
are W5 and W6.

| Assembly | State | Purpose |
|---|---|---|
| `Techee.Protocol` | ✅ W1 contract, W2 implementation, W4 rate limiter | Message schema, capabilities, permissions, control codec, rate ceilings |
| `Techee.Crypto` | ✅ | Transcripts, device ID, CNG identity, pairing |
| `Techee.Store` | ✅ | DPAPI-protected trust + grant persistence |
| `Techee.Signaling` | ✅ | WebSocket client, registration handshake |
| `Techee.WebRtc` | ✅ SDP auth, peer connection, codec inspection | SIPSorcery wrapper, SDP auth, authenticated peer recreation |
| `Techee.Windows.Host` | ✅ geometry, quality, pixel conversion, capture pump, input injection | Capture, encode, frame pump, `SendInput` (power is W7) |
| `Techee.Session` | ✅ W3 media, W4 input | Host session orchestration; the only assembly that sees both capture and transport |
| `Techee.Windows.Service` | ⛏ W6 | Machine lifecycle, boot start |
| `Techee.Windows.UI` | ⛏ W5 | Controller |

Nothing in `Techee.Protocol`, `Techee.Crypto` (except `CngDeviceIdentity`), or
`Techee.Store` (except `DpapiFileStore`) references a Windows type, so the protocol
and crypto suites run on any runner.

## Layering

```
  Techee.Windows.UI          Techee.Windows.Service
  (controller, per-user)     (host lifecycle, SYSTEM, boot)
          │                            │
          │                    secure local IPC   ← W6
          │                            │
          │                  Techee.Windows.Host
          │                  (capture · input · power, interactive session)
          └──────────────┬─────────────┘
                         │
     ┌───────────────────┼───────────────────┐
 Techee.WebRtc     Techee.Signaling     Techee.Store
     │                   │                   │
     └──────────┬────────┴─────────┬─────────┘
                │                  │
        Techee.Protocol      Techee.Crypto
        (no OS types)        (CNG isolated to one class)
```

## Identity

A non-exportable P-256 key in CNG. Signing happens inside the provider; no code in
this repository ever holds the private key.

**Provider preference:** Microsoft Platform Crypto Provider (TPM) → Microsoft Software
KSP. The result is reported in `KeyProtectionLevel` rather than being silently
equivalent — a security property nobody can observe is one nobody maintains.

### Key scope is a decision, not a fallback

Verified on real hardware (see [WINDOWS_SECURITY.md](WINDOWS_SECURITY.md)):
machine-scoped CNG keys **require elevation**; user-scoped keys work unelevated in
both the TPM and the software KSP, non-exportable in both.

| Scope | Used by | Why |
|---|---|---|
| `Machine` | Unattended **host** service | The service registers at boot, before any desktop session exists, and must keep one identity across user logoff. Created once by the elevated installer; opened by the service as SYSTEM. |
| `User` | **Controller-only** install | The identity belongs to that person. Unavailable before login — for a controller, correct. |

`OpenOrCreate` **never downgrades the scope**. A host asking for a machine key without
elevation gets an exception naming the cause. Silently creating a per-user key would
give the service a different identity than the installer registered and break every
pairing, with no visible reason.

The DPAPI store scope must match: a SYSTEM service with a machine identity needs a
`LocalMachine` store, or it starts, cannot read its own pairings, and appears to have
forgotten every device.

## Registration

Identical to Android's, against the unmodified broker:

```
register        { deviceId, publicKey, meta? }
  → register-challenge { challenge, expiresInMs, protocol }
  → sign  "techee-register-v1\n<deviceId>\n<challenge>"  in the TPM
register-proof  { deviceId, signature }
  → registered  { deviceId, iceServers, protocol }
```

The broker re-derives the device ID from the presented key and refuses if they
disagree, so nothing is registered on the client's say-so.

Two encoding traps, both now pinned by fixtures and by tests:

- **Signatures are DER**, not IEEE-P1363. .NET's default `SignData` overload emits
  P1363; Techee must pass `DSASignatureFormat.Rfc3279DerSequence`.
- **The device ID hashes the SPKI DER**, not the raw EC point.

`SignalingClient` treats the broker as hostile: every frame is parsed defensively and
a malformed one is dropped, never thrown. Android's client uses throwing JSON getters
on its socket thread, so one bad frame ends its connection — a Windows service must
not inherit that, because it may be the only way back into an office machine.

## Trust and grants

Two stores, deliberately separate, both DPAPI-protected under `%ProgramData%\Techee`
for a host or `%LocalAppData%\Techee` for a controller.

- **TrustStore** — who. Paired peers and their state.
- **GrantStore** — what. Standing unattended permissions.

`GrantStore.FindUsableFor` is the single gate between "a stranger dialled in" and "a
screen started being captured on an unattended machine". All four must hold:

1. the controller is in the trust store and is `Trusted` — not pending, not revoked;
2. a grant names that controller;
3. the grant is active and unexpired;
4. the grant confers at least `screen.view`.

Two differences from Android, both deliberate and both tested:

| | Android | Windows |
|---|---|---|
| Identity check before capture | Grant looked up by a **broker-supplied** controller ID; the peer's key is only proven later, at SDP verification — after capture has started | Trust checked **first**, before anything starts |
| Revoked peer's SDP | Still authenticates (`resolvePeerPub` ignores `TrustState`) | `PublicKeyForSdp` returns null for anything not `Trusted` |
| Re-pairing a trusted peer | `savePending` overwrites unconditionally, downgrading it | `Save` refuses; `Remove` first for a deliberate re-pair |
| `RequireUnlock` | Keyguard sampled once at session start | Re-evaluated per call |

## Pairing

Byte-identical to Android, including its weaknesses, because an endpoint that framed
things differently could not pair with any existing phone:

- controller proof over `nonce ‖ hostIdentitySpki ‖ controllerEphemeralSpki`
- host proof over `controllerEphemeralSpki ‖ controllerIdentitySpki`
- safety number: `sha256(shared ‖ min(pubA,pubB) ‖ max(pubA,pubB))`, first 6 bytes as
  zero-padded decimals

Both proofs are **raw concatenations with no domain separator and no length framing**.
That is unambiguous only because every field is fixed-length, so
`VerifyControllerProof` **enforces** those lengths rather than assuming them — an
over-long nonce would otherwise shift the field boundary.

One deliberate divergence: `Pairing.SafetyNumber` **throws on an empty shared secret**.
Android falls back to `ByteArray(0)`, producing a number that matches on both peers
while proving nothing about the key exchange — a fail-open the user cannot detect.
Windows makes that state unrepresentable.

The safety number is pinned in `protocol/fixtures/pairing.json` and asserted in Node,
Kotlin and C#. If the three disagreed, pairing would fail in front of a user with no
diagnosable cause.

## The video pipeline

Measured in W3 — full detail in [WINDOWS_VIDEO_PIPELINE.md](WINDOWS_VIDEO_PIPELINE.md).

The headline: **1080p at 30 FPS is not achievable with software VP8 on the reference
hardware (49.3 ms/frame); 720p at 30 FPS is (30.5 ms/frame), with about 8% headroom
rather than the 30% an earlier measurement suggested**. The default profile is 720p30,
automatic adaptation never climbs above it, and 1080p is offered at an honest 20 FPS as
an explicit operator choice.

Reconnection is **authenticated peer recreation**, not ICE restart: SIPSorcery 10.0.15
holds its ICE credentials in `readonly` fields, so `restartIce()` re-gathers candidates
against unchanged `ice-ufrag`/`ice-pwd` and produces a byte-identical offer. The full
evidence is in the pipeline document.

The constraint is libvpx's software encoder, not SIPSorcery's transport, so ADR 0001
stands. The documented fallback — a native encoder behind the `Techee.WebRtc`
interfaces — remains available.

Two findings shaped the pipeline: `EncodeVideoFaster` is unimplemented in
SIPSorceryMedia.Encoders 10.0.4, so BGRA→I420 must happen in managed code (hence
`PixelConvert` and its vectorised path); and DXGI reports the desktop rectangle in
logical pixels while Desktop Duplication delivers physical ones, so capture and
`SendInput` live in different coordinate systems (hence `DisplayGeometry`).

## Input injection

Added in W4. The transport already decoded control frames and raised them; what W4 adds
is everything between that event and the desktop.

```
RTCDataChannel "control"                    reliable, ordered, host-created
        │
   ControlCodec.DecodeRaw                   size gate before parse; null on anything
        │                                   malformed — never reaches the handler
   TecheePeerConnection.ControlReceived     ← SIPSorcery network thread
        │
   PeerControlHandler.Handle                1. is it an input command?
        │                                   2. rate limiter
        │                                   3. grant re-read + permission check
        │                                   4. enqueue
   ═════╪═════ queue, capacity 256 ═════
        │
   PeerControlHandler worker                one thread, so ordering is guaranteed
        │                                   5. secure-desktop check, per command
        │                                   6. coordinate / key translation
   IInputInjector                           logical virtual-desktop pixels
        │
   SendInputInjector → user32!SendInput     absolute, whole-virtual-desktop
```

There is no other route to `SendInputInjector` from network data. It is constructed in
one place — `WindowsHostSession.CreateControlHandler` — and handed straight to the
handler that owns it; nothing else holds a reference, and a host built without an
injector factory never subscribes to `ControlReceived` at all.

**Rate limiting runs before authorization**, which is worth stating because the reverse
looks more natural. The limiter is the denial-of-service guard, so nothing an
unauthorized peer does may route around it: authorizing first would leave a peer with no
grant able to burn an unbounded number of grant lookups per second simply by being
refused very quickly. The cost is that a refused flood is counted as rate-limited rather
than refused; both counters are reported.

**Malformed frames never reach the limiter**, because the codec drops them first. They
therefore consume no rate budget. The mitigation for a flood of them is the 65536-byte
size gate applied *before* the JSON parse, plus SCTP's own flow control.

**The split is the design.** `Handle` runs on SIPSorcery's network thread and does only
arithmetic and a queue push. Execution happens on a private worker because a
`pointer.swipe` is a gesture with a duration — up to ten seconds — and running it inline
would stall the transport for that long. The single worker also serialises input: a
button-up that overtook its button-down would leave the host's mouse stuck.

**Three decisions worth recording:**

- **Scan codes, not virtual keys.** The protocol carries W3C `KeyboardEvent.code`, which
  names a physical key *position*; a scan code means the same thing. Mapping to virtual
  keys instead would freeze the sender's keyboard layout onto the host, so `Ctrl`+`KeyZ`
  from a US controller would arrive as `Ctrl`+`W` on an AZERTY host — undo becoming
  close-window. `KeyMap` is a pure table and is unit-tested without a keyboard.
- **Absolute positioning, always.** Relative movement is subject to pointer acceleration,
  so a remote drag would not end where the operator released it and the error would
  accumulate. Every event carries `MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK`, and
  the 0..65535 conversion normalises against the union of all monitors rather than the
  primary — the bug that works perfectly until a second monitor appears.
- **A press is one `SendInput` batch.** Move and button-down in separate calls can be
  interleaved with real hardware input, and the click then lands wherever the local mouse
  moved in between.

`Techee.Windows.Host` still references no Techee project. `IInputInjector` speaks
pixels and buttons, not `Control` records; the translation lives in `Techee.Session`,
which is the only assembly that sees both sides.

### Nothing is left held

A remote session can end between a `keyboard.keyDown` and its `keyboard.keyUp` — the
network drops, the grant is revoked, the operator closes the window. Without care, the
person sitting at the machine is left with Ctrl logically held down by nothing, or the
left button stuck mid-drag, with no way to diagnose it.

`SendInputInjector` tracks exactly what it pressed, in two sets guarded by the same lock
as the injection that mutates them, and records a press only after the OS accepted it.
`ReleaseAllPressed` releases that set and nothing else — releasing every modifier
unconditionally would clobber a key the local user is physically holding, turning
cleanup into interference with someone else's typing.

It runs on every path out:

| Path | Where |
|---|---|
| Hangup, `EndAsync`, host shutdown | `DisposePeerAsync` → handler `Dispose` |
| Authenticated peer recreation | same, before the replacement peer is built |
| Authorization lost mid-session | `PeerControlHandler.RequestRelease`, queued as work |
| Injector disposal | `SendInputInjector.Dispose`, as a backstop |

The authorization case needs its own path because the matching key-up would itself be
refused — the release has to be initiated by the refusal rather than waited for. It is
coalesced, so a revoked controller that keeps sending queues one release rather than
filling the queue with them, and nothing is queued at all when nothing is held.

### Rate limiting

`ControlRateLimiter` implements the ceilings in [PROTOCOL.md](PROTOCOL.md) §5.4 —
`pointer.move` 250/s, other commands 100/s in aggregate, `system.*` one per five seconds.
A token bucket rather than a fixed window, so the burst that a real drag looks like is
absorbed while a sustained flood is clamped.

Exceeding a ceiling **drops the frame and never ends the session**. That is the specified
behaviour: a controller on a bad network must not be able to disconnect itself, and a
hostile one must not be able to wedge the input queue.

### What Windows does not do

`nav.key` — Android's `BACK`, `HOME` and `RECENTS` — is decoded, authorized, and then
counted as unsupported. Mapping it onto Windows shortcuts (`Alt`+`Left` for back, say)
would fire a real accelerator into whatever application has focus, which is worse than
doing nothing. This matters more than it sounds: the **shipped Android controller sends
only `tap`, `swipe` and `nav.key`**, so against a Windows host its three navigation
buttons do nothing. Keyboard input needs a v1 controller, which is W5.
