using System.Text.Json;
using SIPSorcery.Net;
using Techee.Crypto;
using Techee.Protocol;
using Techee.Session;
using Techee.Signaling;
using Techee.Store;
using Techee.WebRtc;

namespace Techee.Windows.ControllerApp;

/// <summary>Why a controller session ended.</summary>
public enum ControllerOutcome
{
    /// <summary>Still running.</summary>
    Running,

    /// <summary>The operator closed the viewer or pressed Ctrl+C.</summary>
    ClosedLocally,

    /// <summary>The host hung up.</summary>
    HostHungUp,

    /// <summary>The broker refused the dial. <see cref="ControllerSession.FailureReason"/> says why.</summary>
    JoinFailed,

    /// <summary>The host declined an attended session.</summary>
    ConsentRefused,

    /// <summary>The host's SDP did not authenticate.</summary>
    SdpRejected,

    /// <summary>The broker socket went away.</summary>
    Disconnected,
}

/// <summary>
/// The controller half of a Techee session, on Windows.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of <see cref="WindowsHostSession"/>, and the production promotion of the
/// <c>Controller</c> class that until now lived privately inside
/// <c>ControlOverBrokerTests</c>. The message flow is deliberately identical to that
/// class's, because that flow is the one covered by the broker-backed suite — this is
/// the same orchestration with a lifetime, diagnostics and a teardown path attached,
/// not a second implementation of it.
/// </para>
/// <para>
/// <b>The host is always the offerer.</b> This side answers, exactly as Android does.
/// That is not an arbitrary convention: whichever end shares its screen creates the
/// video track and the control channel, so a controller that offered would have to
/// negotiate ownership of the channel with the host.
/// </para>
/// <para>
/// <b>The broker is untrusted here too.</b> It relays the offer and could rewrite it
/// freely, so the offer's signature is verified against this controller's own trust
/// store before an answer is produced. A host that is unpaired, pending, or revoked
/// fails that check and the session never reaches media — see
/// <see cref="TrustStore.PublicKeyForSdp"/>.
/// </para>
/// </remarks>
public sealed class ControllerSession : IAsyncDisposable
{
    private readonly IDeviceIdentity _identity;
    private readonly SignalingClient _signaling;
    private readonly TrustStore _trust;
    private readonly Action<string> _log;
    private readonly bool _forceRelay;

    private readonly TaskCompletionSource<ControllerOutcome> _finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TecheePeerConnection? _peer;
    private ControllerControlSender? _sender;

    public ControllerSession(
        IDeviceIdentity identity,
        SignalingClient signaling,
        TrustStore trust,
        string hostId,
        Action<string> log,
        bool forceRelay = false)
    {
        _identity = identity;
        _signaling = signaling;
        _trust = trust;
        HostId = hostId;
        _log = log;
        _forceRelay = forceRelay;
    }

    /// <summary>The host this session dials. Fixed at construction, never taken from a message.</summary>
    public string HostId { get; }

    public TecheePeerConnection? Peer => _peer;

    /// <summary>The control sender, once the host has created the channel.</summary>
    public ControllerControlSender? Sender => _sender;

    public LinkState LinkState => _peer?.State ?? LinkState.Connecting;

    /// <summary>Whether the control channel has been created and is worth sending on.</summary>
    public bool ControlReady => _peer is { State: LinkState.Connected } && _sender is not null;

    /// <summary>Set when the session ends for a reason worth printing.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Completes when the session is over.</summary>
    public Task<ControllerOutcome> Completion => _finished.Task;

    /// <summary>Raised with each reassembled inbound video frame, on the receive thread.</summary>
    public event Action<byte[], uint>? VideoFrameReceived;

    /// <summary>Raised when the link state changes, for the viewer's status line.</summary>
    public event Action<LinkState>? LinkStateChanged;

    /// <summary>Raised once the host announces what it is.</summary>
    public event Action<EndpointMeta>? HostAnnounced;

    /// <summary>
    /// Dials the host and pumps signaling until the session ends.
    /// </summary>
    /// <remarks>
    /// The join is sent <b>after</b> the pump is running, not before. The host can send
    /// its offer the instant the broker forwards the join-request, and an offer that
    /// arrives before anything is reading the inbox would sit in the channel until the
    /// next message woke the reader — which, on a session that then goes quiet, is
    /// never.
    /// </remarks>
    public async Task<ControllerOutcome> RunAsync(CancellationToken ct)
    {
        var pump = PumpAsync(ct);

        _log($"dialling {Short(HostId)}…");
        await _signaling.JoinAsync(HostId, ct).ConfigureAwait(false);

        await pump.ConfigureAwait(false);
        return await _finished.Task.ConfigureAwait(false);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var m in _signaling.Messages.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (m.Type)
                {
                    case "offer" when m.From == HostId:
                        await OnOfferAsync(m, ct).ConfigureAwait(false);
                        break;

                    case "ice" when m.From == HostId && _peer is not null:
                        OnIce(m);
                        break;

                    case "join-pending":
                        OnJoinPending(m);
                        break;

                    case "join-failed":
                        Finish(ControllerOutcome.JoinFailed, m.GetString("reason") ?? "unknown");
                        return;

                    case "consent" when !m.GetBool("accepted", true):
                        Finish(ControllerOutcome.ConsentRefused, "the host declined the session");
                        return;

                    case "consent":
                        _log("host accepted; waiting for the offer");
                        break;

                    case "hangup" when m.From == HostId:
                        Finish(ControllerOutcome.HostHungUp, "the host ended the session");
                        return;

                    // The host recreates its peer after a transport failure and re-offers.
                    // Nothing to do here: the next `offer` is handled like the first, and
                    // it re-authenticates from scratch.
                    case "restart" when m.From == HostId:
                        _log("host is recreating its transport; expecting a fresh offer");
                        break;

                    case "session-replaced":
                        Finish(ControllerOutcome.Disconnected, "another session replaced this one");
                        return;
                }
            }

