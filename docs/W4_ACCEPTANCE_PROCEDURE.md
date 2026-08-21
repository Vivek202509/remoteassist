# W4 manual acceptance procedure

**Status after the W4 audit.** Most of what this document originally described as manual
is now automated and has been executed. What remains genuinely manual is listed first, so
nobody repeats work the suite already does.

| Criterion | Status | Why |
|---|---|---|
| **D7** mouse click / drag | **HOST PASS** · controller acceptance **NYT** | Proven against a real window (A32) and a real channel (A28); no Android controller has driven it |
| **D7** wheel | **NOT EXERCISED** | `pointer.wheel` has no v0 form; the shipped Android controller cannot send one |
| **D8** keyboard | **HOST PASS** · controller acceptance **NYT — W5** | Typed into a real `EDIT` control (A32); **no controller can send `keyboard.*` yet** |
| **D9** DPI 100 % | **PASS** | Verified on the reference machine (A32) |
| **D9** DPI 125 / 150 % | **NYT** | Requires a scaled display; §4 below |
| **D10** multi-monitor | **NYT** | Requires a second monitor; §5 below |
| **D12** view-only cannot inject | **PASS** | Automated over the real broker (A28) |
| **D13** mid-session revocation | **PASS** | Automated (A28) |
| **D14** `--no-input` | **PASS** | Automated (A28, A29) |
| **D15** malformed frames | **PASS** | Automated (A28, A24) |
| **D16** secure desktop | **PARTIAL** | Probe verified on hardware; a physical UAC prompt is still manual — §6.4 |

So the manual work left is **§4 (scaled displays)**, **§5 (second monitor)**,
**§6.4 (UAC)**, and the whole of §2 and §3 repeated with a **real Android controller**
once one exists.

**Rule, inherited from `GALAXY_A03_REAL_DEVICE_TEST_MATRIX.md`:** a compiling build, a
passing unit test, or an emulator run is never evidence for a row marked *device
required*. Do not mark a row PASS from anything but a direct observation.

---

## 0. What the automated tests already settle, and what they do not

The Windows suite covers input thoroughly, and it is worth being precise about the line.

**Settled without hardware** (`PeerControlHandlerTests`, `KeyMapTests`,
`ControlRateLimiterTests` — 54 tests):

- Which events a given control frame produces, and in what order.
- That a view-only grant, an expired grant, a revoked grant and a locked workstation each
  refuse input on the very next frame.
- Coordinate arithmetic at every DPI scale and across monitors with negative origins.
- Modifier ordering, the scan-code table, and that unmapped keys are dropped.
- Rate ceilings, including refill and a backwards-running clock.

**Settled on real hardware** (`RealInputTests` A27, `RealApplicationInputTests` A32):

- `SendInput` accepts the events. This matters most for interop: a wrong `INPUT` layout
  or `cbSize` makes the call return zero and inject nothing, while every managed test
  still passes.
- **An ordinary window receives the input as ordinary `WM_*` messages**, at the client
  coordinates it was aimed at — clicks, right-clicks, a drag with ten intermediate moves,
  distinct key down and up, and `héllo` read back out of a real `EDIT` control.
- The extended-key flag round-trips: `ArrowUp` arrives as VK_UP, not VK_NUMPAD8.
- A held modifier is released by teardown, and `GetAsyncKeyState` agrees.
- The secure-desktop probe costs about 5 µs, so running it per command is affordable.

**Settled over a real broker and a real data channel** (`ControlOverBrokerTests`, A28):
view-only refusal, control-enabled execution, mid-session revocation and restoration,
`--no-input`, malformed frames, and release-on-hangup.

**Not settled by any of it:**

- **The reference machine runs at 100% scale and has one monitor.** DPI and multi-monitor
  arithmetic is exercised on hardware only at 100%, and otherwise against fabricated
  `DisplayInfo` values. D9 and D10 exist because that arithmetic is where this class of
  bug lives.
- **No Android controller has sent anything.** The controller in A28 is a second
  SIPSorcery peer in the same process. It proves the host's half of the contract; it
  cannot prove Android's.
