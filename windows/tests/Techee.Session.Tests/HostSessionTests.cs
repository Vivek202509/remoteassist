using System.Text.Json;
using Techee.Crypto;
using Techee.Protocol;
using Techee.Session;
using Techee.Signaling;
using Techee.Store;
using Techee.WebRtc;
using Techee.Windows.Host;
using Techee.Windows.Host.Tests;
using Xunit.Abstractions;

namespace Techee.Session.Tests;

/// <summary>
/// The Windows host's session orchestration: authorisation, ownership, negotiation,
/// recovery and teardown.
/// </summary>
/// <remarks>
/// <para>
/// Messages are fed to <see cref="WindowsHostSession.HandleAsync"/> directly, exactly as
/// the broker loop would. The signaling client is deliberately never connected — its
/// sends drop silently on a closed socket — so these tests are about what the host
/// <i>decides</i>. That the same decisions hold against the real broker is
/// <see cref="HostSessionBrokerTests"/>.
/// </para>
/// <para>
/// Every case here is an authorisation property. The broker is untrusted, so a host that
/// believed <c>unattended</c>, <c>grantId</c> or <c>permissions</c> from a join-request
/// could be handed a session it never granted.
/// </para>
/// </remarks>
public class HostSessionTests(ITestOutputHelper output) : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private T Track<T>(T value) where T : IDisposable
    {
        _disposables.Add(value);
        return value;
    }

    private sealed record Fixture(
        WindowsHostSession Session,
        EphemeralDeviceIdentity HostIdentity,
        EphemeralDeviceIdentity ControllerIdentity,
        TrustStore Trust,
        GrantStore Grants,
        List<VideoPipeline> PipelinesCreated);

    private Fixture Build(
        bool trustController = true,
        bool withGrant = true,
        bool workstationLocked = false,
        long? grantExpiresAt = null)
    {
        var hostIdentity = Track(new EphemeralDeviceIdentity());
        var controllerIdentity = Track(new EphemeralDeviceIdentity());

        var trust = new TrustStore(new InMemoryStore());
        var grants = new GrantStore(new InMemoryStore());

        if (trustController)
        {
            var pub = Convert.ToBase64String(controllerIdentity.PublicKeySpkiDer);
            trust.Save(new PeerIdentity
            {
                PublicKeySpkiB64 = pub,
                Name = "Test controller",
                SharedSecretB64 = Convert.ToBase64String(new byte[32]),
                State = TrustState.PendingConfirm,
            });
            trust.Confirm(pub);
        }

        if (withGrant)
        {
            grants.Save(new Grant
            {
                GrantId = "grant-1",
                ControllerId = controllerIdentity.DeviceId,
                PermissionTokens = ["screen.view", "input.control"],
                ExpiresAt = grantExpiresAt,
            });
        }

        // Never connected. Sends drop on a closed socket, which is what makes the
        // decision logic testable without a broker.
        var signaling = new SignalingClient(new Uri("ws://127.0.0.1:9"), hostIdentity);
        _disposables.Add(new AsyncDisposeAdapter(signaling));

        var pipelines = new List<VideoPipeline>();

        var session = new WindowsHostSession(
            hostIdentity,
            signaling,
            trust,
            grants,
            pipelineFactory: () =>
            {
                var pipeline = new VideoPipeline(
                    new FakeScreenSource(), new FakeEncoder(), new AdaptiveQuality(VideoProfile.Hd));
                pipelines.Add(pipeline);
                return pipeline;
            },
            workstationLocked: () => workstationLocked,
            log: m => output.WriteLine(m));

        return new Fixture(session, hostIdentity, controllerIdentity, trust, grants, pipelines);
    }

    private sealed class AsyncDisposeAdapter(IAsyncDisposable inner) : IDisposable
    {
        public void Dispose() => inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static SignalingMessage Message(string type, object body)
    {
        var json = JsonSerializer.Serialize(body);
        using var doc = JsonDocument.Parse(json);
        return new SignalingMessage(type, doc.RootElement.Clone());
    }

    private static SignalingMessage JoinRequest(string controllerId) =>
        Message("join-request", new
        {
            controllerId,
            // The broker's claims. Deliberately generous and deliberately ignored.
            unattended = true,
            grantId = "attacker-supplied",
            permissions = new[] { "screen.view", "input.control", "power.reboot" },
            peerMetaTrusted = false,
        });

    // ---- authorisation ----

    [Fact]
    public async Task An_untrusted_controller_is_refused()
    {
        var f = Build(trustController: false);
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        Assert.Equal(JoinRefusal.NotTrusted, f.Session.LastRefusal);
        Assert.Null(f.Session.ControllerId);
        Assert.Null(f.Session.Peer);
        // No capture device was opened for a controller that was never authorised.
        Assert.Empty(f.PipelinesCreated);
    }

    [Fact]
    public async Task A_trusted_controller_with_no_grant_is_refused()
    {
        var f = Build(withGrant: false);
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        Assert.Equal(JoinRefusal.NoGrant, f.Session.LastRefusal);
        Assert.Null(f.Session.Peer);
        Assert.Empty(f.PipelinesCreated);
    }

    [Fact]
    public async Task An_expired_grant_is_refused()
    {
        // Expiry is honoured including 0 — the epoch, i.e. maximally expired.
        var f = Build(grantExpiresAt: 0);
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        Assert.Equal(JoinRefusal.NoGrant, f.Session.LastRefusal);
        Assert.Null(f.Session.Peer);
    }

    [Fact]
    public async Task A_malformed_join_request_is_refused_without_a_crash()
    {
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(Message("join-request", new { unattended = true }));

        Assert.Equal(JoinRefusal.Malformed, f.Session.LastRefusal);
        Assert.Null(f.Session.Peer);
    }

    [Fact]
    public async Task An_authorised_controller_gets_a_session_and_an_offer()
    {
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        Assert.Equal(JoinRefusal.None, f.Session.LastRefusal);
        Assert.Equal(HostSessionState.Negotiating, f.Session.State);
        Assert.Equal(f.ControllerIdentity.DeviceId, f.Session.ControllerId);

        // The session runs under this host's own grant, not the id the broker supplied.
        Assert.Equal("grant-1", f.Session.ActiveGrantId);

        Assert.NotNull(f.Session.Peer);
        Assert.Equal(f.ControllerIdentity.DeviceId, f.Session.Peer!.PeerId);

        // Capture came up with the session, and its frames are pointed at the transport.
        var pipeline = Assert.Single(f.PipelinesCreated);
        Assert.True(pipeline.IsRunning);
        Assert.True(pipeline.HasSink);
    }

    [Fact]
    public async Task A_second_controller_cannot_displace_the_owner()
    {
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        var owner = f.Session.Peer;

        // A different, also-trusted device dialling in mid-session.
        using var intruder = new EphemeralDeviceIdentity();
        var pub = Convert.ToBase64String(intruder.PublicKeySpkiDer);
        f.Trust.Save(new PeerIdentity
        {
            PublicKeySpkiB64 = pub,
            Name = "Intruder",
            SharedSecretB64 = Convert.ToBase64String(new byte[32]),
            State = TrustState.PendingConfirm,
        });
        f.Trust.Confirm(pub);
        f.Grants.Save(new Grant
        {
            GrantId = "grant-2",
            ControllerId = intruder.DeviceId,
            PermissionTokens = ["screen.view"],
        });

        await f.Session.HandleAsync(JoinRequest(intruder.DeviceId));

        Assert.Equal(JoinRefusal.AlreadyOwned, f.Session.LastRefusal);
        Assert.Equal(f.ControllerIdentity.DeviceId, f.Session.ControllerId);
        Assert.Same(owner, f.Session.Peer);
        Assert.Single(f.PipelinesCreated);
    }

    // ---- session traffic ----

    [Fact]
    public async Task An_inbound_offer_is_ignored_because_the_host_is_the_offerer()
    {
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        var peer = f.Session.Peer;

        await f.Session.HandleAsync(Message("offer", new
        {
            from = f.ControllerIdentity.DeviceId,
            sdp = "v=0\r\n",
            fpSig = "",
        }));

        Assert.Same(peer, f.Session.Peer);
        Assert.Equal(HostSessionState.Negotiating, f.Session.State);
    }

    [Fact]
    public async Task A_hangup_from_a_stranger_does_not_end_the_session()
    {
        // Otherwise any registered device could end anyone else's session.
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        using var stranger = new EphemeralDeviceIdentity();
        await f.Session.HandleAsync(Message("hangup", new { from = stranger.DeviceId }));

        Assert.Equal(f.ControllerIdentity.DeviceId, f.Session.ControllerId);
        Assert.NotNull(f.Session.Peer);
    }

    [Fact]
    public async Task A_hangup_from_the_owner_ends_the_session_and_releases_capture()
    {
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        var pipeline = Assert.Single(f.PipelinesCreated);

        await f.Session.HandleAsync(Message("hangup", new { from = f.ControllerIdentity.DeviceId }));

        Assert.Equal(HostSessionState.Closed, f.Session.State);
        Assert.Null(f.Session.ControllerId);
        Assert.Null(f.Session.Peer);

        // No viewer means no capture: the DXGI session and encoder are released, not
        // merely paused.
        Assert.False(pipeline.IsRunning);
        Assert.Null(f.Session.Pipeline);
    }

    [Fact]
    public async Task A_restart_request_from_a_stranger_is_ignored()
    {
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        var peer = f.Session.Peer;

        using var stranger = new EphemeralDeviceIdentity();
        await f.Session.HandleAsync(Message("restart", new { from = stranger.DeviceId }));

        Assert.Same(peer, f.Session.Peer);
        Assert.Equal(0, f.Session.PeerRecreations);
    }

    // ---- recovery: authenticated peer recreation ----

    [Fact]
    public async Task Recovery_replaces_the_peer_with_fresh_ice_credentials()
    {
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        var before = f.Session.Peer!;
        var beforeUfrag = Ufrag(before.CreateOffer().Sdp);

        await f.Session.RecoverAsync();

        var after = f.Session.Peer!;
        var afterUfrag = Ufrag(after.CreateOffer().Sdp);

        output.WriteLine($"ufrag {beforeUfrag} -> {afterUfrag}");

        Assert.NotSame(before, after);
        Assert.NotEqual(beforeUfrag, afterUfrag);
        Assert.Equal(1, f.Session.PeerRecreations);

        // Same controller, same grant. Recovery is not a renegotiation of who this is.
        Assert.Equal(f.ControllerIdentity.DeviceId, f.Session.ControllerId);
        Assert.Equal("grant-1", f.Session.ActiveGrantId);
    }

    [Fact]
    public async Task Recovery_reuses_the_pipeline_rather_than_reopening_capture()
    {
        // The expensive DXGI and encoder state must survive a transport gap, or every
        // blip costs a duplication-session teardown and rebuild.
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        await f.Session.RecoverAsync();
        await f.Session.RecoverAsync();

        Assert.Single(f.PipelinesCreated);
        Assert.True(f.PipelinesCreated[0].HasSink);
        Assert.Same(f.PipelinesCreated[0], f.Session.Pipeline);
    }

    [Fact]
    public async Task Repeated_recovery_leaves_exactly_one_peer()
    {
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        var seen = new List<TecheePeerConnection>();
        for (var i = 0; i < 5; i++)
        {
            await f.Session.RecoverAsync();
            seen.Add(f.Session.Peer!);
        }

        Assert.Equal(5, f.Session.PeerRecreations);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Same(seen[^1], f.Session.Peer);
        Assert.Single(f.PipelinesCreated);
    }

    [Fact]
    public async Task Concurrent_recovery_triggers_collapse_into_one_replacement()
    {
        // Five connection-state callbacks must not produce five replacement peers.
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => f.Session.RecoverAsync()));

        Assert.True(f.Session.PeerRecreations is >= 1 and <= 5, $"got {f.Session.PeerRecreations}");
        Assert.NotNull(f.Session.Peer);
        Assert.Single(f.PipelinesCreated);
    }

    [Fact]
    public async Task A_controller_revoked_while_the_link_was_down_does_not_come_back()
    {
        // The property that makes recovery safe: reconnecting is not a privilege.
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        Assert.NotNull(f.Session.Peer);

        f.Trust.Revoke(Convert.ToBase64String(f.ControllerIdentity.PublicKeySpkiDer));

        await f.Session.RecoverAsync();

        Assert.Equal(JoinRefusal.NotTrusted, f.Session.LastRefusal);
        Assert.Equal(HostSessionState.Closed, f.Session.State);
        Assert.Null(f.Session.Peer);
        Assert.Null(f.Session.ControllerId);
        Assert.False(f.PipelinesCreated[0].IsRunning);
    }

    [Fact]
    public async Task A_grant_revoked_while_the_link_was_down_does_not_come_back()
    {
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        f.Grants.Revoke("grant-1");

        await f.Session.RecoverAsync();

        Assert.Equal(JoinRefusal.NoGrant, f.Session.LastRefusal);
        Assert.Equal(HostSessionState.Closed, f.Session.State);
        Assert.Null(f.Session.Peer);
    }

    // ---- capture lifecycle ----

    [Fact]
    public async Task Capture_is_paused_for_the_gap_and_resumed_by_recovery()
    {
        // Encoding frames that have nowhere to go is pure cost on a machine someone is
        // also sitting at, and a recovery can last a while.
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        var pipeline = Assert.Single(f.PipelinesCreated);

        Assert.True(pipeline.IsRunning);
        Assert.Equal(1, pipeline.PumpGenerations);

        await f.Session.RecoverAsync();

        // Same pipeline, running again, on a second pump generation: the DXGI session
        // and the encoder were never rebuilt.
        Assert.Same(pipeline, f.Session.Pipeline);
        Assert.True(pipeline.IsRunning);
        Assert.Equal(2, pipeline.PumpGenerations);
        Assert.False(pipeline.IsDisposed);
        Assert.True(pipeline.HasSink);
    }

    [Fact]
    public async Task Many_reconnects_leave_one_pipeline_and_one_live_pump()
    {
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        for (var i = 0; i < 6; i++) await f.Session.RecoverAsync();

        var pipeline = Assert.Single(f.PipelinesCreated);

        output.WriteLine($"{pipeline.PumpGenerations} pump generations after 6 reconnects");

        Assert.Equal(7, pipeline.PumpGenerations);
        Assert.True(pipeline.IsRunning);
        Assert.False(pipeline.IsWedged);
        Assert.False(pipeline.IsDisposed);
        Assert.Equal(6, f.Session.PeerRecreations);
    }

    [Fact]
    public async Task Ending_the_session_disposes_capture_rather_than_only_stopping_it()
    {
        // No viewer means no Desktop Duplication session held open.
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        var pipeline = Assert.Single(f.PipelinesCreated);

        await f.Session.EndAsync();

        Assert.True(pipeline.IsDisposed);
        Assert.False(pipeline.IsRunning);
        Assert.Null(f.Session.Pipeline);
    }

    [Fact]
    public async Task Disposing_the_session_releases_capture_even_mid_session()
    {
        var f = Build();

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        var pipeline = Assert.Single(f.PipelinesCreated);

        await f.Session.DisposeAsync();

        Assert.True(pipeline.IsDisposed);
        Assert.False(pipeline.IsRunning);
    }

    [Fact]
    public async Task A_refused_reconnect_releases_capture()
    {
        // Recovery that fails re-authorisation must not leave the pump running with
        // nowhere to send.
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        var pipeline = Assert.Single(f.PipelinesCreated);

        f.Grants.Revoke("grant-1");
        await f.Session.RecoverAsync();

        Assert.Equal(HostSessionState.Closed, f.Session.State);
        Assert.True(pipeline.IsDisposed);
        Assert.False(pipeline.IsRunning);
    }

    private static string Ufrag(string sdp) => sdp
        .Split('\n')
        .Select(l => l.Trim())
        .First(l => l.StartsWith("a=ice-ufrag:", StringComparison.Ordinal))["a=ice-ufrag:".Length..];
}
