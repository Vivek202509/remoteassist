# Galaxy A03 Core — Remote Cellular Telephony Feasibility

**Target host:** Samsung Galaxy A03 Core, SM-A032F/DS, Android 13 (One UI Core 5), Unisoc SC9863A, BSNL SIM
**Target controller:** Vivo V25 Pro (Funtouch OS), secondary Android user profile `A03 Controller`
**Repository:** `techee` (`com.remoteassist`)
**Status of this document:** Phases 0–2 and 5 implemented; Phases 3–4 instrumented but **awaiting physical execution on the A03**
**Date:** 2026-08-12

---

## 0. Executive summary

The requirement is a two-way cellular voice bridge:

```
Vivo mic  → Internet → A03 → BSNL cellular UPLINK   → called person
called person → BSNL cellular DOWNLINK → A03 → Internet → Vivo speaker
```

**Finding: the uplink direction is not achievable by any non-root, third-party Android
application on this device, and this is an architectural property of the platform
rather than a permission that can be requested.** The downlink direction is gated
behind a `signature|privileged` permission that a normal APK also cannot hold.

The decisive supporting evidence is already in hand and comes from the user's own
prior testing: with **shell/ADB privilege** (scrcpy), downlink capture succeeded but
**uplink injection still failed**. If uplink injection were merely a permissions
problem, shell privilege would have moved the needle. It did not. That places the
blocker below the permission layer, in the modem/audio-DSP routing itself.

The recommendation is therefore an **external hardware audio bridge**, for which the
user has already physically validated the interface (CTIA TRRS, both directions PASS).

Formal verdict: **HARDWARE AUDIO BRIDGE REQUIRED** (decision-tree RESULT C — see §18).

---

## 1. Current Techee architecture (as found, Phase 0)

Single Gradle project, one Android application module plus a Node signaling server.

| Layer | Location | What it does |
|---|---|---|
| App shell | `MainActivity.kt`, `MainViewModel.kt`, `RemoteApp.kt` | Single-activity Compose UI; dual-role (host *and* controller) in one APK |
| DI | `ServiceLocator.kt` | Manual singleton wiring; also the single `SignalingClient.Listener` |
| Signaling | `signaling/SignalingClient.kt`, `server/src/server.js` | OkHttp WebSocket ↔ Node `ws` server; 6-digit session codes, join/consent, offer/answer/ICE relay |
| WebRTC | `webrtc/WebRtcCore.kt`, `webrtc/RtcSession.kt` | One `PeerConnection` per session; Unified Plan, MAXBUNDLE, continual gathering |
| Session | `session/SessionManager.kt` | Owns the single active `RtcSession`, fans signaling into it |
| Host capture | `host/ScreenCaptureService.kt` | `mediaProjection` FGS + `ScreenCapturerAndroid` |
| Host input | `host/RemoteInputService.kt` | `AccessibilityService`; `dispatchGesture` / `performGlobalAction` / `ACTION_SET_TEXT` only |
| Unattended | `host/UnattendedHostService.kt`, `unattended/GrantStore.kt` | Persistent FGS holding the projection Intent; standing grants with scope + expiry |
| Trust | `identity/DeviceIdentity.kt`, `trust/*`, `crypto/Crypto.kt` | Per-device keypair, pairing, safety numbers, SDP-fingerprint signing |
| Wake | `fcm/RemoteFcmService.kt`, `host/SignalingService.kt` | FCM push → short-lived `connectedDevice` FGS reconnects signaling |
| TURN | `infra/turnserver.conf`, `server/src/turn.js` | coturn with time-limited HMAC credentials |

### Security posture found in place (good)

- Per-device keypair; **the DTLS fingerprint in every SDP is signed** and verified
  against the paired peer's public key (`RtcSession.verifyRemoteSdp`) — genuine MITM
  protection, not just "we use DTLS".
- TURN credentials are ephemeral HMAC, minted server-side (`server/src/turn.js`).
- No hard-coded secrets; `server/.env.example` carries placeholders only.
- Mandatory, non-dismissible foreground-service notification on every capture path.
- Standing grants carry scope + expiry + `requireUnlock`, and downgrade to view-only
  when the keyguard is locked.