- **No controller has sent a v1 frame at all.** The shipped Android controller emits only
  `tap`, `swipe` and `nav.key`. Wheel, keyboard and explicit button down/up have never
  crossed a data channel from a real device.
- **A real UAC prompt has not been driven.** §6.4 remains manual, deliberately.

---

## 1. Prerequisites

Everything in `W3_ACCEPTANCE_PROCEDURE.md` §1, plus:

| Requirement | Notes |
|---|---|
| **A grant carrying `input.control`** | `techee-host trust` no longer confers it by default; pass `--control` |
| **An interactive desktop session** | Same constraint as capture. `SendInput` from a Session 0 service reaches nothing |
| A scratch application to be driven | Notepad and a browser are enough for D7 and D8 |

> **Run this on a machine you do not mind being driven.** A controller with
> `input.control` has full mouse and keyboard. Use `--no-input` on the host if you only
> mean to repeat the W3 video tests.

### CLI additions since W3

```
techee-host trust --pub BASE64 [--name NAME] [--grant-id ID] [--control] [--store DIR]
techee-host run   --broker URL [--force-relay] [--profile NAME] [--no-input] [--store DIR]
```

- `--control` — grant `input.control` alongside `screen.view`.
- `--no-input` — refuse input at the transport level whatever the grant says. The host
  also stops advertising the `input.receive` capability.

### Reading the counters

`run` prints an `input` line once any control frame has arrived:

```
           input | accepted 412 executed 412 | refused 0 rate-limited 0 queue-dropped 0
                 | unsupported 0 failed 0
```

Each number distinguishes a different cause of "my clicks do nothing":

| Counter | Means |
|---|---|
| `refused` | The grant does not permit it. Check `--control` |
| `rate-limited` | The controller exceeded a §5.4 ceiling. Expected to be non-zero during fast drags |
| `queue-dropped` | The worker fell behind — the backstop behind the rate limiter. Should be zero |
| `unsupported` | Decoded and authorized, but Windows has no action. `nav.key` lands here |
| `failed` | Injection threw. Should be zero |
| `accepted` but not `executed` | The worker is blocked, or the secure desktop is up |

---

## 2. D7 — mouse

1. Pair or seed trust as in `W3_ACCEPTANCE_PROCEDURE.md` §2, but add `--control`.
2. Start the host, connect the controller, confirm the desktop is visible.
3. Open Notepad on the host and maximise it.

### 2.1 Click accuracy

Tap the host's Start button, then a menu entry. Then tap a specific word in Notepad.

- The caret must land **in the word tapped**, not several characters away.
- Repeat near each of the four screen edges. Edge accuracy is where an off-by-one in the
  0..65535 conversion shows up, and the centre of the screen will not reveal it.

### 2.2 Drag

Drag across a line of text in Notepad.

- Text must be **selected**, not merely have the caret moved. A selection proves the
  intermediate moves were delivered; a drag that teleports is frequently registered as a
  click.
- Drag a window by its title bar from one side of the screen to the other. It must follow
  the pointer rather than jumping at the end.

### 2.3 Wheel

**Requires a v1 controller** — `pointer.wheel` has no v0 form and the shipped Android
build will not send it. If none is available, record D7's wheel element as **NOT
EXERCISED** rather than PASS.

With one: scroll a long document up and down, and confirm the direction is not inverted.

### Record

```
Date / tester:
Host machine / GPU / Windows build:
Controller:
Display scale:                        Monitors:
Click accuracy, centre:               Click accuracy, edges:
Drag selects text?      y/n           Window drag follows?  y/n
Wheel tested?           y/n           Direction correct?    y/n
Counters at end (accepted/executed/refused/rate-limited/queue-dropped/unsupported/failed):
```

---

## 3. D8 — keyboard

**Requires a v1 controller.** `keyboard.keyDown`, `keyboard.keyUp` and `keyboard.text`
have no v0 form. The shipped Android controller has no keyboard sender at all, so this
row is **not reachable** until the W5 Windows controller exists or a v1 harness is
written for the purpose.

When one is available:

1. **Plain text** — type `the quick brown fox` into Notepad. It must appear once,
   in order, with no dropped or doubled characters.
