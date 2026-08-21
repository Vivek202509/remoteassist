# Techee for Windows — security

What is protected, how, and where the boundaries genuinely are. Claims here that could
be tested have been tested on real hardware; claims that could not are marked as such.

## Scope

Covers the W2 surface — identity, key storage, registration, trust, grants, and
persistence — plus the W4 input-injection surface in [its own section](#input-injection-w4).
The service/agent IPC boundary and power control are **not implemented** and are
therefore not covered; they arrive in W6 and W7, each with its own threat-model pass.

## Verified on hardware

Measured with a probe on the development machine (Windows 11, TPM present, .NET 10),
not inferred from documentation:

| Configuration | Result |
|---|---|
| Software KSP, machine scope, unelevated | ❌ `CryptographicException: Access denied` |
| Software KSP, user scope, unelevated | ✅ created, **non-exportable** |
| Platform Crypto Provider (TPM), machine scope, unelevated | ❌ `Access denied` |
| Platform Crypto Provider (TPM), user scope, unelevated | ✅ created, **non-exportable** |

Both providers refused `CngKey.Export(EccPrivateBlob)` in both scopes. P-256 SPKI DER
was 91 bytes; DER signatures 71–72 bytes.

Two conclusions drive the design:

1. **Machine-scoped keys require administrator rights.** The elevated installer creates
   the host identity; the service opens it as SYSTEM.
2. **The TPM is reachable without elevation**, so even a controller-only installation
   gets hardware-backed key storage.

## Identity

| Property | Windows | Android (for comparison) |
|---|---|---|
| Curve | P-256 | P-256 |
| Storage | CNG — TPM preferred, software KSP fallback | AndroidKeyStore |
| Exportable | No, verified | No |
| Hardware-backed | Yes when a TPM is present | **Not requested** — no StrongBox |
| Attested | No | No |
| Requires user auth to sign | No | No (`setUserAuthenticationRequired(false)`) |

Windows is currently the stronger of the two, because it explicitly prefers the TPM
whereas Android takes whatever the default provider gives it.

**Not yet done:** key attestation. Neither platform proves to the broker that the key
is hardware-resident, so the broker cannot distinguish a TPM-backed host from a
software one. Worth adding on both platforms together.

## What a compromised broker can and cannot do

The invariant the whole system defends: **a compromised broker must be able to disrupt
sessions but never to become a media or control endpoint.**

**Can:** deny service; see who talks to whom and when; see device IDs, public keys, the
pairing graph, and grant metadata; fabricate `peerMeta` (which is why it is flagged
`peerMetaTrusted: false`); set the advisory `unattended` flag; replay old SDP.

**Cannot:** read or inject media; read or inject control frames; forge an SDP that a
peer will accept, because each peer signs its own DTLS fingerprint with its identity
key; register as a device without that device's private key; make a Windows host
auto-accept an unattended session, because the host consults its own local trust and
grant stores and ignores the broker's flag.

## Data at rest

| Item | Protection |
|---|---|
| Identity private key | Non-exportable, in CNG/TPM. Never touches the filesystem |
| Pairing shared secrets | DPAPI-encrypted file |
| Grants | DPAPI-encrypted file |
| TURN credentials | Memory only, time-limited, never persisted |

DPAPI scope must match the identity scope. `LocalMachine` protects against **offline**
attack — a stolen disk or a copied file — not against an attacker who already has code
execution on the machine. That is the honest boundary, and the same one Windows offers
for any machine-scoped secret.

`ProtectedData.Protect` is called with fixed extra entropy. That is not a secret (it is
in the binary), but it does stop another application under the same account decrypting
Techee's store by passing the bytes to `Unprotect` with default parameters.

Writes are write-then-replace, so losing power mid-save cannot leave a truncated trust
store where the pairings used to be.

## Logging

Never logged: private keys, pairing shared secrets, TURN credentials, clipboard
contents, typed text, screen contents.

Two types carry secrets and both override `ToString`, because the compiler-generated
record `ToString` prints every property and an interpolated log line is the easiest way
to leak one:

- `IceServer` → credential redacted
- `PeerIdentity` → shared secret redacted, device ID truncated

Both have tests asserting the secret does not appear in the rendered string.

## Fail-closed behaviour

| Situation | Behaviour |
|---|---|
| Unreadable/corrupt grant store | No unattended access. Attended sessions still work, so an operator can recover remotely |
| Unreadable/corrupt trust store | No trusted peers; re-pairing required. The service still starts — a host that will not boot is worse than one that forgot its pairings |
| Peer not `Trusted` | No SDP key returned, no unattended access |
| Grant conferring no `screen.view` | No capture starts |
| Unknown permission token | Dropped. A permission this build cannot enforce must not be honoured |
| Malformed control frame | Dropped, never thrown |
| Grant conferring no `input.control` | Control frames decode, authorize false, and inject nothing |
| Grant revoked or expired mid-session | Input stops on the **next frame**, not at the next reconnect |
| Workstation locked with `RequireUnlock` | Grant downgrades to view-only; input stops mid-session |
| Secure desktop in front | Nothing injected; the operator is told why |
| Control ceiling exceeded | Frame dropped; the session survives |
| Injection throws | Counted, logged, worker survives |
| Unmapped key code | Dropped; never guessed at a virtual key |
| Malformed broker frame | Dropped, connection survives |
| Machine key without elevation | Loud exception, never a silent user-scoped key |
| Empty shared secret in a safety number | Throws — the fail-open Android has is unrepresentable here |

## Divergences from Android, and why

Each is a deliberate improvement, not an accidental incompatibility. None changes the
wire protocol.

| Android behaviour | Windows behaviour | Reason |
|---|---|---|
| `resolvePeerPub` ignores `TrustState`, so a **revoked peer's SDP still authenticates** | `PublicKeyForSdp` returns null unless `Trusted` | Revocation that does not revoke is worse than none: the operator believes they have acted |
| `savePending` overwrites unconditionally, downgrading a trusted peer | `Save` refuses to downgrade | Otherwise a replayed `pair-complete` is a denial of service against unattended access |
| Unattended grant looked up by broker-supplied ID **before** the peer's key is proven; capture starts first | Trust checked before anything starts | An office PC should not begin capturing its screen for an unauthenticated peer |
| Safety number falls back to an empty secret | Throws | It would match on both peers while proving nothing |
| Keyguard sampled once at session start | Lock state re-evaluated per call | A machine that locks mid-session should lose control rights |
| Throwing JSON getters on the socket thread | Defensive parsing throughout | One malformed frame must not disconnect an unattended host |

Where Android's behaviour is on the wire — the unframed pairing proofs, the 48-bit
safety number, the raw un-KDF'd ECDH secret — Windows reproduces it **exactly**,
weaknesses included, because diverging would make pairing impossible. Those are
recorded as protocol-level issues in [PROTOCOL.md](PROTOCOL.md) §2.3 and must be fixed
on both platforms together, as a versioned change.

## Input injection (W4)

The first code in Techee that acts on a remote instruction rather than merely showing
one. Three properties carry the weight.

### Authorization is per command, not per session

`PeerControlHandler` takes the grant as a **callback** and re-reads it through
`GrantStore.FindUsableFor` on every frame. That is what makes the promise in the
divergences table below true of input and not only of the join handshake:

| Event mid-session | Effect on input |
|---|---|
| Grant revoked | Next frame refused |
| Grant expires | Next frame refused |
| Peer's trust revoked | Next frame refused — `FindUsableFor` consults the trust store |
| Workstation locks with `RequireUnlock` | Downgraded to `screen.view`; next frame refused |

The alternative — capturing the grant at join — would mean a controller revoked during a
session kept full control until it reconnected, and a machine that locked kept accepting
keystrokes at the lock screen.

Refusals are **silent**. The sender is not told, because telling it would make the host
an oracle for exactly which permissions a stolen grant still carries.

### The secure desktop is respected, not defeated

UAC prompts, the logon screen and Ctrl+Alt+Del run on a separate desktop object that a
process on `Default` cannot post input to. That isolation is the only reason software
requesting elevation cannot approve its own elevation.

Techee does not attempt to work around it. `SecureDesktop.InputBlockedReason()` probes
the input desktop before every command and reports
`secure desktop active — remote input temporarily unavailable` verbatim to the operator.
Measured at roughly 5 µs per probe, so checking it 250 times a second is affordable —
which matters, because caching it would be wrong: prompts appear and vanish mid-session.

**An operator who is told this asks the person at the machine to click Yes. An operator
whose clicks vanish silently concludes the product is broken.** That is the whole
argument for spending a syscall per command.

The same applies to UIPI: `SendInput` returning fewer events than requested with
`ERROR_ACCESS_DENIED` means the foreground window runs at a higher integrity level. It
is reported, not elevated around.

### A session cannot leave input stuck

Tracked per injector, released on hangup, peer recreation, authorization loss, injector
disposal and host shutdown. Only what Techee pressed: the local user's physically-held
keys are never touched, so cleanup cannot become interference.

Verified against the OS rather than only against a fake — `RealApplicationInputTests`
holds left Shift, calls the teardown path, and asserts `GetAsyncKeyState` agrees the key
came back up.

### Rate limiting, now enforced

`ControlRateLimiter` implements the [PROTOCOL.md](PROTOCOL.md) §5.4 ceilings. Two
deliberate readings:

- **"Other commands 100/s" is an aggregate, not per kind.** A per-kind bucket would let a
  hostile peer send 100 taps, then 100 wheels, then 100 keystrokes in the same second —
  three times the ceiling by spelling the flood differently.
- **All `system.*` share one bucket.** Otherwise "one per five seconds" is really one of
  each per five seconds, and `system.shutdown` is reachable immediately after
  `system.restart`.

**Three buckets, fixed at compile time.** The command kind is attacker-influenced, so a
bucket-per-name dictionary would be an unbounded allocation reachable from the network —
and would also hand the attacker an unmetered budget for every new name it invents. Ten
thousand distinct invented kinds still share the one default bucket, and the test asserts
both the behaviour and the absence of any dictionary field.

**The limiter runs before authorization.** It is the denial-of-service guard, so an
unauthorized peer must not be able to route around it by being refused very quickly. See
[WINDOWS_ARCHITECTURE.md](WINDOWS_ARCHITECTURE.md#input-injection) for the full ordering
and for why malformed frames consume no budget.

A clock that runs backwards — an NTP correction, a DST change — refills nothing rather
than driving the bucket into a debt it can never climb out of, which would silently kill
input for the rest of the session.

### What input injection does not protect against

- **A trusted controller doing something destructive.** `input.control` is full control
  of the mouse and keyboard, which is what remote support is. The boundary is the grant,
  not the keystroke; there is no filtering of *what* may be typed and there should not be.
- **Local malware.** Anything already running as the user can call `SendInput` too.
  Techee's events are tagged in `dwExtraInfo`, which makes them attributable to a hook
  that looks, but nothing enforces the tag.
- **Input while the screen is not being watched.** Nothing ties injection to a live video
  frame. A grant with `input.control` but stalled capture can still drive the machine
  blind.

## Not addressed in W2

- **Service ↔ agent IPC.** W6. Must be ACL'd so a local unprivileged process cannot
  impersonate the service, with the agent verifying the server's identity too.
- **Audit log.** The event vocabulary is specified; no sink exists. Refused and
  rate-limited commands are counted and logged but not audited.
- **Code signing.** The build is signing-ready but nothing is signed. W9.
- **Broker-side gaps** — relay messages skipping pairing checks, no rate limiting,
  enumerable 6-digit codes, in-memory state lost on restart — are unchanged by W2 and
  tracked in [ARCHITECTURE.md](ARCHITECTURE.md) §7.
