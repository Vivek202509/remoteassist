using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Techee.Crypto;

/// <summary>
/// Which Windows key container the identity lives in.
/// </summary>
/// <remarks>
/// This is a deliberate choice, never a fallback, because the two scopes mean
/// genuinely different things and silently substituting one for the other produces a
/// host that loses its identity in a way that is very hard to diagnose.
/// </remarks>
public enum KeyScope
{
    /// <summary>
    /// Per-user. Works without elevation.
    /// </summary>
    /// <remarks>
    /// Correct for a machine that is only ever a <b>controller</b> — a laptop someone
    /// dials in from. The identity is that person's, and it is unavailable before
    /// login, which for a controller is exactly right.
    /// </remarks>
    User,

    /// <summary>
    /// Per-machine. <b>Requires elevation to create.</b>
    /// </summary>
    /// <remarks>
    /// Required for an unattended <b>host</b>: the Windows service registers with the
    /// broker at boot, before any desktop session exists, and must keep the same
    /// identity across user logoff. Created once by the elevated installer; the
    /// service then opens it as SYSTEM.
    /// </remarks>
    Machine,
}

/// <summary>
/// The Windows device identity: a non-exportable P-256 key in CNG, held in the TPM
/// when the machine has one.
/// </summary>
/// <remarks>
/// <para>
/// The Windows counterpart of Android's <c>AndroidKeyStore</c> identity. The private
/// key is created inside the provider and never exported — signing happens in the
/// provider, and this class never sees key material.
/// </para>
/// <para>
/// Provider preference within the chosen scope, strongest first:
/// </para>
/// <list type="number">
///   <item><b>Microsoft Platform Crypto Provider</b> — TPM-resident. The key cannot
///   physically leave the chip, so an attacker with full filesystem access still
///   cannot clone the device's identity.</item>
///   <item><b>Microsoft Software KSP</b> — non-exportable and DPAPI-protected at
///   rest. An attacker with sufficient local privilege could use the key in place,
///   but still cannot lift it to another machine.</item>
/// </list>
/// <para>
/// The result is reported in <see cref="Protection"/> rather than being silently
/// equivalent. Note that even the fallback is <i>stronger</i> than the current Android
/// default, which does not request StrongBox and does not attest the key.
/// </para>
/// <para>
/// <b>Verified on real hardware:</b> both providers reject
/// <c>Export(EccPrivateBlob)</c> in both scopes, and machine scope fails with
/// <c>Access denied</c> without elevation. See <c>docs/WINDOWS_SECURITY.md</c>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CngDeviceIdentity : IDeviceIdentity, IDisposable
{
    /// <summary>Mirrors the Android alias so the two are recognisably the same concept.</summary>
    public const string DefaultKeyName = "Techee.DeviceIdentity";

    private const string TpmProviderName = "Microsoft Platform Crypto Provider";

    private static readonly CngProvider TpmProvider = new(TpmProviderName);
    private static readonly CngProvider SoftwareProvider = CngProvider.MicrosoftSoftwareKeyStorageProvider;

    private readonly CngKey _key;
    private readonly ECDsaCng _ecdsa;

    private CngDeviceIdentity(CngKey key, KeyProtectionLevel protection, KeyScope scope)
    {
        _key = key;
        _ecdsa = new ECDsaCng(key) { HashAlgorithm = CngAlgorithm.Sha256 };
        Protection = protection;
        Scope = scope;
        PublicKeySpkiDer = _ecdsa.ExportSubjectPublicKeyInfo();
        DeviceId = TecheeCrypto.DeviceIdFor(PublicKeySpkiDer);
    }

    /// <summary>
    /// Opens this endpoint's Techee identity, creating it on first run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent: a second call returns the same key, so the device ID is stable
    /// across reboots and service restarts. Losing the key means losing the identity
    /// and every pairing with it, so uninstall must not delete it unless asked to.
    /// </para>
    /// <para>
    /// Within the requested scope the TPM is tried first and the software KSP is the
    /// fallback. The <b>scope itself is never downgraded</b>: a host asking for a
    /// machine key without elevation gets an exception, not a per-user key that will
    /// vanish the moment the service runs as a different account.
    /// </para>
    /// </remarks>
    /// <exception cref="CryptographicException">
    /// No provider in the requested scope could supply a key. For
    /// <see cref="KeyScope.Machine"/> this is almost always missing elevation.
    /// </exception>
    public static CngDeviceIdentity OpenOrCreate(
        KeyScope scope = KeyScope.User,
        string keyName = DefaultKeyName)
    {
        var options = scope == KeyScope.Machine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;

        // Prefer an existing key over creating one, and the TPM over software.
        if (TryOpen(keyName, TpmProvider, options, out var tpm))
            return new CngDeviceIdentity(tpm!, KeyProtectionLevel.TpmBacked, scope);

        if (TryOpen(keyName, SoftwareProvider, options, out var software))
            return new CngDeviceIdentity(software!, KeyProtectionLevel.SoftwareKsp, scope);

        // Nothing exists yet. A machine with a working TPM must never silently land
        // on the software provider.
        if (TryCreate(keyName, TpmProvider, scope, out var newTpm))
            return new CngDeviceIdentity(newTpm!, KeyProtectionLevel.TpmBacked, scope);

        if (TryCreate(keyName, SoftwareProvider, scope, out var newSoftware))
            return new CngDeviceIdentity(newSoftware!, KeyProtectionLevel.SoftwareKsp, scope);

        throw new CryptographicException(
            scope == KeyScope.Machine
                ? "Could not create a machine-scoped Techee device identity. Creating a machine " +
                  "key requires administrator rights — this normally runs from the elevated " +
                  "installer or from the Techee service running as SYSTEM. Refusing to fall back " +
                  "to a per-user key, which would give the service a different identity and break " +
                  "every existing pairing."
                : "Could not create a Techee device identity in any CNG provider.");
    }

    /// <summary>True when an identity already exists in the given scope.</summary>
    public static bool Exists(KeyScope scope = KeyScope.User, string keyName = DefaultKeyName)
    {
        var options = scope == KeyScope.Machine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        return SafeExists(keyName, TpmProvider, options) || SafeExists(keyName, SoftwareProvider, options);
    }

    /// <summary>
    /// Deletes the identity. Destroys every pairing that references it, so this is
    /// only ever an explicit user action.
    /// </summary>
    public static void Delete(KeyScope scope = KeyScope.User, string keyName = DefaultKeyName)
    {
        var options = scope == KeyScope.Machine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;

        foreach (var provider in new[] { TpmProvider, SoftwareProvider })
        {
            try
            {
                if (!SafeExists(keyName, provider, options)) continue;
                using var key = CngKey.Open(keyName, provider, options);
                key.Delete();
            }
            catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
            {
                // Absent, or not ours to delete. Deletion is best-effort by nature —
                // uninstall and repair both call this without knowing the state.
            }
        }
    }

    private static bool SafeExists(string keyName, CngProvider provider, CngKeyOpenOptions options)
    {
        try
        {
            return CngKey.Exists(keyName, provider, options);
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
        {
            // A provider that is absent (no TPM) or refuses the query is simply "no".
            return false;
        }
    }

    private static bool TryOpen(
        string keyName, CngProvider provider, CngKeyOpenOptions options, out CngKey? key)
    {
        key = null;
        try
        {
            if (!CngKey.Exists(keyName, provider, options)) return false;
            key = CngKey.Open(keyName, provider, options);
            return true;
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreate(string keyName, CngProvider provider, KeyScope scope, out CngKey? key)
    {
        key = null;

        var parameters = new CngKeyCreationParameters
        {
            Provider = provider,
            KeyCreationOptions = scope == KeyScope.Machine
                ? CngKeyCreationOptions.MachineKey
                : CngKeyCreationOptions.None,
            // No ExportPolicy is set, which means the key is non-exportable. Stated
            // explicitly because the omission IS the security control: adding
            // AllowExport here would quietly turn a hardware-bound identity into a
            // copyable file.
            KeyUsage = CngKeyUsages.Signing,
        };

        try
        {
            key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, parameters);
            return true;
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public byte[] PublicKeySpkiDer { get; }
    public string DeviceId { get; }
    public string PublicKeyB64 => TecheeCrypto.B64(PublicKeySpkiDer);
    public string ShortFingerprint => TecheeCrypto.ShortFingerprint(PublicKeySpkiDer);
    public KeyProtectionLevel Protection { get; }

    /// <summary>Which container the key lives in. Surfaced for diagnostics.</summary>
    public KeyScope Scope { get; }

    /// <summary>
    /// Signs inside the provider, DER-encoded.
    /// </summary>
    /// <remarks>
    /// DER because Java and Node expect it; .NET's default overload would emit
    /// IEEE-P1363 and produce signatures nothing else in Techee can verify.
    /// </remarks>
    public byte[] Sign(byte[] data) =>
        _ecdsa.SignData(data, HashAlgorithmName.SHA256, TecheeCrypto.SignatureFormat);

    public void Dispose()
    {
        _ecdsa.Dispose();
        _key.Dispose();
    }
}
