# W3 manual acceptance procedure

Two criteria remain for W3, and neither can be proven from the repository:

- **D4** — an Android controller renders a live Windows desktop.
- **E2** — a session succeeds through TURN.

Everything else is covered by automated tests. These two need two real machines and an
observed result.

**Rule, inherited from `GALAXY_A03_REAL_DEVICE_TEST_MATRIX.md`:** a compiling build, a
passing unit test, or an emulator run is never evidence for a row marked *device
required*. Do not mark a row PASS from anything but a direct observation.

---

## 0. Why the automated tests are not enough

The Windows solution already proves DXGI → I420 → VP8 → WebRTC → DTLS-SRTP end to end,
with signaling through the real broker, and confirms at the receiver that the packets
arrived. It is worth being precise about what that does **not** settle:

- **Both ends are SIPSorcery.** The receiving peer in every automated test is a second
  SIPSorcery instance. If SIPSorcery packetises VP8 in a way libwebrtc reads differently,
  both ends of the test share the misreading and it passes. Only a real Android decoder
  settles this.
- **Loopback is not a network.** Both peers run in one process. The selected candidate
  pair is always `host`. Nothing crossed a NAT, and no TURN server was involved.
- **Capture and transport have never run together.** The DTLS test uses a synthetic frame
  source; real DXGI capture is proven separately. Joining them in one run happens for the
  first time here.

---

## 1. Prerequisites

### Windows host

| Requirement | Notes |
|---|---|
| Windows 10/11, .NET 10 SDK | |
| A GPU supporting Desktop Duplication | Any modern discrete or integrated GPU |
| **An interactive desktop session** | Desktop Duplication cannot capture from a Session 0 service or over a disconnected RDP session. Run this at the physical console or in a connected RDP session |
| Built host | `dotnet build windows/Techee.Windows.slnx -c Release` |

The host executable is `techee-host` (`windows/src/Techee.Windows.HostApp`). It is a
console harness for acceptance testing, not the W6 service.

### CLI reference

Derived from `Program.cs`; these are the only arguments the executable accepts.

```
techee-host identity [--store DIR]
techee-host trust    --pub BASE64 [--name NAME] [--grant-id ID] [--store DIR]
techee-host list     [--store DIR]
techee-host run      --broker URL [--force-relay] [--profile NAME] [--store DIR]
```

`--profile` is one of `1080p20`, `720p30` (default), `540p30`, `360p20`.
`--store` defaults to `%LOCALAPPDATA%\Techee`, DPAPI-protected.
`--force-relay` takes no value.

Exit codes — the harness fails fast rather than idling in a half-started state:

| Code | Meaning |
|---|---|
| 0 | Clean shutdown (Ctrl+C) |
| 1 | No verb given; usage printed |
| 2 | Bad arguments — missing `--broker`, malformed URL, unknown `--profile`, identity load failure |
| 3 | Registration failed — broker unreachable or the registration was rejected |
| 4 | `--force-relay` requested but the broker offered no TURN server |
| 5 | `host-open` did not take effect |
| 6 | Broker registration was lost while running (see below) |

**Exit 6 matters during a long acceptance run.** If the broker connection drops, the
host is unreachable and will never receive a controller — but from the outside it still
looks healthy. Rather than idle in that state it prints `LOST BROKER REGISTRATION` and
exits. The two common causes are a broker restart and a second `techee-host` process
registering the same device identity, which makes the broker displace the first socket.
**Do not run two instances against one broker with the same store.**

Reconnection is deliberately not implemented here; that belongs to the W6 service.

### Android controller

A debug build of the Techee app on a real handset. **An emulator does not count** —
emulator networking hides exactly the NAT behaviour E2 exists to test.

### Broker and TURN

The broker must be reachable from both devices. For E2 the TURN server must also be
reachable from both, and configured to match:

```bash
TURN_HOST=turn.example.net      # must resolve and be reachable from BOTH devices
TURN_SECRET=<shared secret>     # must match infra/turnserver.conf
TURN_STUN_PORT=3478
TURN_TLS_PORT=5349
```

`TURN_SECRET` must be identical on the broker and in `turnserver.conf`. A mismatch
produces credentials the TURN server rejects, and the symptom is an ICE failure with no
obvious cause.

