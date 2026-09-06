using SIPSorcery.Net;
using Techee.Crypto;
using Techee.WebRtc;
using Xunit;
using Xunit.Abstractions;

namespace Techee.WebRtc.Tests;

/// <summary>
/// What an SDP actually negotiated, as opposed to what the encoder happens to produce.
/// </summary>
/// <remarks>
/// <para>
/// The failure this guards against is quiet: a session can connect, complete DTLS and
/// carry RTP while the far end discards every packet, because it negotiated a payload
/// type this side never sends. Nothing errors — the viewer just sees black.
/// </para>
/// <para>
/// Half the cases here run against SDP that SIPSorcery genuinely produced, and half
/// against hand-written SDP covering outcomes a cooperating library would not generate
/// for us: a declined media section, a codec stripped from the answer, an rtpmap that
/// contradicts its own media line.
/// </para>
/// </remarks>
public class SdpInspectTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<RTCIceServer> NoIceServers = [];

    private static async Task<(EphemeralDeviceIdentity Local, EphemeralDeviceIdentity Peer, TecheePeerConnection Connection)>
        PeerAsync(bool isHost, ITestOutputHelper output)
    {
        var local = new EphemeralDeviceIdentity();
        var peer = new EphemeralDeviceIdentity();

        var connection = new TecheePeerConnection(
            local, peer.DeviceId, _ => peer.PublicKeySpkiDer, output.WriteLine);

        await connection.InitializeAsync(NoIceServers, isHost);
        return (local, peer, connection);
    }

    // ---- against SDP SIPSorcery really produced ----

    [Fact]
    public async Task A_host_offer_negotiates_vp8_on_an_active_video_section()
    {
        var (local, peer, connection) = await PeerAsync(isHost: true, output);
        using var _ = local;
        using var __ = peer;
        await using var ___ = connection;

        var (sdp, _) = connection.CreateOffer();
        var video = SdpInspect.Video(sdp);

        Assert.NotNull(video);
        output.WriteLine($"m=video port {video!.Port}, direction {video.Direction}, " +
                         $"payload types [{string.Join(",", video.PayloadTypes)}]");
        foreach (var map in video.RtpMaps) output.WriteLine($"  rtpmap {map.PayloadType} {map.EncodingName}/{map.ClockRate}");

        Assert.True(video.IsActive, "the offer declined its own video section");

        // The codec claim, made against the m= line rather than the rtpmap alone.
        var vp8 = video.PayloadTypeFor("VP8");
        Assert.NotNull(vp8);
        Assert.Contains(vp8!.Value, video.PayloadTypes);

        // 90 kHz is the RTP clock rate WebRTC video is fixed at.
        var vp8Map = video.RtpMaps.First(m => m.Is("VP8"));
        Assert.Equal(TecheePeerConnection.VideoClockRate, vp8Map.ClockRate);

        // The host shares its screen and receives nothing back on this track.
        Assert.Equal("sendonly", video.Direction);

        // Everything the transport needs, present in the same SDP.
        Assert.NotNull(video.IceUfrag);
        Assert.NotNull(video.IcePwd);
        Assert.NotNull(SdpInspect.Fingerprint(sdp));
    }

    [Fact]
    public async Task A_controller_answer_agrees_on_the_same_vp8_payload_type()
    {
        // The assertion that matters: agreement, not two independent claims about VP8.
        using var hostIdentity = new EphemeralDeviceIdentity();
        using var controllerIdentity = new EphemeralDeviceIdentity();

        await using var host = new TecheePeerConnection(
            hostIdentity, controllerIdentity.DeviceId, _ => controllerIdentity.PublicKeySpkiDer,
            m => output.WriteLine($"host {m}"));
        await using var controller = new TecheePeerConnection(
            controllerIdentity, hostIdentity.DeviceId, _ => hostIdentity.PublicKeySpkiDer,
            m => output.WriteLine($"ctrl {m}"));

        await host.InitializeAsync(NoIceServers, isHost: true);
        await controller.InitializeAsync(NoIceServers, isHost: false);

        var (offer, offerSig) = host.CreateOffer();
        Assert.Equal(SdpVerdict.Ok, controller.AcceptRemoteDescription(
            "offer", hostIdentity.DeviceId, offer, offerSig));

        var (answer, answerSig) = controller.CreateAnswer();
        Assert.Equal(SdpVerdict.Ok, host.AcceptRemoteDescription(
            "answer", controllerIdentity.DeviceId, answer, answerSig));

        var offered = SdpInspect.VideoPayloadType(offer, "VP8");
        var answered = SdpInspect.VideoPayloadType(answer, "VP8");

        output.WriteLine($"offer VP8 pt={offered}, answer VP8 pt={answered}");

        Assert.NotNull(offered);
        Assert.NotNull(answered);
        Assert.Equal(offered, answered);

        // The answering side receives video; it does not send any.
        var answerVideo = SdpInspect.Video(answer)!;
        Assert.True(answerVideo.IsActive, "the controller declined the video section");
        Assert.Equal("recvonly", answerVideo.Direction);

        // The answer carries its own ICE credentials and its own fingerprint, which is
        // what SdpAuth binds the media identity to.
        Assert.NotNull(answerVideo.IceUfrag);
        Assert.NotNull(answerVideo.IcePwd);
        Assert.NotNull(SdpInspect.Fingerprint(answer));
        Assert.NotEqual(SdpInspect.IceUfrag(offer), SdpInspect.IceUfrag(answer));
    }

    [Fact]
    public async Task Inspecting_an_sdp_does_not_disturb_its_signature()
    {
        // SdpInspect is an observer. If it ever normalised or rewrote SDP, the signature
        // that makes a compromised broker survivable would stop verifying.
        var (local, peer, connection) = await PeerAsync(isHost: true, output);
        using var localIdentity = local;
        using var peerIdentity = peer;
        await using var pc = connection;

        var (sdp, signature) = connection.CreateOffer();

        SdpInspect.Video(sdp);
        SdpInspect.VideoPayloadType(sdp, "VP8");
        SdpInspect.IceUfrag(sdp);
        SdpInspect.Fingerprint(sdp);

        var verdict = SdpAuth.Verify(
            "offer", local.DeviceId, local.DeviceId, peer.DeviceId, sdp,
            TecheeCrypto.UnB64(signature), local.PublicKeySpkiDer);

        Assert.Equal(SdpVerdict.Ok, verdict);
        Assert.Equal(SdpAuth.FingerprintOf(sdp), SdpInspect.Fingerprint(sdp));
    }

    // ---- outcomes a cooperating library would not hand us ----

    private const string DeclinedVideo = """
        v=0
        o=- 1 1 IN IP4 127.0.0.1
        s=-
        t=0 0
        a=ice-ufrag:ABCD
        a=ice-pwd:0123456789abcdef
        a=fingerprint:sha-256 AA:BB
        m=video 0 UDP/TLS/RTP/SAVPF 96
        a=rtpmap:96 VP8/90000
        a=recvonly
        """;

    [Fact]
    public void A_video_section_answered_with_port_zero_is_not_a_negotiated_codec()
    {
        // The trap: a complete-looking m=video block with a VP8 rtpmap and a direction,
        // which nonetheless means "no video". Reading the rtpmap alone would call this a
        // success and the viewer would see nothing.
        var video = SdpInspect.Video(DeclinedVideo);

        Assert.NotNull(video);
        Assert.False(video!.IsActive);
        Assert.True(video.Offers("VP8"), "the rtpmap is present; it is the port that declines");

        Assert.False(SdpInspect.NegotiatedVideo(DeclinedVideo, "VP8"),
            "a declined section must not count as negotiated video");
    }

    private const string H264Only = """
        v=0
        o=- 1 1 IN IP4 127.0.0.1
        s=-
        t=0 0
        m=video 9 UDP/TLS/RTP/SAVPF 102
        a=rtpmap:102 H264/90000
        a=recvonly
        """;

    [Fact]
    public void An_answer_without_vp8_is_reported_as_no_vp8()
    {
        // A peer that strips VP8 and answers H264 only. The host's encoder would keep
        // producing VP8 that the far end cannot decode.
        Assert.False(SdpInspect.NegotiatedVideo(H264Only, "VP8"));
        Assert.Null(SdpInspect.VideoPayloadType(H264Only, "VP8"));

        Assert.True(SdpInspect.NegotiatedVideo(H264Only, "H264"));
        Assert.Equal(102, SdpInspect.VideoPayloadType(H264Only, "H264"));
    }

    private const string ContradictoryRtpMap = """
        v=0
        o=- 1 1 IN IP4 127.0.0.1
        s=-
        t=0 0
        m=video 9 UDP/TLS/RTP/SAVPF 102
        a=rtpmap:96 VP8/90000
        a=rtpmap:102 H264/90000
        a=recvonly
        """;

    [Fact]
    public void An_rtpmap_the_media_line_never_offered_is_not_a_negotiated_codec()
    {
        // Stale or injected text. VP8 has an rtpmap but payload type 96 is absent from
        // the m= line, so nothing was negotiated for it.
        Assert.Null(SdpInspect.VideoPayloadType(ContradictoryRtpMap, "VP8"));
        Assert.False(SdpInspect.NegotiatedVideo(ContradictoryRtpMap, "VP8"));
        Assert.Equal(102, SdpInspect.VideoPayloadType(ContradictoryRtpMap, "H264"));
    }

    [Fact]
    public void An_sdp_with_no_video_section_reports_nothing_rather_than_throwing()
    {
        const string audioOnly = """
            v=0
            o=- 1 1 IN IP4 127.0.0.1
            s=-
            t=0 0
            m=audio 9 UDP/TLS/RTP/SAVPF 111
            a=rtpmap:111 opus/48000/2
            """;

        Assert.Null(SdpInspect.Video(audioOnly));
        Assert.False(SdpInspect.NegotiatedVideo(audioOnly, "VP8"));
        Assert.Null(SdpInspect.VideoPayloadType(audioOnly, "VP8"));

        Assert.NotNull(SdpInspect.Audio(audioOnly));
        Assert.Equal(111, SdpInspect.Audio(audioOnly)!.PayloadTypeFor("opus"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an sdp at all")]
    [InlineData("v=0\nm=video\n")]
    [InlineData("v=0\nm=video notaport UDP/TLS/RTP/SAVPF xx\na=rtpmap:oops\n")]
    public void Malformed_sdp_is_reported_rather_than_throwing(string sdp)
    {
        // The broker is untrusted and relays SDP verbatim, so this parser sees whatever a
        // hostile party sends. It must never be the thing that takes the host down.
        var video = SdpInspect.Video(sdp);
        Assert.False(SdpInspect.NegotiatedVideo(sdp, "VP8"));

        if (video is not null) Assert.Null(video.PayloadTypeFor("VP8"));
    }

    [Fact]
    public void Media_level_ice_credentials_win_over_session_level_ones()
    {
        // Under BUNDLE both levels can appear. The media section's own credentials are
        // the ones that apply to its transport.
        const string bothLevels = """
            v=0
            o=- 1 1 IN IP4 127.0.0.1
            s=-
            t=0 0
            a=ice-ufrag:SESSION
            a=ice-pwd:sessionpassword
            m=video 9 UDP/TLS/RTP/SAVPF 96
            a=rtpmap:96 VP8/90000
            a=ice-ufrag:MEDIA
            a=ice-pwd:mediapassword
            a=sendonly
            """;

        var video = SdpInspect.Video(bothLevels)!;

        Assert.Equal("MEDIA", video.IceUfrag);
        Assert.Equal("mediapassword", video.IcePwd);
    }

    [Fact]
    public void Session_level_ice_credentials_apply_when_the_media_section_omits_them()
    {
        const string sessionOnly = """
            v=0
            o=- 1 1 IN IP4 127.0.0.1
            s=-
            t=0 0
            a=ice-ufrag:SESSION
            a=ice-pwd:sessionpassword
            m=video 9 UDP/TLS/RTP/SAVPF 96
            a=rtpmap:96 VP8/90000
            a=sendonly
            """;

        var video = SdpInspect.Video(sessionOnly)!;

        Assert.Equal("SESSION", video.IceUfrag);
        Assert.Equal("sessionpassword", video.IcePwd);
    }
}
