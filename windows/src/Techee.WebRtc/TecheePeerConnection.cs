using System.Text;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Techee.Crypto;
using Techee.Protocol;

namespace Techee.WebRtc;

/// <summary>Where the media is actually flowing.</summary>
public enum LinkState
{
    Connecting,
    Connected,

    /// <summary>
    /// Lost but recoverable. This peer is finished; the session layer is standing up a
    /// replacement. See <see cref="TecheePeerConnection.TransportRecoveryNeeded"/>.
    /// </summary>
    Recovering,

    Closed,
}

/// <summary>What the session is doing, for the diagnostics view.</summary>
public sealed record SessionTelemetry(
    LinkState State,
    bool UsingRelay,
    double RoundTripMs,
    double PacketLossFraction,
    long VideoBytesSent,
    long VideoFramesSent,
    string? LocalCandidateTypes,
    string? RemoteCandidateType);

/// <summary>
/// A Techee peer connection: authenticated WebRTC over SIPSorcery.
/// </summary>
/// <remarks>
/// <para>
/// Everything crossing the broker is signed or verified here. The broker relays offers
/// and answers and could rewrite them freely, so each side signs its own DTLS
/// fingerprint with its identity key and verifies the peer's against the trust store —
/// see <see cref="SdpAuth"/>. A rewritten SDP fails verification and the broker cannot
/// become a media endpoint.
/// </para>
/// <para>
/// The host is always the offerer, matching Android: whichever side is sharing its
/// screen creates the connection, the video track, and the control channel.
/// </para>
/// </remarks>
public sealed class TecheePeerConnection : IAsyncDisposable
{
    /// <summary>Matches Android's channel label so the two interoperate.</summary>
    public const string ControlChannelLabel = "control";

    /// <summary>VP8, dynamic payload type 96. The only codec both platforms share today.</summary>
    private static readonly VideoFormat Vp8 = new(VideoCodecsEnum.VP8, 96);

    /// <summary>The RTP clock rate for WebRTC video. Fixed by RFC 7742, not a tuning knob.</summary>
    public const int VideoClockRate = 90_000;

    /// <summary>
    /// The RTP timestamp increment for one frame at a given frame rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SIPSorcery owns the video clock: <c>SendVideo</c> documents its argument as "the
    /// duration in RTP timestamp units, added to the previous RTP timestamp". Techee
    /// therefore supplies an <i>increment</i>, never an absolute time, and nothing here
    /// reads a wall clock — so no timestamp can move backwards when the system clock
    /// does, whether from NTP correction, a DST change, or resume from sleep.
    /// </para>
    /// <para>
    /// Clamped to at least 1. A zero increment would give consecutive frames identical
    /// timestamps, which a decoder reads as one frame arriving in pieces rather than two
    /// frames — an absurd frame rate is a reason to send slowly, never a reason to emit
    /// an un-decodable stream.
    /// </para>
    /// </remarks>
    public static uint RtpDurationFor(int fps) =>
        (uint)Math.Max(1, VideoClockRate / Math.Max(1, fps));

    private readonly IDeviceIdentity _identity;
    private readonly Func<string, byte[]?> _peerKeyLookup;
    private readonly Action<string>? _log;
    private readonly EndpointMeta? _localMeta;

    private int _helloSent;

    private readonly object _gate = new();
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _control;
    private MediaStreamTrack? _videoTrack;

    private long _videoBytes, _videoFrames;
    private long _videoPacketsReceived, _videoBytesReceived, _videoFramesReceived;
    private bool _disposed;

    // Single-flight recovery. SIPSorcery reports disconnected and failed independently
    // and can report either more than once, so without this a flapping link would ask
    // the session layer for several replacement peers at once.
    private int _recoveryRequested;

    // Which candidate types this side gathered. Diagnostic only — see GetTelemetry for
    // why gathered is not the same as selected.
    private readonly HashSet<string> _localCandidateTypes = [];
    private bool _relayCandidateGathered;

    /// <param name="peerKeyLookup">
    /// Resolves a device ID to its trusted public key, or null. Backed by the trust
    /// store, which returns null for unpaired, pending, and <b>revoked</b> peers — the
    /// last being the case Android currently gets wrong.
    /// </param>
    /// <param name="localMeta">
    /// What to announce in <c>hello</c>. Defaults to a Windows host that shares its
    /// screen and accepts input; a host running with input disabled must pass its own,
    /// so it does not advertise something it has switched off.
    /// </param>
    public TecheePeerConnection(
        IDeviceIdentity identity,
        string peerId,
        Func<string, byte[]?> peerKeyLookup,
        Action<string>? log = null,
        EndpointMeta? localMeta = null)
    {
        _identity = identity;
        PeerId = peerId;
        _peerKeyLookup = peerKeyLookup;
        _log = log;
        _localMeta = localMeta;
    }

