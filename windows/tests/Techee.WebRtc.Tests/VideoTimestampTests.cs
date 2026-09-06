using Techee.WebRtc;
using Xunit;
using Xunit.Abstractions;

namespace Techee.WebRtc.Tests;

/// <summary>
/// The video RTP clock.
/// </summary>
/// <remarks>
/// <para>
/// Techee does not maintain a timestamp. SIPSorcery's <c>SendVideo</c> documents its
/// argument as "the duration in RTP timestamp units of the video sample. This value is
/// added to the previous RTP timestamp when building the RTP header", so what Techee
/// supplies is an <i>increment</i> and the library accumulates it.
/// </para>
/// <para>
/// That is the property worth protecting: because no wall clock is read anywhere on this
/// path, an NTP correction, a DST change, or a resume from sleep cannot move a timestamp
/// backwards. A local counter derived from <c>DateTime.Now</c> — the obvious-looking
/// implementation — would break all three.
/// </para>
/// </remarks>
public class VideoTimestampTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(30, 3000)]  // 720p30 and 540p30
    [InlineData(20, 4500)]  // 1080p20 and the 360p floor
    [InlineData(15, 6000)]
    [InlineData(60, 1500)]
    public void The_increment_matches_the_90khz_video_clock(int fps, uint expected)
    {
        // 90 kHz is fixed by RFC 7742 for WebRTC video; the increment is one frame's
        // worth of that clock.
        Assert.Equal(expected, TecheePeerConnection.RtpDurationFor(fps));
        Assert.Equal(90_000, TecheePeerConnection.VideoClockRate);
    }

    [Fact]
    public void Every_profile_in_the_ladder_produces_a_usable_increment()
    {
        // The frame rates the pipeline actually paces to.
        foreach (var fps in new[] { 20, 30 })
        {
            var duration = TecheePeerConnection.RtpDurationFor(fps);
            output.WriteLine($"{fps} fps -> {duration} ticks");

            Assert.True(duration > 0);
            // One frame at N fps must advance the clock by exactly 1/N of a second.
            Assert.Equal((uint)(TecheePeerConnection.VideoClockRate / fps), duration);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_nonsensical_frame_rate_never_produces_a_zero_or_negative_increment(int fps)
    {
        // A zero increment would give consecutive frames identical timestamps, which a
        // decoder reads as one frame arriving in pieces rather than two frames.
        var duration = TecheePeerConnection.RtpDurationFor(fps);

        Assert.True(duration > 0);
        Assert.Equal(90_000u, duration); // clamped to 1 fps rather than dividing by zero
    }

    [Fact]
    public void An_absurdly_high_frame_rate_still_advances_the_clock()
    {
        // 90000 fps would divide to exactly 1; anything beyond it would truncate to 0
        // without the clamp, and duplicate timestamps break playback.
        Assert.Equal(1u, TecheePeerConnection.RtpDurationFor(90_000));
        Assert.Equal(1u, TecheePeerConnection.RtpDurationFor(200_000));
        Assert.Equal(1u, TecheePeerConnection.RtpDurationFor(int.MaxValue));
    }

    [Fact]
    public void Increments_stay_positive_across_a_profile_switch()
    {
        // Switching 720p30 -> 1080p20 -> 540p30 changes the increment but never its sign,
        // so the accumulated timestamp only ever moves forward. The frame rate changing
        // mid-stream is normal and is not a discontinuity.
        var sequence = new[] { 30, 20, 30, 20, 30 };

        uint clock = 0;
        var previous = 0u;

        foreach (var fps in sequence)
        {
            var duration = TecheePeerConnection.RtpDurationFor(fps);
            clock += duration;

            Assert.True(clock > previous, $"the clock did not advance at {fps} fps");
            previous = clock;
        }

        output.WriteLine($"clock after {sequence.Length} frames across three profiles: {clock}");
    }

    [Fact]
    public void A_long_session_accumulates_forward_and_wraps_rather_than_regressing()
    {
        // A 32-bit RTP timestamp at 90 kHz wraps about every 13 hours 15 minutes, so an
        // unattended host will cross it. Wrapping is correct and expected; what must not
        // happen is a genuine backwards step. This walks the counter across the boundary
        // the way an overnight session would.
        var duration = TecheePeerConnection.RtpDurationFor(30);

        // Start just short of the wrap.
        var clock = uint.MaxValue - (duration * 5);
        var wrapped = false;

        for (var frame = 0; frame < 20; frame++)
        {
            var next = unchecked(clock + duration);

            // Unsigned forward difference stays small through the wrap; only a real
            // regression exceeds half the range.
            var delta = unchecked(next - clock);
            Assert.True(delta <= uint.MaxValue / 2, "the step was not forward progress");
            Assert.Equal(duration, delta);

            if (next < clock) wrapped = true;
            clock = next;
        }

        Assert.True(wrapped, "the test did not actually cross the wrap boundary");

        output.WriteLine($"crossed the 32-bit wrap; final clock {clock}");
    }

    [Fact]
    public void Seconds_of_video_map_to_the_expected_number_of_ticks()
    {
        // A sanity check on the units themselves: one second of 30 fps video must advance
        // the clock by exactly the clock rate. Getting this wrong by a factor of the
        // frame rate is the classic 90 kHz mistake, and it shows up as playback running
        // at the wrong speed rather than as an error.
        var duration = TecheePeerConnection.RtpDurationFor(30);
        var oneSecond = duration * 30;

        Assert.Equal((uint)TecheePeerConnection.VideoClockRate, oneSecond);
    }
}
