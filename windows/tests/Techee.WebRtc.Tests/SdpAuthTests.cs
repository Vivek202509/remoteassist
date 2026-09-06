using System.Text;
using Techee.Crypto;
using Techee.WebRtc;
using Xunit;

namespace Techee.WebRtc.Tests;

/// <summary>
/// Adversarial tests for SDP authentication, with real P-256 keys and no mocks.
/// </summary>
/// <remarks>
/// Mirrors Android's <c>SdpAuthTest</c> case for case. This is the control that makes a
/// compromised broker survivable, so each test names the specific attack it blocks
/// rather than merely asserting a boolean.
/// </remarks>
public class SdpAuthTests
{
    private const string FpA = "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";
    private const string FpB = "99:88:77:66:55:44:33:22:11:00:FF:EE:DD:CC:BB:AA";

    private static string Sdp(string fingerprint = FpA, string extra = "") =>
        "v=0\n" +
        "o=- 1 1 IN IP4 0.0.0.0\n" +
        "s=-\n" +
        $"a=fingerprint:sha-256 {fingerprint}\n" +
        "a=setup:actpass\n" +
        extra;

    private sealed record Party(EphemeralDeviceIdentity Identity)
    {
        public string Id => Identity.DeviceId;
        public byte[] Key => Identity.PublicKeySpkiDer;
    }

    private static Party NewParty() => new(new EphemeralDeviceIdentity());

    // ---- the happy path ----

    [Fact]
    public void A_correctly_signed_offer_is_accepted()
    {
        var host = NewParty();
        var controller = NewParty();
        var sdp = Sdp();

        var signature = TecheeCrypto.UnB64(SdpAuth.Sign(host.Identity, "offer", controller.Id, sdp));

        Assert.Equal(SdpVerdict.Ok, SdpAuth.Verify(
            "offer", host.Id, host.Id, controller.Id, sdp, signature, host.Key));
    }

    [Fact]
    public void An_answer_round_trips_in_the_other_direction()
    {
        var host = NewParty();
        var controller = NewParty();
        var sdp = Sdp(FpB);

        var signature = TecheeCrypto.UnB64(SdpAuth.Sign(controller.Identity, "answer", host.Id, sdp));

        Assert.Equal(SdpVerdict.Ok, SdpAuth.Verify(
            "answer", controller.Id, controller.Id, host.Id, sdp, signature, controller.Key));
    }

    // ---- every rejection path ----

    [Fact]
    public void No_expected_peer_is_a_rejection_not_an_adoption()
    {
        // The original Android bug: with no session peer, whatever `from` claimed was
        // adopted. An attacker could then own the session by speaking first.
        var attacker = NewParty();
        var local = NewParty();
        var sdp = Sdp();
        var signature = TecheeCrypto.UnB64(SdpAuth.Sign(attacker.Identity, "offer", local.Id, sdp));

        Assert.Equal(SdpVerdict.NoExpectedPeer, SdpAuth.Verify(
            "offer", attacker.Id, "", local.Id, sdp, signature, attacker.Key));
    }

    [Fact]
    public void An_sdp_from_a_third_party_is_rejected()
    {
        var expected = NewParty();
        var attacker = NewParty();
        var local = NewParty();
        var sdp = Sdp();

        // Perfectly valid — signed by the attacker, for the right recipient. It is
        // rejected because it is not from the peer this session belongs to.
        var signature = TecheeCrypto.UnB64(SdpAuth.Sign(attacker.Identity, "offer", local.Id, sdp));

        Assert.Equal(SdpVerdict.PeerMismatch, SdpAuth.Verify(
            "offer", attacker.Id, expected.Id, local.Id, sdp, signature, attacker.Key));
    }

    [Fact]
    public void An_unpaired_peer_is_a_rejection_never_a_skipped_check()
    {
        // Absence of evidence must not be evidence of absence. A missing key is the
        // exact condition under which the old implementation returned "verified".
        var peer = NewParty();
        var local = NewParty();
        var sdp = Sdp();
        var signature = TecheeCrypto.UnB64(SdpAuth.Sign(peer.Identity, "offer", local.Id, sdp));

        Assert.Equal(SdpVerdict.UnknownPeerKey, SdpAuth.Verify(
            "offer", peer.Id, peer.Id, local.Id, sdp, signature, peerPublicKeySpkiDer: null));

        Assert.Equal(SdpVerdict.UnknownPeerKey, SdpAuth.Verify(
            "offer", peer.Id, peer.Id, local.Id, sdp, signature, peerPublicKeySpkiDer: []));
    }

