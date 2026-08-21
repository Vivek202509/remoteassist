# The Windows controller harness (`techee-ctl`)

`techee-ctl` is the controller counterpart to `techee-host`. It exists so the acceptance
rows that need a *controller* can be executed at all.

Until it existed, the only controller in the tree was a private `Controller` class inside
`windows/tests/Techee.Session.Tests/ControlOverBrokerTests.cs`. That made three matrix
rows unrunnable rather than merely untested:

- **D6** — Windows controller → Windows host. No controller, so **BLOCKED**.
- **D7** — wheel accuracy. `pointer.wheel` has no v0 form, so the shipped Android
  controller cannot express one. Unreachable.
- **D8** — keyboard. The shipped Android controller has **no keyboard sender at all**.
  Unreachable.

The host side of D7 and D8 has been proven against a real window receiving real `WM_*`
messages since A32. What was missing was something able to send a notch or a keystroke.

> **This does not change D4.** D4 needs a real Android handset rendering a Windows
> desktop, because both ends of a `techee-host` → `techee-ctl` session run SIPSorcery and
> the same libvpx build. They can therefore agree on a misreading of VP8 packetisation and
> both be wrong. Only a libwebrtc decoder settles that. See
> [`W3_ACCEPTANCE_PROCEDURE.md`](W3_ACCEPTANCE_PROCEDURE.md) §0.

---

## 1. What it is and is not

A console harness with a viewer window, not a product. No reconnection, no pairing UI, no
session browser. What it does have is every counter that distinguishes one failure from
another, visible while the session is live.

Trust is seeded from the command line, exactly as on the host. That bypasses the pairing
exchange — so **it is not evidence for D3** — but it does *not* bypass SDP verification. A
host whose offer is not signed by the key seeded here is refused, which is the property
D6 is actually about.

The controller keeps **its own identity and its own store**:

| | Host | Controller |
|---|---|---|
| CNG key | `Techee.DeviceIdentity` | `Techee.ControllerIdentity` |
| Store | `%LOCALAPPDATA%\Techee` | `%LOCALAPPDATA%\Techee\controller` |

That separation is not cosmetic. Both apps can run on one machine, and sharing an
identity would have the second registration displace the first socket at the broker —
which is the documented exit-6 failure, arrived at by accident.

---

## 2. CLI reference

```
techee-ctl identity [--store DIR] [--key NAME]
techee-ctl trust    --pub BASE64 [--name NAME] [--store DIR]
techee-ctl list     [--store DIR]
techee-ctl connect  --broker URL --host DEVICEID
                    [--pair] [--view-only] [--headless]
                    [--record FILE] [--snapshot FILE] [--seconds N]
                    [--force-relay] [--store DIR] [--key NAME]
```

| Exit | Meaning |
|---|---|
| 0 | Clean shutdown, and media was decoded |
| 1 | No verb given; usage printed |
| 2 | Bad arguments, or no trusted key for the host |
| 3 | Registration failed — broker unreachable or rejected |
| 4 | `--force-relay` requested but the broker offered no TURN server |
| 5 | The dial was refused (`join-failed`, or the host declined consent) |
| 6 | The host's SDP did not authenticate |
| 7 | The session ran but **no frame ever decoded** |

Exit 7 is the one worth wiring into a script. A session that connects, exchanges ICE, and
carries no decodable video looks entirely healthy from the outside.

---

## 3. Single-machine smoke test — do this first

Both apps run on one laptop. They keep separate CNG keys and separate stores precisely so
they can, and it costs about two minutes.

It is worth doing before involving a second machine because it eliminates most of what
goes wrong: a wrong key seeded, a firewall, a mistyped device ID, a broker that is not
listening. What remains after it passes is the genuinely two-machine part — the network.

> **If your SDK is a user-local install** (`~/.dotnet`, which is how a machine without
> admin rights gets one), set `DOTNET_ROOT` before running either binary:
>
> ```powershell
> $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
> ```
>
> The apphost checks PATH, `DOTNET_ROOT`, the registry, and `C:\Program Files\dotnet` —
> a `~/.dotnet` install is none of them, so without this the executables fail with
> "You must install .NET to run this application" on the machine that just built them.
> `Techee-Lab.ps1` sets it for you.

Three terminals, all in the repo root:

```powershell
# 1 — broker
cd server; $env:PORT="8080"; node src/server.js

# 2 — host
$H = "windows\src\Techee.Windows.HostApp\bin\Release\net10.0\techee-host.exe"
$C = "windows\src\Techee.Windows.ControllerApp\bin\Release\net10.0-windows\techee-ctl.exe"

& $H identity                       # note the public key
& $C identity                       # note the public key
& $H trust --pub <C public key> --name "loopback controller" --control
& $C trust --pub <H public key> --name "loopback host"
& $H run --broker ws://127.0.0.1:8080

# 3 — controller
& $C connect --broker ws://127.0.0.1:8080 --host <H device id> --pair `
     --record loopback.ivf --snapshot loopback.png
```

You should see your own desktop, recursively, in the viewer.

**What this proves:** registration, pairing, SDP signing and verification, ICE, DTLS-SRTP,
VP8 encode → RTP → depacketise → decode, the control channel, and grant enforcement.

**What it does not prove:** anything about a network. Every candidate pair will be `host`,
nothing crosses a NAT, and the loopback path hides MTU and packet-loss behaviour
entirely. It is a smoke test, not row E1.

---

## 4. Laptop-to-laptop procedure

Two Windows laptops, call them **H** (host) and **C** (controller). The broker runs on H
here to keep it to two machines; a third machine works the same way.

> **`windows/scripts/Techee-Lab.ps1` automates all of §4.** It wraps exactly the commands
> below — nothing more — so use it if you want the steps done for you, and read this
> section if you want to know what it did.
>
> ```powershell
> # H
> .\Techee-Lab.ps1 -Role Host -Action build
> .\Techee-Lab.ps1 -Role Host -Action broker           # opens the port, prints the URL
> .\Techee-Lab.ps1 -Role Host -Action card             # -> host.peercard.json
> .\Techee-Lab.ps1 -Role Host -Action trust -Card .\controller.peercard.json -Control
> .\Techee-Lab.ps1 -Role Host -Action run
>
> # C
> .\Techee-Lab.ps1 -Role Controller -Action build
> .\Techee-Lab.ps1 -Role Controller -Action card       # -> controller.peercard.json
> .\Techee-Lab.ps1 -Role Controller -Action trust -Card .\host.peercard.json
> .\Techee-Lab.ps1 -Role Controller -Action run -Broker ws://<H-IP>:8080 -Pair
> ```
>
> `-Action status` prints what is configured and what is missing, and every run explains
> its exit code rather than leaving you to look it up.
>
> A **peer card** is a small JSON file holding one machine's device ID, public key and
> short fingerprint. It exists so a base64 key can be copied as a file rather than
> retyped. It carries no secret — a public key lets you verify and address a peer, never
> impersonate it — and importing one still prints the TEST-SEEDED warning, because it is
> a transport for a key and not a pairing exchange.

### 4.1 Build, on both

```powershell
dotnet build windows/Techee.Windows.slnx -c Release
```

Binaries land in `windows/src/Techee.Windows.HostApp/bin/Release/net10.0/techee-host.exe`
and `windows/src/Techee.Windows.ControllerApp/bin/Release/net10.0-windows/techee-ctl.exe`.

### 4.2 Start the broker, on H

```powershell
cd server
npm ci
$env:PORT="8080"
npm start
```

It binds `0.0.0.0`. Open the port to the LAN:

```powershell
New-NetFirewallRule -DisplayName "techee-broker" -Direction Inbound -LocalPort 8080 -Protocol TCP -Action Allow
```

Note H's LAN address (`ipconfig`). Everything below uses `ws://<H-IP>:8080`.

### 4.3 Exchange identities

On **H**:

```powershell
techee-host identity
```

On **C**:

```powershell
techee-ctl identity
```

Each prints a device ID and a base64 public key. Now cross-seed — **the host's key goes to
the controller and vice versa**:

On **H**, trust the controller and grant it control:

```powershell
techee-host trust --pub <CONTROLLER PUBLIC KEY> --name "Laptop C" --control
```

Omit `--control` for a view-only grant, which is how you exercise D12.

On **C**, trust the host so its offer authenticates:

```powershell
techee-ctl trust --pub <HOST PUBLIC KEY> --name "Laptop H"
```

Both will print the TEST-SEEDED warning. That is correct and it is not noise: the safety
number on a seeded peer is meaningless, and the marker is written into the store so a
seeded peer cannot later pass for a paired one.

### 4.4 Run the host, on H

