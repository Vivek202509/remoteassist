# Cross-platform test matrix

Evidence log for Techee across Android and Windows.

**Rule, inherited from `GALAXY_A03_REAL_DEVICE_TEST_MATRIX.md`:** a compiling
build, a passing unit test, or an emulator run is never evidence for a row marked
*device required*. Those rows need two real machines and an observed result.

Status keys: **PASS** · **FAIL** · **NYT** (not yet tested) · **N/A** ·
**BLOCKED** (dependency not built)

---

## A. Automated — runs in CI today

| # | Check | Command | Count | Status |
|---|---|---|---|---|
| A1 | Protocol conformance, JS | `npm run protocol` | 229 | **PASS** |
| A2 | Broker end-to-end protocol | `npm run smoke` | 46 | **PASS** |
| A3 | Android unit tests | `:app:testDebugUnitTest` | 121 | **PASS** |
| A4 | Android debug build | `:app:assembleDebug` | — | **PASS** |
| A5 | Android lint | `:app:lintDebug` | — | **PASS** |
| A6 | Windows protocol conformance | `dotnet test` | 16 | **PASS** |
| A7 | Windows crypto + pairing | `dotnet test` | 43 (1 skipped) | **PASS** |
| A8 | Windows trust/grant stores | `dotnet test` | 22 | **PASS** |
| A9 | Windows ↔ real broker registration | `dotnet test` | 14 | **PASS** |
| A10 | Windows Release build | `dotnet build -c Release` | 0 warnings | **PASS** |
| A11 | Windows SDP authentication (adversarial) | `dotnet test` | 19 | **PASS** |
| A12 | Windows display geometry / DPI / multi-monitor | `dotnet test` | 15 | **PASS** |
| A13 | Windows adaptive quality | `dotnet test` | 15 | **PASS** |
| A14 | Windows BGRA→I420 (SIMD vs reference) | `dotnet test` | 20 | **PASS** |
| A15 | Windows real DXGI capture + real libvpx VP8 | `dotnet test` | — | **PASS** — against the live desktop |
| A16 | Windows frame pump → RTP sender | `dotnet test` | 9 | **PASS** |
| A17 | Windows host session vs the real broker (join · consent · offer · answer · ICE · hangup · restart) | `dotnet test` | 6 | **PASS** |
| A18 | Windows reconnection — fresh ICE credentials, re-authorisation, single-flight | `dotnet test` | — | **PASS** |
| A19 | Windows capture lifecycle — one pump, deterministic disposal, wedged-pump safety | `dotnet test` | 10 | **PASS** |
| A20 | Windows back-pressure — bounded latest-wins slot, stress run | `dotnet test` | 7 | **PASS** |
| A21 | Windows SDP/codec negotiation inspection | `dotnet test` | 13 | **PASS** |
| A22 | Windows video RTP timestamp monotonicity | `dotnet test` | 12 | **PASS** |
| A23 | **Windows loopback media: real DTLS-SRTP, real VP8, confirmed at the receiver** | `dotnet test` | 1 | **PASS** — 41,380 bytes sent; 26 RTP packets / 40,834 payload bytes received by the far peer. **Two in-process SIPSorcery peers — not a network, and not an Android decoder** |
| A24 | Windows input: per-command authorization, coordinate mapping, gestures, modifiers, secure desktop, fail-safety | `dotnet test` | 26 | **PASS** |
| A25 | Windows W3C `KeyboardEvent.code` → scan-code table, incl. the extended-key flag | `dotnet test` | 14 | **PASS** |
| A26 | Control rate ceilings — buckets, refill, aggregate budget, backwards clock | `dotnet test` | 14 | **PASS** |
| A27 | **Windows real `SendInput` on hardware** | `dotnet test` | 3 | **PASS** — `SendInput` accepted the events (proving the `INPUT` layout and `cbSize`), an absolute move landed exactly on its target, and the secure-desktop probe costs ~5 µs. **Three further cases skip**: two are intrusive and need `TECHEE_REAL_INPUT=1`, one needs a second monitor |
| A28 | **Input authorization over the real broker and a real data channel** | `dotnet test` | 9 | **PASS** — view-only refuses all seven reachable commands while video keeps running; the same commands execute under `input.control`; revocation and trust-revocation take effect on the next frame with no reconnect; restoration re-enables without one; a `--no-input` host never subscribes; nine raw malformed frames reach nothing; a hangup mid-press releases the button |
| A29 | Input handler lifecycle — attach, detach, recovery, no duplicate execution | `dotnet test` | 13 | **PASS** — one handler and one injector per peer, retired handlers inert, three consecutive recoveries leave exactly one live handler and one injection per frame |
| A30 | Stuck-key and stuck-button protection | `dotnet test` | 10 | **PASS** — modifiers and buttons released on teardown, on recovery and on authorization loss; releases coalesced; nothing released when nothing was pressed |
| A31 | Interop invariants — native `INPUT` size, wheel-delta overflow | `dotnet test` | 11 | **PASS** — includes a regression witness for the pre-audit arithmetic, which saturated a hostile `dx` of `1e300` to `int.MaxValue` (~17.9 M notches) |
| A32 | **Real Windows application reacts to injected input** | `dotnet test` | 8 | **PASS**, opt-in — see A32 note below |
| A33 | Controller coordinate mapping — letterbox, pillarbox, drag clamping, DPI independence | `dotnet test` | 21 | **PASS** |
| A34 | IVF recording container — header layout, framing, RTP clock wrap | `dotnet test` | 11 | **PASS** |

