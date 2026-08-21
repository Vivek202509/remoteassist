using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Techee.Crypto;
using Techee.Protocol;
using Techee.Store;
using Xunit;

namespace Techee.Store.Tests;

/// <summary>
/// Trust and grant persistence, and the unattended-access decision they combine to
/// make.
/// </summary>
/// <remarks>
/// The <see cref="GrantStore.FindUsableFor"/> tests are the important ones: that
/// method is the single gate between "a stranger dialled in" and "a screen started
/// being captured on an unattended office machine".
/// </remarks>
public class StoreTests
{
    private static (TrustStore trust, GrantStore grants) NewStores()
    {
        var backing = new InMemoryStore();
        return (new TrustStore(backing), new GrantStore(backing));
    }

    private static PeerIdentity NewPeer(IDeviceIdentity identity, TrustState state = TrustState.Trusted) =>
        new()
        {
            PublicKeySpkiB64 = identity.PublicKeyB64,
            Name = "Office Samsung",
            SharedSecretB64 = Convert.ToBase64String(TecheeCrypto.RandomBytes(32)),
            State = state,
            Platform = "android",
            PairedAt = 0,
        };

    private static Grant ControlGrant(string controllerId, params string[] permissions) => new()
    {
        GrantId = "g1",
        ControllerId = controllerId,
        Active = true,
        PermissionTokens = permissions.Length > 0 ? permissions : ["screen.view", "input.control"],
    };

    // ---- trust store ------------------------------------------------------

    [Fact]
    public void A_peer_round_trips_and_derives_its_device_id_from_its_key()
    {
        var (trust, _) = NewStores();
        using var peerIdentity = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(peerIdentity));

        var found = trust.FindByDeviceId(peerIdentity.DeviceId);
        Assert.NotNull(found);