```powershell
techee-host run --broker ws://<H-IP>:8080
```

Wait for `host-open : OK` and `waiting for a controller`.

> On a machine you are actually using, add `--no-input`. The controller can then watch but
> not drive, which is the safe way to run the video half.

### 4.5 Dial, on C

First time only, register the pairing edge — the broker links both directions from one
call, so the host does not need to do it too:

```powershell
techee-ctl connect --broker ws://<H-IP>:8080 --host <HOST DEVICE ID> --pair `
  --record session.ivf --snapshot last-frame.png
```

Subsequent runs can drop `--pair`.

The viewer opens. The status bar carries the numbers that matter:

```
Connected | 1920x1080 | rx frames 412 (superseded 7) | pkts 5533 |
Vp8Decoder(decoded=405, empty=7, failed=0) | rtt 3ms loss 0.0% | direct | dialect v1
```

### 4.6 What to record

| Observation | Where to read it |
|---|---|
| A picture appeared, at the host's resolution | the window, and `--snapshot` |
| Media genuinely crossed the link | `video packets` / `video bytes` in the summary |
| Frames assembled, not just bytes | `video frames` vs `video packets` |
| The frames were decodable | `Vp8Decoder(decoded=…)` — **`failed=0` is the claim** |
| The dialect is v1, not a legacy downgrade | `dialect v1` in the status bar |
| Direct or relayed | `direct` / `RELAY`, and `candidates` in the summary |

Keep `session.ivf`. Play it with something that is not Techee:

```powershell
ffplay session.ivf
ffprobe -show_streams session.ivf
```

That is the part of the evidence which does not depend on this codebase being right.

---

## 5. Driving the rows

### D6 — Windows controller → Windows host

Steps 3.1–3.5 with `--control` granted. The row passes when a picture renders and the
session summary shows `decoded > 0` with `failed = 0`.

### D7 — mouse click / drag / wheel

Click, right-click, drag, and scroll in the viewer. On the host, watch the `input` line:

```
input | accepted 43 executed 43 | refused 0 rate-limited 0 queue-dropped 0 | unsupported 0 failed 0
```

**Wheel is the row's outstanding half.** It is sent here in notches — Windows reports 120
units per notch and the viewer divides by `SystemInformation.MouseWheelScrollDelta` — and
this is the first controller that can send one at all.

Drag deserves specific attention. A press must land on the picture, but a release is
clamped to the nearest edge, and losing pointer capture mid-drag releases everything held.
Those three behaviours exist so a drag that wanders into the letterbox padding cannot
leave a button held down on someone else's machine.

### D8 — keyboard including modifiers and Unicode

Two different paths, and the row needs both:

- **Key positions.** Focus the picture and type. The viewer sends W3C `code` names —
  `KeyA` is "the key where A sits on US QWERTY" — so the host's layout decides the
  character. Check that `ArrowUp` arrives as VK_UP and not VK_NUMPAD8; that is A25's
  extended-key flag, now driven remotely.
- **Literal text.** The box above the picture sends `keyboard.text`. Type `techee w5
  héllo` and press Enter. This is the path A32 was testing, now reachable from a
  controller.

### D12 — a view-only session cannot inject

Re-run 3.3 on the host without `--control`. Everything in the viewer should be refused,
and the host's `input` line should show `refused` climbing while `executed` stays at 0 and
video keeps running.

### E1 / E2 — network path

Same LAN gives you `direct` and a `host` candidate pair — that is E1, and it is the first
time the codebase has selected one over a real NIC rather than in-process loopback.

For E2, pass `--force-relay` **on both ends**. Either end alone proves nothing: the other
side can still offer a direct candidate and the pair will use it.

---

## 6. Known limits

- **Not a D4 substitute.** Stated at the top, restated here because it is the mistake this
  document exists to prevent.
- **No reconnection.** A host re-offer after transport recovery is handled, but a broker
  socket that drops ends the session. Reconnection belongs to the W6 service.
- **Modifier side is assumed left.** WinForms raises `Keys.ShiftKey` rather than
  `LShiftKey`/`RShiftKey` for a bare press, and the distinction is not recoverable from
  the event. It changes nothing about what a shortcut does.
- **D9 is still hardware.** The viewer's mapping is scale-independent and unit-tested at
  100/125/150 %, but the host still needs a physically scaled display for the row.
- **One host at a time**, and no display selection — `display.select` is not sent.
