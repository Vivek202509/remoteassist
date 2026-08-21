using System.Security.Cryptography;

namespace Techee.Crypto;

/// <summary>
/// This endpoint's long-lived cryptographic identity.
/// </summary>
/// <remarks>
/// The public key <i>is</i> the identity; the device ID is derived from it. The
/// private key never leaves its store and is never exported, serialised, or logged.
/// <para>
/// An interface rather than a concrete class because the storage backend is the one
/// genuinely platform-specific part of Techee's security model — TPM-backed CNG on
/// Windows, the Keystore on Android — while everything above it is portable. It also
/// lets the whole protocol stack be tested with an ephemeral key and no hardware.
/// </para>
/// </remarks>
public interface IDeviceIdentity
{
    /// <summary>X.509 SubjectPublicKeyInfo DER.</summary>
    byte[] PublicKeySpkiDer { get; }

    /// <summary>Lowercase hex SHA-256 of <see cref="PublicKeySpkiDer"/>.</summary>
    string DeviceId { get; }

    /// <summary>Standard base64 of <see cref="PublicKeySpkiDer"/>, as sent to the broker.</summary>
    string PublicKeyB64 { get; }

    /// <summary>Short human-comparable form, e.g. <c>4F2A-9C81-1B03-7DE5</c>.</summary>
    string ShortFingerprint { get; }

    /// <summary>
    /// Where the private key actually lives. Surfaced so an operator can tell a
    /// TPM-backed host from a software-key one; a security property nobody can see
    /// is a security property nobody maintains.
    /// </summary>
    KeyProtectionLevel Protection { get; }

    /// <summary>Signs with ECDSA/P-256/SHA-256, DER-encoded.</summary>
    byte[] Sign(byte[] data);
}

/// <summary>How well the private key is protected. Ordered weakest to strongest.</summary>
public enum KeyProtectionLevel
{
    /// <summary>In-process only, lost on exit. Tests and diagnostics — never production.</summary>
    Ephemeral,

    /// <summary>CNG software KSP, non-exportable, DPAPI-protected at rest.</summary>
    SoftwareKsp,

    /// <summary>CNG Platform Crypto Provider — TPM-resident and non-exportable.</summary>
    TpmBacked,
}

/// <summary>
/// An in-memory identity for tests and for exercising the protocol stack without a
/// TPM or a persisted key.
/// </summary>
/// <remarks>
/// Deliberately has no persistence path at all. There is no configuration mistake
/// that can cause a production host to keep its identity in one of these — it
/// vanishes with the process, which is a loud failure rather than a quiet downgrade.
/// </remarks>
public sealed class EphemeralDeviceIdentity : IDeviceIdentity, IDisposable
{
    private readonly ECDsa _key;

    public EphemeralDeviceIdentity()
    {
        _key = ECDsa.Create(TecheeCrypto.Curve);
        PublicKeySpkiDer = _key.ExportSubjectPublicKeyInfo();
        DeviceId = TecheeCrypto.DeviceIdFor(PublicKeySpkiDer);
    }

    /// <summary>Reconstructs a fixed identity from a PKCS#8 key. Test fixtures only.</summary>
    public static EphemeralDeviceIdentity FromPkcs8(ReadOnlySpan<byte> pkcs8) => new(pkcs8);

    private EphemeralDeviceIdentity(ReadOnlySpan<byte> pkcs8)
    {
        _key = ECDsa.Create();
        _key.ImportPkcs8PrivateKey(pkcs8, out _);
        PublicKeySpkiDer = _key.ExportSubjectPublicKeyInfo();
        DeviceId = TecheeCrypto.DeviceIdFor(PublicKeySpkiDer);
    }

    public byte[] PublicKeySpkiDer { get; }
    public string DeviceId { get; }
    public string PublicKeyB64 => TecheeCrypto.B64(PublicKeySpkiDer);
    public string ShortFingerprint => TecheeCrypto.ShortFingerprint(PublicKeySpkiDer);
    public KeyProtectionLevel Protection => KeyProtectionLevel.Ephemeral;

    public byte[] Sign(byte[] data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, TecheeCrypto.SignatureFormat);

    public void Dispose() => _key.Dispose();
}
