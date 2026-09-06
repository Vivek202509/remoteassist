using System.Text.Json;
using Techee.Crypto;
using Techee.Protocol;
using Xunit;

namespace Techee.Signaling.Tests;

/// <summary>
/// The W2 acceptance tests: a Windows endpoint registering, pairing, and publishing
/// grants against the <b>real, unmodified</b> Node broker.
/// </summary>
/// <remarks>
/// These are the tests that decide whether "Windows is a Techee endpoint" is true.
/// Everything else in W2 is preparation for them.
/// </remarks>
[Collection("broker")]
public class RegistrationTests(BrokerFixture broker)
{
    private static readonly EndpointMeta WindowsHostMeta = TecheeProtocol.LocalMeta(
        "0.1.0",
        ["screen.share", "input.receive", "clipboard", "display.multi", "power.sleep", "power.restart"]);

    private SignalingClient NewClient(IDeviceIdentity identity, EndpointMeta? meta = null) =>
        new(broker.Url, identity, meta);

    /// <summary>Fails loudly if the broker could not start, rather than passing vacuously.</summary>
    private void RequireBroker() =>
        Assert.True(broker.SkipReason is null,
            $"The Node broker did not start ({broker.SkipReason}). These tests are the W2 " +
            "acceptance criteria and must not be silently skipped.");

    private static async Task<SignalingMessage> NextAsync(
        SignalingClient client, string type, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        await foreach (var m in client.Messages.ReadAllAsync(cts.Token))
        {
            if (m.Type == type) return m;
        }
        throw new InvalidOperationException($"channel completed before a '{type}' message arrived");
    }

    // ---- registration -----------------------------------------------------

    [Fact]
    public async Task A_windows_endpoint_registers_with_the_real_broker()
    {
        RequireBroker();

        using var identity = new EphemeralDeviceIdentity();
        await using var client = NewClient(identity, WindowsHostMeta);

        var result = await client.ConnectAndRegisterAsync();

        Assert.True(result.Ok, $"registration failed: {result.Outcome} {result.Reason}");
        Assert.Equal(identity.DeviceId, result.DeviceId);

        // The broker issues ICE servers on registration, so a host is ready to
        // negotiate the moment it comes online.
        Assert.NotNull(result.IceServers);
        Assert.NotEmpty(result.IceServers!);

        // And it advertises the protocol version added in W1.
        Assert.Equal(TecheeProtocol.ProtocolVersion, result.ProtocolVersion);
    }

    [Fact]
    public async Task Registration_survives_a_reconnect_of_the_same_identity()
    {
        RequireBroker();

        // The realistic case: an office host whose network dropped, or whose service
        // restarted. The second socket proves possession of the same non-exportable
        // key, so the broker treats it as the device returning rather than a takeover.
        using var identity = new EphemeralDeviceIdentity();

        await using var first = NewClient(identity, WindowsHostMeta);
        Assert.True((await first.ConnectAndRegisterAsync()).Ok);

        await using var second = NewClient(identity, WindowsHostMeta);
        var again = await second.ConnectAndRegisterAsync();

        Assert.True(again.Ok, $"reconnect failed: {again.Reason}");
        Assert.Equal(identity.DeviceId, again.DeviceId);

        // The displaced socket is told why rather than silently dropped.
        var replaced = await NextAsync(first, "session-replaced");
        Assert.Equal("authenticated-reconnect", replaced.GetString("reason"));
    }

    [Fact]
    public async Task An_identity_whose_key_does_not_match_its_id_is_refused()
    {
        RequireBroker();

        // The device ID is a hash of the public key, so claiming someone else's ID
        // with your own key is the first thing an attacker tries. The broker
        // re-derives it and refuses before even issuing a challenge.
        using var real = new EphemeralDeviceIdentity();
        using var impostorKey = new EphemeralDeviceIdentity();

        await using var client = new SignalingClient(
            broker.Url, new MismatchedIdentity(real.DeviceId, impostorKey), WindowsHostMeta);

        var result = await client.ConnectAndRegisterAsync();

        Assert.False(result.Ok);
        Assert.Equal("identity-mismatch", result.Reason);
    }

