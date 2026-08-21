# Telephony Audio Test Matrix — Galaxy A03 Core (SM-A032F/DS)

Fill the **Actual** and **P/F** columns as each test is run on the physical handset.
Nothing in this file may be marked PASS on the basis of reasoning — only on observed
device behaviour.

**Legend:** `NYT` = not yet tested · `DevOpts` = Developer Options
**Standing precondition for all rows unless stated:** non-root, production-style APK,
Developer Options OFF, USB/wireless debugging OFF.

---

## A. Audio-source capability probes (`TelephonyAudioDiagnostics`)

| ID | Device state | Audio source | Audio destination | Cellular state | Network | Expected | Actual | P/F | Evidence |
|---|---|---|---|---|---|---|---|---|---|
| A1 | App foreground | `MIC` | RMS meter only | IDLE | any | LIVE_AUDIO | — | NYT | `AudioProbe` log |
| A2 | App foreground | `VOICE_COMMUNICATION` | RMS meter | IDLE | any | LIVE_AUDIO | — | NYT | `AudioProbe` log |
| A3 | App foreground | `VOICE_CALL` | RMS meter | IDLE | any | UNINITIALIZED / CONSTRUCT_FAILED | — | NYT | `AudioProbe` log |
| A4 | App foreground | `VOICE_DOWNLINK` | RMS meter | IDLE | any | UNINITIALIZED / CONSTRUCT_FAILED | — | NYT | `AudioProbe` log |
| A5 | App foreground | `VOICE_UPLINK` | RMS meter | IDLE | any | UNINITIALIZED / CONSTRUCT_FAILED | — | NYT | `AudioProbe` log |
| A6 | Mic FGS, Phone app front | `MIC` | RMS meter | **ACTIVE call** | any | LIVE_AUDIO **or** SILENT (concurrent-capture policy) | — | NYT | `AudioProbe` log |
| A7 | Mic FGS, Phone app front | `VOICE_CALL` | RMS meter | **ACTIVE call** | any | UNINITIALIZED (no `CAPTURE_AUDIO_OUTPUT`) | — | NYT | `AudioProbe` log |
| A8 | Mic FGS, Phone app front | `VOICE_DOWNLINK` | RMS meter | **ACTIVE call** | any | UNINITIALIZED | — | NYT | `AudioProbe` log |
| A9 | Mic FGS, Phone app front | `VOICE_UPLINK` | RMS meter | **ACTIVE call** | any | UNINITIALIZED | — | NYT | `AudioProbe` log |
| A10 | Any | `CAPTURE_AUDIO_OUTPUT` permission check | — | any | any | **DENIED** (signature\|privileged) | — | NYT | `permissions` in report |

> A6 is the trap row. `MIC` returning LIVE_AUDIO during a speakerphone call is the
> handset picking the far end up **acoustically**. It is **not** downlink capture and
> must never be recorded as one — see rule R1 below.

---

## B. WebRTC audio transport (no cellular involvement)

| ID | Device state | Audio source | Audio destination | Cellular state | Network | Expected | Actual | P/F | Evidence |
|---|---|---|---|---|---|---|---|---|---|
| B1 | Session live | Vivo mic | A03 (track received, playback suppressed) | IDLE | same Wi-Fi | Track received; `playback=SUPPRESSED` | — | NYT | `SessionManager` log |
| B2 | Session live | A03 mic (`hostMicrophoneEnabled=true`) | Vivo speaker | IDLE | same Wi-Fi | Audible on Vivo | — | NYT | listener |
| B3 | Session live | both | both | IDLE | same Wi-Fi | Full duplex, no cellular | — | NYT | listener |

---

## C. Cellular downlink — TEST B

| ID | Device state | Audio source | Audio destination | Cellular state | Network | Expected | Actual | P/F | Evidence |
|---|---|---|---|---|---|---|---|---|---|
| C1 | A03 in call, earpiece route, **not** speakerphone | cellular downlink | Vivo headphones | ACTIVE | separate networks | **FAIL expected** — no privileged source available | — | NYT | A7/A8 + listener |
| C2 | Same, DevOpts OFF confirmed | cellular downlink | Vivo headphones | ACTIVE | separate | FAIL expected | — | NYT | Settings screenshot |

**Validity rules — a C-row may only be PASS if all hold:**
- R1: speakerphone **OFF** and A03 acoustically isolated (another room), so the far
  end's voice cannot reach the handset microphone.
- R2: the audio arriving at the Vivo originates from a **telephony** source
  (`isCellularDownlinkEvidence == true`), not `MIC`/`VOICE_COMMUNICATION`.
- R3: Developer Options OFF, no ADB attached.

---

## D. Cellular uplink — TEST C (decisive)

| ID | Device state | Audio source | Audio destination | Cellular state | Network | Expected | Actual | P/F | Evidence |
|---|---|---|---|---|---|---|---|---|---|
| D1 | A03 **in another room**, mic not usable by operator | Vivo microphone | BSNL far end | ACTIVE | separate | **FAIL expected** — no injection API | — | NYT | far-end party repeats phrase? |
| D2 | Same, `hostPlaysControllerAudio=false` (default) | Vivo microphone | BSNL far end | ACTIVE | separate | FAIL | — | NYT | far-end report |
| D3 | Control: `hostPlaysControllerAudio=true`, A03 **beside** operator | Vivo mic → A03 speaker → A03 mic (acoustic) | BSNL far end | ACTIVE | separate | Far end may hear — **this is a FALSE PASS, record as FAIL** | — | NYT | see note |

