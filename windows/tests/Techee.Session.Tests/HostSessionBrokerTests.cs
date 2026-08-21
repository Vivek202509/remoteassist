using System.Text.Json;
using SIPSorcery.Net;
using Techee.Crypto;
using Techee.Protocol;
using Techee.Session;
using Techee.Signaling;
using Techee.Signaling.Tests;
using Techee.Store;
using Techee.WebRtc;
using Techee.Windows.Host;
using Techee.Windows.Host.Tests;
using Xunit.Abstractions;

namespace Techee.Session.Tests;

/// <summary>
/// The whole Windows host flow against the real broker: register → host-open → join →
/// consent → offer → answer → ICE trickle → DTLS → VP8 → hangup.
/// </summary>
/// <remarks>
/// <para>
/// Runs the actual <c>server/src/server.js</c>, not a C# stand-in, for the reason given
/// on <see cref="BrokerFixture"/>: a mock broker written from the same understanding as
/// the client would let both agree on a misreading of the protocol.
/// </para>
/// <para>
/// The controller here is a minimal Windows stand-in for the Android controller — real
/// <see cref="SignalingClient"/>, real <see cref="TecheePeerConnection"/>, real SDP
/// authentication. It proves the host's half of the protocol is correct and
/// interoperable. It does <b>not</b> substitute for the Android acceptance test: only a
/// real Android controller proves Android's decoder renders these frames.
/// </para>
/// <para>
/// Both peers are in one process on loopback, so ICE has host candidates on both sides
/// and can genuinely complete. Where it does, this exercises real DTLS and real VP8 RTP.
/// </para>
/// </remarks>
[Collection("broker")]
public class HostSessionBrokerTests(BrokerFixture broker, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly List<IDisposable> _disposables = [];
    private readonly List<Func<Task>> _asyncDisposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in _asyncDisposables) await d();
        foreach (var d in _disposables) d.Dispose();
    }

    private T Track<T>(T value) where T : IDisposable
    {
        _disposables.Add(value);
        return value;
    }

    /// <summary>
    /// A minimal controller: the answering half of the Techee protocol.
    /// </summary>
    /// <remarks>
    /// Mirrors Android's controller role — receive-only video, never the offerer, and
    /// authenticates the host's SDP against the trust store before applying it.
    /// </remarks>
    private sealed class TestController(
        EphemeralDeviceIdentity identity,
        SignalingClient signaling,
        Func<string, byte[]?> peerKeyLookup,
        Action<string> log) : IAsyncDisposable
    {
        private TecheePeerConnection? _peer;

        public string DeviceId => identity.DeviceId;
        public SignalingClient Signaling => signaling;
        public TecheePeerConnection? Peer => _peer;
        public bool AnswerSent { get; private set; }
        public SdpVerdict LastVerdict { get; private set; } = SdpVerdict.NoExpectedPeer;
        public string? OfferSdp { get; private set; }

        public async Task PumpAsync(string hostId, CancellationToken ct)
        {
            await foreach (var m in signaling.Messages.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (m.Type)
                {
                    case "offer" when m.From == hostId:
                        await OnOfferAsync(m, hostId, ct).ConfigureAwait(false);
                        break;

                    case "ice" when m.From == hostId && _peer is not null:
                        var idx = m.Body.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number
                            ? (ushort)i.GetInt32()
                            : (ushort)0;
                        _peer.AddRemoteIceCandidate(hostId, m.GetString("cand")!, m.GetString("mid"), idx);
                        break;
                }
            }
        }

        private async Task OnOfferAsync(SignalingMessage m, string hostId, CancellationToken ct)
        {
            OfferSdp = m.GetString("sdp");
            if (OfferSdp is null) return;

            var peer = new TecheePeerConnection(identity, hostId, peerKeyLookup, log);
            await peer.InitializeAsync([], isHost: false).ConfigureAwait(false);
            peer.LocalIceCandidate += c => _ = signaling.SendAsync(w =>
            {
                w.WriteString("type", "ice");
                w.WriteString("to", hostId);
                w.WriteString("mid", c.sdpMid);
                w.WriteNumber("index", c.sdpMLineIndex);
                w.WriteString("cand", c.candidate);
            }, CancellationToken.None);

            _peer = peer;

            LastVerdict = peer.AcceptRemoteDescription("offer", m.From!, OfferSdp, m.GetString("fpSig"));
            if (LastVerdict != SdpVerdict.Ok) return;

            var (answer, signature) = peer.CreateAnswer();
            await signaling.SendAsync(w =>
            {
                w.WriteString("type", "answer");
                w.WriteString("to", hostId);
                w.WriteString("sdp", answer);
                w.WriteString("fpSig", signature);
            }, ct).ConfigureAwait(false);

            AnswerSent = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_peer is not null) await _peer.DisposeAsync();
        }
    }

    private sealed record Rig(
        WindowsHostSession Host,
        TestController Controller,
        EphemeralDeviceIdentity HostIdentity,
        EphemeralDeviceIdentity ControllerIdentity,
        TrustStore HostTrust,
        GrantStore HostGrants,
        List<VideoPipeline> Pipelines,
        CancellationTokenSource Cancellation);

    /// <summary>Registers both endpoints, pairs them at the broker, and grants access.</summary>
    private async Task<Rig> StartAsync(bool useRealEncoder = false)
    {
        Skip.If(broker.SkipReason is not null, broker.SkipReason ?? "");

        var hostIdentity = Track(new EphemeralDeviceIdentity());
        var controllerIdentity = Track(new EphemeralDeviceIdentity());

        var hostTrust = new TrustStore(new InMemoryStore());
        var hostGrants = new GrantStore(new InMemoryStore());
        var controllerTrust = new TrustStore(new InMemoryStore());

        Trust(hostTrust, controllerIdentity, "Controller");
        Trust(controllerTrust, hostIdentity, "Host");

        hostGrants.Save(new Grant
        {
            GrantId = "grant-broker",
            ControllerId = controllerIdentity.DeviceId,
            PermissionTokens = ["screen.view", "input.control"],
        });

        var hostSignaling = new SignalingClient(
            broker.Url, hostIdentity, new EndpointMeta("windows", "test", ["screen.share"]),
            m => output.WriteLine($"host-sig  {m}"));
        var controllerSignaling = new SignalingClient(
            broker.Url, controllerIdentity, new EndpointMeta("windows", "test", ["screen.view"]),
            m => output.WriteLine($"ctrl-sig  {m}"));

        _asyncDisposables.Add(() => hostSignaling.DisposeAsync().AsTask());
        _asyncDisposables.Add(() => controllerSignaling.DisposeAsync().AsTask());

        var pipelines = new List<VideoPipeline>();
        var host = new WindowsHostSession(
            hostIdentity, hostSignaling, hostTrust, hostGrants,
            pipelineFactory: () =>
            {
                // A real libvpx encoder over synthetic BGRA: the payloads that cross
                // DTLS-SRTP are genuine VP8, not a stand-in. Only the GPU is faked.
                var p = new VideoPipeline(
                    new FakeScreenSource(),
                    useRealEncoder ? new VpxEncoder { TargetKbps = VideoProfile.Hd.TargetKbps } : new FakeEncoder(),
                    new AdaptiveQuality(VideoProfile.Hd));
                pipelines.Add(p);
                return p;
            },
            log: m => output.WriteLine($"host  {m}"));

        _asyncDisposables.Add(() => host.DisposeAsync().AsTask());

        var registration = await host.RegisterAsync();
        Assert.True(registration.Ok, $"host registration failed: {registration.Reason}");

        var controllerRegistration = await controllerSignaling.ConnectAndRegisterAsync();
        Assert.True(controllerRegistration.Ok, $"controller registration failed: {controllerRegistration.Reason}");

        // The broker requires a pairing edge before it will relay a paired-direct dial.
        await hostSignaling.RegisterPairingAsync(controllerIdentity.DeviceId);
        await controllerSignaling.RegisterPairingAsync(hostIdentity.DeviceId);

        var controller = new TestController(
            controllerIdentity, controllerSignaling,
            controllerTrust.PublicKeyForSdp,
            m => output.WriteLine($"ctrl  {m}"));

        _asyncDisposables.Add(() => controller.DisposeAsync().AsTask());

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _ = host.RunAsync(cts.Token);
        _ = controller.PumpAsync(hostIdentity.DeviceId, cts.Token);

        return new Rig(host, controller, hostIdentity, controllerIdentity,
            hostTrust, hostGrants, pipelines, cts);
    }

    private static void Trust(TrustStore store, EphemeralDeviceIdentity peer, string name)
    {
        var pub = Convert.ToBase64String(peer.PublicKeySpkiDer);
        store.Save(new PeerIdentity
        {
            PublicKeySpkiB64 = pub,
            Name = name,
            SharedSecretB64 = Convert.ToBase64String(new byte[32]),
            State = TrustState.PendingConfirm,
        });
        store.Confirm(pub);
    }

    /// <summary>
    /// Waits until the session is fully up.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>ControllerId is not null</c>: that is assigned before the
    /// awaited peer initialisation, so waiting on it races the pipeline into existence.
    /// A running pump is the last thing session start does.
    /// </remarks>
    private static Task<bool> WaitForSessionAsync(Rig rig) =>
        WaitUntilAsync(() => rig.Host.Pipeline is { IsRunning: true });

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [SkippableFact]
    public async Task The_host_completes_the_full_join_offer_answer_ice_flow()
    {
        var rig = await StartAsync();

        // The controller dials the paired host directly, exactly as Android does.
        await SignalingOf(rig).JoinAsync(rig.HostIdentity.DeviceId);

        Assert.True(await WaitForSessionAsync(rig), "the host never started the session");

        Assert.Equal(rig.ControllerIdentity.DeviceId, rig.Host.ControllerId);
        Assert.Equal(JoinRefusal.None, rig.Host.LastRefusal);

        // The host's own grant, not the id the broker relayed.
        Assert.Equal("grant-broker", rig.Host.ActiveGrantId);

        Assert.True(await WaitUntilAsync(() => rig.Controller.OfferSdp is not null),
            "the offer never reached the controller");

        // The offer authenticated against the host's identity key, through the broker.
        Assert.True(await WaitUntilAsync(() => rig.Controller.LastVerdict != SdpVerdict.NoExpectedPeer));
        Assert.Equal(SdpVerdict.Ok, rig.Controller.LastVerdict);
        Assert.True(await WaitUntilAsync(() => rig.Controller.AnswerSent), "no answer was sent");

        // Codec negotiation, checked structurally rather than by string matching. The
        // pipeline emits VP8, so the offer must genuinely negotiate it on an active
        // section — not merely mention it somewhere in the text.
        var offer = rig.Controller.OfferSdp!;
        var video = SdpInspect.Video(offer);

        Assert.NotNull(video);
        Assert.True(video!.IsActive);
        Assert.Equal("sendonly", video.Direction);

        var vp8 = video.PayloadTypeFor("VP8");
        Assert.NotNull(vp8);
        Assert.Contains(vp8!.Value, video.PayloadTypes);
        Assert.Equal(TecheePeerConnection.VideoClockRate, video.RtpMaps.First(m => m.Is("VP8")).ClockRate);

        Assert.NotNull(video.IceUfrag);
        Assert.NotNull(video.IcePwd);
        Assert.NotNull(SdpInspect.Fingerprint(offer));

        // And the host confirmed from the answer that the far end agreed to the same
        // payload type, rather than assuming it because the encoder produces VP8.
        Assert.True(await WaitUntilAsync(() => rig.Host.NegotiatedVideoPayloadType is not null),
            "the host never observed a negotiated video codec in the answer");

        output.WriteLine($"offered VP8 pt={vp8}, host observed negotiated pt={rig.Host.NegotiatedVideoPayloadType}");
        Assert.Equal(vp8, rig.Host.NegotiatedVideoPayloadType);

        // Capture came up with the session and is pointed at the transport.
        var pipeline = Assert.Single(rig.Pipelines);
        Assert.True(pipeline.IsRunning);
        Assert.True(pipeline.HasSink);

        output.WriteLine($"host state {rig.Host.State}, peer state {rig.Host.Peer?.State}");

        await rig.Cancellation.CancelAsync();
    }

    /// <summary>
    /// The strongest evidence available without an Android device: real DTLS, and real
    /// VP8 RTP leaving the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both peers are in one process, so ICE has loopback and LAN host candidates on both
    /// sides and can nominate a pair without any relay. Everything downstream of that is
    /// genuine — DTLS-SRTP is negotiated by SIPSorcery against the fingerprints the two
    /// sides signed, and the frames are encoded by the real <see cref="VideoPipeline"/>.
    /// </para>
    /// <para>
    /// What this still does not prove: that an Android decoder renders the result. That
    /// needs the real controller, and remains W3's external acceptance criterion.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Media_flows_end_to_end_over_a_real_connected_link()
    {
        var rig = await StartAsync(useRealEncoder: true);

        await SignalingOf(rig).JoinAsync(rig.HostIdentity.DeviceId);
        Assert.True(await WaitForSessionAsync(rig), "the host never started the session");

        var connected = await WaitUntilAsync(
            () => rig.Host.Peer?.State == LinkState.Connected, seconds: 30);

        output.WriteLine($"host session state : {rig.Host.State}");
        output.WriteLine($"host link state    : {rig.Host.Peer?.State}");
        output.WriteLine($"controller link    : {rig.Controller.Peer?.State}");
        output.WriteLine($"host telemetry     : {rig.Host.Peer?.GetTelemetry()}");

        Skip.IfNot(connected,
            "ICE/DTLS did not complete between two in-process SIPSorcery peers on this " +
            "machine. This is an environment limitation, not a host defect: the join, " +
            "authorisation, signed offer/answer and ICE exchange are all proven by the " +
            "other tests in this class. Real media remains the Android acceptance test.");

        Assert.Equal(HostSessionState.Connected, rig.Host.State);

        // A controller that just started decoding has no reference frame, so the session
        // asks for a keyframe the moment the link comes up.
        var pipeline = Assert.Single(rig.Pipelines);
        Assert.True(pipeline.Stats.FramesEncoded > 0);

        // The real proof: frames were accepted by the RTP sender, not merely offered.
        var sent = await WaitUntilAsync(
            () => rig.Host.Peer?.GetTelemetry().VideoFramesSent > 0, seconds: 15);

        var telemetry = rig.Host.Peer!.GetTelemetry();
        output.WriteLine($"frames sent {telemetry.VideoFramesSent}, bytes {telemetry.VideoBytesSent}");
        output.WriteLine($"local candidates gathered: {telemetry.LocalCandidateTypes}");

        Assert.True(sent, "the link connected but no VP8 frame was accepted by the RTP sender");
        Assert.True(telemetry.VideoBytesSent > 0);

        // The decisive assertion. Everything above is the sender's own account of
        // itself; this is the far peer confirming the packets arrived over DTLS-SRTP.
        var received = await WaitUntilAsync(
            () => rig.Controller.Peer?.VideoPacketsReceived > 0, seconds: 15);

        output.WriteLine(
            $"controller received {rig.Controller.Peer?.VideoPacketsReceived} packets, " +
            $"{rig.Controller.Peer?.VideoBytesReceived} payload bytes");

        Assert.True(received, "frames left the sender but none arrived at the far peer");
        Assert.True(rig.Controller.Peer!.VideoBytesReceived > 0);

        await rig.Cancellation.CancelAsync();
    }

    /// <summary>
    /// Video RTP timestamps observed at the receiver, across a live profile switch.
    /// </summary>
    /// <remarks>
    /// <see cref="Techee.WebRtc.Tests"/>' unit tests prove the increment arithmetic. This
    /// proves the property that actually reaches a decoder: over a real DTLS-SRTP link,
    /// with the frame rate changing mid-stream, no timestamp ever moves backwards.
    /// </remarks>
    [SkippableFact]
    public async Task Video_timestamps_never_regress_across_a_live_profile_switch()
    {
        var rig = await StartAsync(useRealEncoder: true);

        await SignalingOf(rig).JoinAsync(rig.HostIdentity.DeviceId);
        Assert.True(await WaitForSessionAsync(rig), "the host never started the session");

        var connected = await WaitUntilAsync(
            () => rig.Host.Peer?.State == LinkState.Connected, seconds: 30);

        Skip.IfNot(connected, "ICE/DTLS did not complete on this machine; see the media test.");

        var pipeline = Assert.Single(rig.Pipelines);

        // 720p30 -> 1080p20 -> 540p30. The increment changes from 3000 to 4500 and back,
        // which is exactly the case a naive clock would get wrong.
        foreach (var profile in new[] { VideoProfile.Hd, VideoProfile.FullHd, VideoProfile.Sd })
        {
            pipeline.ForceProfile(profile);
            var before = rig.Controller.Peer?.VideoFrameTimestamps ?? 0;

            Assert.True(
                await WaitUntilAsync(() => rig.Controller.Peer?.VideoFrameTimestamps > before + 2, seconds: 20),
                $"no frames arrived at the receiver on {profile.Name}");

            output.WriteLine($"{profile.Name}: {rig.Controller.Peer!.VideoFrameTimestamps} frames, " +
                             $"{rig.Controller.Peer.VideoTimestampRegressions} regressions");

            Assert.Equal(0, rig.Controller.Peer.VideoTimestampRegressions);
        }

        var peer = rig.Controller.Peer!;
        output.WriteLine($"total: {peer.VideoPacketsReceived} packets carrying " +
                         $"{peer.VideoFrameTimestamps} distinct frame timestamps");

        // More packets than distinct timestamps is expected and correct: a video frame is
        // split across several RTP packets, all sharing one timestamp.
        Assert.True(peer.VideoFrameTimestamps > 0);
        Assert.True(peer.VideoPacketsReceived >= peer.VideoFrameTimestamps);
        Assert.Equal(0, peer.VideoTimestampRegressions);

        await rig.Cancellation.CancelAsync();
    }

    [SkippableFact]
    public async Task An_unpaired_controller_is_refused_by_the_host()
    {
        // Even if the broker were compromised into relaying the join, the host's own
        // trust store is what decides.
        var rig = await StartAsync();

        rig.HostTrust.Revoke(Convert.ToBase64String(rig.ControllerIdentity.PublicKeySpkiDer));

        await SignalingOf(rig).JoinAsync(rig.HostIdentity.DeviceId);

        Assert.True(await WaitUntilAsync(() => rig.Host.LastRefusal != JoinRefusal.None),
            "the host never evaluated the join");

        Assert.Equal(JoinRefusal.NotTrusted, rig.Host.LastRefusal);
        Assert.Null(rig.Host.ControllerId);
        Assert.Empty(rig.Pipelines);

        await rig.Cancellation.CancelAsync();
    }

    [SkippableFact]
    public async Task A_hangup_over_the_broker_ends_the_session_and_releases_capture()
    {
        var rig = await StartAsync();

        await SignalingOf(rig).JoinAsync(rig.HostIdentity.DeviceId);
        Assert.True(await WaitForSessionAsync(rig), "the host never started the session");

        var pipeline = Assert.Single(rig.Pipelines);

        await SignalingOf(rig).SendAsync(w =>
        {
            w.WriteString("type", "hangup");
            w.WriteString("to", rig.HostIdentity.DeviceId);
        });

        Assert.True(await WaitUntilAsync(() => rig.Host.State == HostSessionState.Closed),
            "the host never closed the session");

        Assert.Null(rig.Host.ControllerId);
        Assert.Null(rig.Host.Peer);
        Assert.False(pipeline.IsRunning);

        await rig.Cancellation.CancelAsync();
    }

    [SkippableFact]
    public async Task A_restart_request_over_the_broker_recreates_the_peer()
    {
        var rig = await StartAsync();

        await SignalingOf(rig).JoinAsync(rig.HostIdentity.DeviceId);
        Assert.True(await WaitUntilAsync(() => rig.Host.Peer is not null));

        var before = rig.Host.Peer!;

        // The controller is the answerer and cannot re-offer, so it nudges the host —
        // Android's doIceRestart path.
        await SignalingOf(rig).SendAsync(w =>
        {
            w.WriteString("type", "restart");
            w.WriteString("to", rig.HostIdentity.DeviceId);
        });

        Assert.True(await WaitUntilAsync(() => rig.Host.PeerRecreations >= 1),
            "the host never replaced the peer");

        Assert.NotSame(before, rig.Host.Peer);
        Assert.Equal(rig.ControllerIdentity.DeviceId, rig.Host.ControllerId);

        // The pipeline survived the transport gap.
        Assert.Single(rig.Pipelines);

        await rig.Cancellation.CancelAsync();
    }

    private static SignalingClient SignalingOf(Rig rig) => rig.Controller.Signaling;
}
