# TRRS / HFP Audio Bridge — Hardware Fallback Architecture

**Why this document exists:** `GALAXY_A03_TELEPHONY_FEASIBILITY.md` concludes that no
non-root, third-party Android application can place audio on the Galaxy A03's
cellular uplink (blocker B2), and cannot capture the downlink either (blocker B1).
The only remaining way to reach the cellular audio plane is to present the A03 with
something it already accepts as **call-audio peripheral hardware**.

Two such interfaces exist on SM-A032F, and **both have already been physically
validated in both directions on this exact handset** (wired headset test):

```
wired headset mic  → A03 → BSNL caller       PASS (verified)
BSNL caller → A03  → wired headset earpiece  PASS (verified)
```

---

## 0. Read this first — pick the interface before designing anything

| | **Option 1: Bluetooth HFP** | **Option 2: CTIA TRRS** |
|---|---|---|
| Analogue design required | **None** | Substantial (bias, attenuation, isolation) |
| Risk of damaging the phone | Negligible | Real if done wrong |
| Accidental button/answer events | None | **Yes** — mic-pin shorting is the headset button |
| Ground-loop noise | None | Must be engineered out |
| Echo handling | Codec-level, well-trodden | Must be designed |
| Already verified on this A03 | Not yet | **Yes** |
| Wireless / placement freedom | Yes | Cable-tethered |
| Failure mode | Pairing drops | Connector wear |

**Recommendation: evaluate Bluetooth HFP first.** It uses the path Android and the
modem are *designed* to expose to external call-audio hardware, and it eliminates
every analogue hazard documented in §2–§5 below. The TRRS design is retained as the
fallback because it is the one already proven on this handset.

The rest of this document specifies both, TRRS in full detail because it is the one
with electrical requirements that must not be improvised.

---

## 1. CTIA pinout (Samsung standard)

SM-A032F uses **CTIA / AHJ**, not OMTP. Wiring an OMTP headset swaps mic and ground
and will not work.

```
   ┌─────────────────────────── 3.5 mm TRRS plug ───────────────────────────┐

     TIP        RING 1       RING 2        SLEEVE
      │           │            │             │
   ┌──┴──┐   ┌────┴───┐   ┌────┴────┐   ┌────┴────┐
   │  L  │   │   R    │   │   GND   │   │  MIC    │
   │audio│   │ audio  │   │ (common)│   │ + bias  │
   └─────┘   └────────┘   └─────────┘   └─────────┘
    out→      out→          ref           ←in
```

| Contact | CTIA function | Direction (from phone) |
|---|---|---|
| Tip | Left audio | Output |
| Ring 1 | Right audio | Output |
| Ring 2 | Ground / common | Reference |
| Sleeve | Microphone + DC bias | Input |

> **OMTP (legacy) swaps Ring 2 and Sleeve.** Verify with a multimeter before
> connecting anything: on CTIA the *sleeve* carries the DC bias voltage.

---

## 2. Microphone bias — the constraint that dominates the design

The phone detects and powers an electret capsule by sourcing DC through an internal
bias resistor on the sleeve.

| Parameter | Typical range | Design action |
|---|---|---|
| Open-circuit bias voltage | 1.8 – 2.8 V | **Measure on the actual A03 before design** |
| Internal bias resistor | 1.5 – 2.7 kΩ (commonly 2.2 kΩ) | Infer from loaded vs. open voltage |
| Expected loaded mic-pin voltage | ~1.0 – 2.2 V | Target operating point |
| Signal level the phone expects | **~1 – 20 mV RMS** (electret, ≈ −42 dBV/Pa) | Sets the attenuation requirement |

### Mandatory rules

- **R1 — Never DC-couple a driver output to the sleeve.** Always AC-couple through a
  series capacitor. A DC path collapses the bias, and the phone will either
  mis-detect the accessory or see a permanent "button held" state.
- **R2 — Present a realistic DC load.** A resistor of roughly **1.5 – 2.2 kΩ from
  sleeve to ground** emulates an electret capsule so the phone enumerates a
  *headset* (4-pole) rather than *headphones* (3-pole). Without it, the mic path is
  never activated.
- **R3 — Never short the sleeve to ground, even briefly.** Sub-100 Ω sleeve-to-ground
  is exactly how the headset **button** is signalled. A stray short will
  answer/hang-up calls — the opposite of the requirement. Include series resistance
  and avoid hot-plugging a powered output.