2. **Unicode** — type `héllo 日本語 🌍`. All three must render. This exercises
   `KEYEVENTF_UNICODE` including a surrogate pair, which is the case that produces two
   replacement characters if the pair is split across `SendInput` calls.
3. **Modifiers** — `Ctrl`+`A` then `Ctrl`+`C` then `Ctrl`+`V`. Select-all, copy and paste
   must each take effect.
4. **A non-US layout, if one is available.** Switch the *host* to AZERTY and send
   `KeyA` again. It must produce `q` — the character that physical key bears on that
   layout. If it produces `a`, the implementation is sending virtual keys and the
   scan-code path has regressed.
5. **Sticky-key check** — after every test above, type on the host's own keyboard. If
   characters come out capitalised or a shortcut fires, a modifier was left down.

### Record

```
Plain text correct?      y/n
Unicode incl. emoji?     y/n
Ctrl+A / Ctrl+C / Ctrl+V effective?   y/n
Non-US layout tested?    y/n     Layout:            Result:
Modifiers released cleanly?   y/n
```

---

## 4. D9 — DPI scaling

Needs one display, reconfigured three times. **Log out and back in between changes** —
Windows applies scaling to already-running processes inconsistently, and a stale host
process is a misleading test rather than a failing one.

For each of 100%, 125% and 150%:

1. Set the scale, log out, log in, restart the host.
2. Confirm the startup line reports both geometries, e.g.
   `capturing \\.\DISPLAY1: 1920x1080 physical, 1536x864 logical (scale 1.25)`.
3. Tap the four corners and the centre. Each must land within a few pixels.

**This is the test the whole `DisplayGeometry` type exists for.** Capture delivers
physical pixels; `SendInput` takes logical ones. Code that conflates them is exactly 25%
out at 125% scaling — correct at the top-left corner and increasingly wrong toward the
bottom-right, which is the signature to look for.

### Record

```
| Scale | Reported physical | Reported logical | Corner accuracy | Centre accuracy |
| 100%  |                   |                  |                 |                 |
| 125%  |                   |                  |                 |                 |
| 150%  |                   |                  |                 |                 |
```

---

## 5. D10 — multi-monitor

Needs a second monitor. Arrange it **to the left of** or **above** the primary, so its
virtual-desktop coordinates are negative. A second monitor to the right exercises almost
nothing: naive implementations work there and fail on negative origins.

1. Restart the host. It captures the primary by default and reports
   `input enabled across 2 display(s)`.
2. Confirm input lands on the **captured** display, not the primary, and not spread
   across the union.
3. Tap the extreme corners of the captured display. Nothing may land on the other
   monitor — the "last addressable pixel" rule is what stops the far edge spilling over.
4. If the controller can select a display, switch to the secondary and repeat.
5. Un-skip `RealInputTests.A_multi_monitor_desktop_normalises_against_the_union_not_the_primary`
   — it runs automatically once a second monitor is present, and it checks the union
   normalisation independently of anything observed by eye.

### Record

```
Monitor arrangement (which is left/above):
Virtual desktop bounds reported:
Input lands on the captured display?   y/n
Corners stay on the captured display?  y/n
Display switching tested?              y/n
Multi-monitor unit test now running?   y/n     Result:
```

---

## 6. Cross-cutting checks

Run these once, during any of the above.

### 6.1 A view-only grant cannot control — **automated (A28)**

Re-seed the grant **without** `--control`, reconnect, and try to click.

- Nothing must move.
- `refused` must climb.

This is the single most important negative result in W4: it is the difference between a
permission system and a decoration. It is now asserted automatically over a real data
channel, with all seven currently-reachable commands. Repeat it by hand only when a real
Android controller is available.

### 6.2 Revocation takes effect mid-session — **automated (A28)**

With a session live and control working, revoke the grant at the host
(`techee-host list` to find it; edit or remove it in the store) and click again.

- Input must stop **without reconnecting**.
- `refused` must climb.

Restoring the grant re-enables the next command, again without a reconnect: authorization
is re-read per command, so there is no session state to rebuild. Both directions are
automated, as is the equivalent through the *trust* store rather than the grant store.

### 6.3 Locking the workstation

