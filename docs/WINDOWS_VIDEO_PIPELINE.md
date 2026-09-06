# Techee for Windows — video pipeline

The W3 risk gate from [ADR 0001](adr/0001-windows-technology-stack.md) was whether
SIPSorcery could hold ~30 FPS with adaptive bitrate. This records what was actually
measured, what the design does about it, and — as of the frame-pump work — what is
genuinely wired end to end.

## Status

**Streaming in-process. Not yet validated against a real Android controller.**

The full chain from Desktop Duplication to VP8 over DTLS-SRTP is implemented and
exercised by automated tests, including against the real Node broker. What has *not*
happened is a real Android device rendering a Windows desktop, and nothing here should
be read as claiming otherwise.

| Piece | State |
|---|---|
| Capture feasibility + cost | ✅ measured |
| Encode cost across the resolution ladder | ✅ measured |
| SDP identity binding (sign + verify, 8 verdicts) | ✅ implemented, adversarially tested |
| Quality ladder + adaptation | ✅ implemented |
| Display geometry + DPI + multi-monitor | ✅ implemented |
| BGRA→I420 conversion (SIMD + scalar reference) | ✅ implemented |
| Frame pump → SIPSorcery video track | ✅ implemented |
| Host session orchestration (join/consent/offer/answer/ICE/hangup) | ✅ implemented, tested against the real broker |
| Bounded back-pressure + capture lifecycle | ✅ implemented |
| Negotiated-codec validation | ✅ implemented |
| Reconnection (authenticated peer recreation) | ✅ implemented |
| **Android controller renders the desktop** | ⛏ **not achieved — external validation required** |
| **TURN / relay path** | ⛏ **not tested** |

## The measurement

Reference hardware: the development machine, Windows 11, 1920×1080 panel at 125%
scaling, .NET 10, `SIPSorceryMedia.Encoders` 10.0.4 (libvpx software VP8).

Each figure is the mean over repeated encodes, **rotating between four genuinely
different captured frames**. An early attempt encoded one static frame repeatedly and
reported ~35 ms; that flatters libvpx badly, because it early-outs on unchanged
macroblocks. The numbers below are the honest ones.

| Resolution | BGRA→I420 | VP8 encode | Total | FPS ceiling |
|---|---|---|---|---|
| 1920×1080 | 18.4 ms | 31.0 ms | **49.3 ms** | 20.3 |
| 1280×720 | 8.6 ms | 21.9 ms | **30.5 ms** | 32.8 |
| 960×540 | 4.1 ms | 12.2 ms | **16.2 ms** | 61.7 |

"FPS ceiling" is convert + encode on one core. Capture, packetisation, DTLS/SRTP and
the network are all on top.

> These figures supersede an earlier set (46.9 / 22.8 / 13.1 ms) that appeared in this
> document and in the profile source comments. The older 720p figure implied roughly 30%
> headroom at 30 FPS; the real figure is about 8%. That difference matters, so the old
> numbers are called out here rather than quietly replaced.

### The verdict

**1080p at 30 FPS is not achievable with software VP8 on this hardware. 720p at 30 FPS
is — but with roughly 8% headroom, not the 30% previously claimed.**

30.5 ms against a 33.3 ms budget fits, and it is not comfortable. A slower machine is
expected to land on 540p, which is why the step down exists rather than being treated as
a failure state.

That is the risk gate answering itself, and it is a design input rather than a failure.
It does not invalidate ADR 0001: the constraint is libvpx's software encoder, not
SIPSorcery's transport. The documented fallback — a native encoder behind the
`Techee.WebRtc` interfaces — remains available if 720p proves insufficient.

### Conversion is not free, and the controller now knows it

The adaptive controller originally judged pressure on **encode time alone**. On this
machine that is badly misleading:

| | encode only | with conversion |
|---|---|---|
| 720p vs 33.3 ms budget | 21.9 ms = **66%** → reads as "hold" | 30.5 ms = **92%** → over the step-down threshold |

Conversion runs on the same thread and spends the same budget, so ignoring it would have
left the controller believing in headroom the frame rate was already spending.
`QualitySample.ProcessingMsPerFrame` is now convert + encode, and that is what
adaptation acts on.

### Two findings that shaped the pipeline

