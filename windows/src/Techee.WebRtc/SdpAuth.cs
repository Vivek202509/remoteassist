using Techee.Crypto;

namespace Techee.WebRtc;

/// <summary>
/// Why an inbound SDP was accepted or rejected.
/// </summary>
/// <remarks>
/// Mirrors Android's <c>SdpVerdict</c> exactly. Distinct reasons exist so a rejection
/// is diagnosable from one audit line without reproducing it, and so the adversarial
/// tests can assert <i>which</i> control stopped an attack rather than merely that
/// something did.
/// </remarks>
public enum SdpVerdict
{
    Ok,

    /// <summary>No session is expecting a peer, so there is nothing to authenticate against.</summary>
    NoExpectedPeer,

    /// <summary><c>from</c> is not the peer this session was established with.</summary>
    PeerMismatch,

    /// <summary>No trusted public key for the expected peer — unpaired, pending, or revoked.</summary>
    UnknownPeerKey,

    /// <summary>The supplied key does not hash to the expected peer's device ID.</summary>
    KeyIdentityMismatch,

    /// <summary>The peer sent no signature at all.</summary>
    MissingSignature,

    /// <summary>The SDP carries no DTLS fingerprint, so there is nothing to bind media to.</summary>
    NoFingerprint,

    /// <summary>The signature did not verify over the transcript.</summary>
    BadSignature,
}

/// <summary>
/// Authenticates remote SDP, fail-closed.
/// </summary>
/// <remarks>
/// <para>
/// This is the control that makes a compromised broker survivable. The broker relays
/// every offer and answer and could rewrite them freely — but each peer signs its own
/// DTLS fingerprint with its identity key, so a rewritten SDP fails verification and a
/// broker cannot insert itself as a media endpoint.
/// </para>
/// <para>
/// <b>Absence of evidence is a rejection, never a pass.</b> An earlier Android version
/// returned "verified" whenever it had no public key for the sender and then adopted
/// whatever <c>from</c> claimed, which let an attacker inject an offer under an
/// unpaired identity mid-handshake and take over the session. Every missing input here
/// is a distinct failure verdict.
/// </para>
/// <para>
/// Deliberately free of SIPSorcery types so the adversarial cases are ordinary unit
/// tests with real P-256 keys — the same reasoning that keeps Android's version free
/// of WebRTC types.
/// </para>
/// </remarks>
public static class SdpAuth
{
    /// <summary>The <c>a=fingerprint:</c> line, or null when the SDP has none.</summary>
    public static string? FingerprintOf(string sdp) => TecheeCrypto.FingerprintLine(sdp);

    /// <summary>
    /// The bytes covered by the signature.
    /// </summary>
    /// <remarks>
    /// Returns null when the SDP has no fingerprint; such an SDP is unsignable and
    /// unverifiable by design.
    /// </remarks>
    public static byte[]? Transcript(string type, string fromId, string toId, string sdp) =>
        TecheeCrypto.SdpTranscript(type, fromId, toId, sdp);

    /// <summary>
    /// Signs an outbound offer or answer.
    /// </summary>
    /// <remarks>
    /// Returns an empty string for an SDP with no fingerprint. The peer then rejects
    /// it with <see cref="SdpVerdict.MissingSignature"/>, which is the correct outcome
    /// for an SDP that could never have been bound to a media identity anyway. Matches
    /// Android's behaviour rather than throwing.
    /// </remarks>
    public static string Sign(IDeviceIdentity identity, string type, string peerId, string sdp)
    {
        var transcript = Transcript(type, identity.DeviceId, peerId, sdp);
        return transcript is null ? string.Empty : TecheeCrypto.B64(identity.Sign(transcript));
    }

    /// <summary>
    /// Authenticates an inbound offer or answer.
    /// </summary>
    /// <param name="type"><c>offer</c> or <c>answer</c>. Bound into the transcript so an
    /// offer's signature cannot be replayed as an answer.</param>
    /// <param name="from">The claimed sender. Broker-stamped, but never trusted as the
    /// peer's identity — it is only compared against <paramref name="expectedPeerId"/>.</param>
    /// <param name="expectedPeerId">The peer this session was started with. Never taken
    /// from the incoming message.</param>
    /// <param name="peerPublicKeySpkiDer">The trusted key for <paramref name="expectedPeerId"/>,
    /// from the trust store. Null means unpaired or revoked — a rejection, not a bypass.</param>
    public static SdpVerdict Verify(
        string type,
        string from,
        string expectedPeerId,
        string localDeviceId,
        string sdp,
        byte[]? signature,
        byte[]? peerPublicKeySpkiDer)
    {
        if (string.IsNullOrWhiteSpace(expectedPeerId)) return SdpVerdict.NoExpectedPeer;

        // An unsolicited offer from anyone other than the peer we are already
        // negotiating with dies here, mid-session or in flight.
        if (from != expectedPeerId) return SdpVerdict.PeerMismatch;

        if (peerPublicKeySpkiDer is null || peerPublicKeySpkiDer.Length == 0)
            return SdpVerdict.UnknownPeerKey;

        // The key must actually be the expected peer's identity, not merely some
        // well-formed key that happened to be handed to us.
        if (TecheeCrypto.DeviceIdFor(peerPublicKeySpkiDer) != expectedPeerId)
            return SdpVerdict.KeyIdentityMismatch;

        if (signature is null || signature.Length == 0) return SdpVerdict.MissingSignature;

        var transcript = Transcript(type, from, localDeviceId, sdp);
        if (transcript is null) return SdpVerdict.NoFingerprint;

        return TecheeCrypto.Verify(peerPublicKeySpkiDer, transcript, signature)
            ? SdpVerdict.Ok
            : SdpVerdict.BadSignature;
    }

    /// <summary>
    /// Whether a rejection should tear the session down.
    /// </summary>
    /// <remarks>
    /// <see cref="SdpVerdict.PeerMismatch"/> and <see cref="SdpVerdict.NoExpectedPeer"/>
    /// mean the SDP came from a third party, so it is discarded <b>without touching the
    /// session</b>. Tearing down there would hand any registered device a trivial way to
    /// kill anyone else's session — trading a takeover bug for a denial-of-service one.
    /// Every other failure is the expected peer failing to authenticate, so the link
    /// must not continue.
    /// </remarks>
    public static bool ShouldTearDown(SdpVerdict verdict) =>
        verdict is not (SdpVerdict.Ok or SdpVerdict.PeerMismatch or SdpVerdict.NoExpectedPeer);
}
