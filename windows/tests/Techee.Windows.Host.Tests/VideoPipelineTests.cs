using Techee.Windows.Host;
using Xunit;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// The frame pump: capture → convert → encode → out.
/// </summary>
/// <remarks>
/// Driven by a fake source and encoder, so pacing, adaptation, keyframes and failure
/// handling are ordinary assertions rather than things only observable on real hardware
/// under real load.
/// </remarks>
public class VideoPipelineTests
{
    /// <summary>Waits for a condition rather than sleeping a fixed time, so a fast machine is not penalised.</summary>
    private static bool WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    private static (VideoPipeline Pipeline, FakeScreenSource Source, FakeEncoder Encoder) Build(
        VideoProfile? start = null)
    {
        var source = new FakeScreenSource();
        var encoder = new FakeEncoder();
        var pipeline = new VideoPipeline(source, encoder, new AdaptiveQuality(start));
        return (pipeline, source, encoder);
    }

    // ---- the pump runs ----

    [Fact]
    public void It_captures_converts_encodes_and_emits()
    {
        var (pipeline, _, encoder) = Build();
        var frames = new List<EncodedFrame>();
        pipeline.FrameEncoded += f => { lock (frames) frames.Add(f); };

        pipeline.Start();
        try
        {
            Assert.True(WaitFor(() => { lock (frames) return frames.Count >= 3; }),
                "the pump should produce frames");
        }
        finally
        {
            pipeline.Stop();
        }

        lock (frames)
        {
            Assert.All(frames, f => Assert.Equal(1280, f.Width));
            Assert.All(frames, f => Assert.Equal(720, f.Height));
            Assert.All(frames, f => Assert.NotEmpty(f.Payload));
        }
        Assert.True(encoder.EncodeCount >= 3);
    }

    [Fact]
    public void The_first_frame_is_a_keyframe()
    {
        // A receiver joining mid-stream needs one to decode anything at all.
        var (pipeline, _, _) = Build();
        EncodedFrame? first = null;
        pipeline.FrameEncoded += f => first ??= f;

        pipeline.Start();
        try { WaitFor(() => first is not null); }
        finally { pipeline.Stop(); }

        Assert.NotNull(first);
        Assert.True(first!.IsKeyFrame);
    }

    [Fact]
    public void Start_is_idempotent_and_stop_is_safe_when_not_running()
    {
        var (pipeline, _, _) = Build();

        pipeline.Stop(); // never started
        pipeline.Start();
        pipeline.Start(); // twice
        Assert.True(pipeline.IsRunning);

        pipeline.Stop();
        pipeline.Stop();
        Assert.False(pipeline.IsRunning);
    }

    // ---- profile application ----

    [Fact]
    public void Starting_applies_the_profile_to_both_the_source_and_the_encoder()
    {
        var (pipeline, source, encoder) = Build();

        pipeline.Start();
        try { WaitFor(() => encoder.EncodeCount > 0); }
        finally { pipeline.Stop(); }

        Assert.Equal((1280, 720), source.OutputSize);
        Assert.Equal(VideoProfile.Hd.TargetKbps, encoder.TargetKbps);
    }

    [Fact]
    public void Forcing_a_profile_resizes_the_source_and_retargets_the_encoder()
    {
        var (pipeline, source, encoder) = Build();

        pipeline.Start();
        try
        {
            WaitFor(() => encoder.EncodeCount > 0);
            pipeline.ForceProfile(VideoProfile.Sd);

            Assert.True(WaitFor(() => source.OutputSize == (960, 540)));
            Assert.True(WaitFor(() => encoder.Sizes.Contains((960, 540))));
        }
        finally
        {
            pipeline.Stop();
        }

        Assert.Equal(VideoProfile.Sd.TargetKbps, encoder.TargetKbps);
    }

    [Fact]
    public void A_resolution_change_forces_a_keyframe()
    {
        // Mandatory. A receiver decoding deltas against a frame of the wrong size shows
        // garbage until the next natural keyframe, which on a static desktop can be a
        // very long time.
        var (pipeline, _, encoder) = Build();

        pipeline.Start();
        try
        {
            WaitFor(() => encoder.EncodeCount > 2);
            var before = encoder.KeyFrameRequests;

            pipeline.ForceProfile(VideoProfile.Sd);

            Assert.True(WaitFor(() => encoder.KeyFrameRequests > before),
                "changing resolution must request a keyframe");
        }
        finally
        {
            pipeline.Stop();
        }
    }

