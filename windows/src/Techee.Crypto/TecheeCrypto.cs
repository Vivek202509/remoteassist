using System.Security.Cryptography;
using System.Text;

namespace Techee.Crypto;

/// <summary>
/// Techee's cryptographic primitives, in C#.
/// </summary>
/// <remarks>
/// <para>
/// This is one of three implementations of the same specification — the others are
/// <c>server/src/auth.js</c> plus <c>com.remoteassist.crypto.Crypto</c> on Android.
/// None is derived from the others; all are held to the golden vectors in
/// <c>protocol/fixtures/identity.json</c>. See <c>docs/PROTOCOL.md</c> §2.
/// </para>
/// <para>
/// Three encoding decisions here are the ones that silently break interop, so they
/// are stated at every call site rather than assumed:
/// </para>
/// <list type="number">
///   <item>Public keys are X.509 <b>SubjectPublicKeyInfo DER</b>, never the raw EC
///   point. The device ID hashes the SPKI bytes, so getting this wrong yields a
///   well-formed identity the broker will refuse.</item>
///   <item>Signatures are ASN.1 <b>DER</b>, not IEEE-P1363. Java and Node emit DER by
///   default; .NET emits P1363 by default and must be told otherwise. This is the
///   single easiest way to build a Windows client that cannot register.</item>
///   <item>Base64 is the <b>standard</b> alphabet with padding, not base64url.</item>
/// </list>
/// </remarks>
public static class TecheeCrypto
{
    /// <summary>NIST P-256 (secp256r1). Chosen because Android has it from API 26.</summary>
    public static readonly ECCurve Curve = ECCurve.NamedCurves.nistP256;

    /// <summary>
    /// ECDSA signatures are DER-encoded on the wire.
    /// </summary>
    /// <remarks>
    /// Java's <c>SHA256withECDSA</c> and Node's <c>crypto.sign</c> both produce DER;
    /// .NET's <c>SignData(data, hash)</c> overload produces IEEE-P1363 instead. Every
    /// sign and verify in Techee must pass this format explicitly.
    /// </remarks>
    public const DSASignatureFormat SignatureFormat = DSASignatureFormat.Rfc3279DerSequence;

    // ---- transcript domain separators -------------------------------------
    //
    // The same identity key signs registration proofs, peer challenges and SDP.
    // Without distinct first lines, a signature harvested under one protocol
    // could be replayed as another — which is not hypothetical: the peer
    // challenge relay was previously usable as an oracle to mint registration
    // proofs. These strings are the fix and must never be reused or reordered.

    public const string RegisterContext = "techee-register-v1";
    public const string PeerAuthContext = "techee-peer-auth-v1";
    public const string SdpContext = "techee-sdp-v1";

    /// <summary>
    /// Builds a Techee transcript: UTF-8, LF-joined, <b>no trailing newline</b>.
    /// </summary>
    /// <remarks>
    /// There is no length framing. That is safe only because every field that can
    /// reach this method is constrained to a character set excluding LF — device IDs
    /// are hex, challenges are base64. Relaxing either constraint would make the
    /// encoding ambiguous, so validate before you sign.
    /// </remarks>
    public static byte[] Transcript(params string[] lines) =>
        Encoding.UTF8.GetBytes(string.Join("\n", lines));

    /// <summary>Bytes signed to prove possession of an identity key during registration.</summary>
    public static byte[] RegistrationTranscript(string deviceId, string challengeB64) =>
        Transcript(RegisterContext, deviceId, challengeB64);

    /// <summary>Bytes signed in response to a peer's identity challenge.</summary>
    public static byte[] PeerAuthTranscript(string challengerId, string nonceB64) =>
        Transcript(PeerAuthContext, challengerId, nonceB64);