    [Fact]
    public async Task A_proof_signed_by_the_wrong_key_is_refused()
    {
        RequireBroker();

        // Both the device ID and the public key are public values. Only the private
        // key, which the attacker lacks, can finish the handshake.
        using var victim = new EphemeralDeviceIdentity();
        using var attackerKey = new EphemeralDeviceIdentity();

        await using var client = new SignalingClient(
            broker.Url, new BorrowedPublicKeyIdentity(victim, attackerKey), WindowsHostMeta);

        var result = await client.ConnectAndRegisterAsync();

        Assert.False(result.Ok);
        Assert.Equal("bad-signature", result.Reason);
    }

    [Fact]
    public async Task A_legacy_endpoint_that_sends_no_metadata_still_registers()
    {
        RequireBroker();

        // Endpoint metadata is additive. A client that predates it — every shipped
        // Android build — must be unaffected.
        using var identity = new EphemeralDeviceIdentity();
        await using var client = NewClient(identity, meta: null);

        Assert.True((await client.ConnectAndRegisterAsync()).Ok);
    }

    // ---- capabilities -----------------------------------------------------

    [Fact]
    public async Task A_paired_controller_learns_the_windows_host_platform_and_capabilities()
    {
        RequireBroker();

        using var hostIdentity = new EphemeralDeviceIdentity();
        using var controllerIdentity = new EphemeralDeviceIdentity();

        await using var host = NewClient(hostIdentity, WindowsHostMeta);
        await using var controller = NewClient(controllerIdentity, TecheeProtocol.LocalMeta("0.1.0", ["screen.receive", "input.send"]));

        Assert.True((await host.ConnectAndRegisterAsync()).Ok);
        Assert.True((await controller.ConnectAndRegisterAsync()).Ok);

        await host.RegisterPairingAsync(controllerIdentity.DeviceId);
        await NextAsync(host, "pairing-registered");

        await controller.JoinAsync(hostIdentity.DeviceId);
        var pending = await NextAsync(controller, "join-pending");

        Assert.True(pending.Body.TryGetProperty("peerMeta", out var meta));
        var parsed = TecheeProtocol.ParseEndpointMeta(meta);

        Assert.NotNull(parsed);
        Assert.Equal("windows", parsed!.Platform);
        Assert.Equal("0.1.0", parsed.Version);
        Assert.Contains("power.sleep", parsed.Capabilities);
        Assert.Contains("display.multi", parsed.Capabilities);

        // ...and the broker flags it as something it did not verify.
        Assert.False(pending.GetBool("peerMetaTrusted", true));
    }

    [Fact]
    public async Task An_unpaired_controller_cannot_dial_a_windows_host()
    {
        RequireBroker();

        using var hostIdentity = new EphemeralDeviceIdentity();
        using var strangerIdentity = new EphemeralDeviceIdentity();

        await using var host = NewClient(hostIdentity, WindowsHostMeta);
        await using var stranger = NewClient(strangerIdentity);

        Assert.True((await host.ConnectAndRegisterAsync()).Ok);
        Assert.True((await stranger.ConnectAndRegisterAsync()).Ok);

        await stranger.JoinAsync(hostIdentity.DeviceId);
        var failed = await NextAsync(stranger, "join-failed");

        Assert.Equal("not-paired", failed.GetString("reason"));
    }