    [Fact]
    public void A_keyframe_can_be_requested_on_demand()
    {
        // What a controller reporting loss, or a second viewer joining, triggers.
        var (pipeline, _, encoder) = Build();

        pipeline.Start();
        try
        {
            WaitFor(() => encoder.EncodeCount > 0);
            var before = encoder.KeyFrameRequests;

            pipeline.RequestKeyFrame();

            Assert.True(WaitFor(() => encoder.KeyFrameRequests > before));
        }
        finally
        {
            pipeline.Stop();
        }
    }

    // ---- adaptation ----

    [Fact]
    public void Reported_packet_loss_steps_the_profile_down()
    {
        var (pipeline, source, _) = Build();

        pipeline.Start();
        try
        {
            WaitFor(() => pipeline.Stats.FramesEncoded > 0);
            Assert.Equal(VideoProfile.Hd, pipeline.Profile);

            pipeline.ReportNetwork(packetLossFraction: 0.20, roundTripMs: 400);

            Assert.Equal(VideoProfile.Sd, pipeline.Profile);
            Assert.True(WaitFor(() => source.OutputSize == (960, 540)),
                "the source must actually be resized, not merely the profile changed");
        }
        finally
        {
            pipeline.Stop();
        }
    }

    [Fact]
    public void A_slow_encoder_steps_the_profile_down()
    {
        // Encode pressure, not the network. The controller sees both halves.
        var source = new FakeScreenSource();
        var encoder = new FakeEncoder { EncodeDelay = TimeSpan.FromMilliseconds(40) };
        using var pipeline = new VideoPipeline(source, encoder);

        pipeline.Start();
        try
        {
            Assert.True(WaitFor(() => pipeline.Stats.AverageEncodeMs > 30, 5000),
                "the fake encoder should register as expensive");

            pipeline.ReportNetwork(packetLossFraction: 0.0, roundTripMs: 20);

            Assert.Equal(VideoProfile.Sd, pipeline.Profile);
        }
        finally
        {
            pipeline.Stop();
        }
    }

    [Fact]
    public void Timings_are_reset_when_the_profile_changes()
    {
        // Encode costs from the previous profile do not describe the new one, and
        // leaving them would have the controller adapt again on stale evidence.
        var source = new FakeScreenSource();
        var encoder = new FakeEncoder { EncodeDelay = TimeSpan.FromMilliseconds(40) };
        using var pipeline = new VideoPipeline(source, encoder);

        pipeline.Start();
        try
        {
            WaitFor(() => pipeline.Stats.AverageEncodeMs > 30, 5000);

            encoder.EncodeDelay = TimeSpan.Zero;
            pipeline.ForceProfile(VideoProfile.Sd);

            Assert.True(WaitFor(() => pipeline.Stats.AverageEncodeMs < 20, 5000),
                "post-change timings should reflect the new profile, not the old one");
        }
        finally
        {
            pipeline.Stop();
        }
    }

    // ---- robustness ----

    [Fact]
    public void A_capture_timeout_is_not_an_error()
    {
        // Desktop Duplication only delivers on change, so an idle desktop times out
        // continuously. Treating that as a fault would tear down healthy sessions.
        var (pipeline, source, _) = Build();
        source.SimulateTimeout = true;

        pipeline.Start();
        try
        {
            Assert.True(WaitFor(() => pipeline.Stats.CaptureTimeouts > 3));
            Assert.Equal(0, pipeline.Stats.FramesDropped);
            Assert.True(pipeline.IsRunning);
        }
        finally
        {
            pipeline.Stop();
        }
    }

    [Fact]
    public void A_capture_exception_does_not_kill_the_pump()
    {
        // On an unattended office host this thread is the only way anyone gets back in.
        var (pipeline, source, _) = Build();
        source.ThrowOnce = new InvalidOperationException("simulated device loss");

        pipeline.Start();
        try
        {
            Assert.True(WaitFor(() => pipeline.Stats.FramesEncoded > 0, 5000),
                "the pump should recover and keep producing frames");
            Assert.True(pipeline.IsRunning);
        }
        finally
        {
            pipeline.Stop();
        }
    }

