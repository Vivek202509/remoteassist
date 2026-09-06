using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Techee.Crypto.Tests;

/// <summary>
/// Holds the C# crypto implementation to <c>protocol/fixtures/identity.json</c> — the
/// same file Node and Android are held to.
/// </summary>
/// <remarks>
/// These are the tests that decide whether a Windows machine can register with the
/// broker at all. A failure here is not a style problem; it means the Windows
/// endpoint has a different identity than it thinks, or produces signatures nothing
/// else can verify.
/// </remarks>
public class CryptoFixtureTests
{
    private static JsonDocument Fx() => Fixtures.Load("identity.json");

    private static byte[] FixtureSpki()
    {
        using var fx = Fx();
        return Convert.FromBase64String(
            fx.RootElement.GetProperty("key").GetProperty("publicKeySpkiB64").GetString()!);
    }

    private static ECDsa FixturePrivateKey()
    {
        using var fx = Fx();
        var pkcs8 = Convert.FromBase64String(
            fx.RootElement.GetProperty("key").GetProperty("privateKeyPkcs8B64").GetString()!);
        var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(pkcs8, out _);
        return ec;
    }

    // ---- device identity derivation ---------------------------------------

    [Fact]
    public void Device_id_derives_from_the_spki_der_exactly_as_pinned()
    {
        using var fx = Fx();
        var expected = fx.RootElement.GetProperty("deviceId").GetProperty("expected").GetString();

        Assert.Equal(expected, TecheeCrypto.DeviceIdFor(FixtureSpki()));
    }

    [Fact]
    public void Device_id_matches_the_shape_the_broker_enforces()
    {
        // server/src/auth.js rejects anything not matching /^[0-9a-f]{64}$/, so an
        // uppercase or truncated derivation fails at the broker with a misleading
        // "malformed-device-id" rather than anywhere useful.
        var id = TecheeCrypto.DeviceIdFor(FixtureSpki());
        Assert.Matches("^[0-9a-f]{64}$", id);
    }

    [Fact]
    public void Device_id_hashes_the_spki_not_the_raw_ec_point()
    {
        // The trap this pins: ExportParameters().Q is a well-formed public key that
        // produces a completely different, silently-wrong device ID.
        using var key = FixturePrivateKey();
        var parameters = key.ExportParameters(false);
        var rawPoint = new byte[1 + parameters.Q.X!.Length + parameters.Q.Y!.Length];
        rawPoint[0] = 0x04;
        parameters.Q.X.CopyTo(rawPoint, 1);
        parameters.Q.Y.CopyTo(rawPoint, 1 + parameters.Q.X.Length);

        Assert.NotEqual(TecheeCrypto.DeviceIdFor(rawPoint), TecheeCrypto.DeviceIdFor(FixtureSpki()));
    }

    // ---- transcripts ------------------------------------------------------

    [Fact]
    public void Registration_transcript_vectors()
    {
        using var fx = Fx();

        foreach (var v in fx.RootElement
                     .GetProperty("transcripts").GetProperty("registration")
                     .GetProperty("vectors").EnumerateArray())
        {
            var built = TecheeCrypto.RegistrationTranscript(
                v.GetProperty("deviceId").GetString()!,
                v.GetProperty("challengeB64").GetString()!);

            Assert.Equal(v.GetProperty("expectedUtf8").GetString(), Encoding.UTF8.GetString(built));
        }
    }

    [Fact]
    public void Peer_auth_transcript_vectors()
    {
        using var fx = Fx();

        foreach (var v in fx.RootElement
                     .GetProperty("transcripts").GetProperty("peerAuth")
                     .GetProperty("vectors").EnumerateArray())
        {
            var built = TecheeCrypto.PeerAuthTranscript(
                v.GetProperty("challengerId").GetString()!,
                v.GetProperty("nonceB64").GetString()!);

            Assert.Equal(v.GetProperty("expectedUtf8").GetString(), Encoding.UTF8.GetString(built));
        }
    }