    [Fact]
    public void A_well_formed_key_that_is_not_the_expected_peers_is_rejected()
    {
        // Guards the trust-store lookup: a key must actually hash to the identity we
        // expect, not merely be a valid key someone handed us.
        var expected = NewParty();
        var other = NewParty();
        var local = NewParty();
        var sdp = Sdp();
        var signature = TecheeCrypto.UnB64(SdpAuth.Sign(other.Identity, "offer", local.Id, sdp));

        Assert.Equal(SdpVerdict.KeyIdentityMismatch, SdpAuth.Verify(
            "offer", expected.Id, expected.Id, local.Id, sdp, signature, other.Key));
    }

    [Fact]
    public void An_unsigned_sdp_is_rejected()
    {
        var peer = NewParty();
        var local = NewParty();

        Assert.Equal(SdpVerdict.MissingSignature, SdpAuth.Verify(
            "offer", peer.Id, peer.Id, local.Id, Sdp(), null, peer.Key));

        Assert.Equal(SdpVerdict.MissingSignature, SdpAuth.Verify(
            "offer", peer.Id, peer.Id, local.Id, Sdp(), [], peer.Key));
    }

    [Fact]
    public void An_sdp_with_no_fingerprint_cannot_be_authenticated()
    {
        var peer = NewParty();
        var local = NewParty();
        var noFingerprint = "v=0\ns=-\n";

        Assert.Equal(SdpVerdict.NoFingerprint, SdpAuth.Verify(
            "offer", peer.Id, peer.Id, local.Id, noFingerprint, [1, 2, 3], peer.Key));

        // ...and signing one produces an empty signature rather than throwing, so the
        // failure surfaces at the peer as MissingSignature instead of crashing us.
        Assert.Equal(string.Empty, SdpAuth.Sign(peer.Identity, "offer", local.Id, noFingerprint));
    }

    [Fact]
    public void A_garbage_signature_is_rejected()
    {
        var peer = NewParty();
        var local = NewParty();

        Assert.Equal(SdpVerdict.BadSignature, SdpAuth.Verify(
            "offer", peer.Id, peer.Id, local.Id, Sdp(), [0xDE, 0xAD, 0xBE, 0xEF], peer.Key));
    }

    // ---- tampering ----

    [Fact]
    public void Swapping_the_fingerprint_invalidates_the_signature()
    {
        // The core MITM scenario: a broker substitutes its own DTLS fingerprint so it
        // terminates the media itself.
        var peer = NewParty();
        var local = NewParty();
        var original = Sdp(FpA);
        var signature = TecheeCrypto.UnB64(SdpAuth.Sign(peer.Identity, "offer", local.Id, original));

        Assert.Equal(SdpVerdict.BadSignature, SdpAuth.Verify(
            "offer", peer.Id, peer.Id, local.Id, Sdp(FpB), signature, peer.Key));
    }

    [Fact]
    public void Tampering_anywhere_else_in_the_sdp_also_invalidates_it()
    {
        // The full-SDP digest is why: changing a codec or an ICE ufrag is detected even
        // though the fingerprint line is untouched.
        var peer = NewParty();
        var local = NewParty();
        var original = Sdp();
        var signature = TecheeCrypto.UnB64(SdpAuth.Sign(peer.Identity, "offer", local.Id, original));

        Assert.Equal(SdpVerdict.BadSignature, SdpAuth.Verify(
            "offer", peer.Id, peer.Id, local.Id, Sdp(FpA, "a=ice-ufrag:attacker\n"), signature, peer.Key));
    }

    [Fact]
    public void An_offer_signature_cannot_be_replayed_as_an_answer()
    {
        var peer = NewParty();
        var local = NewParty();
        var sdp = Sdp();
        var offerSignature = TecheeCrypto.UnB64(SdpAuth.Sign(peer.Identity, "offer", local.Id, sdp));

        Assert.Equal(SdpVerdict.BadSignature, SdpAuth.Verify(
            "answer", peer.Id, peer.Id, local.Id, sdp, offerSignature, peer.Key));
    }

    [Fact]
    public void A_signature_for_one_recipient_cannot_be_reused_against_another()
    {
        // The recipient is bound in, so a broker cannot harvest an offer meant for
        // device A and present it to device B.
        var peer = NewParty();
        var intended = NewParty();
        var other = NewParty();
        var sdp = Sdp();
        var signature = TecheeCrypto.UnB64(SdpAuth.Sign(peer.Identity, "offer", intended.Id, sdp));

        Assert.Equal(SdpVerdict.Ok, SdpAuth.Verify(
            "offer", peer.Id, peer.Id, intended.Id, sdp, signature, peer.Key));

        Assert.Equal(SdpVerdict.BadSignature, SdpAuth.Verify(
            "offer", peer.Id, peer.Id, other.Id, sdp, signature, peer.Key));
    }