    [Fact]
    public void An_encoder_returning_nothing_counts_as_a_drop_rather_than_a_crash()
    {
        var (pipeline, _, encoder) = Build();
        encoder.ReturnNull = true;

        pipeline.Start();
        try
        {
            Assert.True(WaitFor(() => pipeline.Stats.FramesDropped > 2));
            Assert.Equal(0, pipeline.Stats.FramesEncoded);
            Assert.True(pipeline.IsRunning);
        }
        finally
        {
            pipeline.Stop();
        }
    }

    [Fact]
    public void A_throwing_frame_handler_does_not_kill_the_pump()
    {
        // The handler is the session layer. A bug there must not take the host down.
        var (pipeline, _, _) = Build();
        var calls = 0;
        pipeline.FrameEncoded += _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("handler bug");
        };

        pipeline.Start();
        try
        {
            Assert.True(WaitFor(() => Volatile.Read(ref calls) > 3));
            Assert.True(pipeline.IsRunning);
        }
        finally
        {
            pipeline.Stop();
        }
    }

    [Fact]
    public void Row_padding_from_the_source_is_handled()
    {
        // GPU staging textures are routinely padded. Ignoring the pitch shears the
        // image diagonally — obvious in production, invisible without a padded fake.
        var source = new FakeScreenSource { ExtraStrideBytes = 128 };
        source.Resize(1280, 720);
        var encoder = new FakeEncoder();
        using var pipeline = new VideoPipeline(source, encoder);

        pipeline.Start();
        try
        {
            Assert.True(WaitFor(() => pipeline.Stats.FramesEncoded > 2));
            Assert.Equal(0, pipeline.Stats.FramesDropped);
        }
        finally
        {
            pipeline.Stop();
        }
    }

    // ---- telemetry ----

    [Fact]
    public void Stats_report_what_the_pipeline_actually_did()
    {
        var (pipeline, _, _) = Build();

        pipeline.Start();
        try { WaitFor(() => pipeline.Stats.FramesEncoded > 3); }
        finally { pipeline.Stop(); }

        var stats = pipeline.Stats;
        Assert.True(stats.FramesCaptured > 0);
        Assert.True(stats.FramesEncoded > 0);
        Assert.True(stats.BytesEncoded > 0);
        Assert.True(stats.AverageFrameBytes > 0);
        Assert.Equal(VideoProfile.Hd, stats.Profile);
        Assert.True(stats.EncodePressure >= 0);
    }

    [Fact]
    public void Protected_content_masking_is_surfaced()
    {
        // Windows blanks DRM-protected windows itself. Reporting it turns an
        // unexplained black rectangle into "protected content hidden".
        var (pipeline, source, _) = Build();
        source.ProtectedContentMasked = true;

        pipeline.Start();
        try { WaitFor(() => pipeline.Stats.FramesEncoded > 0); }
        finally { pipeline.Stop(); }

        Assert.True(pipeline.Stats.ProtectedContentMasked);
    }

    [Fact]
    public void Pacing_keeps_the_frame_rate_near_the_profile()
    {
        // The pump must not free-run: sending faster than the profile asks wastes
        // bandwidth the adaptive controller is trying to manage.
        var (pipeline, _, _) = Build(VideoProfile.Low); // 20 FPS

        pipeline.Start();
        try
        {
            Thread.Sleep(1500);
            var encoded = pipeline.Stats.FramesEncoded;

            // 1.5s at 20 FPS is ~30 frames. Allow generous slack for scheduling, but
            // catch a pump that ignores pacing entirely.
            Assert.InRange(encoded, 5, 60);
        }
        finally
        {
            pipeline.Stop();
        }
    }

    [Fact]
    public void Disposing_stops_the_pump_and_releases_the_source_and_encoder()
    {
        var source = new FakeScreenSource();
        var encoder = new FakeEncoder();
        var pipeline = new VideoPipeline(source, encoder);

        pipeline.Start();
        WaitFor(() => pipeline.Stats.FramesEncoded > 0);

        pipeline.Dispose();

        Assert.False(pipeline.IsRunning);
    }
}