    [Fact]
    public async Task A_third_party_cannot_forge_a_pairing_into_a_windows_host()
    {
        RequireBroker();

        // Without the W1 broker fix, this device could make itself dialable by
        // inserting an edge between two identities it has nothing to do with.
        using var hostIdentity = new EphemeralDeviceIdentity();
        using var meddlerIdentity = new EphemeralDeviceIdentity();

        await using var host = NewClient(hostIdentity, WindowsHostMeta);
        await using var meddler = NewClient(meddlerIdentity);

        Assert.True((await host.ConnectAndRegisterAsync()).Ok);
        Assert.True((await meddler.ConnectAndRegisterAsync()).Ok);

        // Claim to be the host while pairing the host to itself.
        await meddler.SendAsync(w =>
        {
            w.WriteString("type", "register-pairing");
            w.WriteString("myPub", hostIdentity.DeviceId);
            w.WriteString("peerPub", meddlerIdentity.DeviceId);
        });

        var failed = await NextAsync(meddler, "pairing-failed");
        Assert.Equal("identity-mismatch", failed.GetString("reason"));

        // And the forged edge really is absent.
        await meddler.JoinAsync(hostIdentity.DeviceId);
        var joinFailed = await NextAsync(meddler, "join-failed");
        Assert.Equal("not-paired", joinFailed.GetString("reason"));
    }

    // ---- grants -----------------------------------------------------------

    [Fact]
    public async Task A_permission_scoped_grant_makes_a_join_unattended()
    {
        RequireBroker();

        using var hostIdentity = new EphemeralDeviceIdentity();
        using var controllerIdentity = new EphemeralDeviceIdentity();

        await using var host = NewClient(hostIdentity, WindowsHostMeta);
        await using var controller = NewClient(controllerIdentity);

        Assert.True((await host.ConnectAndRegisterAsync()).Ok);
        Assert.True((await controller.ConnectAndRegisterAsync()).Ok);

        await host.RegisterPairingAsync(controllerIdentity.DeviceId);
        await NextAsync(host, "pairing-registered");

        var grant = new Grant
        {
            GrantId = "office-hp-1",
            ControllerId = controllerIdentity.DeviceId,
            Active = true,
            PermissionTokens = ["screen.view", "input.control", "system.sleep"],
        };
        await host.RegisterGrantAsync(grant);
        var registered = await NextAsync(host, "grant-registered");
        Assert.Equal("office-hp-1", registered.GetString("grantId"));

        await controller.JoinAsync(hostIdentity.DeviceId);
        var request = await NextAsync(host, "join-request");

        Assert.True(request.GetBool("unattended"));
        Assert.Equal("office-hp-1", request.GetString("grantId"));

        var permissions = request.Body.GetProperty("permissions")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("input.control", permissions);
        Assert.Contains("system.sleep", permissions);

        // The property that matters most: a grant covering sleep must not silently
        // extend to shutdown or restart.
        Assert.DoesNotContain("system.shutdown", permissions);
        Assert.DoesNotContain("system.restart", permissions);
    }

    [Fact]
    public async Task Without_a_grant_a_join_is_attended()
    {
        RequireBroker();

        using var hostIdentity = new EphemeralDeviceIdentity();
        using var controllerIdentity = new EphemeralDeviceIdentity();

        await using var host = NewClient(hostIdentity, WindowsHostMeta);
        await using var controller = NewClient(controllerIdentity);

        Assert.True((await host.ConnectAndRegisterAsync()).Ok);
        Assert.True((await controller.ConnectAndRegisterAsync()).Ok);

        await host.RegisterPairingAsync(controllerIdentity.DeviceId);
        await NextAsync(host, "pairing-registered");

        await controller.JoinAsync(hostIdentity.DeviceId);
        var request = await NextAsync(host, "join-request");

        Assert.False(request.GetBool("unattended"));
    }