- Revocation implemented end-to-end (`MainViewModel.revokePeer`).

---

## 2. Existing audio implementation (as found)

**None.** This is the single most important Phase 0 result.

The mandated keyword sweep over the whole repository returned:

| Symbol | Occurrences in app code |
|---|---|
| `AudioRecord`, `AudioTrack`, `AudioManager`, `AudioDeviceInfo`, `AudioFocusRequest` | 0 |
| `MODE_IN_CALL`, `MODE_IN_COMMUNICATION` | 0 |
| `VOICE_CALL`, `VOICE_UPLINK`, `VOICE_DOWNLINK`, `VOICE_COMMUNICATION`, `MIC` | 0 |
| `CAPTURE_AUDIO_OUTPUT` | 0 |
| `RECORD_AUDIO` | manifest only — **commented out** |
| `MODIFY_AUDIO_SETTINGS` | 0 |
| `TelephonyManager`, `TelecomManager`, `InCallService`, `ConnectionService`, `CallScreeningService`, `PhoneStateListener`, `TelephonyCallback` | 0 |
| Bluetooth SCO | 0 |
| `BOOT_COMPLETED`, `RECEIVE_BOOT_COMPLETED` | **0 — no reboot handling of any kind** |

The `PeerConnection` carried **one video track (screen) plus one control
DataChannel**. No audio track, no audio transceiver, no `AudioDeviceModule`
configuration. The WebRTC dependency exists, but — per the brief's warning — the
dependency existing is not the feature existing.

### Pre-existing defect found and fixed

`RtcSession.onRemoteOffer` chained answer creation off the wrong `SdpObserver`
callback:

```kotlin
// BEFORE — observer{} binds its lambda to onCreateSuccess,
// but setRemoteDescription only ever fires onSetSuccess.
pc!!.setRemoteDescription(observer {
    pc!!.createAnswer(...)          // never reached
}, SessionDescription(OFFER, sdp))
```

The answerer therefore never produced an answer and **no session could ever complete
negotiation**. Fixed by introducing a distinct `setObserver { }` bound to
`onSetSuccess`, and by sending SDP only after `setLocalDescription` succeeds. This
was blocking all WebRTC work, audio or otherwise.

---

## 3. Android API investigation

### 3.1 Downlink — can the APK capture cellular call audio?

The only APIs that expose telephony audio are the `MediaRecorder.AudioSource`
telephony sources:

| Source | Constant | Gate |
|---|---|---|
| `VOICE_UPLINK` | 2 | `CAPTURE_AUDIO_OUTPUT` |
| `VOICE_DOWNLINK` | 3 | `CAPTURE_AUDIO_OUTPUT` |
| `VOICE_CALL` | 4 | `CAPTURE_AUDIO_OUTPUT` |

`android.permission.CAPTURE_AUDIO_OUTPUT` is declared in the platform manifest as
`android:protectionLevel="signature|privileged"`. That means it is grantable only to
an app signed with the **platform key**, or installed into a **privileged system
partition**. There is no runtime prompt, no Settings toggle, and no user consent that
can grant it to a sideloaded or Play-installed APK. Declaring it in our manifest
would not grant it — it would only misrepresent the app's capability, which is why
the manifest deliberately **does not** declare it (see the comment block in
`AndroidManifest.xml`).

Two further platform behaviours compound this:

1. **Concurrent-capture policy (Android 10+).** When a higher-priority use case owns
   the input — and an active telephony call is the canonical example — ordinary apps
   are silenced rather than errored. They receive a stream of zeroes. This is why the
   diagnostic treats `peak == 0` as its own outcome (`SILENT`) instead of success.
2. **Background microphone restriction (Android 11+).** A non-visible app gets no
   microphone at all. During the decisive test the Phone app owns the screen, so any
   probe must run inside a `microphone`-typed foreground service or every result is
   a false negative. `DiagnosticsService` exists precisely to remove that confound.

**`AudioPlaybackCapture` (API 29+) does not help.** It captures app playback streams
from the AudioFlinger mixer, and only where the playing app allows capture. Cellular
call audio does not traverse that mixer at all (see §3.3), so the API is structurally
incapable of seeing it, independent of permissions.

