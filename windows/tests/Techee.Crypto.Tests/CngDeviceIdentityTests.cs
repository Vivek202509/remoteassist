using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Xunit;

namespace Techee.Crypto.Tests;

/// <summary>
/// Exercises the real CNG-backed identity on the machine running the tests.
/// </summary>
/// <remarks>
/// <para>
/// These touch actual key storage, so they use a per-run key name and delete it
/// afterwards — a stray key called <c>Techee.DeviceIdentity</c> left behind by a test
/// would become the host's real identity.
/// </para>
/// <para>
/// User scope is exercised unconditionally because it needs no elevation. Machine
/// scope is exercised only when elevated, and there is a separate test asserting the
/// unelevated failure is a clear refusal rather than a silent downgrade — that
/// distinction is the whole reason <see cref="KeyScope"/> exists.
/// </para>
/// <para>
/// Skipped on non-Windows rather than failed: <c>Techee.Crypto</c> is deliberately
/// platform-neutral apart from this one class, so the rest of the suite must keep
/// running on a Linux CI runner.
/// </para>
/// </remarks>
/// <devdoc>
/// Annotated Windows-only for the platform-compatibility analyser. The attribute is
/// an analyser annotation, not a runtime gate: every test still executes on Linux and
/// exits through its <c>Skip</c> guard before reaching a Windows API. Suppressing
/// CA1416 instead would silence the analyser everywhere in the file, including places
/// where a genuinely unguarded call could creep in later.
/// </devdoc>
[SupportedOSPlatform("windows")]
public class CngDeviceIdentityTests
{
    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Unique per run, so concurrent or interrupted runs cannot collide.</summary>
    private static string TestKeyName() => $"Techee.Test.{Guid.NewGuid():N}";

    /// <summary>Runs <paramref name="body"/> against a throwaway user-scoped identity.</summary>
    [SupportedOSPlatform("windows")]
    private static void WithIdentity(Action<CngDeviceIdentity, string> body)
    {
        var keyName = TestKeyName();
        try
        {
            using var identity = CngDeviceIdentity.OpenOrCreate(KeyScope.User, keyName);
            body(identity, keyName);
        }
        finally
        {
            CngDeviceIdentity.Delete(KeyScope.User, keyName);
        }
    }

    [SkippableFact]
    public void A_cng_identity_produces_a_valid_techee_device_id()
    {
        Skip.IfNot(OnWindows, "CNG is Windows-only");

        WithIdentity((identity, _) =>
        {
            // The same derivation the broker independently re-computes from the public
            // key we present. If these disagree, registration fails with
            // "identity-mismatch" and nothing else works.
            Assert.Equal(TecheeCrypto.DeviceIdFor(identity.PublicKeySpkiDer), identity.DeviceId);
            Assert.Matches("^[0-9a-f]{64}$", identity.DeviceId);
            Assert.Equal(Pairing.P256SpkiLength, identity.PublicKeySpkiDer.Length);
        });
    }

    [SkippableFact]
    public void A_cng_identity_signs_in_the_wire_format_the_broker_verifies()
    {
        Skip.IfNot(OnWindows, "CNG is Windows-only");

        WithIdentity((identity, _) =>
        {
            var transcript = TecheeCrypto.RegistrationTranscript(identity.DeviceId, "bm9uY2U=");
            var signature = identity.Sign(transcript);

            // DER, not IEEE-P1363 — the encoding trap that would otherwise produce a
            // Windows host whose signatures nothing else can verify.
            Assert.Equal(0x30, signature[0]);
            Assert.NotEqual(64, signature.Length);
            Assert.True(TecheeCrypto.Verify(identity.PublicKeySpkiDer, transcript, signature));
        });
    }

    [SkippableFact]
    public void The_identity_is_stable_across_reopens()
    {
        Skip.IfNot(OnWindows, "CNG is Windows-only");

        // An office host must keep the same identity across reboots and service
        // restarts, or every pairing breaks every time it restarts.
        var keyName = TestKeyName();
        try
        {
            string firstId;
            using (var first = CngDeviceIdentity.OpenOrCreate(KeyScope.User, keyName))
            {
                firstId = first.DeviceId;
            }

            using var second = CngDeviceIdentity.OpenOrCreate(KeyScope.User, keyName);
            Assert.Equal(firstId, second.DeviceId);
        }
        finally
        {
            CngDeviceIdentity.Delete(KeyScope.User, keyName);
        }
    }

    [SkippableFact]
    public void The_private_key_is_non_exportable()
    {
        Skip.IfNot(OnWindows, "CNG is Windows-only");

        // The security property the whole design rests on. If this ever stops
        // throwing, the identity has become a copyable file and a stolen disk image
        // is a cloned device.
        WithIdentity((identity, keyName) =>
        {
            var provider = identity.Protection == KeyProtectionLevel.TpmBacked
                ? new CngProvider("Microsoft Platform Crypto Provider")
                : CngProvider.MicrosoftSoftwareKeyStorageProvider;

            using var key = CngKey.Open(keyName, provider, CngKeyOpenOptions.None);
            Assert.Throws<CryptographicException>(() => key.Export(CngKeyBlobFormat.EccPrivateBlob));
        });
    }