            // The inbox completed, which only happens when the socket closed.
            Finish(ControllerOutcome.Disconnected, "the broker connection closed");
        }
        catch (OperationCanceledException)
        {
            Finish(ControllerOutcome.ClosedLocally, null);
        }
    }

    private void OnJoinPending(SignalingMessage m)
    {
        var unattended = m.GetBool("unattended");

        // Reported, never believed. The broker is telling us what it thinks the host's
        // grant says; the host re-reads its own copy before executing anything, so this
        // is a progress message and not a statement about what we may do.
        _log(unattended
            ? "broker: the host has a standing grant for this controller (unattended)"
            : "broker: no standing grant; the host must accept this session interactively");
    }

    private void OnIce(SignalingMessage m)
    {
        var index = m.Body.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number
            ? (ushort)i.GetInt32()
            : (ushort)0;

        var candidate = m.GetString("cand");
        if (candidate is null) return;

        _peer!.AddRemoteIceCandidate(HostId, candidate, m.GetString("mid"), index);
    }

    private async Task OnOfferAsync(SignalingMessage m, CancellationToken ct)
    {
        var offer = m.GetString("sdp");
        if (offer is null)
        {
            _log("ignored an offer with no SDP");
            return;
        }

        // A re-offer means the host rebuilt its transport. The old peer is spent; keeping
        // it would leave two connections racing for the same session.
        if (_peer is { } previous)
        {
            _log("replacing the previous peer for a re-offer");
            _sender = null;
            await previous.DisposeAsync().ConfigureAwait(false);
            _peer = null;
        }

        var peer = new TecheePeerConnection(
            _identity,
            HostId,
            _trust.PublicKeyForSdp,
            _log,
            // Announced so the host addresses this controller in v1 rather than
            // downgrading it to the legacy dialect. Without it the whole v1 vocabulary —
            // wheel, keyboard — is unreachable no matter what both ends implement.
            new EndpointMeta("windows", ControllerVersion, ControllerCapabilities));

        var iceServers = _signaling.IceServers
            .Select(s => new RTCIceServer
            {
                urls = s.Urls,
                username = s.Username ?? "",
                credential = s.Credential ?? "",
            })
            .ToList();

        await peer.InitializeAsync(iceServers, isHost: false, _forceRelay).ConfigureAwait(false);

        peer.LocalIceCandidate += c => _ = _signaling.SendAsync(w =>
        {
            w.WriteString("type", "ice");
            w.WriteString("to", HostId);
            w.WriteString("mid", c.sdpMid);
            w.WriteNumber("index", c.sdpMLineIndex);
            w.WriteString("cand", c.candidate);
        }, CancellationToken.None);

        peer.StateChanged += s => LinkStateChanged?.Invoke(s);
        peer.PeerAnnounced += meta => HostAnnounced?.Invoke(meta);
        peer.VideoFrameReceived += (frame, timestamp) => VideoFrameReceived?.Invoke(frame, timestamp);

        _peer = peer;

        // Verified before an answer exists. Producing one first and discarding it on a
        // bad verdict would mean this side had already committed a DTLS fingerprint to
        // an unauthenticated peer.
        var verdict = peer.AcceptRemoteDescription("offer", m.From!, offer, m.GetString("fpSig"));
        if (verdict != SdpVerdict.Ok)
        {
            _log($"REFUSED the host's offer: {verdict}");
            Finish(ControllerOutcome.SdpRejected, $"the host's SDP did not authenticate ({verdict})");
            return;
        }

        _sender = ControllerControlSender.For(peer);

        var (answer, signature) = peer.CreateAnswer();
        await _signaling.SendAsync(w =>
        {
            w.WriteString("type", "answer");
            w.WriteString("to", HostId);
            w.WriteString("sdp", answer);
            w.WriteString("fpSig", signature);
        }, ct).ConfigureAwait(false);

        _log("offer authenticated; answer sent");
    }

    /// <summary>Ends the session locally, telling the host rather than just vanishing.</summary>
    public async Task EndAsync()
    {
        if (_peer is not null)
        {
            // Anything still held remotely is released before the channel goes. The host
            // has its own stuck-input net, but relying on it means a lost gesture leaves
            // a button down on someone else's machine until their net notices.
            _sender?.ReleaseAll();

            try
            {
                await _signaling.SendAsync(w =>
                {
                    w.WriteString("type", "hangup");
                    w.WriteString("to", HostId);
                }).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log($"could not send hangup: {e.GetType().Name}");
            }
        }

        Finish(ControllerOutcome.ClosedLocally, null);
    }

    private void Finish(ControllerOutcome outcome, string? reason)
    {
        FailureReason ??= reason;
        _finished.TrySetResult(outcome);
    }

    public async ValueTask DisposeAsync()
    {
        _sender = null;

        if (_peer is { } peer)
        {
            _peer = null;
            await peer.DisposeAsync().ConfigureAwait(false);
        }

        Finish(ControllerOutcome.ClosedLocally, null);
    }

    internal const string ControllerVersion = "w5-controller";

    /// <summary>
    /// What this controller advertises.
    /// </summary>
    /// <remarks>
    /// Receives a screen and sends input. Note what is absent: nothing about clipboard or
    /// power, because this harness implements neither and a capability that describes
    /// something unimplemented is how a peer ends up offering an affordance that does
    /// nothing.
    /// </remarks>
    internal static readonly string[] ControllerCapabilities = ["screen.receive", "input.send"];

    private static string Short(string deviceId) =>
        deviceId.Length <= 12 ? deviceId : deviceId[..12] + "…";
}