    /// <summary>The peer this session belongs to. Fixed at construction, never taken from a message.</summary>
    public string PeerId { get; }

    public LinkState State { get; private set; } = LinkState.Connecting;

    /// <summary>Raised for each validated inbound control message.</summary>
    /// <remarks><c>hello</c> is absorbed by the transport and never raised here.</remarks>
    public event Action<Control>? ControlReceived;

    /// <summary>Raised when the peer announces itself.</summary>
    public event Action<EndpointMeta>? PeerAnnounced;

    /// <summary>
    /// What the peer said it is, or null if it never announced itself.
    /// </summary>
    /// <remarks>
    /// Null means a v0 peer — every shipped Android build before W5. Trustworthy as the
    /// peer's own claim, because it arrived over the authenticated channel rather than
    /// via the broker. Still never an authorization input.
    /// </remarks>
    public EndpointMeta? PeerMeta { get; private set; }

    /// <summary>
    /// The dialect to address the peer in.
    /// </summary>
    /// <remarks>
    /// Legacy until the peer proves otherwise by announcing itself. Defaulting the other
    /// way would send a v1 frame to a shipped Android build, which would drop it.
    /// </remarks>
    public int PeerProtocolVersion { get; private set; } = TecheeProtocol.LegacyVersion;

    /// <summary>This endpoint's own advertisement.</summary>
    public EndpointMeta LocalMeta => _localMeta ?? TecheeProtocol.LocalMeta(
        TecheeProtocol.ProtocolVersion.ToString(), DefaultHostCapabilities);

    public event Action<LinkState>? StateChanged;

    /// <summary>
    /// Raised once when this peer's transport is beyond recovery and the session layer
    /// must replace it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not called an ICE restart. SIPSorcery 10.0.15 cannot perform one:
    /// <c>RtpIceChannel.LocalIceUser</c> and <c>LocalIcePassword</c> are <c>readonly</c>
    /// fields assigned once in the constructor, and <c>RTCPeerConnection.restartIce()</c>
    /// only calls <c>RtpIceChannel.Restart()</c>, which re-gathers candidates while
    /// keeping those credentials. RFC 8445 §9 requires fresh credentials, so a restart
    /// in this library is indistinguishable from a re-offer and recovery does not
    /// actually happen — proven by
    /// <c>IceCredentialTests.An_in_place_ice_restart_issues_fresh_ice_credentials</c>.
    /// </para>
    /// <para>
    /// Recovery is therefore authenticated peer recreation: this connection is disposed
    /// and a new one is built, which mints new ICE credentials in its constructor. The
    /// replacement re-authenticates from scratch — recovery is never authorisation.
    /// </para>
    /// <para>
    /// Raised at most once per connection. A peer that has asked for recovery is spent;
    /// it never asks again and never becomes usable again.
    /// </para>
    /// </remarks>
    public event Action? TransportRecoveryNeeded;

    /// <summary>Raised for each locally gathered ICE candidate, for the session layer to relay.</summary>
    public event Action<RTCIceCandidate>? LocalIceCandidate;