    [Fact]
    public async Task A_revoked_grant_no_longer_makes_a_join_unattended()
    {
        RequireBroker();

        using var hostIdentity = new EphemeralDeviceIdentity();
        using var controllerIdentity = new EphemeralDeviceIdentity();

        await using var host = NewClient(hostIdentity, WindowsHostMeta);
        await using var controller = NewClient(controllerIdentity);

        Assert.True((await host.ConnectAndRegisterAsync()).Ok);
        Assert.True((await controller.ConnectAndRegisterAsync()).Ok);

        await host.RegisterPairingAsync(controllerIdentity.DeviceId);
        await NextAsync(host, "pairing-registered");

        await host.RegisterGrantAsync(new Grant
        {
            GrantId = "temp",
            ControllerId = controllerIdentity.DeviceId,
            Active = true,
            PermissionTokens = ["screen.view", "input.control"],
        });
        await NextAsync(host, "grant-registered");

        await host.RevokeGrantAsync("temp");

        await controller.JoinAsync(hostIdentity.DeviceId);
        var request = await NextAsync(host, "join-request");

        Assert.False(request.GetBool("unattended"));
    }

    // ---- robustness -------------------------------------------------------

    [Fact]
    public async Task A_malformed_broker_frame_does_not_kill_the_connection()
    {
        RequireBroker();

        // Android's client uses throwing JSON getters on its socket thread, so one
        // bad frame ends its connection. A Windows service must not inherit that: it
        // may be the only way back into an office machine.
        using var identity = new EphemeralDeviceIdentity();
        await using var client = NewClient(identity, WindowsHostMeta);

        Assert.True((await client.ConnectAndRegisterAsync()).Ok);

        // The broker ignores unknown types, so this exercises our own dispatch path
        // with shapes a hostile broker could send.
        await client.SendAsync(w => w.WriteString("type", "definitely-not-a-real-message"));
        await client.SendAsync(w => w.WriteNumber("type", 42));
        await client.SendAsync(w => w.WriteString("nothing", "useful"));

        // Still alive and still functional.
        await client.RequestTurnCredentialsAsync();
        var turn = await NextAsync(client, "turn-credentials");
        Assert.Equal("turn-credentials", turn.Type);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Turn_credentials_can_be_refreshed()
    {
        RequireBroker();

        using var identity = new EphemeralDeviceIdentity();
        await using var client = NewClient(identity, WindowsHostMeta);

        Assert.True((await client.ConnectAndRegisterAsync()).Ok);

        await client.RequestTurnCredentialsAsync();
        await NextAsync(client, "turn-credentials");

        Assert.NotEmpty(client.IceServers);
    }

    [Fact]
    public void An_ice_server_never_prints_its_credential()
    {
        // A TURN secret in a log line is a real leak, and an interpolated record is
        // the easiest way to cause one.
        var server = new IceServer("turn:example.org:3478", "1767225600:techee", "s3cr3t");

        Assert.DoesNotContain("s3cr3t", server.ToString());
        Assert.Contains("redacted", server.ToString());
    }

    // ---- adversarial identity doubles -------------------------------------

    /// <summary>Claims one device ID while holding a different key.</summary>
    private sealed class MismatchedIdentity(string claimedId, IDeviceIdentity realKey) : IDeviceIdentity
    {
        public byte[] PublicKeySpkiDer => realKey.PublicKeySpkiDer;
        public string DeviceId => claimedId;
        public string PublicKeyB64 => realKey.PublicKeyB64;
        public string ShortFingerprint => realKey.ShortFingerprint;
        public KeyProtectionLevel Protection => KeyProtectionLevel.Ephemeral;
        public byte[] Sign(byte[] data) => realKey.Sign(data);
    }

    /// <summary>
    /// Presents the victim's public key and ID — both public values — but signs with
    /// the attacker's private key.
    /// </summary>
    private sealed class BorrowedPublicKeyIdentity(IDeviceIdentity victim, IDeviceIdentity attacker) : IDeviceIdentity
    {
        public byte[] PublicKeySpkiDer => victim.PublicKeySpkiDer;
        public string DeviceId => victim.DeviceId;
        public string PublicKeyB64 => victim.PublicKeyB64;
        public string ShortFingerprint => victim.ShortFingerprint;
        public KeyProtectionLevel Protection => KeyProtectionLevel.Ephemeral;
        public byte[] Sign(byte[] data) => attacker.Sign(data);
    }
}
