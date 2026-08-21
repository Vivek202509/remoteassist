using Techee.Windows.Host;
using Xunit;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// Adaptive quality behaviour.
/// </summary>
/// <remarks>
/// Every rule here would otherwise only be observable on a genuinely bad network,
/// which is not a reproducible test environment. Keeping the controller as pure logic
/// makes "does it recover, and does it oscillate?" an ordinary assertion.
/// </remarks>
public class VideoQualityTests
{
    private static QualitySample Good(VideoProfile p) =>
        new(EncodeMsPerFrame: p.FrameBudgetMs * 0.3, AchievedFps: p.Fps, PacketLossFraction: 0.0, RoundTripMs: 20);

    private static QualitySample CpuBound(VideoProfile p) =>
        new(EncodeMsPerFrame: p.FrameBudgetMs * 0.95, AchievedFps: p.Fps * 0.6, PacketLossFraction: 0.0, RoundTripMs: 20);

    private static QualitySample Lossy(VideoProfile p) =>
        new(EncodeMsPerFrame: p.FrameBudgetMs * 0.3, AchievedFps: p.Fps, PacketLossFraction: 0.12, RoundTripMs: 300);

    // ---- the ladder reflects the measurements ----

    // Measured total per-frame cost on the reference machine: convert + encode.
    // 1080p 18.4 + 31.0 = 49.3 ms | 720p 8.6 + 21.9 = 30.5 ms | 540p 4.1 + 12.2 = 16.2 ms.
    private const double FullHdMs = 49.3;
    private const double HdMs = 30.5;
    private const double SdMs = 16.2;

    [Fact]
    public void The_default_profile_is_the_one_the_hardware_can_actually_sustain()
    {
        // 1080p costs 49.3 ms/frame, well over the 33.3 ms a 30 FPS budget allows.
        // 720p costs 30.5 ms and fits. The default must be the achievable one.
        var q = new AdaptiveQuality();

        Assert.Equal(VideoProfile.Hd, q.Current);
        Assert.Equal(1280, q.Current.Width);
        Assert.Equal(720, q.Current.Height);
        Assert.Equal(30, q.Current.Fps);

        Assert.True(HdMs < VideoProfile.Hd.FrameBudgetMs,
            $"720p30 must fit its budget: {HdMs} ms against {VideoProfile.Hd.FrameBudgetMs:0.0} ms");
    }

    [Fact]
    public void The_default_is_not_full_hd_because_this_machine_cannot_hold_it()
    {
        // The specific mistake this guards against: defaulting to 1080p30, where measured
        // local processing alone already exceeds the frame budget before a single packet
        // is sent.
        Assert.NotEqual(VideoProfile.FullHd, new AdaptiveQuality().Current);
        Assert.True(FullHdMs > 1000.0 / 30,
            "1080p30 would be over budget on local processing alone");
    }

    [Fact]
    public void Full_hd_is_offered_at_an_honest_frame_rate()
    {
        // Offering 1080p30 would be a claim the measurements do not support. It is
        // published at 20 FPS instead, which the pipeline can actually deliver.
        Assert.Equal(20, VideoProfile.FullHd.Fps);
        Assert.True(VideoProfile.FullHd.FrameBudgetMs >= FullHdMs,
            $"the 1080p budget must accommodate the measured {FullHdMs} ms/frame total cost");
    }

    [Fact]
    public void The_fallback_below_the_default_is_960x540()
    {
        // The documented step down when 720p comes under pressure.
        var q = new AdaptiveQuality();
        var stepped = q.Observe(CpuBound(q.Current));

        Assert.Equal(960, stepped.Width);
        Assert.Equal(540, stepped.Height);
        Assert.Equal(30, stepped.Fps);
        Assert.True(SdMs < stepped.FrameBudgetMs * 0.6,
            "540p should be comfortably inside budget, not another marginal step");
    }

    [Fact]
    public void Every_profile_in_the_ladder_is_reachable_and_distinct()
    {
        // 1080p / 720p / 540p are the three the milestone requires; 360p is the floor.
        var byName = VideoProfile.Ladder.ToDictionary(p => p.Name);

        Assert.Contains("1080p20", byName);
        Assert.Contains("720p30", byName);
        Assert.Contains("540p30", byName);
        Assert.Equal(VideoProfile.Ladder.Count, byName.Count);
    }

