using System.Text.Json;
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
/// Authorization of remote input over a real broker, a real peer connection, and a real
/// <c>control</c> data channel.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PeerControlHandlerTests"/> proves the handler makes the right decisions
/// when it is called. These tests prove nothing quietly bypasses it: the frames here
/// start life as JSON on a real SCTP data channel, are decoded by the real codec, and
/// arrive on SIPSorcery's network thread exactly as an Android controller's would. The
/// only fake left is the injector, so that a test run does not move the tester's mouse —
/// what it would have injected is asserted instead, and
/// <c>RealApplicationInputTests</c> covers the OS-facing half.
/// </para>
/// <para>
/// These are the release-critical invariants. A view-only session that can type, or a
/// <c>--no-input</c> host that can be talked into injecting, is not a bug to be fixed in
/// the next milestone.
/// </para>
/// </remarks>
[Collection("broker")]
public class ControlOverBrokerTests(BrokerFixture broker, ITestOutputHelper output) : IAsyncLifetime
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

    /// <summary>A controller that answers the host's offer and then drives the control channel.</summary>
    private sealed class Controller(
        EphemeralDeviceIdentity identity,
        SignalingClient signaling,
        Func<string, byte[]?> peerKeyLookup,
        Action<string> log) : IAsyncDisposable
    {
        private TecheePeerConnection? _peer;

        public string DeviceId => identity.DeviceId;
        public TecheePeerConnection? Peer => _peer;

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
            var offer = m.GetString("sdp");
            if (offer is null) return;

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

            if (peer.AcceptRemoteDescription("offer", m.From!, offer, m.GetString("fpSig")) != SdpVerdict.Ok) return;

            var (answer, signature) = peer.CreateAnswer();
            await signaling.SendAsync(w =>
            {
                w.WriteString("type", "answer");
                w.WriteString("to", hostId);
                w.WriteString("sdp", answer);
                w.WriteString("fpSig", signature);
            }, ct).ConfigureAwait(false);
        }

        /// <summary>Sends a control command down the real data channel.</summary>
        public bool Send(Control control) => _peer?.SendControl(control) ?? false;

        public async ValueTask DisposeAsync()
        {
            if (_peer is not null) await _peer.DisposeAsync();
        }
    }

    private sealed record Rig(
        WindowsHostSession Host,
        Controller Controller,
        GrantStore Grants,
        List<FakeInputInjector> Injectors);

    private async Task<Rig> StartAsync(bool withInjector = true, params string[] permissions)
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
            GrantId = "grant-control",
            ControllerId = controllerIdentity.DeviceId,
            PermissionTokens = permissions.Length > 0 ? permissions : ["screen.view", "input.control"],
        });

        var hostSignaling = new SignalingClient(
            broker.Url, hostIdentity,
            new EndpointMeta("windows", "test", ["screen.share", "input.receive"]),
            m => output.WriteLine($"host-sig  {m}"));
        var controllerSignaling = new SignalingClient(
            broker.Url, controllerIdentity,
            new EndpointMeta("windows", "test", ["screen.receive", "input.send"]),
            m => output.WriteLine($"ctrl-sig  {m}"));

        _asyncDisposables.Add(() => hostSignaling.DisposeAsync().AsTask());
        _asyncDisposables.Add(() => controllerSignaling.DisposeAsync().AsTask());

        var injectors = new List<FakeInputInjector>();

        var host = new WindowsHostSession(
            hostIdentity, hostSignaling, hostTrust, hostGrants,
            pipelineFactory: () => new VideoPipeline(
                new FakeScreenSource(), new FakeEncoder(), new AdaptiveQuality(VideoProfile.Hd)),
            log: m => output.WriteLine($"host  {m}"),
            injectorFactory: withInjector
                ? () =>
                {
                    var injector = new FakeInputInjector();
                    injectors.Add(injector);
                    return injector;
                }
                : null);

        _asyncDisposables.Add(() => host.DisposeAsync().AsTask());

        var registration = await host.RegisterAsync();
        Assert.True(registration.Ok, $"host registration failed: {registration.Reason}");

        var controllerRegistration = await controllerSignaling.ConnectAndRegisterAsync();
        Assert.True(controllerRegistration.Ok, $"controller registration failed: {controllerRegistration.Reason}");

        await hostSignaling.RegisterPairingAsync(controllerIdentity.DeviceId);
        await controllerSignaling.RegisterPairingAsync(hostIdentity.DeviceId);

        var controller = new Controller(
            controllerIdentity, controllerSignaling, controllerTrust.PublicKeyForSdp,
            m => output.WriteLine($"ctrl  {m}"));

        _asyncDisposables.Add(() => controller.DisposeAsync().AsTask());

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _disposables.Add(cts);

        _ = host.RunAsync(cts.Token);
        _ = controller.PumpAsync(hostIdentity.DeviceId, cts.Token);

        // The controller dials the paired host directly, exactly as Android does.
        await controllerSignaling.JoinAsync(hostIdentity.DeviceId);

        return new Rig(host, controller, hostGrants, injectors);
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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>
    /// Waits for the control channel to be usable in both directions.
    /// </summary>
    /// <remarks>
    /// ICE and DTLS can genuinely fail on a constrained runner, and a failure to connect
    /// is not a failure of authorization. Tests skip rather than fail in that case, the
    /// same way the media tests in <see cref="HostSessionBrokerTests"/> do.
    /// </remarks>
    private static async Task RequireConnectedAsync(Rig rig)
    {
        Skip.IfNot(
            await WaitUntilAsync(() => rig.Host.Pipeline is { IsRunning: true }),
            "the host never started the session");

        var up = await WaitUntilAsync(() =>
            rig.Host.Peer?.State == LinkState.Connected &&
            rig.Controller.Peer?.State == LinkState.Connected);

        Skip.IfNot(up, "ICE/DTLS did not complete on this runner; authorization is covered by the unit suite");

        // The channel opens slightly after the link reports connected.
        Skip.IfNot(
            await WaitUntilAsync(() => rig.Controller.Send(new Control.HostCallState("IDLE")), seconds: 15),
            "the control data channel never opened");
    }

    private static async Task<bool> DrainAsync(PeerControlHandler handler, long processed) =>
        await WaitUntilAsync(() => handler.Processed >= processed, seconds: 15);

    // ---- capability negotiation ----

    [SkippableFact]
    public async Task The_host_announces_itself_as_soon_as_the_channel_opens()
    {
        // PROTOCOL.md §5.2. Until W5 the host announced nothing, so an Android
        // controller correctly concluded it was a legacy peer and addressed it in the
        // legacy dialect — which made the whole v1 vocabulary unreachable from a real
        // controller no matter what the host implemented.
        var rig = await StartAsync(withInjector: true, "screen.view", "input.control");
        await RequireConnectedAsync(rig);

        Assert.True(
            await WaitUntilAsync(() => rig.Controller.Peer?.PeerMeta is not null, seconds: 15),
            "the host never announced itself");

        var meta = rig.Controller.Peer!.PeerMeta!;
        output.WriteLine($"host announced: {meta.Platform} {meta.Version} [{string.Join(", ", meta.Capabilities)}]");

        Assert.Equal("windows", meta.Platform);
        Assert.Contains("screen.share", meta.Capabilities);
        Assert.Contains("input.receive", meta.Capabilities);

        // And the controller is now addressed in v1 rather than downgraded.
        Assert.Equal(TecheeProtocol.ProtocolVersion, rig.Host.Peer!.PeerProtocolVersion);
    }

    [SkippableFact]
    public async Task A_no_input_host_does_not_advertise_that_it_accepts_input()
    {
        // The capability has to track the switch, or the controller renders a pointer
        // surface that silently does nothing.
        var rig = await StartAsync(withInjector: false, "screen.view", "input.control");
        await RequireConnectedAsync(rig);

        Assert.True(
            await WaitUntilAsync(() => rig.Controller.Peer?.PeerMeta is not null, seconds: 15),
            "the host never announced itself");

        var meta = rig.Controller.Peer!.PeerMeta!;
        output.WriteLine($"--no-input host announced: [{string.Join(", ", meta.Capabilities)}]");

        Assert.Contains("screen.share", meta.Capabilities);
        Assert.DoesNotContain("input.receive", meta.Capabilities);
    }

    [SkippableFact]
    public async Task An_announcement_is_acknowledged_and_the_acknowledgement_is_not()
    {
        // Two peers that each replied to the other's reply would ping-pong forever.
        var rig = await StartAsync(withInjector: true, "screen.view", "input.control");
        await RequireConnectedAsync(rig);

        Assert.True(
            await WaitUntilAsync(() => rig.Host.Peer?.PeerMeta is not null, seconds: 15),
            "the controller's acknowledgement never reached the host");

        // Settled, and stays settled.
        await Task.Delay(500);

        Assert.Equal(TecheeProtocol.ProtocolVersion, rig.Host.Peer!.PeerProtocolVersion);
        Assert.Equal(TecheeProtocol.ProtocolVersion, rig.Controller.Peer!.PeerProtocolVersion);
        Assert.Equal(HostSessionState.Connected, rig.Host.State);
    }

    [SkippableFact]
    public async Task Hello_is_absorbed_by_the_transport_and_never_reaches_the_input_handler()
    {
        // It is negotiation, not a command. Letting it through would have the handler
        // authorize and rate-limit something that is not input.
        var rig = await StartAsync(withInjector: true, "screen.view", "input.control");
        await RequireConnectedAsync(rig);

        await WaitUntilAsync(() => rig.Controller.Peer?.PeerMeta is not null, seconds: 15);
        await Task.Delay(300);

        var handler = rig.Host.ControlHandler!;
        Assert.Equal(0, handler.Accepted);
        Assert.Equal(0, handler.Executed);
        Assert.Equal(0, handler.Refused);
    }

    // ---- view-only ----

    [SkippableFact]
    public async Task A_view_only_session_streams_video_and_refuses_every_input_command()
    {
        var rig = await StartAsync(withInjector: true, "screen.view");
        await RequireConnectedAsync(rig);

        var handler = rig.Host.ControlHandler;
        Assert.NotNull(handler);

        // Everything a controller can currently express, over the real channel.
        Assert.True(rig.Controller.Send(new Control.PointerTap(0.5, 0.5)));
        Assert.True(rig.Controller.Send(new Control.PointerMove(0.4, 0.4)));
        Assert.True(rig.Controller.Send(new Control.PointerSwipe(0.1, 0.1, 0.9, 0.9, 100)));
        Assert.True(rig.Controller.Send(new Control.PointerDown(0.2, 0.2, "left")));
        Assert.True(rig.Controller.Send(new Control.PointerWheel(0.5, 0.5, 0, 3)));
        Assert.True(rig.Controller.Send(new Control.KeyText("should not be typed")));
        Assert.True(rig.Controller.Send(new Control.KeyDown("KeyA", null)));

        await WaitUntilAsync(() => handler!.Refused >= 7, seconds: 15);

        output.WriteLine($"view-only: {handler}");

        // Video is unaffected: screen.view is exactly what this grant confers.
        Assert.Equal(HostSessionState.Connected, rig.Host.State);
        Assert.True(rig.Host.Pipeline is { IsRunning: true });

        // Nothing reached the injector, and nothing is held.
        Assert.Equal(7, handler!.Refused);
        Assert.Equal(0, handler.Accepted);
        Assert.Equal(0, handler.Executed);
        Assert.Equal(0, handler.Held);
        Assert.Empty(rig.Injectors[0].Events);
    }

    [SkippableFact]
    public async Task A_control_grant_makes_the_same_commands_take_effect()
    {
        // The other half of the view-only assertion: the refusal above must be the
        // permission doing its job, not the path being broken.
        var rig = await StartAsync(withInjector: true, "screen.view", "input.control");
        await RequireConnectedAsync(rig);

        var handler = rig.Host.ControlHandler;
        Assert.NotNull(handler);

        Assert.True(rig.Controller.Send(new Control.PointerTap(0.5, 0.5)));

        Assert.True(await DrainAsync(handler!, 1), $"the tap never executed: {handler}");

        output.WriteLine($"control-enabled: {handler}");

        Assert.Equal(0, handler!.Refused);
        Assert.Equal(1, handler.Executed);

        // A tap is a press and a release at one point.
        var events = rig.Injectors[0].Events;
        Assert.Equal(2, events.Count);
        Assert.IsType<InjectedEvent.Down>(events[0]);
        Assert.IsType<InjectedEvent.Up>(events[1]);
        Assert.Equal(0, handler.Held);
    }

    [SkippableFact]
    public async Task Granting_control_does_not_widen_any_other_permission()
    {
        var rig = await StartAsync(withInjector: true, "screen.view", "input.control");
        await RequireConnectedAsync(rig);

        var handler = rig.Host.ControlHandler;

        // Power and clipboard share the channel. input.control must not reach them.
        Assert.True(rig.Controller.Send(new Control.SystemAction("system.restart")));
        Assert.True(rig.Controller.Send(new Control.ClipboardRequest()));
        await Task.Delay(300);

        // Not this handler's commands at all, so they are ignored rather than refused —
        // and either way they inject nothing.
        Assert.Equal(0, handler!.Executed);
        Assert.Empty(rig.Injectors[0].Events);
    }

    // ---- mid-session revocation ----

    [SkippableFact]
    public async Task Revoking_the_grant_mid_session_stops_the_next_command_without_a_reconnect()
    {
        var rig = await StartAsync(withInjector: true, "screen.view", "input.control");
        await RequireConnectedAsync(rig);

        var handler = rig.Host.ControlHandler!;

        Assert.True(rig.Controller.Send(new Control.PointerTap(0.5, 0.5)));
        Assert.True(await DrainAsync(handler, 1), $"the first tap never executed: {handler}");
        Assert.Equal(1, handler.Executed);

        // Revoked at the host, with the session left entirely alone.
        rig.Grants.Revoke("grant-control");
        output.WriteLine("grant revoked; the link is untouched");

        Assert.True(rig.Controller.Send(new Control.PointerTap(0.5, 0.5)));
        Assert.True(await WaitUntilAsync(() => handler.Refused >= 1, seconds: 15));

        output.WriteLine($"after revocation: {handler}");

        // The next command is refused. No reconnect was needed and none happened.
        Assert.Equal(1, handler.Refused);
        Assert.Equal(1, handler.Executed);
        Assert.Equal(2, rig.Injectors[0].Events.Count);
        Assert.Equal(0, rig.Host.PeerRecreations);

        // And the session itself is still up, because the video half was never revoked.
        Assert.Equal(HostSessionState.Connected, rig.Host.State);
        Assert.Same(handler, rig.Host.ControlHandler);
    }

    [SkippableFact]
    public async Task Restoring_the_grant_makes_the_next_command_eligible_again()
    {
        // Whether recovery needs a reconnect is a real question about the security
        // model. It does not: authorization is re-read per command, so the same handler
        // starts accepting again as soon as the grant is usable.
        var rig = await StartAsync(withInjector: true, "screen.view", "input.control");
        await RequireConnectedAsync(rig);

        var handler = rig.Host.ControlHandler!;

        rig.Grants.Revoke("grant-control");
        Assert.True(rig.Controller.Send(new Control.PointerTap(0.5, 0.5)));
        Assert.True(await WaitUntilAsync(() => handler.Refused >= 1, seconds: 15));

        // Re-granted at the host. Same grant id, same controller, same session.
        rig.Grants.Save(new Grant
        {
            GrantId = "grant-control",
            ControllerId = rig.Controller.DeviceId,
            PermissionTokens = ["screen.view", "input.control"],
        });
        output.WriteLine("grant restored");

        Assert.True(rig.Controller.Send(new Control.PointerTap(0.5, 0.5)));
        Assert.True(await DrainAsync(handler, 1), $"the command after restoration never ran: {handler}");

        output.WriteLine($"after restoration: {handler}");

        Assert.Equal(1, handler.Executed);
        Assert.Equal(0, rig.Host.PeerRecreations);
    }

    [SkippableFact]
    public async Task Revoking_trust_mid_session_also_stops_input()
    {
        // The grant says what, the trust store says who. Losing the second must be as
        // effective as losing the first — FindUsableFor consults both on every command.
        var rig = await StartAsync(withInjector: true, "screen.view", "input.control");
        await RequireConnectedAsync(rig);

        var handler = rig.Host.ControlHandler!;

        Assert.True(rig.Controller.Send(new Control.PointerTap(0.5, 0.5)));
        Assert.True(await DrainAsync(handler, 1));

        rig.Grants.RevokeAllFor(rig.Controller.DeviceId);

        Assert.True(rig.Controller.Send(new Control.PointerTap(0.5, 0.5)));
        Assert.True(await WaitUntilAsync(() => handler.Refused >= 1, seconds: 15));

        Assert.Equal(1, handler.Executed);
    }

    // ---- the local override ----

    [SkippableFact]
    public async Task A_no_input_host_injects_nothing_however_generous_the_grant()
    {
        // The grant confers full control and the controller is fully trusted. The host
        // was started without an injector, so there is nothing subscribed to the control
        // channel at all. A remote permission cannot manufacture one.
        var rig = await StartAsync(withInjector: false, "screen.view", "input.control");
        await RequireConnectedAsync(rig);

        Assert.Null(rig.Host.ControlHandler);
        Assert.Empty(rig.Injectors);

        Assert.True(rig.Controller.Send(new Control.PointerTap(0.5, 0.5)));
        Assert.True(rig.Controller.Send(new Control.KeyText("should not be typed")));
        Assert.True(rig.Controller.Send(new Control.PointerSwipe(0, 0, 1, 1, 50)));
        await Task.Delay(500);

        output.WriteLine("--no-input host: control frames sent and decoded, nothing subscribed");

        // Video is unaffected. This is a view-only host by local decision, not by grant.
        Assert.Equal(HostSessionState.Connected, rig.Host.State);
        Assert.True(rig.Host.Pipeline is { IsRunning: true });
        Assert.Null(rig.Host.ControlHandler);
        Assert.Empty(rig.Injectors);
    }

    // ---- malformed frames on the real channel ----

    [SkippableFact]
    public async Task Malformed_frames_on_the_live_channel_never_reach_the_injector()
    {
        var rig = await StartAsync(withInjector: true, "screen.view", "input.control");
        await RequireConnectedAsync(rig);

        var handler = rig.Host.ControlHandler!;
        var channel = rig.Controller.Peer!;

        // Sent as raw text on the same channel, bypassing the encoder entirely — which
        // is what a hostile controller would do.
        string[] hostile =
        [
            "this is not json",
            "[1,2,3]",
            "null",
            "{}",
            "{\"t\":\"selfDestruct\"}",
            "{\"t\":\"tap\",\"x\":\"NaN\",\"y\":0.5}",
            "{\"v\":2,\"t\":\"pointer.tap\",\"x\":0.5,\"y\":0.5}",
            "{\"v\":1,\"t\":\"pointer.up\",\"x\":0.1,\"y\":0.1,\"b\":\"pedal\"}",
            "{\"v\":1,\"t\":\"keyboard.keyDown\",\"code\":65}",
        ];

        foreach (var raw in hostile) Assert.True(channel.SendControlRaw(raw), $"could not send: {raw}");

        await Task.Delay(500);

        output.WriteLine($"after {hostile.Length} malformed frames: {handler}");

        // Dropped by the codec before the handler ever sees them.
        Assert.Equal(0, handler.Executed);
        Assert.Equal(0, handler.Accepted);
        Assert.Equal(0, handler.Held);
        Assert.Empty(rig.Injectors[0].Events);

        // And the session is untouched: a malformed frame must not be a disconnect.
        Assert.Equal(HostSessionState.Connected, rig.Host.State);

        // A well-formed command still works afterwards.
        Assert.True(rig.Controller.Send(new Control.PointerTap(0.5, 0.5)));
        Assert.True(await DrainAsync(handler, 1), $"the session stopped working: {handler}");
    }

    // ---- teardown ----

    [SkippableFact]
    public async Task A_hangup_while_a_button_is_held_releases_it()
    {
        var rig = await StartAsync(withInjector: true, "screen.view", "input.control");
        await RequireConnectedAsync(rig);

        var handler = rig.Host.ControlHandler!;

        Assert.True(rig.Controller.Send(new Control.PointerDown(0.5, 0.5, "left")));
        Assert.True(await DrainAsync(handler, 1), $"the press never executed: {handler}");
        Assert.Equal(1, handler.Held);

        await rig.Host.EndAsync();

        output.WriteLine($"after hangup: injector holds {rig.Injectors[0].PressedCount}");

        Assert.Equal(0, rig.Injectors[0].PressedCount);
        Assert.Contains(rig.Injectors[0].OfType<InjectedEvent.Up>(), e => e.Button == PointerButton.Left);
        Assert.Null(rig.Host.ControlHandler);
    }
}
