using SIPSorcery.Net;
using Techee.Crypto;
using Techee.Protocol;
using Techee.Signaling;
using Techee.Store;
using Techee.WebRtc;
using Techee.Windows.Host;

namespace Techee.Session;

/// <summary>What the host session is doing.</summary>
public enum HostSessionState
{
    /// <summary>Not registered. Nothing is captured and no transport exists.</summary>
    Idle,

    /// <summary>Registered and announced; waiting for a controller to dial in.</summary>
    Listening,

    /// <summary>A controller has dialled in and is being authorised.</summary>
    Authorizing,

    /// <summary>Authorised; offer/answer and ICE are in flight.</summary>
    Negotiating,

    /// <summary>Media is flowing.</summary>
    Connected,

    /// <summary>The transport died and a replacement peer is being built.</summary>
    Recovering,

    /// <summary>Finished. Terminal.</summary>
    Closed,
}

/// <summary>Why a join request was refused. Recorded so a refusal is diagnosable.</summary>
public enum JoinRefusal
{
    /// <summary>Not refused.</summary>
    None,

    /// <summary>The broker sent a join-request with no usable controller ID.</summary>
    Malformed,

    /// <summary>Another controller already owns this host's session.</summary>
    AlreadyOwned,

    /// <summary>The controller is not a trusted paired peer — unpaired, pending, or revoked.</summary>
    NotTrusted,

    /// <summary>No usable unattended grant, and this host has no attended consent UI.</summary>
    NoGrant,
}

/// <summary>
/// The Windows host's end of a Techee session.
/// </summary>
/// <remarks>
/// <para>
/// Follows Android's <c>SessionManager</c>/<c>RtcSession</c> semantics deliberately and
/// speaks the broker's existing message set — <c>host-open</c>, <c>join-request</c>,
/// <c>consent</c>, <c>offer</c>, <c>answer</c>, <c>ice</c>, <c>restart</c>,
/// <c>hangup</c>. There is no Windows-only signaling dialect: the same Android
/// controller that talks to an Android host talks to this.
/// </para>
/// <para>
/// <b>The host is always the offerer</b>, matching Android. An inbound offer is
/// therefore never expected and is discarded.
/// </para>
/// <para>
/// <b>The broker is untrusted.</b> Its <c>unattended</c>, <c>grantId</c>,
/// <c>permissions</c> and <c>peerMeta</c> fields are display hints, never authorisation.
/// Every decision is re-made here against this machine's own trust store and grant
/// store, which is why a compromised broker cannot manufacture a session.
/// </para>
/// <para>
/// <b>One session at a time.</b> A second controller dialling in while a session is
/// owned is refused rather than allowed to displace the first, so session ownership
/// cannot be taken by whoever dials last.
/// </para>
/// </remarks>
public sealed class WindowsHostSession : IAsyncDisposable
{
    private readonly IDeviceIdentity _identity;
    private readonly SignalingClient _signaling;
    private readonly TrustStore _trust;
    private readonly GrantStore _grants;
    private readonly Func<VideoPipeline> _pipelineFactory;
    private readonly Func<IInputInjector>? _injectorFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<bool> _workstationLocked;
    private readonly Action<string>? _log;

    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private TecheePeerConnection? _peer;
    private PeerVideoSink? _sink;
    private PeerControlHandler? _control;
    private IInputInjector? _injector;
    private VideoPipeline? _pipeline;

    private string? _controllerId;
    private string? _grantId;
    private volatile bool _disposed;
    private int _recovering;

    public WindowsHostSession(
        IDeviceIdentity identity,
        SignalingClient signaling,
        TrustStore trust,
        GrantStore grants,
        Func<VideoPipeline> pipelineFactory,
        Func<DateTimeOffset>? clock = null,
        Func<bool>? workstationLocked = null,
        Action<string>? log = null,
        Func<IInputInjector>? injectorFactory = null)
    {
        _identity = identity;
        _signaling = signaling;
        _trust = trust;
        _grants = grants;
        _pipelineFactory = pipelineFactory;
        _injectorFactory = injectorFactory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _workstationLocked = workstationLocked ?? (() => false);
        _log = log;
    }