Windows solution total: **498 passed, 0 failed, 13 skipped** on .NET SDK 10.0.400
(11 of the skips are the opt-in and second-monitor real-hardware cases). Per-assembly:
Protocol 34 · Store 22 · Crypto 42 (1 skipped) · Signaling 14 · WebRtc 59 (1 skipped) ·
Windows.Host 136 (11 skipped) · Session 191.

A33 is the one worth reading. A coordinate mapping that is wrong by a few percent still
produces a session that connects, streams and reports every counter as healthy — it just
puts the operator's clicks somewhere they did not aim. It covers the `PROTOCOL.md` §5.3
letterbox bug from both directions, and the reject-on-press / clamp-on-drag distinction
that stops a drag into the padding from stranding a held button on the host.

**A32 runs only with `TECHEE_REAL_INPUT=1`** and was executed manually on the reference
machine. It drives a purpose-built top-level window — an ordinary `HWND` with a real
`comctl32` `EDIT` child — and asserts on the `WM_*` messages Windows actually delivered:

| Evidence | Observed |
|---|---|
| Click reaches a real window | `WM_MOUSEMOVE`, `WM_LBUTTONDOWN`, `WM_LBUTTONUP` at client `(291,150)`, down and up identical |
| Right-click is distinct | `WM_RBUTTONDOWN` with no `WM_LBUTTONDOWN` |
| Coordinate mapping is monotonic | `(116,60)` → `(291,150)` → `(465,240)` across three aimed regions, ordered on both axes |
| Drag is a real drag | press at index 1, release at 12, **10 intermediate `WM_MOUSEMOVE`** |
| Unicode typing | `techee w4 héllo` read back from the `EDIT` control via `WM_GETTEXT` |
| Key down/up distinct | `WM_KEYDOWN` and `WM_KEYUP`, both `vk=0x7C` (VK_F13) |
| Extended-key flag | `ArrowUp` arrives as `0x26` (VK_UP), **not** `0x68` (VK_NUMPAD8) |
| Stuck-key release | left Shift held, released by teardown, `GetAsyncKeyState` agrees |

A3 breakdown: `SdpAuthTest` 16 · `ProtocolFixtureTest` 14 · `InputRouterParseTest` 14 ·
`AudioProbeClassifierTest` 13 · `PairingFixtureTest` 12 · `SignalMathTest` 11 ·
`UplinkInjectionPathTest` 10 · `RegistrationAuthTest` 9 · `InputRouterInteropTest` 8 ·
`IceInspectTest` 8 · `SessionCodesTest` 6.

A27's intrusive cases are gated behind `TECHEE_REAL_INPUT=1` rather than skipped for a
missing capability: they move the real cursor and press a real key. A suite that types
into whatever window has focus is a suite nobody runs twice, so the default run proves
the interop without disturbing the machine — it moves the cursor to the position it is
already at. Both have been run manually on the reference machine and pass.

