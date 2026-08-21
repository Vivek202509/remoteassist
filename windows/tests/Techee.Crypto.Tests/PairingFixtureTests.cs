using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Techee.Crypto.Tests;

/// <summary>
/// Holds the Windows pairing implementation to <c>protocol/fixtures/pairing.json</c>.
/// </summary>
/// <remarks>
/// The safety number is the assertion that matters most in this file. It is what two
/// people read aloud to each other, so a mismatch between Android and Windows is not
/// a subtle bug — it is a product that cannot be paired, with no error message that
/// explains why.
/// </remarks>
public class PairingFixtureTests
{
    private static JsonDocument Fx() => Fixtures.Load("pairing.json");

    private static byte[] Key(string name)
    {
        using var fx = Fx();
        return Convert.FromBase64String(fx.RootElement.GetProperty("keys").GetProperty(name).GetString()!);
    }

    private static byte[] Nonce()
    {
        using var fx = Fx();
        return Convert.FromBase64String(fx.RootElement.GetProperty("nonceB64").GetString()!);
    }

    private static byte[] SharedSecret()
    {
        using var fx = Fx();
        return Convert.FromBase64String(
            fx.RootElement.GetProperty("ecdh").GetProperty("sharedSecretB64").GetString()!);
    }

    // ---- the assumption the unframed transcripts rest on -------------------

    [Fact]
    public void P256_spki_der_is_always_the_pinned_length()
    {
        // The proof transcripts are raw concatenations with no length framing, so
        // this is load-bearing rather than trivia: if SPKI DER were variable-length,
        // the concatenation would be ambiguous and the proofs forgeable.
        using var fx = Fx();
        var expected = fx.RootElement.GetProperty("lengths").GetProperty("p256SpkiDer").GetInt32();

        Assert.Equal(expected, Pairing.P256SpkiLength);
        foreach (var name in new[]
                 {
                     "hostIdentitySpkiB64", "controllerIdentitySpkiB64",
                     "hostEphemeralSpkiB64", "controllerEphemeralSpkiB64",
                 })
        {
            Assert.Equal(expected, Key(name).Length);
        }

        // And a freshly generated key must also be 91 bytes, not just the fixtures.
        using var fresh = ECDsa.Create(TecheeCrypto.Curve);
        Assert.Equal(expected, fresh.ExportSubjectPublicKeyInfo().Length);
    }

    // ---- ECDH -------------------------------------------------------------

    [Fact]
    public void Ecdh_reproduces_the_pinned_raw_shared_secret_from_either_side()
    {
        // The trap: .NET's DeriveKeyMaterial applies a hash, DeriveRawSecretAgreement
        // does not. Android returns the raw X coordinate, so only the latter matches —
        // and getting it wrong changes the safety number rather than failing loudly.
        var expected = SharedSecret();

        using var hostEph = ECDiffieHellman.Create();
        hostEph.ImportPkcs8PrivateKey(Key("hostEphemeralPkcs8B64"), out _);
        Assert.Equal(expected, TecheeCrypto.Agree(hostEph, Key("controllerEphemeralSpkiB64")));

        using var ctrlEph = ECDiffieHellman.Create();
        ctrlEph.ImportPkcs8PrivateKey(Key("controllerEphemeralPkcs8B64"), out _);
        Assert.Equal(expected, TecheeCrypto.Agree(ctrlEph, Key("hostEphemeralSpkiB64")));
    }

    [Fact]
    public void The_raw_shared_secret_is_not_hashed()
    {
        // Guards against a future "improvement" that adds a KDF on one platform only.
        using var hostEph = ECDiffieHellman.Create();
        hostEph.ImportPkcs8PrivateKey(Key("hostEphemeralPkcs8B64"), out _);

        var raw = TecheeCrypto.Agree(hostEph, Key("controllerEphemeralSpkiB64"));
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Key("controllerEphemeralSpkiB64"), out _);
        var hashed = hostEph.DeriveKeyMaterial(peer.PublicKey);