**`EncodeVideoFaster` is unimplemented.** `SIPSorceryMedia.Abstractions.RawImage` takes
a raw pointer, a stride, and accepts `Bgra` — which would have let the encoder read a
mapped GPU staging texture directly, eliminating both the managed copy and the managed
colour conversion. It throws `NotImplementedException` in 10.0.4 for every pixel format.
So `EncodeVideo(byte[], I420)` is the only real entry point, and the BGRA→I420
conversion has to happen in managed code. That is why `PixelConvert` exists and why it
has a vectorised path — at 720p it still costs 8.6 ms, a quarter of the frame budget,
purely rearranging pixels.

**Desktop Duplication reports physical pixels; the desktop rectangle is logical.** On
the reference machine DXGI reports the desktop as 1536×864 while duplication delivers
1920×1080 — the 125% DPI scale. Capture is physical, `SendInput` is logical. Conflating
them puts every remote click 25% away from where the operator aimed. `DisplayGeometry`
keeps the two apart and is tested against mixed-DPI and negative-coordinate layouts.

## The quality ladder

Derived from the measurements, not chosen for tidiness:

| Profile | Resolution | FPS | Target | Role |
|---|---|---|---|---|
| `1080p20` | 1920×1080 | 20 | 6000 kbps | Detail over smoothness. **Operator-selected only.** 49.3 ms against a 50 ms budget — a ceiling, not comfort |
| `720p30` | 1280×720 | 30 | 3500 kbps | **Default.** 30.5 ms/frame measured, ~8% headroom |
| `540p30` | 960×540 | 30 | 1800 kbps | First step down. 16.2 ms — about half the budget |
| `360p20` | 640×360 | 20 | 800 kbps | Floor — degraded but usable over a poor relay |

1080p is published at **20 FPS, not 30**, because 30 is a claim the measurements do not
support.

### Adaptation rules

Three behaviours are deliberate and each has a test:

- **Down fast, up slow.** One bad window steps down; four consecutive good windows are
  needed to climb. Oscillating between profiles is more visible to an operator than
  sitting one step low.
- **Automatic adaptation stops at 720p.** It will not climb into 1080p on its own —
  that would silently trade the operator's frame rate for resolution they never asked
  for. Reachable only through an explicit choice.
- **The floor never disconnects.** At 360p, continued pressure holds rather than
  dropping the session.

Pinning a profile is a preference, not a guarantee: a machine that cannot sustain the
choice is still stepped down rather than delivering a slideshow.

Switching is a reconfiguration, not a teardown — the duplication session and encoder
survive it, and every switch is announced with a keyframe so a receiver is never left
decoding deltas against a frame of the wrong size.

## The frame pump

```
DXGI Desktop Duplication
  → BGRA (mapped staging texture)
  → PixelConvert → I420
  → VpxEncoder → VP8
  → EncodedFrame
  → IEncodedVideoSink
  → PeerVideoSink → TecheePeerConnection.SendVideoFrame
  → SIPSorcery → DTLS-SRTP → remote controller
```

`Techee.Windows.Host` references **no** Techee project and knows nothing about WebRTC,
SDP, or the broker. It produces VP8 and hands it to an `IEncodedVideoSink`. The peer
connection is adapted onto that interface from `Techee.Session`, which is the only
assembly that sees both sides. Signaling concerns never reach the capture thread.

### Back-pressure: latest-frame-wins, depth one

Between encoding and the transport sits a **one-deep slot**. If the sink cannot keep up,
the frame waiting there is displaced by the newer one and counted as superseded. Nothing
is ever queued, so memory cannot grow with session duration — a remote desktop that
buffers is a remote desktop that shows the operator the past.

The send stage runs on its own thread. If encoding were held behind a slow network, the
adaptive controller's encode timings would silently start measuring the link instead of
the CPU, and it would step resolution down for the wrong reason.

Exposed per session: `FramesCaptured`, `FramesEncoded`, `FramesSent`, `FramesDropped`
(capture/encode failures), `FramesSuperseded`, `SendQueueDepth`. The depth is read from
the slot rather than derived arithmetically — a computed backlog has to account for the
frame currently in flight inside the sink, and getting that subtraction subtly wrong is
how a backlog metric starts lying.

The payload is copied into the slot. `VpxEncoder` reuses its *input* buffer, and the
libvpx wrapper makes no written guarantee about the lifetime of the array it *returns*,
so holding that array across the next encode would be betting on an undocumented detail.
The copy is tens of kilobytes of compressed VP8, not the ~1.4 MB an I420 frame would
cost.

### Capture lifecycle

```
no viewer      → no pipeline exists at all
viewer joins   → create source + encoder + pump, attach sink, start
gap/recovery   → detach sink → stop pump → replace peer → re-attach → restart pump
viewer leaves  → detach → stop → dispose encoder and capture device
```