    // ---- pressure is measured on the whole pipeline, not just the encoder ----

    [Fact]
    public void Conversion_cost_counts_against_the_frame_budget()
    {
        // The real 720p case on this machine: the encode alone looks like 66% of budget
        // and would read as "hold", but conversion takes it to 92%, which is over the
        // step-down threshold. Judging on the encode alone would leave the controller
        // believing in headroom the frame rate was already spending.
        var encodeOnly = new QualitySample(
            EncodeMsPerFrame: 21.9, AchievedFps: 30, PacketLossFraction: 0, RoundTripMs: 20);

        var withConversion = encodeOnly with { ConvertMsPerFrame = 8.6 };

        Assert.Equal(21.9, encodeOnly.ProcessingMsPerFrame, 3);
        Assert.Equal(30.5, withConversion.ProcessingMsPerFrame, 3);

        var budget = VideoProfile.Hd.FrameBudgetMs;
        Assert.True(encodeOnly.ProcessingMsPerFrame / budget < AdaptiveQuality.EncodeOverBudget);
        Assert.True(withConversion.ProcessingMsPerFrame / budget > AdaptiveQuality.EncodeOverBudget);
    }

    [Fact]
    public void A_machine_that_is_over_budget_only_once_conversion_is_counted_steps_down()
    {
        var q = new AdaptiveQuality();

        var next = q.Observe(new QualitySample(
            EncodeMsPerFrame: 21.9,
            AchievedFps: 30,
            PacketLossFraction: 0,
            RoundTripMs: 20,
            ConvertMsPerFrame: 8.6));

        Assert.Equal(VideoProfile.Sd, next);
    }

    [Fact]
    public void The_ladder_descends_monotonically()
    {
        var ladder = VideoProfile.Ladder;
        for (var i = 1; i < ladder.Count; i++)
        {
            Assert.True(ladder[i].Width * ladder[i].Height < ladder[i - 1].Width * ladder[i - 1].Height,
                $"{ladder[i].Name} should be smaller than {ladder[i - 1].Name}");
            Assert.True(ladder[i].TargetKbps < ladder[i - 1].TargetKbps);
        }
    }

    // ---- stepping down ----

    [Fact]
    public void It_steps_down_immediately_when_the_cpu_cannot_keep_up()
    {
        var q = new AdaptiveQuality();
        var before = q.Current;

        var after = q.Observe(CpuBound(before));

        Assert.Equal(VideoProfile.Sd, after);
        Assert.Equal(1, q.Adaptations);
    }

    [Fact]
    public void It_steps_down_on_packet_loss_even_when_the_cpu_is_idle()
    {
        // Sending more bits into a lossy link makes it worse, regardless of how much
        // CPU headroom there is.
        var q = new AdaptiveQuality();

        Assert.Equal(VideoProfile.Sd, q.Observe(Lossy(q.Current)));
    }

    [Fact]
    public void It_keeps_stepping_down_but_stops_at_the_floor()
    {
        // The floor matters: over a poor relay, a degraded picture is always better
        // than dropping the session.
        var q = new AdaptiveQuality(VideoProfile.FullHd);

        for (var i = 0; i < 10; i++) q.Observe(CpuBound(q.Current));

        Assert.Equal(VideoProfile.Low, q.Current);
    }

    [Fact]
    public void At_the_floor_it_holds_rather_than_disconnecting()
    {
        var q = new AdaptiveQuality(VideoProfile.Low);
        var adaptationsBefore = q.Adaptations;

        Assert.Equal(VideoProfile.Low, q.Observe(CpuBound(VideoProfile.Low)));
        Assert.Equal(adaptationsBefore, q.Adaptations);
    }

    // ---- climbing back ----

    [Fact]
    public void It_does_not_climb_on_a_single_good_window()
    {
        // Asymmetric on purpose: oscillating between profiles is more visible to an
        // operator than sitting one step low.
        var q = new AdaptiveQuality(VideoProfile.Sd);

        q.Observe(Good(VideoProfile.Sd));
        Assert.Equal(VideoProfile.Sd, q.Current);
    }