### 3.2 Uplink — can the APK inject audio into the cellular uplink?

Every candidate is enumerated in code at
`android/app/src/main/java/com/remoteassist/telephony/UplinkInjectionPath.kt`, so the
conclusion is greppable and unit-tested rather than only asserted in prose:

| Mechanism | Status | Why |
|---|---|---|
| `AudioTrack(STREAM_VOICE_CALL)` | WRONG_DESTINATION | Mixes into the **local** earpiece/speaker. The far end never hears it. **This is the most common false PASS.** |
| `AudioManager.setMode(MODE_IN_CALL)` | PRIVILEGED_ONLY | Needs `MODIFY_PHONE_STATE` (signature). Even if honoured, selects a *route*, injects nothing. |
| `InCallService` / `setAudioRoute()` | WRONG_DESTINATION | Default-dialer role grants real **call control** (answer/reject/dial/DTMF/mute/route) and **zero** audio-plane access. |
| `ConnectionService` (self-managed) | WRONG_DESTINATION | Integrates the app's *own* VoIP calls. Cannot interpose on a carrier CS call. |
| `startBluetoothSco()` from the app | NO_PUBLIC_API | SCO uplink originates in an *external* headset's mic. An app cannot be its own device's hands-free peer; `BluetoothHeadset` is control-plane only. |
| External Bluetooth HFP unit | REQUIRES_EXTERNAL_HARDWARE | Genuinely works — but it is hardware, not the APK. |
| USB Audio Class device | REQUIRES_EXTERNAL_HARDWARE | An app cannot present as a UAC device (needs gadget mode + ROM/kernel). SM-A032F is micro-USB 2.0; UAC support not assumed. |
| CTIA TRRS headset mic | REQUIRES_EXTERNAL_HARDWARE | **Already verified working on this handset.** No software API drives an analogue input. |

`UplinkInjectionPath.anyViablePath()` returns `false`, and
`UplinkInjectionPathTest.noSoftwareUplinkPathExists` fails the build if anyone
changes that without also revisiting this document and the verdict.

### 3.3 Why uplink is blocked *below* the permission layer

On this class of device the circuit-switched voice path is:

```
modem/RF  ──►  audio DSP / codec (HAL "voice call" route)  ──►  earpiece
handset mic ──►  audio DSP / codec  ──────────────────────────►  modem/RF
```

The application processor asks the audio HAL to *set up a route*; it never carries
the samples. There is no PCM buffer in the Android userspace audio stack for either
call direction to read from or write to. Permissions are irrelevant to something that
was never exposed.

The observed BSNL behaviour corroborates this: the A03 camps on **LTE B28 / 700 MHz**
when idle but drops to **GSM 900** during a voice call — i.e. **CSFB**, a pure
circuit-switched call terminated entirely inside the modem. (Even VoLTE would not
change the conclusion: IMS/RTP still terminates in the modem, not in an app-visible
stream.)

**This is the direct explanation for the user's existing scrcpy result:** shell
privilege was sufficient for downlink capture but still could not inject uplink,
because uplink injection is not a privilege that exists to be granted.

---

## 4. Permissions required (and deliberately not requested)

Added to `AndroidManifest.xml`:

| Permission | Why | Notes |
|---|---|---|
| `RECORD_AUDIO` | WebRTC voice + audio-source diagnostic | Grants **ordinary** sources only |
| `MODIFY_AUDIO_SETTINGS` | Read/observe audio mode & routing | No privileged mode changes attempted |
| `READ_PHONE_STATE` | IDLE / RINGING / OFFHOOK detection | Minimum for call-state |
| `FOREGROUND_SERVICE_MICROPHONE` | Probe validity while Phone app is foregrounded | Android 14+ requirement |

**Deliberately NOT requested:**

| Permission | Why not |
|---|---|
| `CAPTURE_AUDIO_OUTPUT` | `signature\|privileged` — ungrantable to this APK. Declaring it would imply a capability we do not have. Probed at runtime and reported as denied. |
| `READ_CALL_LOG` | Would be needed to surface the incoming caller's **number**. Not requested, so caller ID is honestly reported as unavailable rather than obtained via a broader permission than the feature warrants. |
| `ANSWER_PHONE_CALLS` / `CALL_PHONE` / dialer role | Not requested in this POC. Remote answer/dial is **not** claimed as working (see §8, TEST E). |
| SMS permissions | Out of scope for this phase; secondary to voice per the brief. |