Capture is created on session start, not at registration: a registered host with no
viewer holds no duplication session. Across a reconnect the pump is paused and restarted
while the source and encoder survive — it is the DXGI session and libvpx context that
are expensive to rebuild, not the thread.

`Stop()` waits for the capture thread to actually leave its loop before returning. If
that wait expires the pipeline is marked **wedged**, and a wedged pipeline deliberately
**leaks** its native resources rather than freeing them: releasing a duplication session
or a libvpx context while a thread is still inside them is an access violation that no
catch block saves an unattended host from, whereas leaking costs memory until the
process exits.

## Reconnection: authenticated peer recreation

**Techee for Windows does not support true ICE restart.** An earlier version of this
document stated that `restartIce` was "present, so ICE restart is available for W3's
reconnection work". That was wrong, and it was wrong in a way a method name made easy to
believe.

### The evidence

Traced against the exact pinned dependency, SIPSorcery **10.0.15**:

| Concern | Location | Finding |
|---|---|---|
| `RTCPeerConnection.restartIce()` | `net/WebRTC/RTCPeerConnection.cs:1498` | One line: `_rtpIceChannel.Restart();` |
| `RtpIceChannel.Restart()` | `net/ICE/RtpIceChannel.cs:773` | Disposes timers, clears candidates and checklist, re-inits ICE servers, resets gathering state, calls `StartGathering()`. **Never assigns credentials.** |
| `LocalIceUser` / `LocalIcePassword` | `net/ICE/RtpIceChannel.cs:298-299` | `public readonly string` — **immutable after construction** |
| Credential minting | `net/ICE/RtpIceChannel.cs:393-394` | `Crypto.GetRandomString(...)` in the constructor — the only assignment site |
| SDP serialization | `net/WebRTC/RTCPeerConnection.cs:1280-1281` | Reads those readonly fields directly |

RFC 8445 §9 defines an ICE restart as the generation of **new** ICE credentials. A peer
that re-offers the same `ice-ufrag`/`ice-pwd` has not restarted anything: the far end
cannot distinguish the new gathering round from the old one, and STUN short-term
credentials stay bound to the stale password.

Observed against the unmodified implementation — `ufrag` **AODE → AODE**, `pwd` prefix
**AEAF… → AEAF…**, a byte-identical offer.

The credentials are `readonly` on a privately-held channel. Reaching them would require
reflection against readonly fields, which would also desynchronize the running STUN
integrity checks that key off `LocalIcePassword`. There is no supported path.

### What Techee does instead

Recovery replaces the transport. The peer is disposed and a new one built, which mints
fresh ICE credentials in its constructor.

```
failed transport
  → mark recovering
  → detach sink (stop sending)
  → stop the pump (do not burn CPU encoding into a gap)
  → dispose the old peer
  → RE-AUTHORISE: trust, grant, expiry, lock state, session ownership
  → new peer, fresh ice-ufrag/ice-pwd, fresh signed offer
  → offer/answer, ICE trickle, DTLS
  → re-attach sink, restart the pump
```

**Recovery is not authorisation.** The replacement re-runs the full check against the
same controller the session started with. A controller revoked while the link was down
does not come back; a grant revoked while the link was down does not come back. The peer
is never taken from a message, so no new identity can arrive through this path.

Recovery is single-flight and idempotent: five connection-state callbacks produce one
replacement peer, not five.

`disconnected` does **not** trigger recovery — ICE dips through it routinely and
recovers on its own, and replacing the peer there would turn a NAT rebind into a visible
reconnect. Only `failed` does. The grace period is ICE's own escalation, which keeps one
authority on "is this link dead" rather than racing it with a timer of our own.

The regression test asserting true in-place restart is retained, unweakened, marked
skipped, as an upgrade probe: un-skip it against a newer SIPSorcery, and if it passes,
in-place restart has become possible.

> Android is different. libwebrtc supports a genuine ICE restart via the `IceRestart`
> constraint, and `RtcSession.doIceRestart` uses it. References to ICE restart in the
> Android documentation are accurate; this limitation is Windows-only.

## Video timestamps

Techee does not maintain a timestamp. `RTPSession.SendVideo` documents its argument as
"the duration in RTP timestamp units of the video sample. **This value is added to the
previous RTP timestamp** when building the RTP header" — so Techee supplies an
*increment* and the library accumulates it.

Because **no wall clock is read anywhere on this path**, an NTP correction, a DST change,
or a resume from sleep cannot move a timestamp backwards. A local counter derived from
`DateTime.Now` — the obvious-looking implementation — would break all three.