        // Derived, not stored, so the key and the ID cannot disagree.
        Assert.Equal(peerIdentity.DeviceId, found!.DeviceId);
        Assert.Equal(peerIdentity.ShortFingerprint, found.ShortFingerprint);
    }

    [Fact]
    public void The_trust_store_survives_a_reload()
    {
        var backing = new InMemoryStore();
        using var peerIdentity = new EphemeralDeviceIdentity();

        new TrustStore(backing).Save(NewPeer(peerIdentity));

        var reloaded = new TrustStore(backing);
        Assert.True(reloaded.IsTrusted(peerIdentity.DeviceId));
    }

    [Fact]
    public void Re_pairing_cannot_downgrade_an_already_trusted_peer()
    {
        // Android's savePending is an unconditional overwrite, so any accepted
        // pair-complete knocks a trusted peer back to PENDING_CONFIRM — a denial of
        // service against unattended access. This must not reproduce that.
        var (trust, _) = NewStores();
        using var peerIdentity = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(peerIdentity));
        Assert.True(trust.IsTrusted(peerIdentity.DeviceId));

        var accepted = trust.Save(NewPeer(peerIdentity, TrustState.PendingConfirm));

        Assert.False(accepted);
        Assert.True(trust.IsTrusted(peerIdentity.DeviceId));
    }

    [Fact]
    public void A_deliberate_re_pair_is_still_possible_after_removal()
    {
        // The downgrade guard must not make legitimate re-pairing impossible.
        var (trust, _) = NewStores();
        using var peerIdentity = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(peerIdentity));
        trust.Remove(peerIdentity.PublicKeyB64);

        Assert.True(trust.Save(NewPeer(peerIdentity, TrustState.PendingConfirm)));
        Assert.False(trust.IsTrusted(peerIdentity.DeviceId));
    }

    [Fact]
    public void A_revoked_peer_cannot_authenticate_sdp()
    {
        // The live Android gap this deliberately closes: resolvePeerPub searches all
        // peers regardless of TrustState, so a revoked controller's SDP still
        // verifies. Revocation that does not revoke is worse than none, because the
        // operator believes they have acted.
        var (trust, _) = NewStores();
        using var peerIdentity = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(peerIdentity));
        Assert.NotNull(trust.PublicKeyForSdp(peerIdentity.DeviceId));

        trust.Revoke(peerIdentity.PublicKeyB64);

        Assert.Null(trust.PublicKeyForSdp(peerIdentity.DeviceId));
        Assert.False(trust.IsTrusted(peerIdentity.DeviceId));
    }

    [Fact]
    public void A_pending_peer_cannot_authenticate_sdp_either()
    {
        // Pairing that completed cryptographically but was never confirmed by a human
        // comparing the safety number is not yet trust.
        var (trust, _) = NewStores();
        using var peerIdentity = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(peerIdentity, TrustState.PendingConfirm));

        Assert.Null(trust.PublicKeyForSdp(peerIdentity.DeviceId));

        trust.Confirm(peerIdentity.PublicKeyB64);
        Assert.NotNull(trust.PublicKeyForSdp(peerIdentity.DeviceId));
    }

    [Fact]
    public void An_unknown_peer_has_no_key()
    {
        var (trust, _) = NewStores();
        Assert.Null(trust.PublicKeyForSdp(new string('a', 64)));
        Assert.False(trust.IsTrusted(new string('a', 64)));
    }

    [Fact]
    public void A_peer_never_prints_its_shared_secret()
    {
        var (trust, _) = NewStores();
        using var peerIdentity = new EphemeralDeviceIdentity();
        var peer = NewPeer(peerIdentity);

        trust.Save(peer);

        Assert.DoesNotContain(peer.SharedSecretB64, peer.ToString());
        Assert.Contains("redacted", peer.ToString());
    }

    [Fact]
    public void A_corrupt_trust_store_does_not_prevent_startup()
    {
        // An office host that will not boot is worse than one that has forgotten its
        // pairings and can be re-paired.
        var backing = new InMemoryStore();
        backing.Write("peers", "{ this is not valid json");

        var trust = new TrustStore(backing);
        Assert.Empty(trust.All);
    }

    // ---- the unattended-access gate ---------------------------------------

    [Fact]
    public void A_trusted_peer_with_a_valid_grant_gets_unattended_access()
    {
        var (trust, grants) = NewStores();
        using var controller = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(controller));
        grants.Save(ControlGrant(controller.DeviceId));

        var grant = grants.FindUsableFor(controller.DeviceId, trust, DateTimeOffset.UnixEpoch);

        Assert.NotNull(grant);
        Assert.Contains("input.control", grant!.EffectivePermissions);
    }

    [Fact]
    public void A_grant_without_trust_authorises_nothing()
    {
        // The property Android's unattended path lacks: it looks up the grant by a
        // broker-supplied controller ID and starts capturing before the peer's key has
        // been proven. A grant says what may happen; the trust store says who.
        var (trust, grants) = NewStores();
        using var controller = new EphemeralDeviceIdentity();

        grants.Save(ControlGrant(controller.DeviceId));

        Assert.Null(grants.FindUsableFor(controller.DeviceId, trust, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_revoked_peer_loses_unattended_access_even_with_a_live_grant()
    {
        var (trust, grants) = NewStores();
        using var controller = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(controller));
        grants.Save(ControlGrant(controller.DeviceId));
        Assert.NotNull(grants.FindUsableFor(controller.DeviceId, trust, DateTimeOffset.UnixEpoch));

        trust.Revoke(controller.PublicKeyB64);

        Assert.Null(grants.FindUsableFor(controller.DeviceId, trust, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void An_expired_grant_authorises_nothing()
    {
        var (trust, grants) = NewStores();
        using var controller = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(controller));
        grants.Save(ControlGrant(controller.DeviceId) with { ExpiresAt = 1000 });

        Assert.NotNull(grants.FindUsableFor(controller.DeviceId, trust, DateTimeOffset.FromUnixTimeMilliseconds(999)));
        Assert.Null(grants.FindUsableFor(controller.DeviceId, trust, DateTimeOffset.FromUnixTimeMilliseconds(1001)));
    }

    [Fact]
    public void A_revoked_grant_authorises_nothing()
    {
        var (trust, grants) = NewStores();
        using var controller = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(controller));
        grants.Save(ControlGrant(controller.DeviceId));

        grants.Revoke("g1");

        Assert.Null(grants.FindUsableFor(controller.DeviceId, trust, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_grant_conferring_nothing_does_not_start_a_screen_capture()
    {
        // A grant that permits no screen.view is not a reason to begin capturing an
        // unattended machine's display.
        var (trust, grants) = NewStores();
        using var controller = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(controller));
        grants.Save(ControlGrant(controller.DeviceId, "clipboard.read"));

        Assert.Null(grants.FindUsableFor(controller.DeviceId, trust, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_locked_workstation_downgrades_a_require_unlock_grant()
    {
        var (trust, grants) = NewStores();
        using var controller = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(controller));
        grants.Save(ControlGrant(controller.DeviceId, "screen.view", "input.control", "system.sleep")
            with { RequireUnlock = true });

        var unlocked = grants.FindUsableFor(controller.DeviceId, trust, DateTimeOffset.UnixEpoch, workstationLocked: false);
        Assert.Contains("input.control", unlocked!.EffectivePermissions);

        var locked = grants.FindUsableFor(controller.DeviceId, trust, DateTimeOffset.UnixEpoch, workstationLocked: true);
        Assert.NotNull(locked);
        Assert.Equal(["screen.view"], locked!.EffectivePermissions);
        Assert.DoesNotContain("system.sleep", locked.EffectivePermissions);
    }

    [Fact]
    public void Un_pairing_revokes_every_grant_for_that_controller()
    {
        // Trust and grants are separate stores. Without this, removing a peer leaves
        // grants that would quietly become live again if the identity re-paired.
        var (trust, grants) = NewStores();
        using var controller = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(controller));
        grants.Save(ControlGrant(controller.DeviceId));
        grants.Save(ControlGrant(controller.DeviceId) with { GrantId = "g2" });

        grants.RevokeAllFor(controller.DeviceId);

        Assert.All(grants.All, g => Assert.False(g.Active));
        Assert.Null(grants.FindUsableFor(controller.DeviceId, trust, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_legacy_scope_grant_confers_no_system_power_on_a_windows_host()
    {
        // The upgrade case that matters most: an Android controller paired long ago
        // must not gain the ability to shut down a newly installed Windows host.
        var (trust, grants) = NewStores();
        using var controller = new EphemeralDeviceIdentity();

        trust.Save(NewPeer(controller));
        grants.Save(new Grant
        {
            GrantId = "legacy",
            ControllerId = controller.DeviceId,
            Active = true,
            LegacyScope = ["VIEW", "CONTROL"],
        });

        var grant = grants.FindUsableFor(controller.DeviceId, trust, DateTimeOffset.UnixEpoch);

        Assert.NotNull(grant);
        Assert.Contains("input.control", grant!.EffectivePermissions);
        Assert.DoesNotContain(grant.EffectivePermissions, p => p.StartsWith("system.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_corrupt_grant_store_fails_closed()
    {
        // No unattended access is the safe direction. The host still runs and still
        // accepts attended sessions, so an operator can recover without going there.
        var backing = new InMemoryStore();
        backing.Write("grants", "not json at all");

        Assert.Empty(new GrantStore(backing).All);
    }

    // ---- DPAPI ------------------------------------------------------------
    //
    // The three below are annotated Windows-only for the platform-compatibility
    // analyser. That is an analyser annotation, not a runtime gate: they still execute
    // on Linux and exit through their Skip guard before touching a Windows API.

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Dpapi_round_trips_and_writes_ciphertext_to_disk()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI is Windows-only");

        var dir = Path.Combine(Path.GetTempPath(), $"techee-test-{Guid.NewGuid():N}");
        try
        {
            var store = new DpapiFileStore(dir, DataProtectionScope.CurrentUser);
            const string secret = "a-pairing-shared-secret";

            store.Write("peers", secret);
            Assert.Equal(secret, store.Read("peers"));

            // The point of DPAPI: what lands on disk is not the plaintext.
            var onDisk = File.ReadAllBytes(Path.Combine(dir, "peers.dat"));
            Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(onDisk));

            store.Delete("peers");
            Assert.Null(store.Read("peers"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void An_unreadable_dpapi_file_reads_as_absent_rather_than_throwing()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI is Windows-only");

        var dir = Path.Combine(Path.GetTempPath(), $"techee-test-{Guid.NewGuid():N}");
        try
        {
            var store = new DpapiFileStore(dir, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Path.Combine(dir, "peers.dat"), [0xDE, 0xAD, 0xBE, 0xEF]);

            Assert.Null(store.Read("peers"));

            // And the file is left in place, so the cause stays diagnosable.
            Assert.True(File.Exists(Path.Combine(dir, "peers.dat")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void The_machine_data_directory_is_under_program_data()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows paths");

        // Must survive user profile changes and be readable by the SYSTEM-run service.
        // A hard-coded development path here would be a real deployment bug.
        Assert.Contains("Techee", DpapiFileStore.MachineDirectory);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Techee"),
            DpapiFileStore.MachineDirectory);
    }
}