---

## 5. Physical device

Samsung Galaxy A03 Core, **SM-A032F/DS**, Unisoc SC9863A, 2 GB RAM, micro-USB 2.0,
3.5 mm CTIA headset jack, dual SIM (BSNL in slot 1, resident at all times).

## 6. Android version

Android 13 (API 33), One UI Core 5. Final configuration requires **Developer Options
OFF, USB debugging OFF, wireless debugging OFF, no root, stock ROM**.

## 7. BSNL network behaviour (observed)

| Condition | Observed |
|---|---|
| Idle / data | LTE **Band 28 (700 MHz)** |
| During voice call | Falls back to **GSM 900 MHz** (CSFB) |

Confirms circuit-switched voice handled in-modem. Also implies data throughput drops
to GPRS/EDGE during a call **on the same SIM** — a material constraint for any
software design that would have relied on BSNL data while a BSNL call is up. A
hardware bridge with its own independent data path avoids this trap entirely.

---

## 8. Tests performed

### 8.1 Executed in this environment

| Test | Command | Result |
|---|---|---|
| Signaling protocol smoke (12 assertions) | `npm run smoke` (in `server/`) | **12 passed, 0 failed** |
| Repository keyword sweep (Phase 0) | `Grep` over all tracked files | Complete — see §2 |

### 8.2 NOT executed — toolchain unavailable on this machine

`java`, `JAVA_HOME`, and the Android SDK are **absent from this Windows host**
(verified). Therefore:

- `./gradlew assembleDebug` — **NOT RUN**
- `./gradlew testDebugUnitTest` — **NOT RUN**
- `./gradlew lintDebug` — **NOT RUN**

The new unit tests are written and committed but **have not been executed locally**.
The repository's GitHub Actions workflow (`.github/workflows/ci.yml`) already runs
exactly these three tasks on every push and is the intended verification path. This
is stated plainly rather than reported as a pass.

### 8.3 Physical A03 tests — NOT YET PERFORMED

TESTS A–F (§ manual protocol) require the physical handset and a second party on a
call. None have been run. Every corresponding row in
`TELEPHONY_AUDIO_TEST_MATRIX.md` is marked **NOT YET TESTED**.

---

## 9. Exact commands / builds

```bash
# Server (runs on this machine)
cd server && npm ci && npm run smoke

# Android — requires JDK 17 + Android SDK (platform 35)
cd android
./gradlew assembleDebug testDebugUnitTest lintDebug --no-daemon --stacktrace

# Install the POC (production-style: no ADB needed on the A03 afterwards)
./gradlew installDebug          # only during setup, with Developer Options temporarily ON
```

Logcat filters for the physical tests:

```
adb logcat -s AudioProbe:I TelephonyDiag:I DiagnosticsService:I RtcSession:I CallStateMonitor:I SessionManager:I
```

> Note: ADB is used **only** to read logs during investigation. The final
> configuration requires Developer Options OFF, and the diagnostic reports its
> verdict on-screen and (optionally) to a JSON file, so no ADB is required to
> obtain results.

---

## 10. PASS/FAIL evidence

| Claim | Evidence | Status |
|---|---|---|
| No audio existed before this work | Keyword sweep §2 | **Confirmed** |
| Answerer never created an SDP answer | Code read of `onRemoteOffer` | **Confirmed, fixed** |
| `CAPTURE_AUDIO_OUTPUT` is `signature\|privileged` | Platform manifest protection level | **Confirmed by documentation; runtime-probed by the diagnostic** |
| No public uplink-injection API exists | `UplinkInjectionPath` survey + unit test | **Confirmed** |
| Shell privilege captured downlink but not uplink | User's prior scrcpy testing | **Confirmed (user-supplied)** |
| TRRS headset works both directions | User's prior physical test | **Confirmed (user-supplied)** |
| Downlink capture from a normal APK | — | **NOT YET TESTED on device** |
| Uplink injection from a normal APK | — | **NOT YET TESTED on device** (expected FAIL) |