---

## 2. Setup (both tests)

### 2.1 Print the host identity

```
techee-host identity
```

Record the **device id** and **short fingerprint**. The short fingerprint is what the
operator compares on the controller.

### 2.2 Seed trust

The acceptance harness trusts a controller directly, by public key:

```
techee-host trust --pub <controller-base64-SPKI> --name "Acceptance controller"
```

> ### Acceptance-harness shortcut. Does not prove normal Techee pairing.
>
> **This bypasses interactive pairing on purpose.** It isolates the video test from the
> pairing UX so a failure means one thing rather than two. It is **not** evidence for
> matrix row **D3** (real-device pairing), which remains separately outstanding.
>
> The peer is stored with a **zero shared secret**, so its safety number is meaningless.
> **Do not compare safety numbers in these tests.**
>
> Seeded entries are marked `TEST-SEEDED` in the store itself, so the marking survives
> restarts rather than being a one-off console message. `trust`, `list` and `run` all
> print a prominent warning whenever such an entry exists.
>
> **Session security is unaffected.** Seeding populates the trust store only. Every
> cryptographic session check still runs against the real key: SDP signature
> verification, DTLS fingerprint binding, the expected-peer-identity check, and the
> grant check. The shared secret is used solely to derive the safety number for the
> pairing UX, which is why its absence degrades that display and nothing else.

Verify:

```
techee-host list
```

Expect the controller listed as `Trusted` with a `screen.view` grant.

### 2.3 Register the pairing edge at the broker

The broker refuses a paired-direct dial unless it knows about the pairing
(`server.js`, `case 'join'` → `arePaired`). Both sides must have registered it. If the
controller's join fails with `not-paired`, this step is missing.

---

## 3. Test D4 — Android controller renders the Windows desktop

**Path under test: LAN direct** (record it as such; this is not a TURN test).

### Steps

1. On Windows:

   ```
   techee-host run --broker wss://broker.example.net --profile 720p30
   ```

   Confirm it prints `registered`, the capture resolution, and `waiting for a controller`.

2. On Android, connect to the host by device ID.

3. Observe the host log. Expect, in order:

   ```
   [session] Authorizing
   [session] Negotiating
   [rtc] Connected
   [session] Connected
   [session] video negotiated: VP8 on payload type NN
   ```

4. **Look at the phone.** This is the criterion. Everything above is the host's account
   of itself.

5. Move a window, open the Start menu, drag the mouse. Confirm the phone shows the
   motion, not a frozen first frame.

6. Let it run **five minutes**. Record the counter line every 30 s or so.

7. Disconnect and reconnect Wi-Fi on the phone. Confirm recovery.

8. Hang up from the controller. Confirm the host returns to `Closed` and releases capture.

### Record

```
Date / tester:
Host:            CPU, GPU, Windows build, display resolution + DPI scale
Controller:      handset model, Android version
Network path:    LAN direct  /  same Wi-Fi  /  routed
Profile:         720p30
Achieved FPS:                    (from the host counter line)
Convert ms:                      encode ms:            total ms:
Processing pressure:             % of frame budget
Frames captured / encoded / sent / superseded / dropped:
Send queue depth (must never exceed 1):
Negotiated VP8 payload type:
Local candidate types gathered:
Packet loss (RTCP):
Host CPU % during the session:
Time to first frame on the phone:
Latency, subjective (mouse move → screen update):
Reconnect: recovered? y/n   time to recover:   peer recreations:
Teardown clean? y/n
```

### Pass criteria

All of the following, or the row is not PASS:

1. The phone displays the Windows desktop.
2. The image **moves** — window drags and mouse motion are visible.
3. `[session] video negotiated: VP8 on payload type NN` appears; **not**
   `NOT NEGOTIATED`.
4. The session survives five minutes without a black screen or a stall.
5. `SendQueueDepth` never exceeds 1.
6. Reconnect after the Wi-Fi interruption restores the picture without re-consent.
7. Teardown leaves the host in `Closed` with capture released.

### If it fails

