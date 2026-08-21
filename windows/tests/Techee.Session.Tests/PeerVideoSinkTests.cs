using SIPSorcery.Net;
using Techee.Crypto;
using Techee.Session;
using Techee.WebRtc;
using Techee.Windows.Host;
using Techee.Windows.Host.Tests;
using Xunit.Abstractions;

namespace Techee.Session.Tests;

/// <summary>
/// The capture-to-transport seam: DXGI → I420 → VP8 → <see cref="EncodedFrame"/> →
/// <see cref="TecheePeerConnection"/>.
/// </summary>
/// <remarks>
/// <para>
/// The pump and the encoder are real <see cref="VideoPipeline"/> code driven by the same
/// fakes the pipeline's own tests use; only the GPU and libvpx are substituted. What is
/// under test is the wiring — that encoded output actually reaches the peer, that it
/// stops reaching a detached or replaced peer, and that nothing on this path can kill the
/// capture thread.
/// </para>
/// <para>
/// ICE never completes in-process, so a link is never genuinely <c>Connected</c> here.
/// The peer's connection-state handler is driven directly to reach the states that
/// matter. Real media flow is the Android-controller acceptance test, not this.
/// </para>
/// </remarks>
public class PeerVideoSinkTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<RTCIceServer> NoIceServers = [];

    private static async Task<(EphemeralDeviceIdentity Identity, EphemeralDeviceIdentity Peer, TecheePeerConnection Connection)>
        HostPeerAsync(ITestOutputHelper output)
    {
        var identity = new EphemeralDeviceIdentity();
        var peer = new EphemeralDeviceIdentity();

        var connection = new TecheePeerConnection(
            identity, peer.DeviceId, _ => peer.PublicKeySpkiDer, m => output.WriteLine($"peer  {m}"));

        await connection.InitializeAsync(NoIceServers, isHost: true);
        return (identity, peer, connection);
    }

    private static VideoPipeline PumpWith(FakeScreenSource source, FakeEncoder encoder, ITestOutputHelper output) =>
        new(source, encoder, new AdaptiveQuality(VideoProfile.Hd), m => output.WriteLine($"video {m}"));

    /// <summary>Waits for a condition the pump thread will satisfy, without a fixed sleep.</summary>
    private static bool SpinUntil(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5));

    /// <summary>A sink that records what the pipeline handed it.</summary>
    private sealed class RecordingSink : IEncodedVideoSink
    {
        private long _count;
        public long Count => Interlocked.Read(ref _count);
        public int LastFps { get; private set; }
        public EncodedFrame? Last { get; private set; }

        public void SendEncodedFrame(EncodedFrame frame, int fps)
        {
            Last = frame;
            LastFps = fps;
            Interlocked.Increment(ref _count);
        }
    }

    [Fact]
    public void Encoded_frames_reach_the_attached_sink()
    {
        using var source = new FakeScreenSource();
        using var encoder = new FakeEncoder();
        using var pump = PumpWith(source, encoder, output);

        var sink = new RecordingSink();
        pump.AttachSink(sink);
        Assert.True(pump.HasSink);

        pump.Start();
        Assert.True(SpinUntil(() => sink.Count >= 3), $"only {sink.Count} frames reached the sink");
        pump.Stop();

        output.WriteLine($"delivered {sink.Count} frames");

        // The transport is told the pacing rate so it can derive an RTP duration without
        // knowing what a quality profile is.
        Assert.Equal(VideoProfile.Hd.Fps, sink.LastFps);
        Assert.Equal(VideoProfile.Hd.Width, sink.Last!.Width);
        Assert.Equal(VideoProfile.Hd.Height, sink.Last.Height);
        Assert.NotEmpty(sink.Last.Payload);
    }

    [Fact]
    public void Detaching_the_sink_stops_delivery_without_stopping_capture()
    {
        using var source = new FakeScreenSource();
        using var encoder = new FakeEncoder();
        using var pump = PumpWith(source, encoder, output);

        var sink = new RecordingSink();
        pump.AttachSink(sink);
        pump.Start();
        Assert.True(SpinUntil(() => sink.Count >= 2));

        pump.DetachSink();
        Assert.False(pump.HasSink);

        var afterDetach = sink.Count;
        var capturedAtDetach = pump.Stats.FramesCaptured;

        // Capture must keep running: the expensive DXGI and encoder state is what a
        // reconnect is trying to preserve across the transport gap.
        Assert.True(SpinUntil(() => pump.Stats.FramesCaptured > capturedAtDetach + 2));
        pump.Stop();

        output.WriteLine($"sink saw {afterDetach} then {sink.Count}; captured {pump.Stats.FramesCaptured}");

        // At most one frame may have been in flight on the capture thread across the swap.
        Assert.True(sink.Count <= afterDetach + 1, $"kept delivering after detach: {afterDetach} -> {sink.Count}");
    }

    [Fact]
    public void Attaching_a_replacement_sink_retires_the_previous_one()
    {
        // The authenticated-peer-recreation case. After recovery installs a replacement
        // peer, the stale transport must stop receiving frames — two live sinks would be
        // two transports for one session.
        using var source = new FakeScreenSource();
        using var encoder = new FakeEncoder();
        using var pump = PumpWith(source, encoder, output);

        var stale = new RecordingSink();
        pump.AttachSink(stale);
        pump.Start();
        Assert.True(SpinUntil(() => stale.Count >= 2));

        var fresh = new RecordingSink();
        pump.AttachSink(fresh);
        var staleAtSwap = stale.Count;

        Assert.True(SpinUntil(() => fresh.Count >= 3));
        pump.Stop();

        output.WriteLine($"stale {staleAtSwap} -> {stale.Count}, fresh {fresh.Count}");

        Assert.True(stale.Count <= staleAtSwap + 1, $"stale sink kept receiving: {staleAtSwap} -> {stale.Count}");
        Assert.True(fresh.Count >= 3);
    }

    [Fact]
    public void A_sink_that_throws_does_not_kill_the_pump()
    {
        // The sink runs on the capture thread. On an unattended host, losing that thread
        // loses the only way back into the machine.
        using var source = new FakeScreenSource();
        using var encoder = new FakeEncoder();
        using var pump = PumpWith(source, encoder, output);

        var throwCount = 0;
        pump.AttachSink(new ThrowingSink(() => Interlocked.Increment(ref throwCount)));
        pump.Start();

        Assert.True(SpinUntil(() => Volatile.Read(ref throwCount) >= 3));

        // Still capturing and encoding despite every delivery throwing.
        var stats = pump.Stats;
        pump.Stop();

        output.WriteLine($"threw {throwCount} times; captured {stats.FramesCaptured}, encoded {stats.FramesEncoded}");
        Assert.True(stats.FramesEncoded >= 3);
    }

    private sealed class ThrowingSink(Action onCall) : IEncodedVideoSink
    {
        public void SendEncodedFrame(EncodedFrame frame, int fps)
        {
            onCall();
            throw new InvalidOperationException("transport exploded");
        }
    }

    [Fact]
    public void The_diagnostic_event_still_fires_alongside_the_sink()
    {
        // FrameEncoded remains the diagnostic tap. Wiring the transport must not have
        // silently taken it over.
        using var source = new FakeScreenSource();
        using var encoder = new FakeEncoder();
        using var pump = PumpWith(source, encoder, output);

        var sink = new RecordingSink();
        var observed = 0;
        pump.AttachSink(sink);
        pump.FrameEncoded += _ => Interlocked.Increment(ref observed);

        pump.Start();
        Assert.True(SpinUntil(() => sink.Count >= 3 && Volatile.Read(ref observed) >= 3));
        pump.Stop();

        output.WriteLine($"sink {sink.Count}, observers {observed}");
        Assert.True(observed >= 3);
    }

    [Fact]
    public async Task Frames_offered_before_the_link_connects_are_dropped_and_counted()
    {
        // Capture starts as soon as a controller is authorised, which is before ICE and
        // DTLS finish. Those frames must be dropped, attributably, and must never be
        // queued waiting for a link that may never come up.
        var (identity, peer, connection) = await HostPeerAsync(output);
        using var _ = identity;
        using var __ = peer;
        await using var ___ = connection;

        var sink = new PeerVideoSink(connection, output.WriteLine);
        Assert.Equal(LinkState.Connecting, connection.State);

        var frame = new EncodedFrame([1, 2, 3, 4], 1280, 720, IsKeyFrame: true, TimeSpan.FromMilliseconds(5));
        for (var i = 0; i < 10; i++) sink.SendEncodedFrame(frame, 30);

        output.WriteLine(sink.ToString());

        Assert.Equal(10, sink.FramesOffered);
        Assert.Equal(10, sink.FramesDroppedNotConnected);
        Assert.Equal(0, connection.GetTelemetry().VideoFramesSent);
    }

    [Fact]
    public async Task A_connected_link_stops_dropping_and_forwards_to_the_peer()
    {
        var (identity, peer, connection) = await HostPeerAsync(output);
        using var _ = identity;
        using var __ = peer;
        await using var ___ = connection;

        var sink = new PeerVideoSink(connection, output.WriteLine);
        var frame = new EncodedFrame([1, 2, 3, 4], 1280, 720, IsKeyFrame: true, TimeSpan.FromMilliseconds(5));

        sink.SendEncodedFrame(frame, 30);
        Assert.Equal(1, sink.FramesDroppedNotConnected);

        connection.HandleConnectionStateChange(RTCPeerConnectionState.connected);
        Assert.Equal(LinkState.Connected, connection.State);

        for (var i = 0; i < 5; i++) sink.SendEncodedFrame(frame, 30);

        output.WriteLine(sink.ToString());

        // Offered six, dropped only the one from before the link came up. The remaining
        // five were handed to the peer; whether they leave the NIC depends on DTLS, which
        // no in-process test can establish.
        Assert.Equal(6, sink.FramesOffered);
        Assert.Equal(1, sink.FramesDroppedNotConnected);
    }

    [Fact]
    public async Task A_sink_is_bound_to_one_peer_for_its_lifetime()
    {
        // Recovery replaces the peer, so it replaces the sink. Nothing may repoint a
        // live sink at a different transport.
        var (identity, peer, connection) = await HostPeerAsync(output);
        using var _ = identity;
        using var __ = peer;
        await using var ___ = connection;

        var sink = new PeerVideoSink(connection);

        Assert.Same(connection, sink.Peer);
        Assert.Equal(peer.DeviceId, sink.Peer.PeerId);
        Assert.Throws<ArgumentNullException>(() => new PeerVideoSink(null!));
    }

    [Fact]
    public async Task The_whole_path_runs_from_capture_to_a_real_peer_connection()
    {
        // The Priority 2 composition, end to end in-process: real VideoPipeline, real
        // TecheePeerConnection, adapted by PeerVideoSink. Only the GPU and libvpx are
        // fakes.
        var (identity, peer, connection) = await HostPeerAsync(output);
        using var _ = identity;
        using var __ = peer;
        await using var ___ = connection;

        using var source = new FakeScreenSource();
        using var encoder = new FakeEncoder();
        using var pump = PumpWith(source, encoder, output);

        var sink = new PeerVideoSink(connection, output.WriteLine);
        pump.AttachSink(sink);

        connection.HandleConnectionStateChange(RTCPeerConnectionState.connected);
        pump.Start();

        Assert.True(SpinUntil(() => sink.FramesOffered >= 5), $"only {sink.FramesOffered} frames offered");
        pump.Stop();

        var stats = pump.Stats;
        output.WriteLine($"{stats.FramesCaptured} captured, {stats.FramesEncoded} encoded, {sink}");

        Assert.True(stats.FramesEncoded >= 5);
        Assert.True(sink.FramesOffered >= 5);

        // Nothing was dropped for a disconnected link, because the link was up before
        // the pump started.
        Assert.Equal(0, sink.FramesDroppedNotConnected);
    }
}