## 11. Log excerpts

None from the A03 yet. The diagnostic emits one line per source, e.g.:

```
I/AudioProbe: VOICE_DOWNLINK -> UNINITIALIZED peak=0 rms=0.0 frames=0 err=...
I/AudioProbe: MIC            -> LIVE_AUDIO    peak=8412 rms=1103.4 frames=23
```

Fill these into `TELEPHONY_AUDIO_TEST_MATRIX.md` as they are produced.

## 12. Audio-route observations

To be captured by `TelephonyAudioDiagnostics` (`audioMode`, `communicationDevice`,
input/output `AudioDeviceInfo` lists, speakerphone/SCO state) during idle vs. active
call. Not yet collected.

## 13. Network / TURN observations

`IceCandidateLog` now records host/srflx/relay counts per session and
`SessionManager.usedRelayCandidate()` exposes whether a relay candidate was gathered.
Expectation for BSNL mobile data ↔ Vivo mobile data: **CGNAT on both sides forces a
TURN relay path**. Not yet measured — Tests 1–5 of the network matrix are pending.

## 14. Reboot / unattended findings

Code inspection result — **no reboot handling exists**:

- No `RECEIVE_BOOT_COMPLETED` permission, no `BroadcastReceiver`, none in the manifest.
- `UnattendedHostService.onStartCommand` explicitly `stopSelf()`s on a null restart
  Intent, because the stored MediaProjection grant cannot be reconstructed.

Platform limitations that **cannot** be engineered away and are reported as real:

1. **MediaProjection consent cannot survive a reboot.** On Android 11+ the permission
   Intent is effectively single-use and Android 14+ tightens re-acquisition further.
   After every reboot, **a human must physically tap "Start now" on the A03.**
2. **Direct-boot / first unlock.** Encrypted storage (the trust store and grants) is
   unavailable until first unlock, so nothing meaningful can run before someone
   unlocks the phone.
3. **Samsung background restrictions.** One UI aggressively sleeps apps; the A03 Core
   is a 2 GB low-RAM device, making foreground-service eviction materially more
   likely than on a flagship. "Unrestricted battery" + removal from Sleeping/Deep
   Sleeping app lists is mandatory, and still not a guarantee.