**Validity rules — a D-row may only be PASS if all hold:**
- R4: the A03 is several metres away / acoustically isolated.
- R5: **nobody speaks near the A03's physical microphone.**
- R6: the far-end party successfully repeats a **random phrase** spoken only into the
  Vivo microphone.
- R7: `hostPlaysControllerAudio` is **false** (no speaker→mic acoustic loop).

> D3 exists solely to document the false-positive mechanism. If D3 "works" and D1
> does not, the system has an acoustic echo, not an uplink bridge.

---

## E. Full duplex — TEST D

| ID | Measure | Target | Actual | P/F | Evidence |
|---|---|---|---|---|---|
| E1 | One-way latency | < 300 ms conversational | — | NYT | — |
| E2 | Echo | none perceptible | — | NYT | — |
| E3 | Clipping | none | — | NYT | — |
| E4 | Dropouts over 2 min | 0 | — | NYT | — |
| E5 | WebRTC jitter | < 30 ms | — | NYT | `getStats` |
| E6 | Packet loss | < 1 % | — | NYT | `getStats` |
| E7 | Audio-route changes | none unexpected | — | NYT | diagnostics report |

---

## F. Incoming call — TEST E

| ID | Device state | Event | Expected | Actual | P/F | Evidence |
|---|---|---|---|---|---|---|
| F1 | Host armed | External call to BSNL number | `CallStateMonitor` → RINGING | — | NYT | `CallStateMonitor` log |
| F2 | Session live | RINGING relayed to Vivo | Controller shows RINGING | — | NYT | DataChannel |
| F3 | No session | FCM wake → controller notification | Notification on Vivo | — | NYT | — |
| F4 | — | Caller **number** shown | **Unavailable** (READ_CALL_LOG not requested) | — | NYT | by design, §4 |
| F5 | — | Remote **answer** from Vivo | **NOT IMPLEMENTED / NOT CLAIMED** | — | NYT | requires dialer role |

---

## G. Network matrix — TEST F

| ID | A03 network | Controller network | Expected ICE path | Actual | P/F | Evidence |
|---|---|---|---|---|---|---|
| G1 | Wi-Fi | same Wi-Fi | host | — | NYT | `IceCandidateLog` |
| G2 | Wi-Fi (LAN A) | Wi-Fi (LAN B) | srflx | — | NYT | `IceCandidateLog` |
| G3 | BSNL mobile data | Wi-Fi | srflx or relay | — | NYT | `IceCandidateLog` |
| G4 | BSNL mobile data | Vivo mobile data | **relay (CGNAT)** | — | NYT | `usedRelayCandidate()` |
| G5 | Wi-Fi → mobile data mid-session | unchanged | ICE restart recovers | — | NYT | `LinkState` RECOVERING→CONNECTED |
| G6 | During active BSNL **voice** call | any | data degrades to GPRS/EDGE (CSFB) | — | NYT | throughput observation |

> G1 alone must never be treated as success — the requirement is unrelated networks.

---

## H. Unattended / lifecycle

| ID | Condition | Expected | Actual | P/F | Evidence |
|---|---|---|---|---|---|
| H1 | Screen locked | Session continues; control downgrades per grant | — | NYT | — |
| H2 | App backgrounded | FGS keeps session alive | — | NYT | — |
| H3 | Process killed by OS | Session lost; FCM wake re-establishes signaling | — | NYT | — |
| H4 | Mobile-data reconnect | ICE restart recovers | — | NYT | — |
| H5 | Battery saver ON | Survives / documented failure | — | NYT | — |
| H6 | Samsung "Sleeping apps" restriction | Must be excluded manually | — | NYT | — |
| H7 | **Reboot** | **Host does NOT auto-restore — human tap required** | — | **Known limitation** | §14 |
| H8 | First unlock after reboot | Encrypted stores become available | — | NYT | — |
| H9 | FCM wake from Doze | Notification delivered | — | NYT | — |
| H10 | AccessibilityService persistence | Survives reboot after unlock | — | NYT | — |
| H11 | MediaProjection lifetime | Single-use Intent; cannot be revived | — | **Known limitation** | §14 |

---

## I. myOffice compatibility

| ID | Condition | Expected | Actual | P/F | Evidence |
|---|---|---|---|---|---|
| I1 | myOffice launches locally with Techee installed | Works | — | NYT | — |
| I2 | myOffice viewed via remote screen | **Black screen = PASS** (FLAG_SECURE) | — | NYT | — |
| I3 | myOffice with AccessibilityService enabled | May object — **do not bypass** | — | NYT | — |
| I4 | DevOpts OFF for all above | Confirmed | — | NYT | Settings |

---

## J. Automated (host machine / CI)

| ID | Test | Command | Expected | Actual | P/F |
|---|---|---|---|---|---|
| J1 | Signaling protocol smoke | `npm run smoke` | 12 pass | **12 passed, 0 failed** | **PASS** |
| J2 | Android unit tests | `./gradlew testDebugUnitTest` | all pass | **NOT RUN — no JDK/SDK on this host** | — |
| J3 | Android lint | `./gradlew lintDebug` | no errors | **NOT RUN — no JDK/SDK** | — |
| J4 | Android build | `./gradlew assembleDebug` | success | **NOT RUN — no JDK/SDK** | — |

---

## Summary status

| Question | Status |
|---|---|
| Cellular downlink to controller | **NOT YET TESTED** (expected FAIL — B1) |
| Cellular uplink from controller | **NOT YET TESTED** (expected FAIL — B2) |
| Unattended across reboot | **PARTIAL — known hard limitation (H7/H11)** |
| myOffice compatibility | **NOT YET TESTED** (no bypass code exists) |
