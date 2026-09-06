using System.Security.Cryptography;
using System.Text.Json;

namespace Techee.Crypto;

/// <summary>
/// The Techee pairing ceremony, Windows side.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>com.remoteassist.trust.PairingManager</c> byte for byte. The two proofs
/// below are raw concatenations with <b>no domain separator and no length framing</b>,
/// which is not how the rest of Techee builds signed input — but it is what Android
/// ships, and a Windows endpoint that framed them differently could not pair with any
/// existing phone. Reproduced deliberately, not by oversight; see the weakness note in
/// <c>docs/PROTOCOL.md</c> §2.3 and the migration plan in <c>docs/THREAT_MODEL.md</c>.
/// </para>
/// <para>
/// The concatenation is unambiguous today only because every field is fixed-length: a
/// 32-byte nonce and 91-byte P-256 SPKI DERs. <see cref="VerifyControllerProof"/>
/// enforces those lengths rather than trusting them, so a variable-length field can
/// never silently make the encoding ambiguous.
/// </para>
/// </remarks>
public static class Pairing
{
    /// <summary>P-256 SubjectPublicKeyInfo DER is always exactly this long.</summary>
    public const int P256SpkiLength = 91;

    /// <summary>The pairing nonce is always 32 bytes.</summary>
    public const int NonceLength = 32;