**Consequence for the stated requirement ("no PC/laptop at Place A, phone may be
unattended"): a software-only host cannot self-restore after reboot.** This is a
second, independent argument for the hardware bridge, which has no MediaProjection
dependency at all.

## 15. myOffice compatibility

No change requested and none made. Techee contains **no** code that touches
`FLAG_SECURE`, screen-capture restriction, root/accessibility/developer-mode
detection, or any anti-tamper control — the Phase 0 sweep confirms this.

If myOffice renders **black** through remote view, that is `FLAG_SECURE` working as
designed and is recorded as **PASS (expected security behaviour)**. No bypass will be
implemented. Because remote myOffice operation is explicitly not a project
requirement, this costs the project nothing.

Residual risk to verify physically: myOffice may independently object to an enabled
`AccessibilityService`. That is myOffice's prerogative; if it does, the correct
response is to leave myOffice alone and reconsider whether the host app belongs on
that handset at all — not to defeat the detection.

## 16. Security findings

Inherited posture is strong (§1). Changes made preserve it:

- No new network surface; audio rides the **existing** authenticated, DTLS-SRTP
  `PeerConnection`. No parallel transport was created.
- No new secrets; no hard-coded credentials.
- The diagnostic **never writes audio to disk**. Each read is reduced to peak/RMS and
  the PCM buffer is discarded. JSON dumps are opt-in per run and contain measurements
  only.
- Microphone capture happens only under a user-visible foreground-service
  notification.
- Remote audio playback on the host is **default-OFF** (`hostPlaysControllerAudio`)
  — both a privacy control and the guard against the acoustic-loopback false PASS.
- `READ_CALL_LOG` deliberately not requested (§4).

Open items, unchanged by this work and worth tracking:
- `RtcSession.verifyRemoteSdp` returns `true` for an **unpaired** peer (ad-hoc
  code-based sessions rely on the 6-digit code plus explicit consent). Acceptable as
  designed, but pairing should be mandatory for any unattended telephony deployment.
- `BuildConfig.SIGNALING_URL` defaults to plaintext `ws://` in debug. Release uses
  `wss://`. Any real deployment must be `wss://` only.

## 17. Blockers

| # | Blocker | Severity | Can engineering solve it? |
|---|---|---|---|
| B1 | `CAPTURE_AUDIO_OUTPUT` is `signature\|privileged` → no downlink capture | **Hard** | No — requires platform signing or system partition |
| B2 | No public API injects audio into the cellular uplink at any privilege level | **Hard** | No — the plane is not exposed by the OS |
| B3 | MediaProjection consent cannot survive reboot | **Hard** | No — requires a human tap at Place A |
| B4 | BSNL same-SIM data collapses to GPRS/EDGE during a CSFB voice call | **Hard** | Only by using an independent data path |
| B5 | Samsung/low-RAM background eviction of the host FGS | Medium | Mitigable, not eliminable |
| B6 | Local build/test not runnable here (no JDK/SDK) | Low | Yes — run CI or install a JDK |

B1 and B2 together are decisive. B3 and B4 independently undermine the software-only
architecture even if B1/B2 were somehow solved.

## 18. Final feasibility verdict

**HARDWARE AUDIO BRIDGE REQUIRED** — decision-tree **RESULT C**
(*no direct cellular audio access available to the production APK*).

Both directions are unavailable to a non-root third-party APK:

- **Downlink:** blocked by a signature-level permission (B1).
- **Uplink:** no API exists at any privilege level (B2), independently corroborated by
  the user's own shell-privileged scrcpy test failing in exactly this direction.

Per the brief's stop conditions, this is reported as a **FAIL** rather than dressed up.
The instrumentation in Phase 1 exists so this conclusion can be **demonstrated on the
physical handset** rather than accepted on authority — run TEST B/C and the matrix
rows will be filled with device evidence.

## 19. Recommended production architecture

### Control plane — software (Techee, keep and extend)

Techee is genuinely well-suited to everything *except* the audio plane:

1. **Call state → controller.** `CallStateMonitor` (READ_PHONE_STATE) sends
   IDLE/RINGING/OFFHOOK over the existing authenticated DataChannel. Implemented.
2. **Remote dial / answer / reject.** The legitimate route is the
   **`ROLE_DIALER` / `InCallService`** default-dialer role, which grants real call
   control with no audio access. Recommended over AccessibilityService UI-poking,
   which is brittle and harder to justify. **Not yet implemented and not claimed.**
3. **Wake + notification.** Existing FCM path already fits incoming-call alerting.
4. **Screen view/control.** Existing MediaProjection + AccessibilityService, with the
   reboot caveat (B3) documented rather than hidden.

### Audio plane — hardware bridge

An external device at Place A that presents itself to the A03 as an ordinary
**call-audio peripheral**, and to the network as an authenticated encrypted endpoint:

```
BSNL caller ──► A03 (cellular) ──► [TRRS or Bluetooth HFP] ──► bridge ──► encrypted IP ──► Vivo
Vivo mic ────► encrypted IP ────► bridge ──► [headset mic / HFP uplink] ──► A03 ──► BSNL caller
```

Two interface options, both using paths the phone already supports:

- **CTIA TRRS** — already physically verified on this handset in both directions.
  Requires careful analogue design (bias, attenuation, isolation). See
  `TRRS_AUDIO_BRIDGE_ARCHITECTURE.md`.
- **Bluetooth HFP** — the bridge acts as a hands-free unit. Electrically simpler (no
  analogue interface at all) and wireless; adds pairing/robustness considerations.
  **Recommended to evaluate first**, precisely because it avoids every analogue
  pitfall documented in the TRRS design.

Either option also neutralises B3 (no MediaProjection dependency for audio) and B4
(the bridge carries its own data path, so it does not compete with the BSNL voice
call for bandwidth).

---

## Appendix A — Files added/changed in this phase

See §3 of the summary response, or `git diff`.

## Appendix B — Related documents

- `TELEPHONY_AUDIO_TEST_MATRIX.md` — the row-by-row test grid to fill in on device
- `TRRS_AUDIO_BRIDGE_ARCHITECTURE.md` — hardware fallback design