A7's one skipped test is `Machine_scope_works_when_elevated`, which needs an elevated
runner. Its unelevated counterpart — asserting the failure is a loud refusal rather
than a silent downgrade to a user key — **does** run and passes.

The second skipped test is `An_in_place_ice_restart_issues_fresh_ice_credentials`. It is
skipped because SIPSorcery 10.0.15 **cannot** satisfy it: `RtpIceChannel` holds its ICE
credentials in `readonly` fields, so `restartIce()` produces a byte-identical offer. Its
assertions are retained unweakened as an upgrade probe — un-skip it against a newer
SIPSorcery, and if it passes, true in-place ICE restart has become possible. The property
it guards is enforced unconditionally by the peer-recreation tests in A18.

**A9 uses the real broker, not a mock.** The test fixture spawns
`server/src/server.js` as a child process on a free port. A mock would be written from
the same understanding as the client, so the two would agree on a misreading of the
protocol and pass while a real device could not connect.

## B. Cross-language contract

Each row is enforced by the same fixture in two or more languages.

| # | Contract | Fixture | JS | Kotlin | C# |
|---|---|---|---|---|---|
| B1 | Device ID from SPKI DER | `identity.json` | **PASS** | **PASS**¹ | **PASS** |
| B2 | `techee-register-v1` transcript | `identity.json` | **PASS** | **PASS** | **PASS** |
| B3 | `techee-peer-auth-v1` transcript | `identity.json` | **PASS** | **PASS**¹ | **PASS** |
| B4 | `techee-sdp-v1` transcript, incl. CRLF | `identity.json` | **PASS** | **PASS**¹ | **PASS** |
| B5 | DER signature verifies cross-language | `identity.json` | **PASS** | **PASS**¹ | **PASS** |
| B6 | Endpoint metadata parsing | `capabilities.json` | **PASS** | **PASS** | **PASS** |
| B7 | Legacy `Scope` → permissions | `capabilities.json` | **PASS** | **PASS** | **PASS** |
| B8 | Grant evaluation, fail-closed | `capabilities.json` | **PASS** | **PASS** | **PASS** |
| B9 | Control decode, both dialects | `control-v1.json` | **PASS** | **PASS** | **PASS** |
| B10 | Control reject vectors | `control-v1.json` | **PASS** | **PASS** | **PASS** |
| B11 | Dialect downgrade + round trip | `control-v1.json` | **PASS** | **PASS** | **PASS** |
| B12 | Raw ECDH secret (unhashed X) | `pairing.json` | **PASS**² | **PASS** | **PASS** |
| B13 | **Safety number** | `pairing.json` | **PASS**² | **PASS** | **PASS** |
| B14 | Controller proof transcript | `pairing.json` | **PASS**² | **PASS** | **PASS** |
| B15 | Host proof transcript | `pairing.json` | **PASS**² | **PASS** | **PASS** |
| B16 | P-256 SPKI DER is 91 bytes | `pairing.json` | **PASS**² | **PASS** | **PASS** |

¹ Covered by the pre-existing `RegistrationAuthTest` / `SdpAuthTest`, which pin the
same literals with real P-256 keys rather than reading the fixture file. Wiring them to
the fixtures directly is a small tidy-up, not a coverage gap.

² Produced by Node when the fixture was generated; Kotlin and C# both verify it.

**B13 is the one that would fail visibly in front of a user.** The safety number is
read aloud between two people, so if the platforms computed it differently, pairing
would be impossible with no diagnosable cause. All three agree on
`088-180-108-029-158-208` for the pinned vector.

## C. Security properties

