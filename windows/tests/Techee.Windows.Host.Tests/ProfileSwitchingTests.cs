using Techee.Windows.Host;
using Xunit.Abstractions;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// Profile switching as the transport sees it.
/// </summary>
/// <remarks>
/// <see cref="VideoPipelineTests"/> proves the pipeline reconfigures the source and the
/// encoder. This proves the consequence that actually matters to a viewer: frames
/// arriving at the sink change size cleanly and a keyframe accompanies the change. A
/// receiver decoding deltas against a frame of the wrong size shows garbage until the
/// next natural keyframe, which on a static desktop can be a very long time.
/// </remarks>
public class ProfileSwitchingTests(ITestOutputHelper output)
{
    private sealed record Seen(int Width, int Height, bool IsKeyFrame, int Fps);

    private sealed class SizeRecordingSink : IEncodedVideoSink
    {
        private readonly List<Seen> _frames = [];

        public IReadOnlyList<Seen> Frames
        {
            get { lock (_frames) return _frames.ToList(); }
        }

        public void SendEncodedFrame(EncodedFrame frame, int fps)
        {
            lock (_frames) _frames.Add(new Seen(frame.Width, frame.Height, frame.IsKeyFrame, fps));
        }
    }

    private static bool SpinUntil(Func<bool> condition, int seconds = 10) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void The_transport_sees_each_profile_at_its_own_size_and_frame_rate()
    {
        using var source = new FakeScreenSource();
        using var encoder = new FakeEncoder();
        using var pipeline = new VideoPipeline(
            source, encoder, new AdaptiveQuality(VideoProfile.Hd), m => output.WriteLine($"video {m}"));

        var sink = new SizeRecordingSink();
        pipeline.AttachSink(sink);
        pipeline.Start();

        // The three profiles W3 requires, in the order an operator would walk them.
        var requested = new[] { VideoProfile.Hd, VideoProfile.FullHd, VideoProfile.Sd };

        foreach (var profile in requested)
        {
            pipeline.ForceProfile(profile);

            // Two frames, not one: the first proves the switch landed, the second proves
            // the pipeline kept running at the new size rather than emitting once and
            // stalling.
            Assert.True(
                SpinUntil(() => sink.Frames.Count(f => f.Width == profile.Width) >= 2),
                $"the transport did not settle at {profile.Name} ({profile.Width}x{profile.Height})");

            var atProfile = sink.Frames.Where(f => f.Width == profile.Width).ToList();

            // The transport is told the new pacing rate, not the previous one, and never
            // a half-applied width/height pairing.
            Assert.All(atProfile, f => Assert.Equal(profile.Fps, f.Fps));
            Assert.All(atProfile, f => Assert.Equal(profile.Height, f.Height));

            output.WriteLine($"{profile.Name}: {atProfile.Count} frames, " +
                             $"{atProfile.Count(f => f.IsKeyFrame)} keyframes");
        }

        pipeline.Stop();

        var frames = sink.Frames;
        var sizes = frames.Select(f => (f.Width, f.Height)).Distinct().ToList();
        var validSizes = VideoProfile.Ladder.Select(p => (p.Width, p.Height)).ToHashSet();

        // Stated as two independent properties rather than one count, so a failure says
        // which one broke: every requested profile arrived, and nothing else did.
        foreach (var profile in requested)
        {
            Assert.Contains((profile.Width, profile.Height), sizes);
        }

        Assert.All(sizes, size => Assert.Contains(size, validSizes));

        // Each size is introduced by a keyframe, so a receiver is never left decoding
        // deltas against a frame of the wrong dimensions.
        foreach (var (width, height) in sizes)
        {
            var first = frames.First(f => f.Width == width && f.Height == height);
            Assert.True(first.IsKeyFrame,
                $"the first frame at {width}x{height} was a delta, not a keyframe");
        }
    }

    [Fact]
    public void Switching_repeatedly_never_emits_a_mismatched_frame()
    {
        // A display mode change can race a resize, so the pump skips frames whose size
        // does not match the profile it is applying. Cycling hard is how that race is
        // provoked.
        using var source = new FakeScreenSource();
        using var encoder = new FakeEncoder();
        using var pipeline = new VideoPipeline(
            source, encoder, new AdaptiveQuality(VideoProfile.Hd), m => output.WriteLine($"video {m}"));

        var sink = new SizeRecordingSink();
        pipeline.AttachSink(sink);
        pipeline.Start();

        var ladder = new[] { VideoProfile.Hd, VideoProfile.Sd, VideoProfile.FullHd, VideoProfile.Low };
        for (var i = 0; i < 12; i++)
        {
            pipeline.ForceProfile(ladder[i % ladder.Length]);
            Thread.Sleep(15);
        }

        pipeline.Stop();

        var valid = ladder.Select(p => (p.Width, p.Height)).ToHashSet();
        var delivered = sink.Frames.Select(f => (f.Width, f.Height)).Distinct().ToList();

        output.WriteLine($"{sink.Frames.Count} frames across {delivered.Count} distinct sizes");

        // Every frame that reached the transport belongs to some real profile — no
        // half-applied width/height pairing ever escaped.
        Assert.All(delivered, size => Assert.Contains(size, valid));
        Assert.False(pipeline.IsWedged);
    }

    [Fact]
    public void A_profile_switch_does_not_restart_capture()
    {
        // Switching is a reconfiguration, not a teardown. Rebuilding the duplication
        // session on every step would make adaptation more expensive than the load it is
        // reacting to.
        using var source = new FakeScreenSource();
        using var encoder = new FakeEncoder();
        using var pipeline = new VideoPipeline(
            source, encoder, new AdaptiveQuality(VideoProfile.Hd), m => output.WriteLine($"video {m}"));

        pipeline.AttachSink(new SizeRecordingSink());
        pipeline.Start();
        Assert.True(SpinUntil(() => pipeline.Stats.FramesEncoded > 3));

        pipeline.ForceProfile(VideoProfile.Sd);
        pipeline.ForceProfile(VideoProfile.FullHd);

        Assert.True(SpinUntil(() => pipeline.Stats.Profile.Name == VideoProfile.FullHd.Name));

        Assert.Equal(1, pipeline.PumpGenerations);
        Assert.True(pipeline.IsRunning);

        pipeline.Stop();
    }
}
