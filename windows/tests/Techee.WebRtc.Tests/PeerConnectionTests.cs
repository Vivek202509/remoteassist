using SIPSorcery.Net;
using Techee.Crypto;
using Techee.Protocol;
using Techee.WebRtc;
using Xunit;
using Xunit.Abstractions;

namespace Techee.WebRtc.Tests;

/// <summary>
/// Two real SIPSorcery peer connections negotiating through Techee's authentication.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SdpAuthTests"/> proves the verdicts with synthetic SDP. This proves the
/// same logic works against SDP that SIPSorcery actually produces — including that its
/// fingerprint lines are where <see cref="SdpAuth"/> expects, which is the assumption
/// the whole MITM defence rests on.
/// </para>
/// <para>
/// No network: the two connections exchange descriptions in-process, the way the broker
/// would relay them. ICE never completes here, so this covers signalling and
/// authentication rather than media flow.
/// </para>
/// </remarks>
public class PeerConnectionTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<RTCIceServer> NoIceServers = [];

    private sealed class Endpoint : IAsyncDisposable
    {
        public required EphemeralDeviceIdentity Identity { get; init; }
        public required TecheePeerConnection Connection { get; init; }

        public ValueTask DisposeAsync()
        {
            Identity.Dispose();
            return Connection.DisposeAsync();
        }
    }

    /// <summary>Builds a pair that trust each other, as a completed pairing would leave them.</summary>
    private static async Task<(Endpoint Host, Endpoint Controller)> PairAsync(ITestOutputHelper output)
    {
        var hostIdentity = new EphemeralDeviceIdentity();
        var controllerIdentity = new EphemeralDeviceIdentity();

        var host = new TecheePeerConnection(
            hostIdentity, controllerIdentity.DeviceId,
            _ => controllerIdentity.PublicKeySpkiDer,
            m => output.WriteLine($"host  {m}"));

        var controller = new TecheePeerConnection(
            controllerIdentity, hostIdentity.DeviceId,
            _ => hostIdentity.PublicKeySpkiDer,
            m => output.WriteLine($"ctrl  {m}"));

        await host.InitializeAsync(NoIceServers, isHost: true);
        await controller.InitializeAsync(NoIceServers, isHost: false);

        return (
            new Endpoint { Identity = hostIdentity, Connection = host },
            new Endpoint { Identity = controllerIdentity, Connection = controller });
    }

    [Fact]
    public async Task A_host_offer_is_signed_and_the_controller_accepts_it()
    {
        var (host, controller) = await PairAsync(output);
        await using var _ = host;
        await using var __ = controller;

        var (sdp, signature) = host.Connection.CreateOffer();

        output.WriteLine($"offer is {sdp.Length} bytes, signature {signature.Length} chars");
        Assert.NotEmpty(signature);

        // The assumption the whole MITM defence rests on: SIPSorcery emits a
        // fingerprint where SdpAuth looks for it.
        Assert.NotNull(SdpAuth.FingerprintOf(sdp));
        Assert.Contains("a=fingerprint:sha-256", sdp);

        var verdict = controller.Connection.AcceptRemoteDescription(
            "offer", host.Identity.DeviceId, sdp, signature);

        Assert.Equal(SdpVerdict.Ok, verdict);
    }

    [Fact]
    public async Task A_full_offer_answer_exchange_authenticates_in_both_directions()
    {
        var (host, controller) = await PairAsync(output);
        await using var _ = host;
        await using var __ = controller;

        var (offer, offerSig) = host.Connection.CreateOffer();
        Assert.Equal(SdpVerdict.Ok, controller.Connection.AcceptRemoteDescription(
            "offer", host.Identity.DeviceId, offer, offerSig));

        var (answer, answerSig) = controller.Connection.CreateAnswer();
        Assert.NotEmpty(answerSig);
        Assert.NotNull(SdpAuth.FingerprintOf(answer));

        Assert.Equal(SdpVerdict.Ok, host.Connection.AcceptRemoteDescription(
            "answer", controller.Identity.DeviceId, answer, answerSig));
    }

    [Fact]
    public async Task A_broker_that_rewrites_the_fingerprint_is_caught()
    {
        // The attack the design exists to stop: a compromised broker substitutes its own
        // DTLS fingerprint so it terminates the media itself.
        var (host, controller) = await PairAsync(output);
        await using var _ = host;
        await using var __ = controller;

        var (offer, offerSig) = host.Connection.CreateOffer();

        var original = SdpAuth.FingerprintOf(offer)!;
        var tampered = offer.Replace(original, "a=fingerprint:sha-256 " + string.Join(':',
            Enumerable.Repeat("AA", 32)));

        output.WriteLine($"original: {original}");
        output.WriteLine($"tampered: {SdpAuth.FingerprintOf(tampered)}");

        var verdict = controller.Connection.AcceptRemoteDescription(
            "offer", host.Identity.DeviceId, tampered, offerSig);

        Assert.Equal(SdpVerdict.BadSignature, verdict);
        Assert.Equal(LinkState.Closed, controller.Connection.State);
    }

    [Fact]
    public async Task An_unsigned_offer_is_refused()
    {
        var (host, controller) = await PairAsync(output);
        await using var _ = host;
        await using var __ = controller;

        var (offer, _) = host.Connection.CreateOffer();

        Assert.Equal(SdpVerdict.MissingSignature, controller.Connection.AcceptRemoteDescription(
            "offer", host.Identity.DeviceId, offer, signatureB64: null));
    }

    [Fact]
    public async Task An_offer_from_an_untrusted_peer_is_refused()
    {
        // The trust store returning null is what an unpaired, pending, or REVOKED peer
        // looks like. Absence of a key must never be a skipped check.
        using var hostIdentity = new EphemeralDeviceIdentity();
        using var strangerIdentity = new EphemeralDeviceIdentity();

        await using var controller = new TecheePeerConnection(
            hostIdentity, strangerIdentity.DeviceId, _ => null, output.WriteLine);
        await controller.InitializeAsync(NoIceServers, isHost: false);

        await using var stranger = new TecheePeerConnection(
            strangerIdentity, hostIdentity.DeviceId, _ => hostIdentity.PublicKeySpkiDer);
        await stranger.InitializeAsync(NoIceServers, isHost: true);

        var (offer, sig) = stranger.CreateOffer();

        Assert.Equal(SdpVerdict.UnknownPeerKey, controller.AcceptRemoteDescription(
            "offer", strangerIdentity.DeviceId, offer, sig));
    }

    [Fact]
    public async Task An_offer_from_a_third_party_does_not_close_the_session()
    {
        // Tearing down here would let any registered device kill anyone else's session
        // by sending them an offer.
        var (host, controller) = await PairAsync(output);
        await using var _ = host;
        await using var __ = controller;

        using var thirdParty = new EphemeralDeviceIdentity();
        var (offer, sig) = host.Connection.CreateOffer();

        var verdict = controller.Connection.AcceptRemoteDescription(
            "offer", thirdParty.DeviceId, offer, sig);

        Assert.Equal(SdpVerdict.PeerMismatch, verdict);
        Assert.NotEqual(LinkState.Closed, controller.Connection.State);
    }

    [Fact]
    public async Task Ice_candidates_from_a_third_party_are_ignored()
    {
        // Android discards the sender here, so any registered device can inject
        // candidates into an unrelated live session. This does not repeat that.
        var (host, controller) = await PairAsync(output);
        await using var _ = host;
        await using var __ = controller;

        using var thirdParty = new EphemeralDeviceIdentity();

        Assert.False(controller.Connection.AddRemoteIceCandidate(
            thirdParty.DeviceId, "candidate:1 1 udp 1 10.0.0.1 5000 typ host", "0", 0));

        Assert.True(controller.Connection.AddRemoteIceCandidate(
            host.Identity.DeviceId, "candidate:1 1 udp 1 10.0.0.1 5000 typ host", "0", 0));
    }

    [Fact]
    public async Task A_failed_transport_asks_to_be_replaced_exactly_once()
    {
        // The recovery policy, without needing a real network failure. Five callbacks
        // must not produce five replacement peers: the session layer would end up with
        // several concurrent transports for one session, which is the duplicate-ownership
        // case reconnection exists to avoid.
        var (host, controller) = await PairAsync(output);
        await using var _ = host;
        await using var __ = controller;

        var requests = 0;
        host.Connection.TransportRecoveryNeeded += () => Interlocked.Increment(ref requests);

        Assert.False(host.Connection.RecoveryRequested);

        for (var i = 0; i < 5; i++)
        {
            host.Connection.HandleConnectionStateChange(RTCPeerConnectionState.failed);
        }

        Assert.Equal(1, requests);
        Assert.True(host.Connection.RecoveryRequested);
        Assert.Equal(LinkState.Recovering, host.Connection.State);
    }

    [Fact]
    public async Task A_disconnected_transport_waits_rather_than_replacing_itself()
    {
        // ICE dips through disconnected routinely and recovers on its own. Replacing the
        // peer here would turn a NAT rebind into a visible reconnect. The grace period is
        // ICE's own escalation to failed.
        var (host, controller) = await PairAsync(output);
        await using var _ = host;
        await using var __ = controller;

        var requests = 0;
        host.Connection.TransportRecoveryNeeded += () => Interlocked.Increment(ref requests);

        host.Connection.HandleConnectionStateChange(RTCPeerConnectionState.disconnected);

        Assert.Equal(0, requests);
        Assert.False(host.Connection.RecoveryRequested);
        Assert.Equal(LinkState.Recovering, host.Connection.State);

        // Recovering it on its own is the expected outcome, not a replacement peer.
        host.Connection.HandleConnectionStateChange(RTCPeerConnectionState.connected);

        Assert.Equal(0, requests);
        Assert.Equal(LinkState.Connected, host.Connection.State);
    }

    [Fact]
    public async Task The_host_offers_video_and_a_control_channel()
    {
        var (host, controller) = await PairAsync(output);
        await using var _ = host;
        await using var __ = controller;

        var (offer, _) = host.Connection.CreateOffer();

        Assert.Contains("m=video", offer);
        Assert.Contains("VP8", offer, StringComparison.OrdinalIgnoreCase);
        // The DataChannel shows up as an SCTP m-section.
        Assert.Contains("m=application", offer);
    }

    [Fact]
    public async Task Sending_before_the_link_is_connected_is_a_no_op_not_a_crash()
    {
        // The pump starts producing frames as soon as capture is running, which can be
        // before ICE completes. Dropping them silently is correct; throwing on the
        // capture thread would take the host down.
        var (host, controller) = await PairAsync(output);
        await using var _ = host;
        await using var __ = controller;

        host.Connection.SendVideoFrame(new byte[128], fps: 30);
        Assert.False(host.Connection.SendControl(new Control.PointerTap(0.5, 0.5)));

        var telemetry = host.Connection.GetTelemetry();
        Assert.Equal(0, telemetry.VideoFramesSent);
    }

    [Fact]
    public async Task Telemetry_reports_state_without_fabricating_numbers()
    {
        var (host, controller) = await PairAsync(output);
        await using var _ = host;
        await using var __ = controller;

        var telemetry = host.Connection.GetTelemetry();

        Assert.Equal(LinkState.Connecting, telemetry.State);
        Assert.Equal(0, telemetry.VideoBytesSent);
        // No RTCP report yet, so loss is zero because nothing has said otherwise —
        // not because a number was invented.
        Assert.Equal(0.0, telemetry.PacketLossFraction);
        Assert.False(telemetry.UsingRelay);
    }

    [Fact]
    public async Task Using_the_connection_before_initialisation_fails_clearly()
    {
        using var identity = new EphemeralDeviceIdentity();
        using var peer = new EphemeralDeviceIdentity();
        await using var connection = new TecheePeerConnection(identity, peer.DeviceId, _ => null);

        var e = Assert.Throws<InvalidOperationException>(() => connection.CreateOffer());
        Assert.Contains("InitializeAsync", e.Message);
    }
}