| # | Property | Where | Status |
|---|---|---|---|
| C1 | Wrong private key rejected | smoke 2 | **PASS** |
| C2 | Replayed challenge rejected | smoke 3 | **PASS** |
| C3 | Expired challenge rejected | smoke 4 | **PASS** |
| C4 | Challenge is single-use | smoke 5 | **PASS** |
| C5 | Device-ID takeover rejected, both variants | smoke 6a/6b | **PASS** |
| C6 | Unauthenticated socket refused | smoke 6c | **PASS** |
| C7 | Third party cannot forge a pairing edge | smoke | **PASS** |
| C8 | Third party cannot revoke a pairing edge | smoke | **PASS** |
| C9 | Capability advertisement authorizes nothing | protocol + Kotlin | **PASS** |
| C10 | Control grant does not imply power | protocol + Kotlin + smoke | **PASS** |
| C11 | Legacy grant confers no `system.*` | protocol + Kotlin + smoke | **PASS** |
| C12 | Malformed frames never throw | protocol + Kotlin | **PASS** |
| C13 | Unsupported protocol version rejected | protocol + Kotlin | **PASS** |
| C14 | Dialects stay disjoint | protocol + Kotlin | **PASS** |
| C15 | SDP verification fails closed, 8 verdicts | `SdpAuthTest` | **PASS** |
| C16 | Revoked peer cannot authenticate SDP | — | **PARTIAL** — **PASS** on Windows (`TrustStore.PublicKeyForSdp`); still **FAIL** on Android, see `ARCHITECTURE.md` §7 |
| C17 | Relay messages require pairing | — | **NYT** — not enforced |
| C18 | Rate limiting on control frames | `ControlRateLimiterTests` | **PARTIAL** — **PASS** on Windows (W4: `ControlRateLimiter`, enforced per session in `PeerControlHandler`); still unenforced on Android and in the broker |
| C19 | Wrong private key refused by the real broker | `RegistrationTests` | **PASS** |
| C20 | Claimed ID not matching the key refused | `RegistrationTests` | **PASS** |
| C21 | Unpaired controller cannot dial a Windows host | `RegistrationTests` | **PASS** |
| C22 | Third party cannot forge a pairing into a Windows host | `RegistrationTests` | **PASS** |
| C23 | Identity private key is non-exportable | `CngDeviceIdentityTests` | **PASS** (verified against CNG) |
| C24 | Machine key without elevation refuses, never downgrades | `CngDeviceIdentityTests` | **PASS** |
| C25 | Grant without trust authorises nothing | `StoreTests` | **PASS** |
| C26 | Re-pairing cannot downgrade a trusted peer | `StoreTests` | **PASS** |
| C27 | Secrets absent from rendered log strings | `StoreTests`, `RegistrationTests` | **PASS** |
| C28 | Corrupt grant store fails closed | `StoreTests` | **PASS** |
| C29 | Safety number refuses an empty secret | `PairingFixtureTests` | **PASS** (Windows only; Android still fails open) |
| C30 | Windows SDP: unpaired peer rejected, never skipped | `SdpAuthTests` | **PASS** |
| C31 | Windows SDP: fingerprint substitution detected | `SdpAuthTests` | **PASS** |
| C32 | Windows SDP: offer signature not replayable as answer | `SdpAuthTests` | **PASS** |
| C33 | Windows SDP: signature not reusable against another recipient | `SdpAuthTests` | **PASS** |
| C34 | Windows SDP: third party cannot tear down a session | `SdpAuthTests` | **PASS** |
| C35 | Windows signature verifies under Android's transcript layout | `SdpAuthTests` | **PASS** |

## D. Interoperability — device required

These are the W3–W5 acceptance criteria. Step-by-step procedures, including what to
record and what must **not** happen, are in
[`W3_ACCEPTANCE_PROCEDURE.md`](W3_ACCEPTANCE_PROCEDURE.md) (D4, E2) and
[`W4_ACCEPTANCE_PROCEDURE.md`](W4_ACCEPTANCE_PROCEDURE.md) (D7–D10).