    /// <summary>Creates the connection and, as host, the video track and control channel.</summary>
    /// <param name="forceRelay">
    /// Discards host and server-reflexive candidates so the session can only succeed
    /// through TURN.
    /// </param>
    /// <remarks>
    /// <paramref name="forceRelay"/> exists to make the relay path <b>provable</b>. With
    /// the default policy a session on the same LAN will select a direct pair and never
    /// touch TURN, so a passing test says nothing about whether TURN works — and the
    /// first time it matters would be a real session across CGNAT. Forcing relay turns
    /// "TURN is configured" into "TURN carried this session".
    /// </remarks>
    public async Task InitializeAsync(
        IReadOnlyList<RTCIceServer> iceServers,
        bool isHost,
        bool forceRelay = false)
    {
        var pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = [.. iceServers],
            iceTransportPolicy = forceRelay ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all,
        });

        if (isHost)
        {
            _videoTrack = new MediaStreamTrack(Vp8, MediaStreamStatusEnum.SendOnly);
            pc.addTrack(_videoTrack);

            // Only the host creates the channel, matching Android, so there is exactly
            // one and no negotiation over who owns it.
            _control = await pc.createDataChannel(ControlChannelLabel, new RTCDataChannelInit { ordered = true });
            BindControlChannel(_control);
        }
        else
        {
            pc.addTrack(new MediaStreamTrack(Vp8, MediaStreamStatusEnum.RecvOnly));
            pc.ondatachannel += channel =>
            {
                if (channel.label != ControlChannelLabel) return;
                _control = channel;
                BindControlChannel(channel);
            };
        }

        pc.onconnectionstatechange += HandleConnectionStateChange;

        // Inbound video, counted. This is the receiving end's only honest evidence that
        // media actually crossed the link: the sender's own counters prove a frame was
        // handed to the RTP stack, not that anything arrived.
        pc.OnRtpPacketReceived += (_, mediaType, packet) =>
        {
            if (mediaType != SDPMediaTypesEnum.video) return;
            Interlocked.Increment(ref _videoPacketsReceived);
            Interlocked.Add(ref _videoBytesReceived, packet?.Payload?.Length ?? 0);
            if (packet is not null) ObserveTimestamp(packet.Header.Timestamp);
        };

        // Reassembled frames, for a controller that wants to display one. Counting
        // packets says bytes arrived; only a whole frame can be decoded, and until W5
        // nothing on this side ever asked for one.
        //
        // Raised on SIPSorcery's receive thread, so a subscriber that blocks here stalls
        // reception. The viewer hands off immediately and decodes elsewhere.
        pc.OnVideoFrameReceived += (_, timestamp, frame, _) =>
        {
            if (frame is null || frame.Length == 0) return;
            Interlocked.Increment(ref _videoFramesReceived);

            try
            {
                VideoFrameReceived?.Invoke(frame, timestamp);
            }
            catch (Exception e)
            {
                // A broken subscriber must not take down the receive loop, and must not
                // be able to make the link look failed when it is healthy.
                _log?.Invoke($"[peer] video frame subscriber threw: {e.GetType().Name}");
            }
        };

        pc.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            lock (_gate)
            {
                _localCandidateTypes.Add(candidate.type.ToString());
                if (candidate.type == RTCIceCandidateType.relay) _relayCandidateGathered = true;
            }
            LocalIceCandidate?.Invoke(candidate);
        };

        lock (_gate) _pc = pc;
    }

    /// <summary>Maps SIPSorcery's connection state onto Techee's link state and recovery policy.</summary>
    /// <remarks>Internal rather than private so the recovery policy is testable without a network.</remarks>
    internal void HandleConnectionStateChange(RTCPeerConnectionState state)
    {
        switch (state)
        {
            case RTCPeerConnectionState.connected:
                SetState(LinkState.Connected);
                break;

            case RTCPeerConnectionState.disconnected:
                // Transient. ICE frequently dips through disconnected and comes back on
                // its own — a NAT rebind, a lost Wi-Fi frame — so replacing the peer
                // here would turn a hiccup into a visible reconnect and a re-handshake.
                //
                // The grace period is ICE's own: SIPSorcery escalates to failed when its
                // connectivity checks genuinely give up, and that is what triggers
                // recovery below. Borrowing the library's timeout keeps one authority on
                // "is this link dead" instead of racing it with a timer of our own.
                SetState(LinkState.Recovering);
                break;

            case RTCPeerConnectionState.failed:
                // Terminal for this peer. Nothing in SIPSorcery 10.0.15 can refresh the
                // ICE credentials in place, so the only real recovery is a replacement
                // connection built by the session layer.
                SetState(LinkState.Recovering);
                RequestRecoveryOnce();
                break;

            case RTCPeerConnectionState.closed:
                SetState(LinkState.Closed);
                break;
        }
    }

    /// <summary>Asks the session layer for a replacement peer, at most once.</summary>
    /// <remarks>
    /// Idempotent and lock-free: the interlocked exchange is the whole guard, so five
    /// connection-state callbacks produce one request and never five replacement peers.
    /// Deliberately outside <see cref="_gate"/> — the handler runs on a SIPSorcery
    /// network thread and will dispose this connection, which takes that lock.
    /// </remarks>
    private void RequestRecoveryOnce()
    {
        if (Interlocked.Exchange(ref _recoveryRequested, 1) != 0) return;

        _log?.Invoke("[rtc] transport is unrecoverable; requesting a replacement peer");
        TransportRecoveryNeeded?.Invoke();
    }

    /// <summary>
    /// Records one inbound video RTP timestamp and checks it against the previous one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Repeated timestamps are <b>normal and not counted as a fault</b>: one video frame
    /// is usually split across several RTP packets, and every packet of that frame
    /// carries the same timestamp. Distinct values are what correspond to frames, so
    /// those are counted separately.
    /// </para>
    /// <para>
    /// A 32-bit RTP timestamp at 90 kHz wraps roughly every 13 hours 15 minutes, which a
    /// naive comparison would report as a huge backwards jump on any long unattended
    /// session. A decrease larger than half the range is therefore read as a wrap rather
    /// than a regression — the standard RFC 3550 interpretation.
    /// </para>
    /// </remarks>
    private void ObserveTimestamp(uint timestamp)
    {
        lock (_timestampGate)
        {
            if (!_haveTimestamp)
            {
                _haveTimestamp = true;
                _lastTimestamp = timestamp;
                _videoFrameTimestamps = 1;
                return;
            }

            if (timestamp == _lastTimestamp) return;

            // Unsigned difference: forward progress and wraparound both come out small,
            // a genuine regression comes out larger than half the range.
            var delta = unchecked(timestamp - _lastTimestamp);

            if (delta > uint.MaxValue / 2)
            {
                _videoTimestampRegressions++;
                _log?.Invoke($"[rtc] video RTP timestamp went backwards: {_lastTimestamp} -> {timestamp}");
                return;
            }

            _lastTimestamp = timestamp;
            _videoFrameTimestamps++;
        }
    }

    private readonly object _timestampGate = new();
    private uint _lastTimestamp;
    private bool _haveTimestamp;
    private long _videoTimestampRegressions;
    private long _videoFrameTimestamps;

    /// <summary>
    /// Inbound video timestamps that moved backwards. Must stay zero.
    /// </summary>
    /// <remarks>
    /// Wraparound is excluded, so any non-zero value here is a real ordering fault that
    /// would show up to a viewer as stalled or scrambled playback.
    /// </remarks>
    public long VideoTimestampRegressions
    {
        get { lock (_timestampGate) return _videoTimestampRegressions; }
    }

    /// <summary>Distinct inbound video timestamps, i.e. frames rather than packets.</summary>
    public long VideoFrameTimestamps
    {
        get { lock (_timestampGate) return _videoFrameTimestamps; }
    }

    /// <summary>Video RTP packets received from the peer.</summary>
    /// <remarks>
    /// The receiving side's proof that media crossed the link. A packet count is what
    /// distinguishes "the sender thinks it sent" from "the receiver actually got it",
    /// and it is meaningful whether or not anything subscribes to
    /// <see cref="VideoFrameReceived"/>.
    /// </remarks>
    public long VideoPacketsReceived => Interlocked.Read(ref _videoPacketsReceived);

    /// <summary>Payload bytes of received video RTP.</summary>
    public long VideoBytesReceived => Interlocked.Read(ref _videoBytesReceived);

    /// <summary>Whole video frames reassembled from inbound RTP.</summary>
    /// <remarks>
    /// Counted independently of <see cref="VideoFrameReceived"/> so the number is
    /// available to a headless peer that never subscribes. Compare it against
    /// <see cref="VideoPacketsReceived"/> to tell "nothing arrived" from "packets
    /// arrived but never assembled into a frame" — the latter is what a packetisation
    /// disagreement looks like, and the two failures have nothing in common.
    /// </remarks>
    public long VideoFramesReceived => Interlocked.Read(ref _videoFramesReceived);

    /// <summary>
    /// Raised with each reassembled inbound video frame and its RTP timestamp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The encoded frame, not pixels — decoding is the subscriber's business, because a
    /// headless recorder wants the elementary stream and only a viewer wants a bitmap.
    /// </para>
    /// <para>
    /// <b>Raised on the receive thread.</b> A subscriber that blocks here stalls
    /// reception for every stream on this connection, so hand off and return. Exceptions
    /// are caught and logged rather than allowed to escape into SIPSorcery's loop.
    /// </para>
    /// </remarks>
    public event Action<byte[], uint>? VideoFrameReceived;

    /// <summary>Whether this peer has already asked to be replaced.</summary>
    /// <remarks>Lets the session layer and its tests assert the single-flight property.</remarks>
    public bool RecoveryRequested => Volatile.Read(ref _recoveryRequested) != 0;

    private void SetState(LinkState state)
    {
        if (State == state) return;
        State = state;
        _log?.Invoke($"[rtc] {state}");
        StateChanged?.Invoke(state);
    }

    private void BindControlChannel(RTCDataChannel channel)
    {
        // PROTOCOL.md §5.2: a v1 endpoint announces itself as soon as the channel opens,
        // and a peer that never sends `hello` is a v0 peer. Until W5 this host announced
        // nothing, so an Android controller had no way to discover it was talking to
        // Windows and correctly assumed the legacy dialect — which meant the whole v1
        // vocabulary (wheel, keyboard, explicit buttons) was unreachable from a real
        // controller no matter what the host implemented.
        channel.onopen += SendHello;

        // Already open by the time we bind, on the answering side.
        if (channel.readyState == RTCDataChannelState.open) SendHello();

        channel.onmessage += (_, _, data) =>
        {
            // Runs on a network thread. An exception here would take down the process,
            // and on an unattended host that process is the only way back in — the same
            // hazard Android hardened its DataChannel callback against.
            try
            {
                var control = ControlCodec.DecodeRaw(data);
                if (control is null)
                {
                    // Malformed, unknown, oversized, or a future protocol version. All
                    // dropped silently; nothing is echoed back to the sender.
                    return;
                }

                // Absorbed here rather than passed on: `hello` is transport-level
                // negotiation, and the session layer has no decision to make about it.
                if (control is Control.Hello announcement)
                {
                    PeerMeta = announcement.Meta;
                    PeerProtocolVersion = TecheeProtocol.ProtocolVersion;

                    // Acknowledge an announcement, never an acknowledgement — two peers
                    // that each replied to the other's reply would ping-pong forever.
                    if (!announcement.Ack) SendControl(new Control.Hello(LocalMeta, Ack: true));

                    PeerAnnounced?.Invoke(announcement.Meta);
                    return;
                }

                ControlReceived?.Invoke(control);
            }
            catch (Exception e)
            {
                _log?.Invoke($"[rtc] dropped a control frame: {e.GetType().Name}");
            }
        };
    }

    /// <summary>
    /// Announces this endpoint's platform and capabilities on the control channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sent once per channel. Idempotence matters because the answering side may find
    /// the channel already open when it binds, and a duplicate <c>hello</c> would have
    /// the peer re-derive the same conclusion at best and re-render its UI at worst.
    /// </para>
    /// <para>
    /// <b>This is description, not authorization.</b> It arrives over the E2E
    /// authenticated channel so it is a trustworthy statement of what the peer
    /// <i>claims</i>, which is enough to decide whether to show a scroll gesture — and
    /// nowhere near enough to decide whether to honour one. That decision stays with
    /// the grant.
    /// </para>
    /// </remarks>
    private void SendHello()
    {
        if (Interlocked.Exchange(ref _helloSent, 1) != 0) return;

        // Retried on the next open if the send failed, rather than leaving the peer
        // permanently believing we are a v0 endpoint.
        if (!SendControl(new Control.Hello(LocalMeta, Ack: false)))
            Volatile.Write(ref _helloSent, 0);
    }

    /// <summary>
    /// What a Windows host can do, when the caller does not say.
    /// </summary>
    /// <remarks>
    /// Input is included because W4 implements it. A host started with input disabled
    /// passes its own metadata in rather than relying on this — advertising a capability
    /// that has been switched off would have the controller offer an affordance that
    /// silently does nothing.
    /// </remarks>
    private static readonly string[] DefaultHostCapabilities =
        ["screen.share", "input.receive", "display.multi"];

    // ---- signalling ----

    /// <summary>Creates an offer and the signature that binds it to this identity.</summary>
    public (string Sdp, string Signature) CreateOffer()
    {
        var pc = Require();
        var offer = pc.createOffer(null);
        pc.setLocalDescription(offer).GetAwaiter().GetResult();

        return (offer.sdp, SdpAuth.Sign(_identity, "offer", PeerId, offer.sdp));
    }

    /// <summary>Creates an answer to an already-accepted offer.</summary>
    public (string Sdp, string Signature) CreateAnswer()
    {
        var pc = Require();
        var answer = pc.createAnswer(null);
        pc.setLocalDescription(answer).GetAwaiter().GetResult();

        return (answer.sdp, SdpAuth.Sign(_identity, "answer", PeerId, answer.sdp));
    }

    /// <summary>
    /// Authenticates and applies inbound SDP.
    /// </summary>
    /// <remarks>
    /// Fail-closed. A verdict other than <see cref="SdpVerdict.Ok"/> is never applied,
    /// and <see cref="SdpAuth.ShouldTearDown"/> decides whether it also ends the
    /// session — a third party's SDP is discarded without touching it, so nobody can
    /// kill someone else's session by sending them an offer.
    /// </remarks>
    public SdpVerdict AcceptRemoteDescription(string type, string from, string sdp, string? signatureB64)
    {
        var pc = Require();

        var verdict = SdpAuth.Verify(
            type, from, PeerId, _identity.DeviceId, sdp,
            TecheeCrypto.UnB64(signatureB64),
            _peerKeyLookup(PeerId));

        if (verdict != SdpVerdict.Ok)
        {
            // Log the claimed sender, not just the verdict: for an injected SDP the
            // claimed identity is the evidence.
            _log?.Invoke($"[rtc] rejected {type} from '{from}' (expected '{PeerId}'): {verdict}");
            if (SdpAuth.ShouldTearDown(verdict)) SetState(LinkState.Closed);
            return verdict;
        }

        var description = new RTCSessionDescriptionInit
        {
            type = type == "offer" ? RTCSdpType.offer : RTCSdpType.answer,
            sdp = sdp,
        };

        var result = pc.setRemoteDescription(description);
        if (result != SetDescriptionResultEnum.OK)
        {
            _log?.Invoke($"[rtc] setRemoteDescription failed: {result}");
            return SdpVerdict.BadSignature;
        }

        return SdpVerdict.Ok;
    }

    /// <summary>
    /// Adds a remote ICE candidate.
    /// </summary>
    /// <remarks>
    /// <paramref name="from"/> is checked against the session's peer. Android discards
    /// the sender here, so any registered device can inject candidates into an unrelated
    /// live session; this does not repeat that.
    /// </remarks>
    public bool AddRemoteIceCandidate(string from, string candidate, string? sdpMid, ushort sdpMLineIndex)
    {
        if (from != PeerId)
        {
            _log?.Invoke($"[rtc] ignored an ICE candidate from '{from}' (expected '{PeerId}')");
            return false;
        }

        var pc = Require();
        pc.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid,
            sdpMLineIndex = sdpMLineIndex,
        });
        return true;
    }

    // There is deliberately no RestartIce here.
    //
    // SIPSorcery 10.0.15's RTCPeerConnection.restartIce() calls RtpIceChannel.Restart(),
    // which re-gathers candidates but cannot touch LocalIceUser/LocalIcePassword because
    // they are readonly fields set in the RtpIceChannel constructor. The resulting offer
    // is byte-identical, so exposing it would be advertising a recovery mechanism that
    // silently does nothing. Recovery is authenticated peer recreation, owned by the
    // session layer and signalled by TransportRecoveryNeeded.

    // ---- media ----

    /// <summary>
    /// Sends one encoded video frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from the capture pump. Duration is in 90 kHz RTP units, the clock rate
    /// WebRTC video uses.
    /// </para>
    /// <para>
    /// <b>SIPSorcery owns the RTP clock.</b> <c>SendVideo</c> takes a per-frame duration
    /// and accumulates the outgoing timestamp itself, so there is no wall-clock reading
    /// here and nothing that can step backwards across a profile switch or a replacement
    /// peer. A local timestamp counter used to be incremented alongside these calls; it
    /// was never read by anything and has been removed rather than left to imply Techee
    /// controls the clock.
    /// </para>
    /// </remarks>
    public void SendVideoFrame(byte[] encoded, int fps)
    {
        RTCPeerConnection? pc;
        lock (_gate) pc = _pc;

        if (pc is null || State != LinkState.Connected) return;

        var duration = RtpDurationFor(fps);
        try
        {
            pc.SendVideo(duration, encoded);
            Interlocked.Add(ref _videoBytes, encoded.Length);
            Interlocked.Increment(ref _videoFrames);
        }
        catch (Exception e)
        {
            // A send failure is a link problem, not a reason to stop capturing. The
            // connection state change will drive recovery.
            _log?.Invoke($"[rtc] video send failed: {e.GetType().Name}");
        }
    }

    /// <summary>Sends a control message in the dialect the peer speaks.</summary>
    /// <remarks>
    /// Returns false when the command has no representation in a legacy peer's dialect.
    /// Silence is correct there: encoding a Windows power action as something an older
    /// Android build would misread is worse than not sending it.
    /// </remarks>
    public bool SendControl(Control control, int peerProtocolVersion = TecheeProtocol.ProtocolVersion)
    {
        RTCDataChannel? channel;
        lock (_gate) channel = _control;

        if (channel is null || channel.readyState != RTCDataChannelState.open) return false;

        var json = ControlCodec.Encode(control, peerProtocolVersion);
        if (json is null) return false;

        try
        {
            channel.send(Encoding.UTF8.GetBytes(json));
            return true;
        }
        catch (Exception e)
        {
            _log?.Invoke($"[rtc] control send failed: {e.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Sends an arbitrary string on the control channel, bypassing the encoder.
    /// </summary>
    /// <remarks>
    /// <b>Test surface only</b>, and internal so it cannot become a production path.
    /// Proving that a malformed frame cannot reach the injector requires sending one,
    /// and every public way of putting bytes on this channel goes through
    /// <see cref="ControlCodec.Encode"/> — which, correctly, will not produce a
    /// malformed frame. A hostile controller is under no such constraint, so the test
    /// must not be either.
    /// </remarks>
    internal bool SendControlRaw(string json)
    {
        RTCDataChannel? channel;
        lock (_gate) channel = _control;

        if (channel is null || channel.readyState != RTCDataChannelState.open) return false;

        try
        {
            channel.send(Encoding.UTF8.GetBytes(json));
            return true;
        }
        catch (Exception e)
        {
            _log?.Invoke($"[rtc] raw control send failed: {e.GetType().Name}");
            return false;
        }
    }

    // ---- telemetry ----

    /// <summary>
    /// A snapshot for the diagnostics view and for the adaptive controller.
    /// </summary>
    /// <remarks>
    /// Stays on this device and travels only to the paired controller. It is never
    /// reported to any third party, and it contains no screen or clipboard content.
    /// </remarks>
    public SessionTelemetry GetTelemetry()
    {
        RTCPeerConnection? pc;
        lock (_gate) pc = _pc;

        var loss = 0.0;
        var jitterMs = 0.0;

        // RTCP reception reports are what the far end says it actually received, which
        // is the only honest source for loss — the sender's own counters cannot know.
        if (pc?.VideoRtcpSession?.ReceptionReport is { } report)
        {
            try
            {
                var sample = report.GetSample(0);
                // FractionLost is an 8-bit fixed-point fraction: the RFC 3550 encoding
                // of "lost since the last report", not a percentage.
                loss = sample.FractionLost / 256.0;
                // Jitter is in RTP timestamp units; video runs at 90 kHz.
                jitterMs = sample.Jitter / 90.0;
            }
            catch (Exception)
            {
                // No report yet, or a malformed one. Zero is the right answer for
                // "we have not heard otherwise" — better than a fabricated number the
                // adaptive controller would act on.
            }
        }

        lock (_gate)
        {
            return new SessionTelemetry(
                State,
                // Honest naming: this is what was GATHERED, not what was SELECTED.
                // SIPSorcery does not expose the nominated pair, so its absence proves
                // the session could not have used TURN, while its presence does not
                // prove it did. Android's IceInspect carries the same caveat.
                UsingRelay: _relayCandidateGathered,
                // Not RTT: SIPSorcery's reception report does not carry enough to
                // compute one without the sender-report round trip. Reported as jitter
                // so nothing downstream mistakes it for latency.
                RoundTripMs: jitterMs,
                PacketLossFraction: loss,
                Interlocked.Read(ref _videoBytes),
                Interlocked.Read(ref _videoFrames),
                _localCandidateTypes.Count == 0 ? null : string.Join(",", _localCandidateTypes.Order()),
                RemoteCandidateType: null);
        }
    }

    private RTCPeerConnection Require()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pc ?? throw new InvalidOperationException(
                "The peer connection has not been initialised; call InitializeAsync first.");
        }
    }

    public ValueTask DisposeAsync()
    {
        RTCPeerConnection? pc;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            pc = _pc;
            _pc = null;
            _control = null;
        }

        try { pc?.close(); }
        catch (Exception) { /* already gone */ }

        SetState(LinkState.Closed);
        return ValueTask.CompletedTask;
    }
}