        Assert.Equal(32, raw.Length);
        Assert.NotEqual(hashed, raw);
    }

    // ---- safety number ----------------------------------------------------

    [Fact]
    public void Safety_number_matches_the_pinned_value()
    {
        using var fx = Fx();
        var expected = fx.RootElement.GetProperty("safetyNumber").GetProperty("expected").GetString();

        var got = Pairing.SafetyNumber(
            SharedSecret(), Key("hostIdentitySpkiB64"), Key("controllerIdentitySpkiB64"));

        Assert.Equal(expected, got);
    }

    [Fact]
    public void Safety_number_is_order_independent()
    {
        // Both peers compute it locally with their own key first, so it must not
        // depend on argument order or the two screens would never agree.
        var a = Pairing.SafetyNumber(SharedSecret(), Key("hostIdentitySpkiB64"), Key("controllerIdentitySpkiB64"));
        var b = Pairing.SafetyNumber(SharedSecret(), Key("controllerIdentitySpkiB64"), Key("hostIdentitySpkiB64"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Safety_number_has_the_pinned_display_format()
    {
        var got = Pairing.SafetyNumber(SharedSecret(), Key("hostIdentitySpkiB64"), Key("controllerIdentitySpkiB64"));

        // Six zero-padded 3-digit groups: 48 bits, which the threat model records as
        // a known weakness. The format is pinned so a "nicer" rendering on one
        // platform cannot silently break comparison.
        Assert.Matches(@"^\d{3}(-\d{3}){5}$", got);
        foreach (var group in got.Split('-')) Assert.InRange(int.Parse(group), 0, 255);
    }

    [Fact]
    public void Safety_number_changes_if_the_secret_changes()
    {
        // Otherwise the comparison would be theatre.
        var tampered = SharedSecret();
        tampered[0] ^= 0xFF;

        Assert.NotEqual(
            Pairing.SafetyNumber(SharedSecret(), Key("hostIdentitySpkiB64"), Key("controllerIdentitySpkiB64")),
            Pairing.SafetyNumber(tampered, Key("hostIdentitySpkiB64"), Key("controllerIdentitySpkiB64")));
    }

    [Fact]
    public void Safety_number_refuses_an_empty_secret()
    {
        // Android's PairingManager falls back to ByteArray(0) when both lookups miss,
        // which yields a number that MATCHES on both peers while proving nothing about
        // the key exchange — a fail-open the user cannot possibly detect. Windows
        // makes that state unrepresentable.
        Assert.Throws<ArgumentException>(() =>
            Pairing.SafetyNumber([], Key("hostIdentitySpkiB64"), Key("controllerIdentitySpkiB64")));
    }

    // ---- proofs -----------------------------------------------------------

    [Fact]
    public void The_pinned_controller_proof_verifies()
    {
        using var fx = Fx();
        var proof = Convert.FromBase64String(
            fx.RootElement.GetProperty("proofs").GetProperty("controller")
                .GetProperty("signatureB64").GetString()!);

        Assert.True(Pairing.VerifyControllerProof(
            Nonce(),
            Key("hostIdentitySpkiB64"),
            Key("controllerEphemeralSpkiB64"),
            Key("controllerIdentitySpkiB64"),
            proof));
    }

    [Fact]
    public void The_pinned_host_proof_verifies()
    {
        using var fx = Fx();
        var proof = Convert.FromBase64String(
            fx.RootElement.GetProperty("proofs").GetProperty("host")
                .GetProperty("signatureB64").GetString()!);

        Assert.True(Pairing.VerifyHostProof(
            Key("controllerEphemeralSpkiB64"),
            Key("controllerIdentitySpkiB64"),
            Key("hostIdentitySpkiB64"),
            proof));
    }

    [Fact]
    public void A_controller_proof_bound_to_a_different_host_does_not_verify()
    {
        // The host identity is inside the transcript precisely so a proof harvested
        // for one host cannot be redirected to another.
        using var fx = Fx();
        var proof = Convert.FromBase64String(
            fx.RootElement.GetProperty("proofs").GetProperty("controller")
                .GetProperty("signatureB64").GetString()!);

        using var other = ECDsa.Create(TecheeCrypto.Curve);

        Assert.False(Pairing.VerifyControllerProof(
            Nonce(),
            other.ExportSubjectPublicKeyInfo(), // different host
            Key("controllerEphemeralSpkiB64"),
            Key("controllerIdentitySpkiB64"),
            proof));
    }

    [Fact]
    public void A_controller_proof_with_a_swapped_nonce_does_not_verify()
    {
        using var fx = Fx();
        var proof = Convert.FromBase64String(
            fx.RootElement.GetProperty("proofs").GetProperty("controller")
                .GetProperty("signatureB64").GetString()!);

        Assert.False(Pairing.VerifyControllerProof(
            TecheeCrypto.RandomBytes(32),
            Key("hostIdentitySpkiB64"),
            Key("controllerEphemeralSpkiB64"),
            Key("controllerIdentitySpkiB64"),
            proof));
    }

    [Fact]
    public void Proof_verification_enforces_field_lengths()
    {
        // The length checks ARE the security control for an unframed concatenation:
        // an over-long nonce would otherwise shift the field boundary and let one
        // transcript be read two ways.
        using var fx = Fx();
        var proof = Convert.FromBase64String(
            fx.RootElement.GetProperty("proofs").GetProperty("controller")
                .GetProperty("signatureB64").GetString()!);

        Assert.False(Pairing.VerifyControllerProof(
            new byte[64], // wrong nonce length
            Key("hostIdentitySpkiB64"),
            Key("controllerEphemeralSpkiB64"),
            Key("controllerIdentitySpkiB64"),
            proof));

        Assert.False(Pairing.VerifyControllerProof(
            Nonce(),
            Key("hostIdentitySpkiB64"),
            new byte[10], // wrong ephemeral length
            Key("controllerIdentitySpkiB64"),
            proof));
    }

    [Fact]
    public void A_full_pairing_round_trip_agrees_on_the_safety_number()
    {
        // Both halves, end to end, with fresh keys — the thing an operator will
        // actually do, rather than a replay of pinned bytes.
        using var hostIdentity = new EphemeralDeviceIdentity();
        using var controllerIdentity = new EphemeralDeviceIdentity();
        using var hostEph = Pairing.NewEphemeral();
        using var ctrlEph = Pairing.NewEphemeral();

        var hostEphSpki = hostEph.ExportSubjectPublicKeyInfo();
        var ctrlEphSpki = ctrlEph.ExportSubjectPublicKeyInfo();
        var nonce = TecheeCrypto.RandomBytes(Pairing.NonceLength);

        // Controller proves its identity over (nonce || hostPub || ownEph).
        var controllerProof = controllerIdentity.Sign(
            Pairing.ControllerProofTranscript(nonce, hostIdentity.PublicKeySpkiDer, ctrlEphSpki));

        Assert.True(Pairing.VerifyControllerProof(
            nonce, hostIdentity.PublicKeySpkiDer, ctrlEphSpki,
            controllerIdentity.PublicKeySpkiDer, controllerProof));

        // Host proves back over (ctrlEph || ctrlPub).
        var hostProof = hostIdentity.Sign(
            Pairing.HostProofTranscript(ctrlEphSpki, controllerIdentity.PublicKeySpkiDer));

        Assert.True(Pairing.VerifyHostProof(
            ctrlEphSpki, controllerIdentity.PublicKeySpkiDer,
            hostIdentity.PublicKeySpkiDer, hostProof));

        // Both derive the same secret and therefore display the same number.
        var hostSecret = TecheeCrypto.Agree(hostEph, ctrlEphSpki);
        var ctrlSecret = TecheeCrypto.Agree(ctrlEph, hostEphSpki);
        Assert.Equal(hostSecret, ctrlSecret);

        Assert.Equal(
            Pairing.SafetyNumber(hostSecret, hostIdentity.PublicKeySpkiDer, controllerIdentity.PublicKeySpkiDer),
            Pairing.SafetyNumber(ctrlSecret, controllerIdentity.PublicKeySpkiDer, hostIdentity.PublicKeySpkiDer));
    }

    // ---- offer payload ----------------------------------------------------

    [Fact]
    public void Offer_json_uses_the_field_names_android_emits()
    {
        using var fx = Fx();
        var expected = fx.RootElement.GetProperty("offerJson").GetProperty("fields").Strings();

        var offer = new Pairing.Offer("pub", "eph", "nonce", "Office HP", "wss://relay", 1234);
        using var doc = JsonDocument.Parse(offer.ToJson());
        var actual = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(expected.Order(), actual.Order());
    }

    [Fact]
    public void Offer_round_trips_and_rejects_malformed_input()
    {
        var offer = new Pairing.Offer("pub", "eph", "nonce", "Office HP", "wss://relay", 1234);
        var parsed = Pairing.Offer.Parse(offer.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(offer, parsed);

        // A scanned QR is untrusted input: reject, never throw.
        Assert.Null(Pairing.Offer.Parse("not json"));
        Assert.Null(Pairing.Offer.Parse("[]"));
        Assert.Null(Pairing.Offer.Parse("{}"));
        Assert.Null(Pairing.Offer.Parse("""{"hostPub":"a","ephPub":"b","nonce":"c"}""")); // no ttl
        Assert.Null(Pairing.Offer.Parse("""{"hostPub":1,"ephPub":"b","nonce":"c","ttl":1}"""));
    }

    [Fact]
    public void An_expired_offer_is_detected()
    {
        var now = DateTimeOffset.UnixEpoch.AddMinutes(10);
        var expired = new Pairing.Offer("p", "e", "n", "h", "r", now.AddMinutes(-1).ToUnixTimeMilliseconds());
        var live = new Pairing.Offer("p", "e", "n", "h", "r", now.AddMinutes(1).ToUnixTimeMilliseconds());

        Assert.True(expired.IsExpired(now));
        Assert.False(live.IsExpired(now));
    }
}