| # | Scenario | Milestone | Status |
|---|---|---|---|
| D1 | Android → Android (regression) | — | **NYT** |
| D2 | Windows registers with the broker | W2 | **PASS** — automated, against the real broker (A9) |
| D3 | Android ↔ Windows pairing, matching safety numbers | W2 | **PARTIAL** — the safety number is proven identical across all three languages (B13) and both proof directions verify, but **no two real devices have paired**. That needs a phone and a PC, and remains manual |
| D4 | Android controller sees Windows desktop | W3 | **NYT — device required.** No longer blocked: the frame pump, host session orchestration and reconnection are built, and the full chain DXGI→I420→VP8→WebRTC is proven in-process (A14–A18). What is missing is a real Android controller rendering the result — the receiving peer in every automated test is a second SIPSorcery instance, so both ends share an implementation and could share a misreading of VP8 packetisation. This is W3's outstanding exit criterion. See `WINDOWS_VIDEO_PIPELINE.md` |
| D5 | Windows controller sees Android screen | W5 | **BLOCKED** |
| D6 | Windows controller → Windows host | W5 | **NYT — device required. Loopback PASS.** `techee-ctl` exists and the whole chain runs: on one machine, a 25 s session carried **494 video packets / 351,768 bytes / 249 frames, of which the controller decoded 249 with 0 empty and 0 failed at 1280x720**, and the decoded PNG is the host's real desktop. Registration, pairing, SDP signing and verification, ICE, DTLS-SRTP and the control channel all exercised; exit 0. **That is loopback, not two machines** — every candidate pair was `host`, nothing crossed a NAT. The row still needs the two-laptop run in [`W5_CONTROLLER_HARNESS.md`](W5_CONTROLLER_HARNESS.md) §4. **Not a substitute for D4** either: both ends are SIPSorcery on the same libvpx build, so they can share a misreading of VP8 packetisation |
| D7 | Mouse click / drag / wheel accuracy | W4 | **HOST PASS · controller acceptance NYT.** Click, right-click, coordinate mapping and drag are proven against a real window receiving real `WM_*` messages (A32), and authorization is proven over a real data channel (A28). Wheel is **still not exercised end to end**, but it is no longer unreachable: `pointer.wheel` has no v0 form so the shipped Android controller cannot send one, and `techee-ctl` now can. Needs a run on two machines |
| D8 | Keyboard incl. modifiers and Unicode | W4 | **HOST PASS · controller acceptance NYT.** `techee w4 héllo` was typed into a real `EDIT` control, key down/up arrive distinctly, and the extended-key flag round-trips as VK_UP rather than VK_NUMPAD8 (A32); modifier ordering and the whole D8 key vocabulary are covered at the mapping layer (A24, A25). No longer unreachable — the shipped Android controller has no keyboard sender at all, and `techee-ctl` sends both key positions and `keyboard.text`. Needs a run on two machines |
| D9 | DPI 100 / 125 / 150 % coordinate accuracy | W4 | **NYT — hardware required.** Mapping is proven monotonic and correctly positioned against a real window at **100% only** (A32); the arithmetic is unit-covered at every scale, but the reference machine has no scaled display, so 125% and 150% are untested on hardware |
| D10 | Multi-monitor selection and negative coordinates | W4 | **NYT — hardware required.** Unit-covered including negative origins; `RealInputTests` has a second-monitor case that **skips** on the single-monitor reference machine |
| D12 | View-only session cannot inject | W4 | **PASS** — over the real broker and real data channel (A28) |
| D13 | Mid-session revocation stops the next command | W4 | **PASS** — no reconnect required, and restoration re-enables the same way (A28) |
| D14 | `--no-input` cannot be overridden remotely | W4 | **PASS** — structural: no injector, so nothing subscribes to the control channel (A28, A29) |
| D15 | Malformed frames cannot reach `SendInput` | W4 | **PASS** — 9 raw hostile frames on a live channel plus 20 parameterised cases through the decoder (A28, A24) |
| D16 | Secure desktop stays inaccessible | W4 | **PARTIAL** — the probe is verified on hardware and refusal is unit-covered; **a physical UAC prompt has not been driven**, which is a manual item by design (see §6.4 of the W4 procedure) |
| D11 | Clipboard round trip | W8 | **BLOCKED** |

## E. Network and recovery — device required