    public HostSessionState State { get; private set; } = HostSessionState.Idle;

    /// <summary>
    /// Restricts ICE to relay candidates, so a session can only succeed through TURN.
    /// </summary>
    /// <remarks>
    /// A test affordance, not a production mode. On a LAN the default policy selects a
    /// direct pair and never touches TURN, so a working session proves nothing about the
    /// relay path. Setting this before a controller joins makes the TURN acceptance test
    /// conclusive: if it connects, TURN carried it.
    /// </remarks>
    public bool ForceRelay { get; set; }

    /// <summary>The controller that owns the current session, or null.</summary>
    public string? ControllerId => _controllerId;

    /// <summary>The grant the current session is running under, or null.</summary>
    public string? ActiveGrantId => _grantId;

    /// <summary>Why the most recent join request was refused.</summary>
    public JoinRefusal LastRefusal { get; private set; } = JoinRefusal.None;

    /// <summary>How many times this session has replaced its peer transport.</summary>
    public int PeerRecreations { get; private set; }

    public event Action<HostSessionState>? StateChanged;

    /// <summary>The live pipeline, or null when no viewer is connected.</summary>
    /// <remarks>Exposed for diagnostics; the session owns its lifetime.</remarks>
    public VideoPipeline? Pipeline => _pipeline;

    /// <summary>The live transport, or null. Replaced wholesale by recovery.</summary>
    public TecheePeerConnection? Peer => _peer;

    /// <summary>
    /// The live input handler, or null when this host does not accept input.
    /// </summary>
    /// <remarks>
    /// Null covers two different situations that look the same from outside: no injector
    /// was supplied at construction, and no session is running. Its counters are the only
    /// way to tell a refused command from a rate-limited one, which otherwise both
    /// present as "my clicks do nothing".
    /// </remarks>
    public PeerControlHandler? ControlHandler => _control;

    // ---- lifecycle ----

    /// <summary>
    /// Registers with the broker and announces availability.
    /// </summary>
    /// <remarks>
    /// Capture stays off here. A registered host with no viewer must not be running a
    /// DXGI duplication loop — see the pipeline lifecycle in <see cref="StartSessionAsync"/>.
    /// </remarks>
    public async Task<RegistrationResult> RegisterAsync(CancellationToken ct = default)
    {
        var result = await _signaling.ConnectAndRegisterAsync(ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            _log?.Invoke($"[session] registration failed: {result.Reason}");
            return result;
        }

        await _signaling.SendAsync(w => w.WriteString("type", "host-open"), ct).ConfigureAwait(false);
        SetState(HostSessionState.Listening);
        return result;
    }

