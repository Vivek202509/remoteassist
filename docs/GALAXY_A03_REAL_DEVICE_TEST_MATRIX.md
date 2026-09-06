# Galaxy A03 — Real Device Test Matrix

Physical-device evidence log for Techee Remote Phone.

**Automated tests do not populate this file.** A row may only be marked PASS by a
human who performed the step on the real hardware and recorded what happened. A
compiling build, a passing unit test, or an emulator run is never evidence here.

Related evidence, do not delete:
- [`GALAXY_A03_TELEPHONY_FEASIBILITY.md`](GALAXY_A03_TELEPHONY_FEASIBILITY.md)
- [`TELEPHONY_AUDIO_TEST_MATRIX.md`](TELEPHONY_AUDIO_TEST_MATRIX.md)
- [`TRRS_AUDIO_BRIDGE_ARCHITECTURE.md`](TRRS_AUDIO_BRIDGE_ARCHITECTURE.md)

---

## Hardware under test

| Role | Device | Identifier | Notes |
|---|---|---|---|
| HOST (Place A) | Samsung Galaxy A03 | BSNL SIM | Android version: _record at test time_ |
| CONTROLLER (Place B) | Vivo V25 Pro | — | Controller runs in a secondary Android user profile (target) |

## Record format

Every row must carry: Feature, Build, Date, Network, Developer Options state,
Expected, Actual, PASS/FAIL, Evidence.

`Evidence` = screenshot filename, logcat excerpt, or a witnessed description.
`MANUAL TEST REQUIRED` = not yet performed. It is not a soft PASS.

---

## Milestone 1 — Stabilize Core

Build under test: `app-debug.apk`, git `main` + uncommitted Phase 0–2/5 work.

> **Prerequisite for every row below:** the APK must be built with a signaling
> URL both handsets can reach over the internet:
> `./gradlew assembleDebug -PsignalingUrl=wss://<your-host>`
> The default `ws://10.0.2.2:8080` is the emulator's loopback alias and is
> unreachable from a physical handset.

| # | Feature | Build | Date | Network | Dev Options | Expected | Actual | Result | Evidence |
|---|---|---|---|---|---|---|---|---|---|
| 1.1 | A03 APK installation | | | — | ON (install only) | APK installs and launches | | MANUAL TEST REQUIRED | |
| 1.2 | Vivo APK installation | | | — | ON (install only) | APK installs and launches | | MANUAL TEST REQUIRED | |
| 1.3 | Device identity + pairing | | | any | OFF | Both devices show the same safety number; pairing persists | | MANUAL TEST REQUIRED | |
| 1.3a | Authenticated registration handshake | | | any | OFF | Both devices reach `registered`; server logs no `register-failed`. Confirms the Kotlin and Node transcripts agree on real hardware. | | MANUAL TEST REQUIRED | |
| 1.3b | Authenticated reconnect | | | Wi-Fi → mobile data | OFF | Device re-registers after the network switch; any prior socket receives `session-replaced` | | MANUAL TEST REQUIRED | |
| 1.4 | A03 → Vivo screen | | | same Wi-Fi | OFF | Live A03 screen renders on Vivo | | MANUAL TEST REQUIRED | |
| 1.5 | Vivo → A03 tap | | | same Wi-Fi | OFF | Tap lands on the correct element | | MANUAL TEST REQUIRED | |
| 1.6 | Vivo → A03 swipe | | | same Wi-Fi | OFF | Scroll/swipe tracks the gesture | | MANUAL TEST REQUIRED | |
| 1.7 | Vivo → A03 Back | | | same Wi-Fi | OFF | Back navigation occurs | | MANUAL TEST REQUIRED | |
| 1.8 | Different-network connection | | | A03 mobile data ↔ Vivo Wi-Fi | OFF | Session establishes across networks | | MANUAL TEST REQUIRED | |
| 1.9 | TURN path verified | | | A03 mobile data ↔ Vivo mobile data (CGNAT ↔ CGNAT) | OFF | `relay` candidate gathered and session works | | MANUAL TEST REQUIRED | |
| 1.10 | Developer Options OFF | | | any | **OFF**, USB debugging OFF, wireless debugging OFF, USB unplugged | Full session works with no ADB present | | MANUAL TEST REQUIRED | |

### Notes on 1.9 — what counts as TURN verified

`IceCandidateLog.sawRelay()` proves a relay candidate was **gathered**, not that
it was **selected**. Gathering alone is not proof the media flowed through TURN.
To claim the relay path was actually used, capture the selected candidate pair
(WebRTC stats) or demonstrate the session working on a network pair where no
direct path can exist. Record which of those two was done.

---

## Milestone 2+ — not yet started

Rows are added when the milestone is authorized.

---

## Known platform limitations (not test failures)

| Limitation | Consequence | Source |
|---|---|---|
| No third-party access to cellular call audio | Downlink/uplink cannot be captured or injected via Android audio APIs; an external bridge is required | Physical A03 test — `verdict=NO_DIRECT_CELLULAR_AUDIO_ACCESS` |
| MediaProjection requires local user approval | Remote screen may need on-device reauthorization after reboot | Android platform |
| FLAG_SECURE / protected screens | Apps such as myOffice may render black remotely; this is correct behavior | Android platform |