    [SkippableFact]
    public void Exists_and_Delete_behave_as_a_lifecycle()
    {
        Skip.IfNot(OnWindows, "CNG is Windows-only");

        var keyName = TestKeyName();
        try
        {
            Assert.False(CngDeviceIdentity.Exists(KeyScope.User, keyName));

            using (var _ = CngDeviceIdentity.OpenOrCreate(KeyScope.User, keyName))
            {
                Assert.True(CngDeviceIdentity.Exists(KeyScope.User, keyName));
            }

            CngDeviceIdentity.Delete(KeyScope.User, keyName);
            Assert.False(CngDeviceIdentity.Exists(KeyScope.User, keyName));

            // Deleting something already gone must not throw: uninstall and repair
            // both call this without knowing the current state.
            CngDeviceIdentity.Delete(KeyScope.User, keyName);
        }
        finally
        {
            CngDeviceIdentity.Delete(KeyScope.User, keyName);
        }
    }

    [SkippableFact]
    public void The_protection_level_is_reported_honestly()
    {
        Skip.IfNot(OnWindows, "CNG is Windows-only");

        WithIdentity((identity, _) =>
        {
            // Never Ephemeral: a persisted identity that silently fell back to an
            // in-memory key would lose every pairing on restart.
            Assert.NotEqual(KeyProtectionLevel.Ephemeral, identity.Protection);
            Assert.Contains(identity.Protection,
                new[] { KeyProtectionLevel.TpmBacked, KeyProtectionLevel.SoftwareKsp });

            // Surfaced so an operator can see it. A security property nobody can
            // observe is one nobody maintains.
            Assert.True(Enum.IsDefined(identity.Protection));
            Assert.Equal(KeyScope.User, identity.Scope);
        });
    }

    [SkippableFact]
    public void A_cng_identity_and_an_ephemeral_one_are_interchangeable_to_the_protocol()
    {
        Skip.IfNot(OnWindows, "CNG is Windows-only");

        // The IDeviceIdentity abstraction has to hold: the signaling handshake,
        // pairing, and SDP signing must all behave identically whichever backend is
        // in use, or the ephemeral-backed tests would prove nothing about production.
        WithIdentity((cng, _) =>
        {
            using var ephemeral = new EphemeralDeviceIdentity();

            foreach (IDeviceIdentity identity in new IDeviceIdentity[] { cng, ephemeral })
            {
                var data = Encoding.UTF8.GetBytes("techee");
                Assert.True(TecheeCrypto.Verify(identity.PublicKeySpkiDer, data, identity.Sign(data)));
                Assert.Equal(TecheeCrypto.DeviceIdFor(identity.PublicKeySpkiDer), identity.DeviceId);
                Assert.Equal(Convert.ToBase64String(identity.PublicKeySpkiDer), identity.PublicKeyB64);
                Assert.Matches("^[0-9A-F]{4}(-[0-9A-F]{4}){3}$", identity.ShortFingerprint);
            }
        });
    }

    // ---- scope ------------------------------------------------------------

    [SkippableFact]
    public void Machine_scope_without_elevation_refuses_rather_than_downgrading()
    {
        Skip.IfNot(OnWindows, "CNG is Windows-only");
        Skip.If(IsElevated(), "this asserts the unelevated behaviour");

        // Verified on real hardware: CNG returns "Access denied" for a machine-scoped
        // key without administrator rights. The important property is not that it
        // fails — it is that it fails LOUDLY. Silently creating a per-user key here
        // would give the service a different identity than the installer registered,
        // breaking every pairing with no visible cause.
        var keyName = TestKeyName();
        var e = Assert.Throws<CryptographicException>(() =>
            CngDeviceIdentity.OpenOrCreate(KeyScope.Machine, keyName));

        Assert.Contains("administrator", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(CngDeviceIdentity.Exists(KeyScope.User, keyName),
            "a failed machine-scope request must not leave a user-scoped key behind");
    }

    [SkippableFact]
    public void Machine_scope_works_when_elevated()
    {
        Skip.IfNot(OnWindows, "CNG is Windows-only");
        Skip.IfNot(IsElevated(), "machine-scoped keys require administrator rights");

        // The production path for an unattended host: the elevated installer creates
        // this once, and the service opens it as SYSTEM at boot.
        var keyName = TestKeyName();
        try
        {
            using var identity = CngDeviceIdentity.OpenOrCreate(KeyScope.Machine, keyName);

            Assert.Equal(KeyScope.Machine, identity.Scope);
            Assert.Matches("^[0-9a-f]{64}$", identity.DeviceId);

            var data = Encoding.UTF8.GetBytes("techee");
            Assert.True(TecheeCrypto.Verify(identity.PublicKeySpkiDer, data, identity.Sign(data)));
        }
        finally
        {
            CngDeviceIdentity.Delete(KeyScope.Machine, keyName);
        }
    }

    [SkippableFact]
    public void User_and_machine_scopes_are_separate_containers()
    {
        Skip.IfNot(OnWindows, "CNG is Windows-only");

        // Same key name, different scope, must not collide — otherwise a controller
        // identity and a host identity on the same PC would fight over one container.
        var keyName = TestKeyName();
        try
        {
            using var user = CngDeviceIdentity.OpenOrCreate(KeyScope.User, keyName);

            Assert.True(CngDeviceIdentity.Exists(KeyScope.User, keyName));
            Assert.False(CngDeviceIdentity.Exists(KeyScope.Machine, keyName));
        }
        finally
        {
            CngDeviceIdentity.Delete(KeyScope.User, keyName);
        }
    }
}