    /// <summary>
    /// Pumps broker messages until cancelled.
    /// </summary>
    /// <remarks>
    /// One reader, so session state is only ever mutated from this loop plus the
    /// recovery path, both serialised by <see cref="_sessionLock"/>.
    /// </remarks>
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await foreach (var message in _signaling.Messages.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await HandleAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Routes one broker message. Never throws: the broker is untrusted.</summary>
    internal async Task HandleAsync(SignalingMessage message, CancellationToken ct = default)
    {
        try
        {
            switch (message.Type)
            {
                case "join-request":
                    await OnJoinRequestAsync(message, ct).ConfigureAwait(false);
                    break;

                case "answer":
                    await OnAnswerAsync(message).ConfigureAwait(false);
                    break;

                case "ice":
                    OnRemoteIce(message);
                    break;

                case "restart":
                    // The controller is the answerer and cannot re-offer, so it nudges
                    // us — exactly as Android's RtcSession.doIceRestart does.
                    await OnRestartRequestedAsync(message, ct).ConfigureAwait(false);
                    break;

                case "hangup":
                    await OnHangupAsync(message).ConfigureAwait(false);
                    break;

                case "offer":
                    // The host is always the offerer. An inbound offer is either a
                    // confused peer or an attempt to renegotiate the session out from
                    // under us.
                    _log?.Invoke($"[session] ignored an unexpected offer from '{message.From}'");
                    break;

                case "session-replaced":
                    _log?.Invoke("[session] another socket authenticated as this device; closing");
                    await EndAsync().ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception e)
        {
            // This loop is the only way back into an unattended machine. A malformed
            // broker frame must not end it.
            _log?.Invoke($"[session] dropped a '{message.Type}': {e.GetType().Name}");
        }
    }

    // ---- authorisation ----

    private async Task OnJoinRequestAsync(SignalingMessage message, CancellationToken ct)
    {
        var controllerId = message.GetString("controllerId");

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var refusal = Authorize(controllerId, out var grant);
            LastRefusal = refusal;

            if (refusal != JoinRefusal.None)
            {
                _log?.Invoke($"[session] refused '{Redact(controllerId)}': {refusal}");
                if (controllerId is not null) await SendConsentAsync(controllerId, accepted: false, ct).ConfigureAwait(false);
                return;
            }

            SetState(HostSessionState.Authorizing);

            // Consent goes out before the offer so the controller knows it was accepted
            // even if media negotiation then fails.
            await SendConsentAsync(controllerId!, accepted: true, ct).ConfigureAwait(false);
            await StartSessionAsync(controllerId!, grant!, ct).ConfigureAwait(false);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Decides whether a controller may open a session, using only local state.
    /// </summary>
    /// <remarks>
    /// The broker's <c>unattended</c>, <c>grantId</c> and <c>permissions</c> fields are
    /// deliberately not consulted. They are relayed by an untrusted party, and a host
    /// that believed them could be handed a session it never granted.
    /// </remarks>
    private JoinRefusal Authorize(string? controllerId, out Grant? grant)
    {
        grant = null;

        if (string.IsNullOrWhiteSpace(controllerId)) return JoinRefusal.Malformed;

        // Ownership before anything else. A second controller must not be able to
        // displace the first, nor learn whether it would otherwise have been trusted.
        if (_controllerId is not null && _controllerId != controllerId) return JoinRefusal.AlreadyOwned;

        if (!_trust.IsTrusted(controllerId)) return JoinRefusal.NotTrusted;

        // The grant is re-read here, not carried from the join-request, and is
        // re-evaluated against the current lock state on every call.
        grant = _grants.FindUsableFor(controllerId, _trust, _clock(), _workstationLocked());
        return grant is null ? JoinRefusal.NoGrant : JoinRefusal.None;
    }

    private Task SendConsentAsync(string controllerId, bool accepted, CancellationToken ct) =>
        _signaling.SendAsync(w =>
        {
            w.WriteString("type", "consent");
            w.WriteString("controllerId", controllerId);
            w.WriteBoolean("accepted", accepted);
        }, ct);

    // ---- media session ----

    /// <summary>Builds the transport and starts capture for an authorised controller.</summary>
    /// <remarks>
    /// The pipeline is created here rather than at registration: a host with no viewer
    /// must not hold a DXGI duplication session or an encoder.
    /// </remarks>
    private async Task StartSessionAsync(string controllerId, Grant grant, CancellationToken ct)
    {
        _controllerId = controllerId;
        _grantId = grant.GrantId;

        var peer = new TecheePeerConnection(
            _identity,
            controllerId,
            // Resolves through the trust store on every call, so a peer revoked
            // mid-session stops authenticating rather than riding on a cached key.
            _trust.PublicKeyForSdp,
            _log,
            // Announced to the controller over the control channel. `input.receive`
            // appears only when this host will actually act on input, so a --no-input
            // host does not have the controller render a pointer surface that silently
            // does nothing.
            localMeta: LocalMeta());

        peer.LocalIceCandidate += candidate => _ = SendIceAsync(controllerId, candidate);
        peer.StateChanged += OnPeerStateChanged;
        peer.TransportRecoveryNeeded += () => _ = RecoverAsync();

        // Input is opt-in at construction. A host built without an injector — the
        // controller-only install, or any test that has no business moving the real
        // mouse — simply never subscribes, so control frames are decoded and discarded
        // rather than gated by a flag someone could flip.
        var control = CreateControlHandler(controllerId);
        if (control is not null) peer.ControlReceived += control.Handle;

        var iceServers = _signaling.IceServers
            .Select(s => new RTCIceServer { urls = s.Urls, username = s.Username ?? "", credential = s.Credential ?? "" })
            .ToList();

        await peer.InitializeAsync(iceServers, isHost: true, ForceRelay).ConfigureAwait(false);

        var pipeline = _pipeline ?? _pipelineFactory();
        var sink = new PeerVideoSink(peer, _log);

        // Attach replaces, so a recovery cannot leave the retired peer receiving frames.
        pipeline.AttachSink(sink);

        _peer = peer;
        _sink = sink;
        _control = control;
        _pipeline = pipeline;

        SetState(HostSessionState.Negotiating);

        var (sdp, signature) = peer.CreateOffer();
        await SendOfferAsync(controllerId, sdp, signature, ct).ConfigureAwait(false);

        // Capture starts now rather than on connect. Frames encoded during ICE and DTLS
        // are dropped by the sink and counted, which costs a counter increment and gets
        // a keyframe in flight the instant the link comes up.
        pipeline.Start();
        _grants.MarkUsed(grant.GrantId, _clock());
    }

    /// <summary>
    /// What this host announces to the controller in <c>hello</c>.
    /// </summary>
    /// <remarks>
    /// Derived from the injector factory rather than configured separately, so the two
    /// cannot disagree. A capability is a promise about behaviour; advertising one this
    /// build has switched off would be a lie the controller acts on.
    /// </remarks>
    private EndpointMeta LocalMeta()
    {
        var capabilities = _injectorFactory is null
            ? new[] { "screen.share", "display.multi" }
            : ["screen.share", "display.multi", "input.receive"];

        return TecheeProtocol.LocalMeta(HostVersion, capabilities);
    }

    /// <summary>The version string this host reports. Not the protocol version.</summary>
    public const string HostVersion = "w5";

    /// <summary>
    /// Builds the input handler for a session, or null if this host does not accept input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The grant is passed as a <i>callback</i>, not a value. Everything the security
    /// model claims about mid-session revocation depends on that: the handler re-runs
    /// <see cref="GrantStore.FindUsableFor"/> for every command, so a grant that is
    /// revoked, expires, or downgrades because the workstation locked stops authorising
    /// input on the very next frame rather than at the next reconnect.
    /// </para>
    /// <para>
    /// The injector is created per session and disposed with it. A host with no viewer
    /// holds no capture device and no injection capability either.
    /// </para>
    /// </remarks>
    private PeerControlHandler? CreateControlHandler(string controllerId)
    {
        if (_injectorFactory is null) return null;

        IInputInjector injector;
        try
        {
            injector = _injectorFactory();
        }
        catch (Exception e)
        {
            // A host that cannot inject is still a useful host: the operator can watch.
            // Failing the whole session over it would be the wrong trade.
            _log?.Invoke($"[session] input unavailable: {e.GetType().Name}");
            return null;
        }

        _injector = injector;

        return new PeerControlHandler(
            injector,
            () => _grants.FindUsableFor(controllerId, _trust, _clock(), _workstationLocked()),
            () => _pipeline?.Display,
            _clock,
            log: _log);
    }

    private void OnPeerStateChanged(LinkState link)
    {
        switch (link)
        {
            case LinkState.Connected:
                SetState(HostSessionState.Connected);
                // A controller that just started decoding has no reference frame.
                _pipeline?.RequestKeyFrame();
                break;

            case LinkState.Recovering:
                if (State is not HostSessionState.Closed) SetState(HostSessionState.Recovering);
                break;
        }
    }

    private Task SendOfferAsync(string to, string sdp, string signature, CancellationToken ct) =>
        _signaling.SendAsync(w =>
        {
            w.WriteString("type", "offer");
            w.WriteString("to", to);
            w.WriteString("sdp", sdp);
            w.WriteString("fpSig", signature);
        }, ct);

    private Task SendIceAsync(string to, RTCIceCandidate candidate) =>
        _signaling.SendAsync(w =>
        {
            w.WriteString("type", "ice");
            w.WriteString("to", to);
            w.WriteString("mid", candidate.sdpMid);
            w.WriteNumber("index", candidate.sdpMLineIndex);
            w.WriteString("cand", candidate.candidate);
        });

    private Task OnAnswerAsync(SignalingMessage message)
    {
        var peer = _peer;
        var from = message.From;
        if (peer is null || from is null) return Task.CompletedTask;

        var sdp = message.GetString("sdp");
        if (sdp is null) return Task.CompletedTask;

        var verdict = peer.AcceptRemoteDescription("answer", from, sdp, message.GetString("fpSig"));
        if (verdict != SdpVerdict.Ok)
        {
            _log?.Invoke($"[session] answer rejected: {verdict}");
            // A third party's SDP is discarded without touching the session; anything
            // else means the expected peer failed to authenticate.
            if (SdpAuth.ShouldTearDown(verdict)) return EndAsync();
            return Task.CompletedTask;
        }

        InspectNegotiatedVideo(sdp);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records what the answer actually negotiated for video.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pipeline emits VP8 regardless; that says nothing about whether the far end
    /// agreed to receive it. A peer that declines the video section, or answers a codec
    /// this host cannot produce, leaves a session that connects, completes DTLS and
    /// carries RTP while the viewer sees black — with nothing in any log to explain it.
    /// </para>
    /// <para>
    /// Recorded and logged rather than treated as fatal. The control channel and the
    /// session itself are still valid, and an operator staring at a black window is far
    /// better served by a diagnostic naming the codec mismatch than by a disconnect.
    /// </para>
    /// </remarks>
    private void InspectNegotiatedVideo(string answerSdp)
    {
        var video = SdpInspect.Video(answerSdp);

        if (video is null || !video.IsActive)
        {
            NegotiatedVideoPayloadType = null;
            _log?.Invoke("[session] the answer carries no active video section; no picture will arrive");
            return;
        }

        NegotiatedVideoPayloadType = video.PayloadTypeFor(Vp8EncodingName);

        if (NegotiatedVideoPayloadType is null)
        {
            var offered = string.Join(", ", video.RtpMaps.Select(m => m.EncodingName).Distinct());
            _log?.Invoke($"[session] the peer did not negotiate {Vp8EncodingName}; it offered [{offered}]. " +
                         "This host only encodes VP8, so no picture will arrive.");
            return;
        }

        _log?.Invoke($"[session] video negotiated: {Vp8EncodingName} on payload type {NegotiatedVideoPayloadType}");
    }

    /// <summary>The only codec this host encodes.</summary>
    private const string Vp8EncodingName = "VP8";

    /// <summary>
    /// The payload type the peer negotiated for VP8, or null if it negotiated none.
    /// </summary>
    /// <remarks>
    /// Null after an answer has been processed means the far end will not be able to
    /// decode anything this host sends.
    /// </remarks>
    public int? NegotiatedVideoPayloadType { get; private set; }

    private void OnRemoteIce(SignalingMessage message)
    {
        var peer = _peer;
        var from = message.From;
        var candidate = message.GetString("cand");
        if (peer is null || from is null || candidate is null) return;

        var index = message.Body.TryGetProperty("index", out var i)
                    && i.ValueKind == System.Text.Json.JsonValueKind.Number
            ? (ushort)i.GetInt32()
            : (ushort)0;

        // The peer checks `from` against the session's controller, so a registered
        // stranger cannot inject candidates into someone else's live session.
        peer.AddRemoteIceCandidate(from, candidate, message.GetString("mid"), index);
    }

    private async Task OnRestartRequestedAsync(SignalingMessage message, CancellationToken ct)
    {
        if (message.From is null || message.From != _controllerId)
        {
            _log?.Invoke($"[session] ignored a restart from '{Redact(message.From)}'");
            return;
        }

        await RecoverAsync(ct).ConfigureAwait(false);
    }

    private async Task OnHangupAsync(SignalingMessage message)
    {
        if (message.From is null || message.From != _controllerId) return;
        await EndAsync().ConfigureAwait(false);
    }

    // ---- recovery: authenticated peer recreation ----

    /// <summary>
    /// Replaces the transport after an unrecoverable failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not an ICE restart.</b> SIPSorcery 10.0.15 cannot refresh ICE credentials in
    /// place, so recovery disposes the peer and builds a new one, which mints fresh
    /// <c>ice-ufrag</c>/<c>ice-pwd</c> in its constructor.
    /// </para>
    /// <para>
    /// <b>Recovery is not authorisation.</b> The replacement re-runs the full check —
    /// trust, grant, expiry, lock state, session ownership — against the same controller
    /// the session started with. A controller revoked while the link was down does not
    /// come back, and no new identity can arrive through this path because the peer is
    /// never taken from a message.
    /// </para>
    /// <para>
    /// Single-flight and idempotent: concurrent triggers collapse into one replacement,
    /// so a flapping link cannot accumulate peers.
    /// </para>
    /// </remarks>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _recovering, 1) != 0) return;

        try
        {
            await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_disposed || State == HostSessionState.Closed) return;

                var controllerId = _controllerId;
                if (controllerId is null) return;

                SetState(HostSessionState.Recovering);

                // Stop sending before anything else, so no frame can reach a transport
                // that is being torn down.
                _pipeline?.DetachSink();

                // Then pause capture for the duration of the gap. Encoding frames that
                // have nowhere to go is pure cost on a machine someone is also sitting
                // at, and a recovery can last a while. The source and encoder are kept —
                // it is the DXGI duplication session and the libvpx context that are
                // expensive to rebuild, not the thread.
                _pipeline?.Stop();

                await DisposePeerAsync().ConfigureAwait(false);

                // Re-authorise from scratch. This is the check that stops a reconnect
                // from being a privilege.
                var refusal = Authorize(controllerId, out var grant);
                if (refusal != JoinRefusal.None)
                {
                    LastRefusal = refusal;
                    _log?.Invoke($"[session] recovery refused: {refusal}");
                    await EndLockedAsync().ConfigureAwait(false);
                    return;
                }

                PeerRecreations++;
                await StartSessionAsync(controllerId, grant!, ct).ConfigureAwait(false);
                _log?.Invoke($"[session] replaced the peer transport (recreation {PeerRecreations})");
            }
            finally
            {
                _sessionLock.Release();
            }
        }
        finally
        {
            Volatile.Write(ref _recovering, 0);
        }
    }

    // ---- teardown ----

    /// <summary>Ends the session, telling the controller and releasing capture.</summary>
    public async Task EndAsync()
    {
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await EndLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task EndLockedAsync()
    {
        var controllerId = _controllerId;
        if (controllerId is not null)
        {
            try
            {
                await _signaling.SendAsync(w =>
                {
                    w.WriteString("type", "hangup");
                    w.WriteString("to", controllerId);
                }).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Saying goodbye is a courtesy. Releasing the capture device is not.
                _log?.Invoke($"[session] hangup not delivered: {e.GetType().Name}");
            }
        }

        await DisposePeerAsync().ConfigureAwait(false);

        // No viewer means no capture. Disposing rather than merely stopping releases the
        // DXGI duplication session and the encoder, which a stopped pump would keep.
        var pipeline = _pipeline;
        _pipeline = null;
        pipeline?.Dispose();

        _controllerId = null;
        _grantId = null;
        SetState(HostSessionState.Closed);
    }

    private async Task DisposePeerAsync()
    {
        var peer = _peer;
        var control = _control;
        var injector = _injector;

        _peer = null;
        _sink = null;
        _control = null;
        _injector = null;

        if (peer is not null)
        {
            peer.StateChanged -= OnPeerStateChanged;

            // Unsubscribed before disposal so a frame already in flight on the network
            // thread cannot reach a handler whose worker is shutting down.
            if (control is not null) peer.ControlReceived -= control.Handle;

            await peer.DisposeAsync().ConfigureAwait(false);
        }

        // After the peer, so nothing can still be queueing work. Recovery replaces the
        // peer and therefore the handler, which is what stops a retired transport from
        // going on driving the mouse.
        control?.Dispose();
        injector?.Dispose();
    }

    private void SetState(HostSessionState state)
    {
        if (State == state) return;
        State = state;
        _log?.Invoke($"[session] {state}");
        StateChanged?.Invoke(state);
    }

    /// <summary>Device IDs are public keys' hashes, but a full one in a log line is still noise.</summary>
    private static string Redact(string? deviceId) =>
        deviceId is null ? "<none>" : deviceId.Length <= 12 ? deviceId : deviceId[..12] + "…";

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await EndAsync().ConfigureAwait(false);
        _sessionLock.Dispose();
    }
}