    /// <summary>
    /// Bytes signed over an offer or answer, binding media to an identity.
    /// </summary>
    /// <remarks>
    /// Binds, in order: context, offer-vs-answer role, sender, recipient, the DTLS
    /// fingerprint the media layer will actually authenticate against, and a digest
    /// of the whole SDP. The full-SDP digest is what makes tampering detectable
    /// beyond the fingerprint line — swapping a codec or an ICE ufrag invalidates it.
    /// <para>
    /// Returns null when the SDP carries no fingerprint. Such an SDP is unsignable
    /// and unverifiable by design, and the peer will reject it.
    /// </para>
    /// </remarks>
    public static byte[]? SdpTranscript(string type, string fromId, string toId, string sdp)
    {
        var fingerprint = FingerprintLine(sdp);
        if (fingerprint is null) return null;

        return Transcript(
            SdpContext,
            type,
            fromId,
            toId,
            fingerprint,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(sdp))));
    }

    /// <summary>
    /// The first <c>a=fingerprint:</c> line, trimmed, or null.
    /// </summary>
    /// <remarks>
    /// Trimming is required, not cosmetic: real WebRTC SDP is CRLF-delimited, and
    /// splitting on LF leaves a dangling CR that would otherwise end up inside the
    /// signed bytes on one platform but not another.
    /// </remarks>
    public static string? FingerprintLine(string sdp)
    {
        foreach (var line in sdp.Split('\n'))
        {
            if (line.TrimStart().StartsWith("a=fingerprint:", StringComparison.Ordinal))
                return line.Trim();
        }
        return null;
    }

    // ---- identity ---------------------------------------------------------

    /// <summary>
    /// The stable device ID: lowercase hex SHA-256 over the SPKI DER public key.
    /// </summary>
    /// <remarks>
    /// Must match <c>deviceIdFor</c> in <c>server/src/auth.js</c> and
    /// <c>Crypto.publicKeyId</c> on Android. The broker independently re-derives this
    /// from the presented key and refuses the registration if it disagrees, so a
    /// mismatch here is unrecoverable rather than merely inconvenient.
    /// </remarks>
    public static string DeviceIdFor(ReadOnlySpan<byte> publicKeySpkiDer) =>
        Hex(SHA256.HashData(publicKeySpkiDer));

    /// <summary>Short human-comparable fingerprint, e.g. <c>4F2A-9C81-1B03-7DE5</c>.</summary>
    public static string ShortFingerprint(ReadOnlySpan<byte> publicKeySpkiDer)
    {
        var id = DeviceIdFor(publicKeySpkiDer)[..16].ToUpperInvariant();
        return string.Join('-', Enumerable.Range(0, 4).Select(i => id.Substring(i * 4, 4)));
    }

    /// <summary>Verifies an ECDSA/P-256/SHA-256 DER signature over <paramref name="data"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKeySpkiDer, byte[] data, byte[] signature)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(publicKeySpkiDer, out _);
            return ec.VerifyData(data, signature, HashAlgorithmName.SHA256, SignatureFormat);
        }
        catch (CryptographicException)
        {
            // A malformed key or signature is a failed verification, not an
            // exception for the caller to handle. Every call site is a security
            // decision that must fail closed.
            return false;
        }
    }

    // ---- pairing ----------------------------------------------------------

    /// <summary>
    /// The raw ECDH shared secret: the X coordinate, unhashed.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not run through a KDF</b>, because Android's
    /// <c>KeyAgreement.getInstance("ECDH").generateSecret()</c> returns the raw X
    /// coordinate and the safety number both sides display is computed over it.
    /// Applying HKDF here would produce a different number on each platform and
    /// break pairing.
    /// <para>
    /// This value is only ever used to derive the safety number. It must not be used
    /// as a symmetric key — if that is ever wanted, both platforms need a KDF added
    /// together, as a versioned change.
    /// </para>
    /// </remarks>
    public static byte[] Agree(ECDiffieHellman ephemeralPrivate, ReadOnlySpan<byte> peerEphemeralSpkiDer)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerEphemeralSpkiDer, out _);
        return ephemeralPrivate.DeriveRawSecretAgreement(peer.PublicKey);
    }

    /// <summary>
    /// The safety number both peers display and compare out of band.
    /// </summary>
    /// <remarks>
    /// Identical on both sides iff the ECDH exchange was not tampered with. Ordering
    /// is by the lowercase-hex rendering of each DER key so the result does not
    /// depend on which peer computes it.
    /// <para>
    /// <b>Known weakness, matched deliberately.</b> This is 48 bits with no domain
    /// separation — weaker than comparable designs (Signal uses ~112). It is
    /// reproduced exactly as Android computes it because a Windows endpoint that
    /// displayed a different number could not pair at all. Strengthening it is a
    /// coordinated cross-platform change; see docs/THREAT_MODEL.md.
    /// </para>
    /// </remarks>
    public static string SafetyNumber(byte[] shared, byte[] aPubDer, byte[] bPubDer)
    {
        var ordered = new[] { aPubDer, bPubDer }
            .OrderBy(k => Hex(k), StringComparer.Ordinal)
            .ToArray();

        using var sha = SHA256.Create();
        sha.TransformBlock(shared, 0, shared.Length, null, 0);
        sha.TransformBlock(ordered[0], 0, ordered[0].Length, null, 0);
        sha.TransformFinalBlock(ordered[1], 0, ordered[1].Length);
        var digest = sha.Hash!;

        return string.Join('-', digest.Take(6).Select(b => b.ToString("D3")));
    }

    // ---- encodings --------------------------------------------------------

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>Standard base64 with padding — not base64url.</summary>
    public static string B64(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes);

    /// <summary>Decodes standard base64, returning null rather than throwing.</summary>
    public static byte[]? UnB64(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    public static byte[] RandomBytes(int n) => RandomNumberGenerator.GetBytes(n);
}
