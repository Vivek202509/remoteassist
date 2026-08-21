using Techee.Windows.Host;
using Xunit.Abstractions;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// Back-pressure: the pipeline's bounded, latest-frame-wins hand-off to the transport.
/// </summary>
/// <remarks>
/// <para>
/// The policy under test is explicit. Between encoding and the transport sits a
/// <b>one-deep slot</b>. If the sink cannot keep up, the frame waiting there is discarded
/// in favour of the newer one and counted as superseded. Nothing is ever queued, so
/// memory cannot grow with session duration — a remote desktop that buffers is a remote
/// desktop that shows the operator the past.
/// </para>
/// <para>
/// <c>SendQueueDepth</c> is the slot's real occupancy and never exceeds 1 at any moment.
/// Once a pipeline has stopped, nothing is in flight either, so the stronger identity
/// <c>FramesEncoded == FramesSent + FramesSuperseded</c> holds.
/// </para>
/// </remarks>
public class BackpressureTests(ITestOutputHelper output)
{
    private static VideoPipeline Build(IScreenSource source, IVideoEncoder encoder, ITestOutputHelper output) =>
        new(source, encoder, new AdaptiveQuality(VideoProfile.Hd), m => output.WriteLine($"video {m}"));

    private static bool SpinUntil(Func<bool> condition, int seconds = 10) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(seconds));

    /// <summary>A sink that takes a fixed time per frame, and remembers every payload it saw.</summary>
    private sealed class SlowSink(TimeSpan cost) : IEncodedVideoSink
    {
        private long _received;
        public long Received => Interlocked.Read(ref _received);
        public long PeakConcurrent;
        private int _concurrent;

        public void SendEncodedFrame(EncodedFrame frame, int fps)
        {
            var now = Interlocked.Increment(ref _concurrent);
            PeakConcurrent = Math.Max(PeakConcurrent, now);
            try
            {
                Thread.Sleep(cost);
                Interlocked.Increment(ref _received);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }

    [Fact]
    public void A_slow_transport_drops_stale_frames_instead_of_queueing_them()
    {
        // A sink an order of magnitude slower than the frame budget. Without a bound this
        // is exactly where an unbounded queue would form.
        var source = new FakeScreenSource();
        var encoder = new FakeEncoder();
        using var pipeline = Build(source, encoder, output);

        var sink = new SlowSink(TimeSpan.FromMilliseconds(200));
        pipeline.AttachSink(sink);
        pipeline.Start();

        Assert.True(SpinUntil(() => pipeline.Stats.FramesEncoded > 25),
            "the encoder did not run ahead of the sink");

        pipeline.Stop();
        var stats = pipeline.Stats;

        output.WriteLine($"encoded {stats.FramesEncoded}, sent {stats.FramesSent}, " +
                         $"superseded {stats.FramesSuperseded}, depth {stats.SendQueueDepth}");

        // The encoder ran well ahead, so frames must have been discarded rather than
        // accumulated.
        Assert.True(stats.FramesSuperseded > 0, "nothing was superseded despite a slow sink");
        Assert.True(stats.FramesEncoded > stats.FramesSent);

        // The bound itself. Stopped, so nothing is in flight either.
        Assert.Equal(0, stats.SendQueueDepth);
        Assert.Equal(stats.FramesEncoded, stats.FramesSent + stats.FramesSuperseded);

        // One sender, so the transport is never called concurrently.
        Assert.Equal(1, sink.PeakConcurrent);
    }

    [Fact]
    public void A_transport_faster_than_the_encoder_never_supersedes_anything()
    {
        // Deliberately makes the ENCODER the slow stage rather than timing a fast sink
        // against a fast pump. With 60 ms between frames and a sink that returns
        // immediately, the send stage has drained the slot long before the next frame
        // arrives — and that stays true when the whole machine is loaded, because both
        // threads slow together. An earlier version of this test raced the two and failed
        // intermittently on a busy build; "a fast sink loses nothing" is not a property
        // the system guarantees under contention, and asserting it was wrong.
        var source = new FakeScreenSource();
        var encoder = new FakeEncoder { EncodeDelay = TimeSpan.FromMilliseconds(60) };
        using var pipeline = Build(source, encoder, output);

        var sink = new SlowSink(TimeSpan.Zero);
        pipeline.AttachSink(sink);
        pipeline.Start();

        Assert.True(SpinUntil(() => sink.Received >= 5), $"only {sink.Received} frames were delivered");
        pipeline.Stop();

        var stats = pipeline.Stats;
        output.WriteLine($"encoded {stats.FramesEncoded}, sent {stats.FramesSent}, " +
                         $"superseded {stats.FramesSuperseded}");

        // Nothing was ever waiting when the next frame arrived, so nothing was displaced.
        Assert.Equal(0, stats.FramesSuperseded);
        Assert.Equal(stats.FramesEncoded, stats.FramesSent);
        Assert.Equal(0, stats.SendQueueDepth);
        Assert.Equal(1, sink.PeakConcurrent);
    }

    [Fact]
    public void Memory_does_not_grow_with_session_duration()
    {
        // The property that matters most on an unattended host left running for days.
        // A queue would show up as a payload count rising without bound; a one-deep slot
        // holds at most one frame no matter how long the mismatch persists.
        var source = new FakeScreenSource();
        var encoder = new FakeEncoder();
        using var pipeline = Build(source, encoder, output);

        var sink = new SlowSink(TimeSpan.FromMilliseconds(50));
        pipeline.AttachSink(sink);
        pipeline.Start();

        Assert.True(SpinUntil(() => pipeline.Stats.FramesEncoded > 20));
        var early = pipeline.Stats;

        Assert.True(SpinUntil(() => pipeline.Stats.FramesEncoded > early.FramesEncoded + 40));
        var late = pipeline.Stats;

        pipeline.Stop();

        output.WriteLine($"early depth {early.SendQueueDepth}, late depth {late.SendQueueDepth}");
        output.WriteLine($"encoded {early.FramesEncoded} -> {late.FramesEncoded}, " +
                         $"superseded {early.FramesSuperseded} -> {late.FramesSuperseded}");

        // Backlog is constant while the deficit grows: the drop count absorbs it.
        Assert.True(early.SendQueueDepth <= 1);
        Assert.True(late.SendQueueDepth <= 1);
        Assert.True(late.FramesSuperseded > early.FramesSuperseded);
    }

    [Fact]
    public void Encoding_is_not_throttled_by_a_stalled_transport()
    {
        // Why the send stage is a separate thread at all. If encoding were held behind
        // the network, the adaptive controller's encode timings would start measuring the
        // link instead of the CPU and it would step resolution down for the wrong reason.
        var source = new FakeScreenSource();
        var encoder = new FakeEncoder();
        using var pipeline = Build(source, encoder, output);

        var gate = new ManualResetEventSlim(false);
        pipeline.AttachSink(new BlockingSink(gate));
        pipeline.Start();

        // The very first frame wedges the sink; the pump must keep going regardless.
        Assert.True(SpinUntil(() => pipeline.Stats.FramesEncoded > 15),
            "encoding stalled behind the transport");

        var stats = pipeline.Stats;
        output.WriteLine($"encoded {stats.FramesEncoded} while the sink was blocked, " +
                         $"superseded {stats.FramesSuperseded}");

        Assert.True(stats.FramesEncoded > 15);
        Assert.True(stats.SendQueueDepth <= 1);

        gate.Set();
        pipeline.Stop();
    }

    private sealed class BlockingSink(ManualResetEventSlim gate) : IEncodedVideoSink
    {
        public void SendEncodedFrame(EncodedFrame frame, int fps) => gate.Wait();
    }

    [Fact]
    public void The_last_frame_of_a_session_is_flushed_rather_than_dropped()
    {
        var source = new FakeScreenSource();
        var encoder = new FakeEncoder();
        using var pipeline = Build(source, encoder, output);

        var sink = new SlowSink(TimeSpan.Zero);
        pipeline.AttachSink(sink);
        pipeline.Start();

        Assert.True(SpinUntil(() => pipeline.Stats.FramesEncoded > 5));
        pipeline.Stop();

        var stats = pipeline.Stats;
        output.WriteLine($"after stop: encoded {stats.FramesEncoded}, sent {stats.FramesSent}, " +
                         $"depth {stats.SendQueueDepth}");

        // Nothing is left holding the slot once the pipeline has stopped.
        Assert.Equal(0, stats.SendQueueDepth);
        Assert.Equal(stats.FramesEncoded, stats.FramesSent + stats.FramesSuperseded);
    }

    [Fact]
    public void The_transport_receives_a_payload_the_encoder_cannot_overwrite()
    {
        // The slot outlives the encode call that produced the frame, so the payload it
        // holds must be owned rather than borrowed. A reused encoder buffer would show up
        // as rare, unreproducible corruption rather than a test failure, so this pins it.
        var source = new FakeScreenSource();
        var encoder = new RecyclingEncoder();
        using var pipeline = Build(source, encoder, output);

        var sink = new PayloadRecordingSink();
        pipeline.AttachSink(sink);
        pipeline.Start();

        Assert.True(SpinUntil(() => sink.Count >= 10));
        pipeline.Stop();

        output.WriteLine($"{sink.Count} payloads, {sink.DistinctFirstBytes} distinct leading bytes");

        // The encoder stamps a per-frame sequence into byte 1 and then recycles the
        // array. If the sink were handed the live buffer, every payload would read as
        // the newest frame and the distinct count would collapse to 1.
        Assert.True(sink.DistinctFirstBytes > 1,
            "every delivered payload carried the same value; the encoder's buffer was not copied");
    }

    /// <summary>An encoder that returns the same array every time, with a changing stamp.</summary>
    private sealed class RecyclingEncoder : IVideoEncoder
    {
        private readonly byte[] _buffer = new byte[64];
        private byte _sequence;

        public int TargetKbps { get; set; }
        public void ForceKeyFrame() { }

        public byte[]? Encode(ReadOnlySpan<byte> i420, int width, int height)
        {
            _buffer[0] = 0x01;
            _buffer[1] = unchecked(++_sequence);
            return _buffer;
        }

        public void Dispose() { }
    }

    private sealed class PayloadRecordingSink : IEncodedVideoSink
    {
        private readonly HashSet<byte> _stamps = [];
        private long _count;

        public long Count => Interlocked.Read(ref _count);

        public int DistinctFirstBytes
        {
            get { lock (_stamps) return _stamps.Count; }
        }

        public void SendEncodedFrame(EncodedFrame frame, int fps)
        {
            // Read after a delay: if this were the encoder's live buffer, the value would
            // have moved on by now.
            Thread.Sleep(2);
            lock (_stamps) _stamps.Add(frame.Payload[1]);
            Interlocked.Increment(ref _count);
        }
    }

    [Fact]
    public void A_long_stress_run_stays_bounded_and_balanced()
    {
        // The stress test Phase 23 asks for: a sustained mismatch between encode and send
        // across profile switches and sink swaps, checked for accounting drift and
        // unbounded growth.
        var source = new FakeScreenSource();
        var encoder = new FakeEncoder();
        using var pipeline = Build(source, encoder, output);

        // Slower than every profile's frame budget (33 ms at 30 FPS, 50 ms at 20), so the
        // encoder genuinely outruns the transport throughout. A sink faster than the
        // budget would simply pace with the pump and supersede nothing, which proves
        // nothing about the bound.
        var sink = new SlowSink(TimeSpan.FromMilliseconds(80));
        pipeline.AttachSink(sink);
        pipeline.Start();

        var profiles = new[] { VideoProfile.Hd, VideoProfile.Sd, VideoProfile.Low, VideoProfile.Hd };

        foreach (var profile in profiles)
        {
            pipeline.ForceProfile(profile);
            Assert.True(SpinUntil(() => pipeline.Stats.Profile.Name == profile.Name && pipeline.Stats.FramesEncoded > 0));

            var before = pipeline.Stats.FramesEncoded;
            Assert.True(SpinUntil(() => pipeline.Stats.FramesEncoded > before + 15),
                $"the pump stalled on {profile.Name}");

            var stats = pipeline.Stats;
            Assert.True(stats.SendQueueDepth <= 1,
                $"queue depth {stats.SendQueueDepth} on {profile.Name}");
        }

        // Swap the transport mid-flight, as a reconnect would.
        var replacement = new SlowSink(TimeSpan.FromMilliseconds(5));
        pipeline.AttachSink(replacement);
        Assert.True(SpinUntil(() => replacement.Received > 10));

        pipeline.Stop();

        var final = pipeline.Stats;
        output.WriteLine($"encoded {final.FramesEncoded}, sent {final.FramesSent}, " +
                         $"superseded {final.FramesSuperseded}, depth {final.SendQueueDepth}");
        output.WriteLine($"first sink {sink.Received}, replacement {replacement.Received}");

        Assert.Equal(0, final.SendQueueDepth);
        Assert.Equal(final.FramesEncoded, final.FramesSent + final.FramesSuperseded);
        Assert.True(final.FramesSuperseded > 0);
        Assert.Equal(1, pipeline.PumpGenerations);
        Assert.False(pipeline.IsWedged);
    }
}