The increment is `90000 / fps`, clamped to at least 1. Zero would give consecutive frames
identical timestamps, which a decoder reads as one frame arriving in pieces rather than
two frames.

Observed at the receiver over a real link, across a live 720p30 → 1080p20 → 540p30
switch: **0 timestamp regressions**, 272 RTP packets carrying 10 distinct frame
timestamps. More packets than timestamps is correct — one video frame is split across
several RTP packets, all sharing a timestamp.

## SDP and codec validation

Each peer signs its own DTLS fingerprint with its identity key, so a broker that rewrites
SDP invalidates the signature and cannot insert itself as a media endpoint. The eight
verdicts mirror Android's exactly. Two properties worth restating:

- **Absence of evidence is a rejection.** A missing key is `UnknownPeerKey`, never a
  skipped check — the exact bug an earlier Android version had.
- **A third party's SDP does not tear down the session.** `PeerMismatch` and
  `NoExpectedPeer` discard the message and leave the session alone. Tearing down would
  let any registered device kill anyone else's session by sending them an offer, trading
  a takeover bug for a denial-of-service one.

**"The encoder emits VP8" and "the peer agreed to receive VP8" are different claims.** A
session can connect, complete DTLS and carry RTP while the far end discards every packet
because it negotiated a payload type this side never sends — with nothing in any log to
explain the black window. `SdpInspect` reads what was actually negotiated, and the host
records `NegotiatedVideoPayloadType` from the answer, logging the codecs the peer *did*
offer when VP8 is absent.

Two parsing rules carry the weight:

- A codec counts only if its payload type also appears on the `m=` line. An `a=rtpmap`
  naming a payload type the media line never offered is stale text, not a negotiated
  codec.
- A declined media section is answered with **port 0**, not omitted. An SDP can carry a
  complete-looking `m=video` block, a VP8 `rtpmap` and a direction attribute while
  meaning "no video".

`SdpInspect` only observes. It never edits SDP, and its fingerprint accessor delegates to
`SdpAuth` rather than re-parsing — two parsers that could disagree about that line is
how a signature ends up covering something other than what the media is bound to.

## What is actually proven, and by what

| Claim | Evidence |
|---|---|
| Real DXGI capture | `RealCaptureTests` against the live desktop |
| BGRA→I420 correct | `PixelConvertTests`, SIMD checked against a scalar reference |
| Real VP8 encode | `RealCaptureTests`, libvpx frame-type bit verified |
| Encoder output reaches the RTP sender | `PeerVideoSinkTests` |
| Full signaling flow | `HostSessionBrokerTests` against the real `server/src/server.js` |
| Real DTLS-SRTP + VP8 on the wire | loopback: 41,380 bytes sent, 26 RTP packets and 40,834 payload bytes confirmed **received by the far peer** |
| Fresh ICE credentials on reconnect | `IceCredentialTests`, 5 consecutive recreations all distinct |
| Bounded queue | `BackpressureTests`, depth ≤ 1 under a 10× slow sink |

## Honest limitations

- **No Android controller has rendered this.** The receiving peer in every automated test
  is a second SIPSorcery instance, so both ends share an implementation and could share a
  misreading. Interop with libwebrtc's VP8 depacketizer is untested. **This is W3's
  outstanding exit criterion.**
- **TURN is completely untested.** Nothing has traversed a relay. The selected candidate
  pair in every test is `host`. Techee must not be described as Internet-ready remote
  control until this is demonstrated.
- **The loopback test is not a network.** Both peers run in one process; nothing crossed
  a NAT, and no packet loss, jitter or bandwidth limit was involved.
- **Capture and transport have not been proven together end to end.** The DTLS test uses
  a synthetic frame source; real DXGI capture is proven separately. Joining the two in
  one run is the Android acceptance test.
- **No hardware encoding.** libvpx software VP8 only. NVENC/QSV/AMF would change these
  numbers substantially and is the obvious optimisation if 720p proves insufficient.
- **Measured on one machine.** A slower CPU will not hold 720p30; a faster one may hold
  1080p30. The ladder adapts, but the defaults are tuned to this reference point.
- **Desktop Duplication delivers frames only on change.** An idle desktop produces very
  few, which is correct and efficient, but means "FPS" is a ceiling rather than a
  constant rate.
- **Protected content is blacked out** by the OS, not by Techee. Surfaced via
  `ProtectedContentMasked` so it does not appear as a mysterious black region.