    // ---- teardown policy ----

    [Fact]
    public void A_third_partys_sdp_does_not_tear_down_the_session()
    {
        // Deliberate: tearing down here would let any registered device kill anyone
        // else's session by sending them an offer — trading takeover for denial of
        // service.
        Assert.False(SdpAuth.ShouldTearDown(SdpVerdict.PeerMismatch));
        Assert.False(SdpAuth.ShouldTearDown(SdpVerdict.NoExpectedPeer));
        Assert.False(SdpAuth.ShouldTearDown(SdpVerdict.Ok));
    }

    [Fact]
    public void The_expected_peer_failing_to_authenticate_does_tear_down()
    {
        foreach (var verdict in new[]
                 {
                     SdpVerdict.UnknownPeerKey, SdpVerdict.KeyIdentityMismatch,
                     SdpVerdict.MissingSignature, SdpVerdict.NoFingerprint, SdpVerdict.BadSignature,
                 })
        {
            Assert.True(SdpAuth.ShouldTearDown(verdict), $"{verdict} must tear the link down");
        }
    }

    // ---- transcript shape ----

    [Fact]
    public void The_transcript_has_the_six_pinned_lines()
    {
        var transcript = SdpAuth.Transcript("offer", "AAA", "BBB", Sdp());
        Assert.NotNull(transcript);

        var lines = Encoding.UTF8.GetString(transcript!).Split('\n');
        Assert.Equal(6, lines.Length);
        Assert.Equal("techee-sdp-v1", lines[0]);
        Assert.Equal("offer", lines[1]);
        Assert.Equal("AAA", lines[2]);
        Assert.Equal("BBB", lines[3]);
        Assert.Equal($"a=fingerprint:sha-256 {FpA}", lines[4]);
        Assert.Matches("^[0-9a-f]{64}$", lines[5]);
    }

    [Fact]
    public void Crlf_sdp_does_not_leak_a_carriage_return_into_the_fingerprint_line()
    {
        // Real WebRTC SDP is CRLF. Splitting on LF leaves a dangling CR that would end
        // up inside the signed bytes on one platform but not the other — a mismatch
        // that only appears against real hardware.
        var crlf = "v=0\r\ns=-\r\na=fingerprint:sha-256 " + FpA + "\r\n";

        Assert.Equal($"a=fingerprint:sha-256 {FpA}", SdpAuth.FingerprintOf(crlf));
        Assert.DoesNotContain("\r", SdpAuth.FingerprintOf(crlf));
    }

    [Fact]
    public void Only_the_first_fingerprint_line_is_bound_but_the_digest_covers_the_rest()
    {
        // SIPSorcery emits one fingerprint per m-section. The first is bound
        // explicitly; the full-SDP digest covers every other one.
        var two = Sdp(FpA, $"a=fingerprint:sha-256 {FpB}\n");
        Assert.Equal($"a=fingerprint:sha-256 {FpA}", SdpAuth.FingerprintOf(two));

        var peer = NewParty();
        var local = NewParty();
        var signature = TecheeCrypto.UnB64(SdpAuth.Sign(peer.Identity, "offer", local.Id, two));

        Assert.Equal(SdpVerdict.Ok, SdpAuth.Verify(
            "offer", peer.Id, peer.Id, local.Id, two, signature, peer.Key));

        // Changing the SECOND fingerprint still invalidates it.
        var tampered = Sdp(FpA, "a=fingerprint:sha-256 00:00:00\n");
        Assert.Equal(SdpVerdict.BadSignature, SdpAuth.Verify(
            "offer", peer.Id, peer.Id, local.Id, tampered, signature, peer.Key));
    }

    [Fact]
    public void A_windows_signature_verifies_under_the_android_transcript_layout()
    {
        // The direction that matters in production: Windows signs, an Android
        // controller verifies. Android builds these bytes in SdpAuth.transcript, so
        // rebuilding them here from the spec proves the layouts agree.
        var host = NewParty();
        var controller = NewParty();
        var sdp = Sdp();

        var signature = TecheeCrypto.UnB64(SdpAuth.Sign(host.Identity, "offer", controller.Id, sdp));

        var digest = TecheeCrypto.Hex(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sdp)));
        var androidStyle = Encoding.UTF8.GetBytes(string.Join("\n",
            "techee-sdp-v1", "offer", host.Id, controller.Id,
            $"a=fingerprint:sha-256 {FpA}", digest));

        Assert.True(TecheeCrypto.Verify(host.Key, androidStyle, signature!));
    }
}