    [Fact]
    public void Sdp_transcript_vectors_including_crlf_handling()
    {
        using var fx = Fx();

        foreach (var v in fx.RootElement
                     .GetProperty("transcripts").GetProperty("sdp")
                     .GetProperty("vectors").EnumerateArray())
        {
            var sdp = v.GetProperty("sdp").GetString()!;
            var name = v.GetProperty("name").GetString()!;

            var fingerprint = TecheeCrypto.FingerprintLine(sdp);
            var wantFingerprint = v.GetProperty("expectedFingerprintLine");
            Assert.Equal(
                wantFingerprint.ValueKind == JsonValueKind.Null ? null : wantFingerprint.GetString(),
                fingerprint);

            var built = TecheeCrypto.SdpTranscript(
                v.GetProperty("type").GetString()!,
                v.GetProperty("fromId").GetString()!,
                v.GetProperty("toId").GetString()!,
                sdp);

            if (v.TryGetProperty("expectNullTranscript", out var isNull) && isNull.GetBoolean())
            {
                // An SDP with no fingerprint is unsignable and unverifiable by
                // design. Producing a transcript anyway would mean signing something
                // that binds media to nothing.
                Assert.True(built is null, $"{name}: transcript must be undefined");
                continue;
            }

            Assert.True(built is not null, $"{name}: transcript should build");
            Assert.Equal(v.GetProperty("expectedUtf8").GetString(), Encoding.UTF8.GetString(built!));
        }
    }

    [Fact]
    public void Transcripts_have_no_trailing_newline()
    {
        // A trailing LF is invisible in a diff and produces a signature nothing else
        // will verify.
        var t = TecheeCrypto.RegistrationTranscript("abc", "def");
        Assert.NotEqual((byte)'\n', t[^1]);
        Assert.Equal("techee-register-v1\nabc\ndef", Encoding.UTF8.GetString(t));
    }

    [Fact]
    public void The_three_contexts_are_distinct()
    {
        // Domain separation is what stops a signature harvested under one protocol
        // being replayed as another. The peer-challenge relay was previously usable
        // as an oracle to mint registration proofs; these strings are the fix.
        var contexts = new[]
        {
            TecheeCrypto.RegisterContext, TecheeCrypto.PeerAuthContext, TecheeCrypto.SdpContext,
        };
        Assert.Equal(contexts.Length, contexts.Distinct().Count());

        // Same second and third field, different protocol => different bytes.
        Assert.NotEqual(
            Encoding.UTF8.GetString(TecheeCrypto.RegistrationTranscript("id", "nonce")),
            Encoding.UTF8.GetString(TecheeCrypto.PeerAuthTranscript("id", "nonce")));
    }

    // ---- signatures: the actual interop proof -----------------------------

    [Fact]
    public void The_pinned_signature_from_the_fixture_verifies()
    {
        // Signed by Node when the fixture was generated. If C# can verify it, C# and
        // Node agree on transcript bytes, hash, curve, and signature encoding.
        using var fx = Fx();
        var vector = fx.RootElement
            .GetProperty("transcripts").GetProperty("registration")
            .GetProperty("vectors").EnumerateArray()
            .First(v => v.TryGetProperty("signatureB64", out _));

        var transcript = Encoding.UTF8.GetBytes(vector.GetProperty("expectedUtf8").GetString()!);
        var signature = Convert.FromBase64String(vector.GetProperty("signatureB64").GetString()!);

        Assert.True(TecheeCrypto.Verify(FixtureSpki(), transcript, signature));
    }