- **R4 — Current-limit.** Keep injected current into the bias node in the
  microamp range; the node is not designed to sink drive current.

---

## 3. Attenuation — line level to microphone level

The bridge's DAC/codec produces roughly **0.3 – 1.0 V RMS** (line level). The phone's
mic input expects roughly **1 – 20 mV RMS**. The required attenuation is therefore on
the order of **40 – 50 dB (100:1 to 300:1)**.

Feeding line level straight into the mic pin produces severe clipping and distortion
at the far end, and stresses the bias network. **This is the single most common way
this build is done wrong.**

### Reference network (verify against measured bias before building)

```
 DAC out ──[ R_s 10 kΩ ]──┬──[ C_c 1–10 µF ]──► SLEEVE (mic + bias)
                          │
                       [ R_sh 100 Ω ]
                          │
                         GND                  SLEEVE ──[ R_load 2.2 kΩ ]── GND
```

| Element | Value | Purpose |
|---|---|---|
| `R_s` | 10 kΩ | Series arm of the attenuator; also limits current (R4) |
| `R_sh` | 100 Ω | Shunt arm → ≈ 100/(10 000+100) ≈ **−40 dB** |
| `C_c` | 1–10 µF (non-polarised or correctly oriented) | AC coupling; blocks DC (R1) |
| `R_load` | 1.5 – 2.2 kΩ | Emulates electret DC load (R2) |

Add a **trim potentiometer** (e.g. 10 kΩ) in the shunt leg: the correct final value
depends on the measured bias and the codec's output swing, and must be set by ear/meter
against the far end, not assumed.

### Downlink side (phone earpiece output → bridge ADC)

- Phone headphone output: up to ~1 V RMS into 32 Ω — this is a **low-impedance
  speaker drive**, not a line output.
- Attenuate to the bridge's line/ADC input range (typically ~0.5 – 1 V RMS full
  scale): a simple 10:1 divider is usually right, plus AC coupling.
- Sum L (tip) and R (ring 1) to mono through **separate series resistors** (e.g.
  2 × 10 kΩ into a common node). **Never tie L and R directly together** — that
  shorts the two amplifier outputs.

---

## 4. Ground isolation

The bridge is USB-powered while sharing an audio ground with the phone. If the phone
is also charging, both ends can reference mains earth through different paths — a
classic ground loop producing hum and, in the worst case, current through the audio
ground.

**Required approach (choose one):**

1. **Audio isolation transformers** — 600 Ω : 600 Ω, one per direction, in-line on
   the signal path. Simple, passive, effective. Preferred.
2. **Galvanically isolated supply** — power the bridge from an isolated DC-DC
   converter or run it from its own battery/power bank with no other earth
   reference.
3. **Digital isolation** — if using a codec on a separate board, isolate the I²S/I²C
   lines and power the analogue side separately.

Additionally:
- Keep the analogue ground star-connected at one point.
- Do not rely on the USB shield as an audio ground return.
- If the phone must charge while bridged, isolation is **mandatory**, not optional.

---

## 5. ADC / DAC requirements

| Parameter | Requirement | Rationale |
|---|---|---|
| Sample rate | 16 kHz (8 kHz acceptable) | Far end is GSM 900 AMR-NB, ~300–3400 Hz. Higher rates buy nothing. |
| Bit depth | 16-bit | Ample for a 3.4 kHz voice channel |
| Channels | Mono both directions | Call audio is mono |
| ADC input | Line, AC-coupled, ~1 V RMS FS | See §3 |
| DAC output | Line, AC-coupled, level-trimmable | Must be attenuated per §3 |
| SNR | ≥ 80 dB | Comfortably above the cellular channel's own noise floor |
| Latency | < 20 ms in the codec path | Budget below |
| Anti-alias / reconstruction filtering | Built into any modern codec | Do not omit if using discrete converters |

Suitable integrated codecs: ES8388, WM8960, TLV320AIC3204, or any USB Audio Class
adapter with **both** input and output (a headphone-only dongle is useless here).

---

## 6. Echo cancellation

There are two distinct echo paths, and they need different treatment:

| # | Path | Effect | Mitigation |
|---|---|---|---|
| E1 | Bridge DAC → A03 mic input → A03 **sidetone** → A03 earpiece out → bridge ADC → operator | Operator hears themselves delayed | **AEC on the bridge**, reference = transmitted signal, capture = received signal |
| E2 | Vivo speaker → Vivo microphone | Far end hears themselves | WebRTC AEC on the Vivo (already enabled — `setUseHardwareAcousticEchoCanceler(true)`), or simply use a **headset** on the Vivo |

**Design guidance:**
- Run AEC on the bridge with the far-end reference signal (WebRTC AEC3, Speex AEC, or
  the codec's on-chip AEC).
- Add a modest fixed delay estimate; the analogue loop delay is short and stable,
  which makes convergence easy.
- **The cheapest and most reliable mitigation for E2 is a wired headset on the Vivo.**
  Recommend it operationally rather than solving it in software.
- Keep bridge output level as low as the far end tolerates — lower injection level
  directly reduces E1 amplitude.

---

## 7. Audio codec and network transport

| Layer | Choice | Notes |
|---|---|---|
| Codec | **Opus**, 16 kHz mono, 20 ms frames, 16–24 kbps | Already what WebRTC negotiates |
| Transport | **WebRTC / SRTP**, reusing Techee's existing signaling + TURN | Do **not** build a parallel network stack |
| Signaling | Existing Node WS server (`server/src/server.js`) | Bridge registers as a device identity |
| NAT traversal | Existing coturn with ephemeral HMAC credentials | Expect **relay** on BSNL CGNAT |
| Jitter buffer | 40–100 ms adaptive | Mobile links need headroom |

The bridge should appear to the existing architecture as just another authenticated
peer, so pairing, revocation, and audit logging all continue to work unchanged.

---

## 8. Power requirements

| Parameter | Target |
|---|---|
| Supply | 5 V USB (phone charger or power bank) |
| Current | < 500 mA (SBC + codec + Wi-Fi); ESP32-class < 200 mA |
| Continuous operation | Yes — Place A is unattended |
| Brown-out behaviour | Must auto-recover and re-register without human help |
| Optional | Small LiPo for ride-through during power cuts |

Note the practical advantage: the bridge carries **its own data connection**, so it
does not compete with the BSNL SIM's data — which collapses to GPRS/EDGE during a
CSFB voice call (blocker B4).

---

## 9. Security requirements

The bridge is a remote audio tap on a live phone line and must be treated as such:

- Authenticated pairing to a specific controller identity; **no anonymous access**.
- Per-device keypair; reuse Techee's signed-DTLS-fingerprint scheme so the bridge
  cannot be MITM'd.
- DTLS-SRTP for media; TLS/`wss://` for signaling.
- TURN credentials ephemeral and server-minted; **never hard-coded**.
- Secrets in a provisioning file or secure element, **outside source control**.
- Replay-resistant session establishment (nonce challenge, already implemented).
- Session timeout + explicit revocation.
- **Physical indicator (LED) whenever the audio path is live** — the equivalent of the
  mandatory foreground-service notification on Android.
- Connection audit log retained on the bridge.
- Default-deny: no audio flows until a session is authenticated and authorised.

---

## 10. Candidate hardware classes

Classes, not product endorsements — verify current availability and specifications
before purchase.

| Class | Example form | Interface | Pros | Cons |
|---|---|---|---|---|
| **A. Bluetooth HFP bridge** | ESP32 (BT Classic, HFP-HF role) + codec | Bluetooth HFP | **No analogue design; uses the designed path**; wireless | HFP-HF firmware work; pairing robustness |
| **B. SBC + USB audio** | Raspberry Pi Zero 2 W + USB UAC adapter | TRRS via adapter | Full Linux, WebRTC available, easy AEC | Higher power; needs analogue interface per §2–§5 |
| **C. Audio dev board** | ESP32-A1S / ES8388 board | TRRS | Low power, integrated codec | Limited WebRTC stack; more firmware effort |
| **D. Commercial VoIP/Bluetooth gateway** | Bluetooth-to-SIP gateway appliance | Bluetooth HFP | Off-the-shelf, supported | Cost; must still meet §9 security |

**Suggested evaluation order: A → D → B → C.**

---

## 11. Wiring diagram — TRRS bridge (Option 2)

```
        ┌──────────────────── GALAXY A03 (BSNL SIM stays here) ────────────────────┐
        │                          3.5 mm CTIA jack                                │
        └───┬──────────────┬───────────────┬──────────────────┬────────────────────┘
          TIP           RING1           RING2               SLEEVE
        (L out)        (R out)          (GND)            (mic + bias)
            │              │               │                   │
            │              │               │                   │   ┌─────────┐
        [10 kΩ]        [10 kΩ]             │                   ├───┤ 2.2 kΩ  ├── GND
            │              │               │                   │   └─────────┘
            └──────┬───────┘               │                   │   (emulates electret DC load, R2)
                   │                       │                   │
              [ 1 µF ]  AC couple          │              [ 1–10 µF ]  AC couple (R1)
                   │                       │                   │
            ┌──────┴──────┐                │            ┌──────┴──────┐
            │  ISOLATION  │                │            │  ISOLATION  │
            │ XFMR 600:600│                │            │ XFMR 600:600│
            └──────┬──────┘                │            └──────┬──────┘
                   │                       │                   │
              [10:1 divider]               │        ┌──────────┴──────────┐
                   │                       │        │  −40 dB attenuator  │
                   │                       │        │  R_s 10k / R_sh 100 │
                   │                       │        │  + trim pot         │
                   │                       │        └──────────┬──────────┘
                   ▼                       │                   ▲
            ┌──────────────────────────────┴───────────────────┴──────────┐
            │                    AUDIO CODEC (ADC + DAC)                   │
            │              16 kHz / 16-bit mono, AC-coupled I/O            │
            └──────────────────────────────┬───────────────────────────────┘
                                           │ I²S
            ┌──────────────────────────────┴───────────────────────────────┐
            │   BRIDGE CONTROLLER  —  AEC · Opus · DTLS-SRTP · WebRTC       │
            │   Wi-Fi / independent mobile data · LED when audio is live    │
            └──────────────────────────────┬───────────────────────────────┘
                                           │ 5 V USB (isolated supply)
                                    power bank / charger
                                           │
                                 encrypted IP  ⇅  TURN / signaling
                                           │
                                   ┌───────┴────────┐
                                   │  VIVO V25 PRO  │  (headset recommended, §6)
                                   └────────────────┘
```

### Signal budget summary

| Path | From | Attenuation | To |
|---|---|---|---|
| Downlink | A03 tip/ring1, ≤ 1 V RMS | ~ −20 dB + isolation | Codec ADC, ~1 V RMS FS |
| Uplink | Codec DAC, ~1 V RMS | ~ −40 dB + isolation | A03 sleeve, ~1–20 mV RMS |

---

## 12. Bring-up checklist (do these in order)

1. **Measure** the A03's open-circuit sleeve bias voltage and the loaded voltage with
   a known electret. Derive the internal bias resistor. **Do not skip this** — every
   value in §3 depends on it.
2. Confirm the jack is CTIA (bias on **sleeve**, not Ring 2).
3. Build the **downlink** path only. Verify the far end's voice reaches the bridge
   ADC cleanly, with the A03 in another room.
4. Build the **uplink** path with attenuation deliberately set **too low** (extra
   attenuation), then raise it until the far end reports clear speech. Never start
   loud.
5. Verify **no phantom button events** — no calls answered/ended by plugging in or by
   audio transients.
6. Add isolation transformers; confirm hum disappears with the phone charging.
7. Enable AEC; run TEST D (2-minute full-duplex conversation).
8. Re-run the physical protocol's **TEST C** rules R4–R7: A03 acoustically isolated,
   nobody near its microphone, random phrase spoken only into the Vivo. **That is the
   only thing that counts as PASS.**

---

## 13. What this design does *not* solve

Stated explicitly so it is not discovered later:

- It does **not** give remote *screen* access across a reboot — that is blocker B3 and
  is an Android limitation, unrelated to audio.
- It does **not** let the controller dial or answer by itself. Call **control** still
  needs the software path (default-dialer `InCallService` role — see feasibility §19).
  The bridge carries audio only.
- It does **not** work if the A03's headset jack fails or the plug is removed. Option 1
  (Bluetooth HFP) is more robust here.
- It does **not** bypass any Android, Samsung, myOffice, or DRM protection, and must
  never be extended to do so.