| # | Scenario | Expected | Status |
|---|---|---|---|
| E1 | Same LAN, P2P | direct candidate selected | **NYT** — loopback ICE/DTLS between two in-process peers succeeds and selects a `host` pair, which is not a LAN test |
| E2 | Different networks, CGNAT | TURN relay, session usable | **NYT** — **TURN has not been exercised at all from Windows.** Techee must not be described as Internet-ready remote control until this passes |
| E3 | Wi-Fi drop and return | Android: ICE restart. Windows: **authenticated peer recreation** with fresh ICE credentials, no re-consent | **NYT** — the recreation path is covered by automated tests (A18) but has never met a real NIC/IP change |
| E4 | Broker restart mid-session | media survives; **pairings/grants lost** — see §7 | **NYT** |
| E5 | Host sleep → wake | reachable within 30–60 s | **BLOCKED** (W7) |
| E6 | Windows reboot | host auto-registers | **BLOCKED** (W6) |
| E7 | User logoff → login | agent restarts, host recovers | **BLOCKED** (W6) |

## F. Power and scheduling — hardware dependent

Honesty requirement: **software cannot wake a fully powered-off PC** without RTC
wake or Wake-on-LAN support in firmware. These rows record what a *specific
machine* actually does.

| # | Scenario | Status |
|---|---|---|
| F1 | Remote lock | **BLOCKED** (W7) |
| F2 | Remote sleep | **BLOCKED** (W7) |
| F3 | Remote restart | **BLOCKED** (W7) |
| F4 | Wake-timer availability detection | **BLOCKED** (W7) |
| F5 | Scheduled 19:00 sleep | **BLOCKED** (W7) |
| F6 | Scheduled 09:30 wake from sleep | **BLOCKED** (W7) |
| F7 | Wake from hibernate | **BLOCKED** (W7) |
| F8 | Wake from shutdown | expected **NOT SUPPORTED** unless WoL is present and configured — must be reported as such, never claimed |

## G. Definition-of-done tracker

The 20 criteria from the brief.

| # | Criterion | Status |
|---|---|---|
| 1 | Techee installs on Windows | **BLOCKED** (W6/W9) |
| 2 | Windows registers with a cryptographic identity | **PASS** — TPM-backed, non-exportable, registers against the real broker (A9, C23) |
| 3 | Android and Windows can pair | **PARTIAL** — crypto proven identical in all three languages (B12–B16); no real-device pairing yet (D3) |
| 4 | Android connects to Windows over the internet | **BLOCKED** (W3) |
| 5 | Windows desktop renders on Android | **BLOCKED** (W3) |
| 6 | Mouse input works | **PARTIAL** — a real Windows application receives clicks and drags at the right coordinates (A32), authorized over a real channel (A28); **no Android controller has driven it** (D7) |
| 7 | Keyboard input works | **PARTIAL** — Unicode text lands in a real `EDIT` control and extended keys round-trip correctly (A32); **unreachable from the shipped Android controller**, which has no keyboard sender. Needs W5 (D8) |
| 8 | Scrolling works | **PARTIAL** — `pointer.wheel` implemented in notches and bounded against hostile deltas (A24, A31); **never exercised end to end** — it has no v0 form, so it needs a v1 controller (D7) |
| 9 | Works over TURN | **BLOCKED** (W3) |
| 10 | Reconnects after interruption | **BLOCKED** (W3) |
| 11 | Host starts after reboot | **BLOCKED** (W6) |
| 12 | Unattended needs paired identity + grant | **PARTIAL** — enforced and tested on Windows at join (C25, `GrantStore.FindUsableFor`) and now re-checked per input command (A24), so a revoked or locked-out controller loses control mid-session; not yet exercised between two real devices |
| 13 | Remote sleep | **BLOCKED** (W7) |
| 14 | Remote restart | **BLOCKED** (W7) |
| 15 | Mon–Sat 09:30/19:00 schedule | **BLOCKED** (W7) |
| 16 | Android → Android still works | **PARTIAL** — 121 unit tests pass incl. the unchanged legacy parser suite; **not re-verified on hardware** |
| 17 | Existing smoke tests pass | **PASS** — 21 original checks, plus 25 new |
| 18 | New protocol/security tests pass | **PASS** — 229 JS + 34 Kotlin + 348 C# |
| 19 | No remote shell or security bypass | **PASS** — none added; no command execution exists anywhere in the protocol |
| 20 | Docs state power/wake limits honestly | **PARTIAL** — F8 recorded here; `WINDOWS_POWER_MANAGEMENT.md` due in W7 |
