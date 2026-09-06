using System.Text.Json;
using Techee.Crypto;
using Techee.Protocol;
using Techee.Session;
using Techee.Signaling;
using Techee.Store;
using Techee.Windows.Host;
using Techee.Windows.Host.Tests;
using Xunit.Abstractions;

namespace Techee.Session.Tests;

/// <summary>
/// How the input handler is bound to, and unbound from, the peer transport.
/// </summary>
/// <remarks>
/// <para>
/// The properties here are the ones that go wrong invisibly. A handler that outlives its
/// peer keeps accepting input from a torn-down session; two handlers subscribed at once
/// execute every command twice, which turns one remote click into a double-click and one
/// keystroke into two characters. Neither shows up as an error, and both need a specific
/// sequence to reproduce by hand.
/// </para>
/// <para>
/// Recovery is the interesting case, because W3 replaces the whole transport rather than
/// restarting ICE in place. Every replacement must retire exactly one handler and attach
/// exactly one.
/// </para>
/// </remarks>
public class ControlLifecycleTests(ITestOutputHelper output) : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        GC.SuppressFinalize(this);
    }

    private T Track<T>(T value) where T : IDisposable
    {
        _disposables.Add(value);
        return value;
    }

    private sealed record Fixture(
        WindowsHostSession Session,
        EphemeralDeviceIdentity ControllerIdentity,
        GrantStore Grants,
        List<FakeInputInjector> Injectors);

    /// <param name="withInjector">
    /// False models a controller-only or <c>--no-input</c> host: no injector is supplied,
    /// so nothing ever subscribes to the control channel.
    /// </param>
    private Fixture Build(bool withInjector = true, params string[] permissions)
    {
        var hostIdentity = Track(new EphemeralDeviceIdentity());
        var controllerIdentity = Track(new EphemeralDeviceIdentity());

        var trust = new TrustStore(new InMemoryStore());
        var grants = new GrantStore(new InMemoryStore());

        var pub = Convert.ToBase64String(controllerIdentity.PublicKeySpkiDer);
        trust.Save(new PeerIdentity
        {
            PublicKeySpkiB64 = pub,
            Name = "Test controller",
            SharedSecretB64 = Convert.ToBase64String(new byte[32]),
            State = TrustState.PendingConfirm,
        });
        trust.Confirm(pub);

        grants.Save(new Grant
        {
            GrantId = "grant-1",
            ControllerId = controllerIdentity.DeviceId,
            PermissionTokens = permissions.Length > 0 ? permissions : ["screen.view", "input.control"],
        });

        var signaling = new SignalingClient(new Uri("ws://127.0.0.1:9"), hostIdentity);
        _disposables.Add(new AsyncDispose(signaling));

        var injectors = new List<FakeInputInjector>();

        var session = new WindowsHostSession(
            hostIdentity,
            signaling,
            trust,
            grants,
            pipelineFactory: () => new VideoPipeline(
                new FakeScreenSource(), new FakeEncoder(), new AdaptiveQuality(VideoProfile.Hd)),
            log: output.WriteLine,
            injectorFactory: withInjector
                ? () =>
                {
                    var injector = new FakeInputInjector();
                    injectors.Add(injector);
                    return injector;
                }
                : null);

        return new Fixture(session, controllerIdentity, grants, injectors);
    }

    private sealed class AsyncDispose(IAsyncDisposable inner) : IDisposable
    {
        public void Dispose() => inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static SignalingMessage JoinRequest(string controllerId)
    {
        var json = JsonSerializer.Serialize(new { controllerId, unattended = true });
        using var doc = JsonDocument.Parse(json);
        return new SignalingMessage("join-request", doc.RootElement.Clone());
    }

    // ---- attach and detach ----

    [Fact]
    public async Task A_session_attaches_exactly_one_handler_and_one_injector()
    {
        var f = Build();
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        Assert.NotNull(f.Session.ControlHandler);
        Assert.Single(f.Injectors);
    }

    [Fact]
    public async Task A_host_built_without_an_injector_never_attaches_a_handler()
    {
        // This is what --no-input relies on. The refusal is structural: there is nothing
        // subscribed to the control channel, so there is no flag to flip and no
        // authorization decision that could go the other way.
        var f = Build(withInjector: false);
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        Assert.NotNull(f.Session.ControllerId);
        Assert.Null(f.Session.ControlHandler);
        Assert.Empty(f.Injectors);
    }

    [Fact]
    public async Task A_full_grant_cannot_conjure_an_injector_on_a_no_input_host()
    {
        // The grant says input.control. The host was started without injection. The host
        // wins: a local operator's decision is not overridable by a remote permission.
        var f = Build(withInjector: false, "screen.view", "input.control");
        await using var _ = f.Session;

        await f.Session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        Assert.Null(f.Session.ControlHandler);
        Assert.Empty(f.Injectors);
    }

    [Fact]
    public async Task Ending_a_session_detaches_the_handler_and_disposes_the_injector()
    {
        var f = Build();
        await using var session = f.Session;

        await session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        var handler = session.ControlHandler;
        Assert.NotNull(handler);

        await session.EndAsync();

        Assert.Null(session.ControlHandler);

        // The retired handler must refuse work rather than merely being unreachable:
        // a frame already in flight on the network thread can still call into it.
        handler!.Handle(new Control.PointerTap(0.5, 0.5));
        Thread.Sleep(50);
        Assert.Empty(f.Injectors[0].Events);
    }

    [Fact]
    public async Task A_hangup_from_the_controller_detaches_the_handler()
    {
        var f = Build();
        await using var session = f.Session;

        await session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        Assert.NotNull(session.ControlHandler);

        var json = JsonSerializer.Serialize(new { from = f.ControllerIdentity.DeviceId });
        using var doc = JsonDocument.Parse(json);
        await session.HandleAsync(new SignalingMessage("hangup", doc.RootElement.Clone()));

        Assert.Null(session.ControlHandler);
    }

    [Fact]
    public async Task Ending_a_session_twice_is_clean()
    {
        var f = Build();
        await using var session = f.Session;

        await session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        await session.EndAsync();
        await session.EndAsync();

        Assert.Null(session.ControlHandler);
    }

    // ---- recovery ----

    [Fact]
    public async Task Recovery_retires_the_old_handler_and_attaches_exactly_one_new_one()
    {
        var f = Build();
        await using var session = f.Session;

        await session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        var first = session.ControlHandler;
        Assert.NotNull(first);

        await session.RecoverAsync();

        var second = session.ControlHandler;
        Assert.NotNull(second);
        Assert.NotSame(first, second);

        // One injector per peer, and the retired one disposed with its handler.
        Assert.Equal(2, f.Injectors.Count);
        Assert.Equal(1, session.PeerRecreations);
    }

    [Fact]
    public async Task A_late_frame_for_the_retired_peer_cannot_execute_against_the_new_one()
    {
        // The race recovery creates: a control frame decoded on the old peer's network
        // thread arrives after the replacement is live. It must die with its handler.
        var f = Build();
        await using var session = f.Session;

        await session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        var retired = session.ControlHandler;

        await session.RecoverAsync();

        retired!.Handle(new Control.PointerTap(0.5, 0.5));
        Thread.Sleep(50);

        Assert.Empty(f.Injectors[0].Events);
        Assert.Empty(f.Injectors[1].Events);
    }

    [Fact]
    public async Task One_frame_produces_one_injection_after_a_reconnect()
    {
        // If the old handler were still subscribed, a single frame would be executed
        // twice — one remote click becoming a double-click, one keystroke two characters.
        var f = Build();
        await using var session = f.Session;

        await session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));
        await session.RecoverAsync();

        var live = session.ControlHandler!;
        live.Handle(new Control.PointerTap(0.5, 0.5));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (live.Executed < 1 && DateTime.UtcNow < deadline) Thread.Sleep(1);
        Thread.Sleep(50);

        Assert.Equal(1, live.Executed);

        // A tap is one down and one up. Two of each would mean two handlers ran it.
        var current = f.Injectors[1];
        Assert.Single(current.OfType<InjectedEvent.Down>());
        Assert.Single(current.OfType<InjectedEvent.Up>());
        Assert.Empty(f.Injectors[0].Events);
    }

    [Fact]
    public async Task Repeated_recoveries_do_not_accumulate_handlers()
    {
        var f = Build();
        await using var session = f.Session;

        await session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        for (var i = 0; i < 3; i++) await session.RecoverAsync();

        Assert.Equal(3, session.PeerRecreations);
        Assert.Equal(4, f.Injectors.Count);

        var live = session.ControlHandler!;
        live.Handle(new Control.PointerTap(0.5, 0.5));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (live.Executed < 1 && DateTime.UtcNow < deadline) Thread.Sleep(1);
        Thread.Sleep(50);

        // Only the newest injector sees anything, whatever the recovery count.
        for (var i = 0; i < 3; i++) Assert.Empty(f.Injectors[i].Events);
        Assert.Equal(2, f.Injectors[3].Events.Count);
    }

    [Fact]
    public async Task Recovery_releases_anything_the_retired_peer_was_holding()
    {
        // A drag in progress when the link drops. The replacement peer starts with a
        // clean slate, and the user's mouse button is not left down in the meantime.
        var f = Build();
        await using var session = f.Session;

        await session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        var handler = session.ControlHandler!;
        handler.Handle(new Control.PointerDown(0.5, 0.5, "left"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.Executed < 1 && DateTime.UtcNow < deadline) Thread.Sleep(1);
        Assert.Equal(1, handler.Held);

        await session.RecoverAsync();

        Assert.Equal(0, f.Injectors[0].PressedCount);
        Assert.Contains(f.Injectors[0].OfType<InjectedEvent.Up>(), e => e.Button == PointerButton.Left);
    }

    [Fact]
    public async Task Disposing_the_session_releases_held_input()
    {
        var f = Build();
        var session = f.Session;

        await session.HandleAsync(JoinRequest(f.ControllerIdentity.DeviceId));

        var handler = session.ControlHandler!;
        handler.Handle(new Control.KeyDown("ControlLeft", null));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.Executed < 1 && DateTime.UtcNow < deadline) Thread.Sleep(1);
        Assert.Equal(1, handler.Held);

        await session.DisposeAsync();

        Assert.Equal(0, f.Injectors[0].PressedCount);
    }
}