    [Fact]
    public void A_signature_over_a_different_challenge_does_not_verify()
    {
        using var fx = Fx();
        var vector = fx.RootElement
            .GetProperty("transcripts").GetProperty("registration")
            .GetProperty("vectors").EnumerateArray()
            .First(v => v.TryGetProperty("signatureB64", out _));

        var wrong = TecheeCrypto.RegistrationTranscript(
            vector.GetProperty("deviceId").GetString()!,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        var signature = Convert.FromBase64String(vector.GetProperty("signatureB64").GetString()!);

        Assert.False(TecheeCrypto.Verify(FixtureSpki(), wrong, signature));
    }

    [Fact]
    public void Signatures_are_der_encoded_not_ieee_p1363()
    {
        // The single easiest way to build a Windows client that cannot register:
        // .NET's default SignData overload emits raw r||s, which Node and Java reject.
        // One key, signed both ways, so the comparison is exact.
        using var key = ECDsa.Create(TecheeCrypto.Curve);
        var spki = key.ExportSubjectPublicKeyInfo();
        var data = Encoding.UTF8.GetBytes("techee");

        var der = key.SignData(data, HashAlgorithmName.SHA256, TecheeCrypto.SignatureFormat);
        var p1363 = key.SignData(data, HashAlgorithmName.SHA256); // .NET's default

        // DER: SEQUENCE (0x30), length, then two INTEGERs. P1363 for P-256 is a bare
        // 64-byte r‖s with no structure at all.
        Assert.Equal(0x30, der[0]);
        Assert.Equal(64, p1363.Length);
        Assert.NotEqual(64, der.Length);

        // Our verifier accepts DER and rejects P1363 — which is what proves the
        // format is actually enforced rather than incidentally compatible.
        Assert.True(TecheeCrypto.Verify(spki, data, der));
        Assert.False(TecheeCrypto.Verify(spki, data, p1363));

        // And the identity abstraction signs in the wire format, not the default.
        using var identity = new EphemeralDeviceIdentity();
        var fromIdentity = identity.Sign(data);
        Assert.Equal(0x30, fromIdentity[0]);
        Assert.True(TecheeCrypto.Verify(identity.PublicKeySpkiDer, data, fromIdentity));
    }

    [Fact]
    public void Verify_returns_false_rather_than_throwing_on_garbage()
    {
        // Every call site is a security decision and must fail closed, not throw.
        using var identity = new EphemeralDeviceIdentity();
        var data = Encoding.UTF8.GetBytes("techee");

        Assert.False(TecheeCrypto.Verify(identity.PublicKeySpkiDer, data, []));
        Assert.False(TecheeCrypto.Verify(identity.PublicKeySpkiDer, data, [0xDE, 0xAD]));
        Assert.False(TecheeCrypto.Verify([], data, [0x30, 0x00]));
        Assert.False(TecheeCrypto.Verify([1, 2, 3], data, [0x30, 0x00]));
    }

    [Fact]
    public void An_ephemeral_identity_round_trips_through_sign_and_verify()
    {
        using var identity = new EphemeralDeviceIdentity();
        var transcript = TecheeCrypto.RegistrationTranscript(identity.DeviceId, "bm9uY2U=");

        Assert.True(TecheeCrypto.Verify(identity.PublicKeySpkiDer, transcript, identity.Sign(transcript)));
        Assert.Equal(identity.DeviceId, TecheeCrypto.DeviceIdFor(identity.PublicKeySpkiDer));
        Assert.Equal(KeyProtectionLevel.Ephemeral, identity.Protection);
    }

    [Fact]
    public void Short_fingerprint_is_grouped_uppercase_hex()
    {
        var fp = TecheeCrypto.ShortFingerprint(FixtureSpki());
        Assert.Matches("^[0-9A-F]{4}(-[0-9A-F]{4}){3}$", fp);

        using var fx = Fx();
        var id = fx.RootElement.GetProperty("deviceId").GetProperty("expected").GetString()!;
        Assert.Equal(id[..16].ToUpperInvariant(), fp.Replace("-", ""));
    }

    // ---- encodings --------------------------------------------------------

    [Fact]
    public void Base64_is_standard_not_url_safe()
    {
        // base64url swaps '+' and '/' for '-' and '_'. The broker decodes standard
        // base64; a url-safe encoder produces a key it will not recognise.
        var bytes = new byte[] { 0xFB, 0xFF, 0xBE };
        var encoded = TecheeCrypto.B64(bytes);
        Assert.Contains('+', encoded);
        Assert.Contains('/', encoded);
        Assert.Equal(bytes, TecheeCrypto.UnB64(encoded));
    }

    [Fact]
    public void UnB64_returns_null_rather_than_throwing()
    {
        Assert.Null(TecheeCrypto.UnB64("not base64!!!"));
        Assert.Null(TecheeCrypto.UnB64(""));
        Assert.Null(TecheeCrypto.UnB64(null));
    }
}