| Symptom | Likely cause |
|---|---|
| `NOT NEGOTIATED` in the log | Android did not accept VP8. Compare the answer's `m=video` payload types against the offer's |
| Connected, but the phone is black | The most likely genuine interop failure: SIPSorcery's VP8 packetisation vs libwebrtc's depacketizer. Capture the host counters — if `FramesSent` is climbing, the bytes are leaving and the problem is at the decoder |
| Never reaches `Connected` | ICE. Record the gathered candidate types; if only `host`, the two devices cannot reach each other directly and this needs E2 |
| `join-failed: not-paired` | Step 2.3 |
| Capture fails to start | Desktop Duplication cannot run in Session 0 or a disconnected RDP session |

---

## 4. Test E2 — TURN relay

The point of this test is that **it cannot accidentally pass**. `--force-relay` sets
SIPSorcery's ICE transport policy to `relay`, which discards host and server-reflexive
candidates. If the session connects at all, TURN carried it.

Without that flag, two devices on the same Wi-Fi will select a direct pair and never
touch TURN — and a passing test would say nothing about the relay path.

### Steps

1. Confirm the broker issues TURN credentials. On the host:

   ```
   techee-host run --broker wss://broker.example.net --force-relay
   ```

   The startup banner lists the ICE servers the broker offered. If none is a `turn:` URL
   the host **exits with an error** rather than proceeding to fail confusingly.

2. Ideally, put the devices on genuinely different networks — phone on mobile data, host
   on wired. This exercises the real CGNAT case. If that is not possible, `--force-relay`
   still proves the relay path, and you record that the network separation was simulated.

3. Connect from Android and confirm the desktop appears.

4. Run **five minutes**, recording counters.

5. Record the selected candidate type.

### Record

```
Date / tester:
Network separation:   real (mobile data ↔ wired)  /  simulated (--force-relay on one LAN)
TURN host:                            TLS port:
Credentials issued by the broker? y/n     Accepted by coturn? y/n
Candidate types gathered (host):
Candidate types gathered (controller):
Selected pair type:                   ← must be relay
Resolution / profile:
Achieved FPS:
Bitrate (from bytes sent over elapsed):
RTT:                                  Packet loss:
Host CPU %:
Subjective usability:
```

### Pass criteria

1. The broker issued TURN credentials and coturn accepted them.
2. The session reached `Connected` **with relay-only policy**, which is itself the proof
   the relay carried it.
3. Relay candidates appear in the gathered set.
4. The desktop is visible and usable on the phone.
5. Five minutes without a stall.

### A caveat on "selected pair"

`SessionTelemetry.UsingRelay` reports what was **gathered**, not what was **selected** —
SIPSorcery does not expose the nominated pair, and Android's `IceInspect` carries the
same limitation. So:

- Its absence proves the session could **not** have used TURN.
- Its presence does **not** prove it did.

Under `--force-relay` this stops mattering: a connected session could only have used a
relay pair, because the others were never candidates. That is the whole reason the flag
exists. If you need independent confirmation, check the coturn logs for an allocation
matching the session window.

---

## 5. Recording results

Update `docs/CROSS_PLATFORM_TEST_MATRIX.md`:

- **D4** — `NYT` → `PASS` or `FAIL`, with the date, the two devices, and the measured
  FPS and processing figures.
- **E2** — `NYT` → `PASS` or `FAIL`, noting whether the network separation was real or
  simulated.
- **E1** — record if a LAN direct pair was observed during D4.
- **E3** — record if the reconnect step in D4 §3.7 was exercised.

Then update the status lines that currently disclaim these:

- `README.md` — the paragraph beginning *"A Windows desktop has not yet been seen on a
  real Android device"*.
- `docs/WINDOWS_VIDEO_PIPELINE.md` — the Status table and the first two bullets of
  *Honest limitations*.

**Do not describe Techee as Internet-ready remote control until E2 passes.** Until then
the only demonstrated path is a direct one.

---

## 6. What remains outstanding regardless

Passing both tests completes W3's video criteria. It does not cover:

- **D3** — real-device pairing with matching safety numbers. §2.2 deliberately bypasses
  pairing, so these tests are not evidence for it.
- **W4** — mouse and keyboard injection. Not started, deliberately.
- **Sustained-load behaviour.** Five minutes finds stalls and leaks; it does not find a
  slow leak over a working day. A longer soak is worth doing once the short test passes.