    [Fact]
    public void It_climbs_after_sustained_good_windows()
    {
        var q = new AdaptiveQuality(VideoProfile.Sd);

        for (var i = 0; i < AdaptiveQuality.UpshiftWindows; i++) q.Observe(Good(VideoProfile.Sd));

        Assert.Equal(VideoProfile.Hd, q.Current);
    }

    [Fact]
    public void One_bad_window_resets_the_credit_towards_climbing()
    {
        var q = new AdaptiveQuality(VideoProfile.Sd);

        for (var i = 0; i < AdaptiveQuality.UpshiftWindows - 1; i++) q.Observe(Good(VideoProfile.Sd));
        q.Observe(Lossy(VideoProfile.Sd)); // steps down to Low and clears credit

        Assert.Equal(VideoProfile.Low, q.Current);

        // One good window must not immediately climb back.
        q.Observe(Good(VideoProfile.Low));
        Assert.Equal(VideoProfile.Low, q.Current);
    }

    [Fact]
    public void Marginal_conditions_hold_the_profile_rather_than_oscillating()
    {
        // Between the down-shift and up-shift thresholds: neither struggling nor
        // clearly comfortable. Holding is the whole point of the dead band.
        var q = new AdaptiveQuality();
        var marginal = new QualitySample(
            EncodeMsPerFrame: q.Current.FrameBudgetMs * 0.7,
            AchievedFps: q.Current.Fps * 0.9,
            PacketLossFraction: 0.02,
            RoundTripMs: 80);

        for (var i = 0; i < 20; i++) q.Observe(marginal);

        Assert.Equal(VideoProfile.Hd, q.Current);
        Assert.Equal(0, q.Adaptations);
    }

    [Fact]
    public void Automatic_adaptation_stops_at_the_ceiling_rather_than_the_top_of_the_ladder()
    {
        // 1080p cannot hold 30 FPS on the reference hardware, so climbing into it
        // automatically would silently trade the operator's frame rate for resolution
        // they never asked for. It is reachable only by explicit choice.
        var q = new AdaptiveQuality(VideoProfile.Low);

        for (var i = 0; i < 40; i++) q.Observe(Good(q.Current));

        Assert.Equal(AdaptiveQuality.AutoCeiling, q.Current);
        Assert.Equal(VideoProfile.Hd, q.Current);
    }

    [Fact]
    public void A_pinned_full_hd_is_not_climbed_away_from_while_it_is_working()
    {
        // Forcing sets the profile; good windows must not then "climb" anywhere,
        // because there is nowhere above it.
        var q = new AdaptiveQuality();
        q.Force(VideoProfile.FullHd);

        for (var i = 0; i < 20; i++) q.Observe(Good(VideoProfile.FullHd));

        Assert.Equal(VideoProfile.FullHd, q.Current);
    }

    [Fact]
    public void A_full_degrade_and_recover_cycle_returns_to_the_start()
    {
        // The realistic scenario: a laptop goes to a bad hotspot and comes back.
        var q = new AdaptiveQuality();

        q.Observe(Lossy(q.Current));
        q.Observe(Lossy(q.Current));
        Assert.Equal(VideoProfile.Low, q.Current);

        for (var i = 0; i < AdaptiveQuality.UpshiftWindows * 3; i++) q.Observe(Good(q.Current));

        // Back to the default, and no further: 1080p stays operator-only.
        Assert.Equal(VideoProfile.Hd, q.Current);
    }

    // ---- explicit override ----

    [Fact]
    public void An_operator_can_pin_a_profile()
    {
        var q = new AdaptiveQuality();

        q.Force(VideoProfile.FullHd);

        Assert.Equal(VideoProfile.FullHd, q.Current);
    }

    [Fact]
    public void A_pinned_profile_still_degrades_under_real_pressure()
    {
        // Pinning is a preference, not a guarantee. A machine that cannot sustain
        // 1080p must still fall back rather than deliver a slideshow.
        var q = new AdaptiveQuality();
        q.Force(VideoProfile.FullHd);

        Assert.Equal(VideoProfile.Hd, q.Observe(CpuBound(VideoProfile.FullHd)));
    }
}