With `RequireUnlock` set on the grant, press Win+L on the host mid-session.

- Input must stop while locked and resume after unlocking.
- Video may continue; that is `screen.view`, which the downgrade retains.

### 6.4 The secure desktop — **manual, by design**

This one cannot be automated without either weakening UAC or scripting a real elevation
prompt, and the first is out of the question. What *is* automated: the probe reports
"available" on an ordinary interactive desktop, costs ~5 µs, and the handler injects
nothing whenever the probe says unavailable.

Trigger a UAC prompt on the host — launching Task Manager as administrator will do it.

- The host log must print
  `secure desktop active — remote input temporarily unavailable`.
- Clicks must not reach the prompt. **A build that can click a UAC prompt is a bug, not
  a feature**, and is a release blocker.
- After dismissing the prompt locally, input must resume with no reconnect.

Record the result here and in D16.

### 6.5 Rate limiting does not disconnect

Move the controller's pointer as fast as possible for thirty seconds.

- `rate-limited` climbs.
- The session stays connected, and normal input works immediately afterwards.
- `queue-dropped` should stay at zero; a non-zero value means the worker cannot keep up
  with the ceiling and the ceiling is the wrong number.

---

## 7. Recording results

Update `docs/CROSS_PLATFORM_TEST_MATRIX.md`:

- **D7–D10** — `NYT` → `PASS` / `FAIL` / `NOT EXERCISED`, with the date, the hardware,
  and for D9 the three scale factors actually tested.
- **G6, G7, G8** — the mouse, keyboard and scrolling definition-of-done rows.

Then update the status lines that currently disclaim these:

- `README.md` — the `Remote input` row and the `Windows remote input` component row.
- `docs/WINDOWS_ARCHITECTURE.md` — the Status paragraph.

**Do not describe Techee as remote control for Windows until D7 passes.** Until then the
demonstrated capability is remote viewing with an untested input path.

---

## 8. What remains outstanding regardless

Passing all four completes W4's criteria. It does not cover:

- **`nav.key`.** Deliberately unimplemented on Windows. The shipped Android controller's
  three navigation buttons do nothing against a Windows host, and will keep doing nothing
  — see `WINDOWS_ARCHITECTURE.md`.
- **Clipboard.** W8. `clipboard.*` decodes and is ignored.
- **Power control.** W7. `system.*` decodes and is ignored.
- **Monitor hot-plug mid-session.** The display layout is snapshotted when the session
  starts. Plugging in a monitor mid-session leaves absolute coordinates normalised
  against the old union until the next session. The W6 service should watch for
  display-change notifications.
- **Injection while the screen is not being watched.** Nothing ties input to a live video
  frame; a session with stalled capture can still drive the machine blind.
- **Sustained-load behaviour.** As with W3, a short test finds stalls, not a slow leak
  over a working day.

---

## 9. Running the real-hardware tests

Two tiers, so a normal test run never disturbs the machine.

**Default — safe, runs in CI.** The only real input is a cursor move to the position the
cursor is already at, which proves the interop with no visible effect. Nothing types,
nothing takes the foreground, the pointer does not appear to move.

```
dotnet test windows/Techee.Windows.slnx -c Release
```

**Opt-in — intrusive.** Creates a topmost window, takes the foreground, moves the pointer
and presses keys. Every case restores the cursor afterwards, but do not run it while
typing something you care about.

```
TECHEE_REAL_INPUT=1 dotnet test windows/Techee.Windows.slnx -c Release
```

The intrusive set, all of which skip without the variable:

| Test | What it does to the machine |
|---|---|
| `RealInputTests.An_absolute_move_lands_within_a_pixel_of_where_it_was_aimed` | Moves the pointer to screen centre, restores it |
| `RealInputTests.A_keystroke_is_accepted_by_the_OS` | Presses and releases left Shift |
| All 8 `RealApplicationInputTests` | Topmost window, foreground steal, clicks, a drag, typed text, key presses |

One further test, `RealInputTests.A_multi_monitor_desktop_normalises_against_the_union_not_the_primary`,
skips for a different reason — it needs a second monitor — and runs automatically once
one is attached. It is not intrusive.