    /// <summary>How long a pairing offer stays valid. Matches Android's 2 minutes.</summary>
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The payload a host shows as a QR code to start pairing.
    /// </summary>
    /// <remarks>
    /// Field names match Android's JSON exactly — <c>hostPub</c>, <c>ephPub</c>,
    /// <c>nonce</c>, <c>hostName</c>, <c>relay</c>, <c>ttl</c> — because an Android
    /// phone scans this and an Android host emits the same shape.
    /// </remarks>
    public sealed record Offer(
        string HostPubB64,
        string EphPubB64,
        string NonceB64,
        string HostName,
        string Relay,
        long TtlEpochMs)
    {
        public string ToJson()
        {
            var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("hostPub", HostPubB64);
                w.WriteString("ephPub", EphPubB64);
                w.WriteString("nonce", NonceB64);
                w.WriteString("hostName", HostName);
                w.WriteString("relay", Relay);
                w.WriteNumber("ttl", TtlEpochMs);
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>Parses a scanned offer, returning null for anything malformed.</summary>
        public static Offer? Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var o = doc.RootElement;
                if (o.ValueKind != JsonValueKind.Object) return null;

                string? Str(string k) =>
                    o.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

                if (Str("hostPub") is not { } hostPub) return null;
                if (Str("ephPub") is not { } ephPub) return null;
                if (Str("nonce") is not { } nonce) return null;
                if (!o.TryGetProperty("ttl", out var ttlEl) || ttlEl.ValueKind != JsonValueKind.Number) return null;

                return new Offer(
                    hostPub, ephPub, nonce,
                    Str("hostName") ?? "Techee device",
                    Str("relay") ?? "",
                    ttlEl.GetInt64());
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public bool IsExpired(DateTimeOffset now) => TtlEpochMs < now.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Bytes the joining side signs: <c>nonce ‖ hostIdentityPubDer ‖ ownEphemeralPubDer</c>.
    /// </summary>
    /// <remarks>
    /// Binds the host's nonce (freshness), the host identity being paired with (so a
    /// proof cannot be redirected to a different host), and the joiner's own ephemeral
    /// key (so it cannot be swapped mid-flight).
    /// </remarks>
    public static byte[] ControllerProofTranscript(
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> hostIdentitySpki,
        ReadOnlySpan<byte> controllerEphemeralSpki)
    {
        var buffer = new byte[nonce.Length + hostIdentitySpki.Length + controllerEphemeralSpki.Length];
        nonce.CopyTo(buffer);
        hostIdentitySpki.CopyTo(buffer.AsSpan(nonce.Length));
        controllerEphemeralSpki.CopyTo(buffer.AsSpan(nonce.Length + hostIdentitySpki.Length));
        return buffer;
    }

    /// <summary>
    /// Bytes the host signs back: <c>controllerEphemeralPubDer ‖ controllerIdentityPubDer</c>.
    /// </summary>
    /// <remarks>
    /// <b>Weaker than the joining side's proof, matched deliberately.</b> It carries no
    /// nonce and does not cover the host's own ephemeral key, so it proves possession
    /// of the host identity key but contributes no freshness. The out-of-band safety
    /// number remains the real anti-MITM check. Changing this requires a coordinated
    /// Android change.
    /// </remarks>
    public static byte[] HostProofTranscript(
        ReadOnlySpan<byte> controllerEphemeralSpki,
        ReadOnlySpan<byte> controllerIdentitySpki)
    {
        var buffer = new byte[controllerEphemeralSpki.Length + controllerIdentitySpki.Length];
        controllerEphemeralSpki.CopyTo(buffer);
        controllerIdentitySpki.CopyTo(buffer.AsSpan(controllerEphemeralSpki.Length));
        return buffer;
    }

    /// <summary>
    /// Verifies a joining peer's proof, enforcing the field lengths the unframed
    /// concatenation depends on.
    /// </summary>
    /// <remarks>
    /// The length checks are the security control, not validation hygiene. Without
    /// them, a peer supplying an over-long nonce could shift the boundary between
    /// fields and produce a transcript that means something different to each side.
    /// </remarks>
    public static bool VerifyControllerProof(
        byte[] nonce,
        byte[] hostIdentitySpki,
        byte[] controllerEphemeralSpki,
        byte[] controllerIdentitySpki,
        byte[] proof)
    {
        if (nonce.Length != NonceLength) return false;
        if (controllerEphemeralSpki.Length != P256SpkiLength) return false;
        if (controllerIdentitySpki.Length != P256SpkiLength) return false;
        if (hostIdentitySpki.Length != P256SpkiLength) return false;

        var transcript = ControllerProofTranscript(nonce, hostIdentitySpki, controllerEphemeralSpki);
        return TecheeCrypto.Verify(controllerIdentitySpki, transcript, proof);
    }

    /// <summary>Verifies the host's proof, with the same length discipline.</summary>
    public static bool VerifyHostProof(
        byte[] controllerEphemeralSpki,
        byte[] controllerIdentitySpki,
        byte[] hostIdentitySpki,
        byte[] proof)
    {
        if (controllerEphemeralSpki.Length != P256SpkiLength) return false;
        if (controllerIdentitySpki.Length != P256SpkiLength) return false;
        if (hostIdentitySpki.Length != P256SpkiLength) return false;

        var transcript = HostProofTranscript(controllerEphemeralSpki, controllerIdentitySpki);
        return TecheeCrypto.Verify(hostIdentitySpki, transcript, proof);
    }

    /// <summary>Creates an ephemeral P-256 key for one pairing exchange.</summary>
    public static ECDiffieHellman NewEphemeral() => ECDiffieHellman.Create(TecheeCrypto.Curve);

    /// <summary>
    /// Computes the safety number both peers display.
    /// </summary>
    /// <remarks>
    /// Never call this with a fallback empty secret. Android's
    /// <c>PairingManager.safetyNumber</c> falls back to <c>ByteArray(0)</c> when both
    /// lookups miss, which produces a number that <i>matches on both sides</i> while
    /// proving nothing about the ECDH — a fail-open the user cannot detect. This
    /// signature requires a real secret so the same mistake is not expressible.
    /// </remarks>
    public static string SafetyNumber(byte[] sharedSecret, byte[] aIdentitySpki, byte[] bIdentitySpki)
    {
        ArgumentNullException.ThrowIfNull(sharedSecret);
        if (sharedSecret.Length == 0)
        {
            throw new ArgumentException(
                "Refusing to compute a safety number over an empty shared secret: it would " +
                "match on both peers while proving nothing about the key exchange.",
                nameof(sharedSecret));
        }

        return TecheeCrypto.SafetyNumber(sharedSecret, aIdentitySpki, bIdentitySpki);
    }
}
