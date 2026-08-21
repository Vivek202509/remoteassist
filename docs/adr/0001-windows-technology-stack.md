# ADR 0001 — Windows technology stack

- **Status:** Accepted (W1)
- **Date:** 2026-08-15
- **Supersedes:** nothing
- **Affects:** `windows/` (to be created in W2), `docs/WINDOWS_ARCHITECTURE.md`

## Context

Techee is an Android remote-support product: a Node signaling broker, coturn, and
a Kotlin app that is simultaneously host and controller. We need Windows to
become a *peer endpoint* of that same system — not a second product that happens
to share a TURN server.

The constraint that dominates every other consideration is this: a Windows
endpoint must be able to register against the **existing, unmodified** broker,
using the **existing** identity derivation and the **existing** signed
transcripts, and must pair with an Android device through the **existing**
pairing ceremony. Anything that forces a fork of the crypto or the wire format
fails the brief regardless of its other merits.

Concretely, a Windows implementation must reproduce, byte for byte:

| Property | Requirement |
|---|---|
| Identity key | ECDSA P-256 (secp256r1) |
| Public key encoding | X.509 SubjectPublicKeyInfo DER, standard base64 |
| Device ID | lowercase hex SHA-256 over the SPKI DER |
| Signature encoding | ASN.1 DER `SEQUENCE {r, s}` — *not* IEEE-P1363 |
| Transcripts | UTF-8, LF-joined, domain-separated, no trailing newline |
| ECDH | P-256, raw shared secret (no KDF, matching Android) |
| Media | WebRTC with DTLS-SRTP, DTLS fingerprint bound to the identity key |

And it must additionally do things Android cannot: capture a desktop at 30 FPS,
inject `SendInput`, run as a service across Session 0 isolation, and manage
power/wake scheduling.

## Decision

**.NET 10 (C#) for everything under `windows/`, with SIPSorcery for WebRTC and
CNG/TPM for key storage.**

### Runtime: .NET 10, C#

Chosen over C++/WinRT, Rust, and Electron.

- **Windows API access without a foreign-function boundary.** Services,
  CNG, WASAPI, `SendInput`, power management, Windows Graphics Capture and
  Desktop Duplication are all reachable through supported interop
  (`CsWin32`/P-Invoke and WinRT projections). Rust would need a hand-maintained
  binding surface for the same APIs; C++/WinRT would cost us memory safety across
  a network-facing parser.
- **First-class crypto that already matches Techee's wire format.** Verified
  empirically before writing this ADR — see *Validation* below.
- **Windows Service hosting is a solved problem** (`Microsoft.Extensions.Hosting.WindowsServices`),
  which matters a great deal for Phase 9, where the service/agent split is the
  hard part.
- **Single-file, AOT-capable, signable** publishing for Phase 19.
- Electron is rejected outright: it cannot inject input, cannot run as a service,
  and would put a Chromium runtime inside a security product's TCB.

### WebRTC: SIPSorcery (`SIPSorcery` 10.0.15, pure managed)

Chosen over Google libwebrtc via `Microsoft.MixedReality.WebRTC`, and over
hand-rolling.

- `Microsoft.MixedReality.WebRTC` is **abandoned** (archived, last release 2021,
  no .NET 8/10 support). Taking a dependency on it would mean owning a native
  libwebrtc build ourselves.
- SIPSorcery is actively maintained, targets `net10.0`, and — decisively — gives
  us **direct access to the SDP and the DTLS certificate**. Techee's entire
  MITM defence is signing the local DTLS fingerprint and verifying the peer's
  (`SdpAuth`); an abstraction that hides the fingerprint would break the security
  model. SIPSorcery exposes it.
- It is pure managed code, so it does not undermine single-file publishing.

**Accepted risk:** SIPSorcery's media stack is less battle-hardened than
libwebrtc's, particularly for hardware-encoded video under packet loss. We
mitigate by keeping the media layer behind `Techee.WebRtc` interfaces so it can
be swapped without touching protocol, crypto, or capture — and by deferring the
judgement to W3, when we can measure it rather than guess. If SIPSorcery cannot
hold 30 FPS with adaptive bitrate over TURN, the fallback is a native libwebrtc
build behind the same interface. **This is the single largest technical risk in
the Windows programme and W3 is where it gets tested.**

### Key storage: CNG, TPM-backed when available

Mirrors Android's `AndroidKeyStore` posture as closely as Windows allows:

1. **Preferred** — CNG key in the Microsoft Platform Crypto Provider (TPM),
   non-exportable. This is the true analogue of a hardware-backed Keystore key.
2. **Fallback** — CNG key in the Microsoft Software KSP, non-exportable, machine
   scope, DPAPI-protected at rest.

The private key is never exported, never written to a file, and never logged. The
fallback is recorded in diagnostics so an operator can tell whether a given host
is TPM-backed. Notably this is *stronger* than the current Android default, which
does not request StrongBox and does not attest the key.

### Project layout

Platform-neutral protocol/crypto assemblies stay free of Windows types, so they
are testable on any runner and reusable by the controller UI:

```
windows/
  src/
    Techee.Protocol/     # message schema, capabilities, permissions — no Windows types
    Techee.Crypto/       # transcripts, device ID derivation, safety numbers
    Techee.Signaling/    # WebSocket client, registration handshake, reconnect
    Techee.WebRtc/       # SIPSorcery wrapper, SDP auth, authenticated peer recreation
    Techee.Windows.Host/ # capture, input, power  (Windows-only)
    Techee.Windows.Service/
    Techee.Windows.UI/   # WinUI 3 controller
  tests/
    Techee.Protocol.Tests/  Techee.Crypto.Tests/  Techee.Windows.Tests/
```

UI framework (WinUI 3 vs Avalonia) is **explicitly deferred to W5**. It does not
constrain W2–W4 and choosing it now would be a guess.

## Validation

This ADR is not a paper decision. Before accepting it we verified the load-bearing
claim — that .NET can produce a Techee identity the **unmodified** broker accepts —
by generating a P-256 identity in .NET 10, deriving the device ID, building the
`techee-register-v1` transcript, signing it, and verifying the result with the real
`server/src/auth.js`:

```
beginRegistration ok: true
completeRegistration ok: true
```

Two encoding traps were confirmed in the process and are now pinned by fixtures in
`protocol/fixtures/identity.json`:

1. .NET's `ECDsa.SignData(data, hash)` defaults to **IEEE-P1363** (raw `r‖s`),
   which Node rejects. Windows must pass
   `DSASignatureFormat.Rfc3279DerSequence` explicitly.
2. The device ID hashes the **SPKI DER**, not the raw EC point. `ExportSubjectPublicKeyInfo()`
   is correct; `ExportParameters().Q` is not.

Both are exactly the kind of silent mismatch that would otherwise surface as "the
Windows machine can't register" with no useful error.

## Consequences

**Positive.** One identity model, one pairing ceremony, one control vocabulary
across platforms. Protocol and crypto assemblies are unit-testable without a
Windows GUI runner. The broker needs no Windows-specific code paths.

**Negative.** A third language to keep in sync — mitigated by the shared fixtures
being the contract rather than any one implementation. SIPSorcery's media
performance is unproven for our workload. A .NET runtime dependency (mitigated by
self-contained publishing).

**Deferred.** UI framework (W5); whether hardware video encoding is reachable
through SIPSorcery or needs a native encoder (W3); pre-logon/secure-desktop
support, which Phase 18 explicitly scopes out and which must be designed
deliberately if it is ever wanted.
