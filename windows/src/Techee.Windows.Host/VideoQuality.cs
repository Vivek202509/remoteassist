namespace Techee.Windows.Host;

/// <summary>
/// A capture/encode operating point.
/// </summary>
/// <remarks>
/// <para>
/// The ladder is derived from measurements on real hardware rather than chosen for
/// tidiness — see <c>docs/WINDOWS_VIDEO_PIPELINE.md</c>. Measured total per-frame cost
/// (BGRA→I420 conversion plus software VP8 encode) on the reference machine:
/// </para>
/// <list type="table">
///   <item><term>1080p</term><description>convert 18.4 ms + encode 31.0 ms = <b>49.3 ms</b></description></item>
///   <item><term>720p</term><description>convert 8.6 ms + encode 21.9 ms = <b>30.5 ms</b></description></item>
///   <item><term>540p</term><description>convert 4.1 ms + encode 12.2 ms = <b>16.2 ms</b></description></item>
/// </list>
/// <para>
/// 1080p cannot sustain 30 FPS (49.3 ms against a 33.3 ms budget), so it is available
/// only as an explicit "detail over smoothness" choice with an honest lower frame-rate
/// cap. Conversion is counted because it competes for the same frame budget the encode
/// does — see <see cref="QualitySample.ProcessingMsPerFrame"/>.
/// </para>
/// </remarks>
public sealed record VideoProfile(string Name, int Width, int Height, int Fps, int TargetKbps)
{
    /// <summary>
    /// 1920×1080 at 20 FPS. Detail over smoothness; measured 49.3 ms/frame.
    /// </summary>
    /// <remarks>
    /// The 50 ms budget at 20 FPS clears the measured cost by ~1%. That is not comfort,
    /// it is a ceiling: this profile is operator-pinned only, and the controller will
    /// still step down from it under real pressure.
    /// </remarks>
    public static readonly VideoProfile FullHd = new("1080p20", 1920, 1080, 20, 6000);

    /// <summary>
    /// 1280×720 at 30 FPS. The default: measured 30.5 ms/frame against a 33.3 ms budget.
    /// </summary>
    /// <remarks>
    /// Roughly 8% headroom on the reference machine — it fits, but not generously. A
    /// slower machine is expected to land on <see cref="Sd"/>, which is why the step down
    /// exists rather than being treated as a failure state.
    /// </remarks>
    public static readonly VideoProfile Hd = new("720p30", 1280, 720, 30, 3500);

    /// <summary>
    /// 960×540 at 30 FPS. First step down under load or a constrained link.
    /// </summary>
    /// <remarks>Measured 16.2 ms/frame — about half the 33.3 ms budget, so comfortably sustainable.</remarks>
    public static readonly VideoProfile Sd = new("540p30", 960, 540, 30, 1800);

    /// <summary>640×360 at 20 FPS. The floor — degraded but usable over a poor relay.</summary>
    public static readonly VideoProfile Low = new("360p20", 640, 360, 20, 800);

    /// <summary>Ordered best to worst. Adaptation walks this ladder one step at a time.</summary>
    public static readonly IReadOnlyList<VideoProfile> Ladder = [FullHd, Hd, Sd, Low];

    /// <summary>The per-frame time budget this profile implies.</summary>
    public double FrameBudgetMs => 1000.0 / Fps;

    public override string ToString() => $"{Name} ({Width}x{Height}@{Fps}, {TargetKbps}kbps)";
}

/// <summary>What the adaptive controller observed over one evaluation window.</summary>
/// <remarks>
/// <see cref="ConvertMsPerFrame"/> is optional so a caller that only has encode timings
/// still produces a valid sample; it simply under-reports pressure by the conversion
/// cost. The pipeline always supplies both.
/// </remarks>
public sealed record QualitySample(
    double EncodeMsPerFrame,
    double AchievedFps,
    double PacketLossFraction,
    double RoundTripMs,
    double ConvertMsPerFrame = 0)
{
    /// <summary>
    /// Total per-frame CPU cost: conversion plus encoding.
    /// </summary>
    /// <remarks>
    /// This, not the encode time alone, is what competes with the frame budget. On the
    /// reference machine 720p encodes in 21.9 ms — a comfortable-looking 66% of the
    /// 33.3 ms budget — but conversion adds 8.6 ms, putting real usage at 92%. Judging
    /// pressure on the encode alone would leave the controller believing there was
    /// headroom that the frame rate was already spending.
    /// </remarks>
    public double ProcessingMsPerFrame => EncodeMsPerFrame + ConvertMsPerFrame;
}

/// <summary>
/// Chooses an operating point from measured pipeline and network behaviour.
/// </summary>
/// <remarks>
/// <para>
/// Pure logic with no capture, encoder, or WebRTC dependency, so every adaptation rule
/// is an ordinary unit test rather than something only observable on a bad network.
/// </para>
/// <para>
/// Two asymmetries are deliberate. It <b>steps down immediately but climbs back
/// slowly</b>, because oscillating between profiles is more visible to an operator than
/// sitting one step low. And it never drops the session: over a poor relay, degrading
/// to 360p is always preferable to disconnecting, which is what Phase 23 asks for.
/// </para>
/// </remarks>
public sealed class AdaptiveQuality
{
    /// <summary>Consecutive good windows required before climbing. Asymmetric on purpose.</summary>
    public const int UpshiftWindows = 4;

    /// <summary>Processing time above this fraction of the frame budget means the CPU cannot keep up.</summary>
    public const double EncodeOverBudget = 0.85;

    /// <summary>Processing time below this leaves room to climb.</summary>
    public const double EncodeComfortable = 0.55;

    /// <summary>Loss above this is a network problem; sending more bits would make it worse.</summary>
    public const double LossHigh = 0.05;

    /// <summary>Loss below this is clean enough to consider climbing.</summary>
    public const double LossLow = 0.01;

    /// <summary>
    /// The best profile automatic adaptation will climb to.
    /// </summary>
    /// <remarks>
    /// <b>Not the top of the ladder.</b> 1080p measures 49.3 ms/frame on the reference
    /// hardware, so it cannot hold 30 FPS; it is published at 20 FPS as an explicit
    /// "detail over smoothness" choice. Climbing into it automatically would silently
    /// trade the operator's frame rate for resolution they did not ask for, so it is
    /// reachable only through <see cref="Force"/>.
    /// </remarks>
    public static readonly VideoProfile AutoCeiling = VideoProfile.Hd;

    private static readonly int CeilingIndex =
        VideoProfile.Ladder.ToList().FindIndex(p => p.Name == AutoCeiling.Name);

    private int _index;
    private int _goodWindows;

    public AdaptiveQuality(VideoProfile? start = null)
    {
        var initial = start ?? VideoProfile.Hd;
        _index = IndexOf(initial);
        if (_index < 0) _index = IndexOf(VideoProfile.Hd);
    }

    private static int IndexOf(VideoProfile p) =>
        VideoProfile.Ladder.ToList().FindIndex(x => x.Name == p.Name);

    public VideoProfile Current => VideoProfile.Ladder[_index];

    /// <summary>How many times the profile has changed. Surfaced in diagnostics.</summary>
    public int Adaptations { get; private set; }

    /// <summary>
    /// Feeds one observation window and returns the profile to use next.
    /// </summary>
    public VideoProfile Observe(QualitySample sample)
    {
        var budget = Current.FrameBudgetMs;

        // Total processing cost, not encode alone. Conversion runs on the same thread
        // and spends the same budget, so ignoring it is how a machine at 92% of budget
        // looks like one at 66%.
        var encodePressure = sample.ProcessingMsPerFrame / budget;

        var struggling =
            encodePressure > EncodeOverBudget
            || sample.PacketLossFraction > LossHigh
            // Achieving far below the target frame rate means something upstream of
            // the encoder — capture, or the link — is the constraint.
            || sample.AchievedFps < Current.Fps * 0.7;

        if (struggling)
        {
            _goodWindows = 0;
            if (_index < VideoProfile.Ladder.Count - 1)
            {
                _index++;
                Adaptations++;
            }
            // At the floor we stay put. Degrading further is not available, and
            // dropping the session would be worse than a poor picture.
            return Current;
        }

        var comfortable =
            encodePressure < EncodeComfortable
            && sample.PacketLossFraction < LossLow
            && sample.AchievedFps >= Current.Fps * 0.95;

        if (!comfortable)
        {
            // Neither struggling nor clearly comfortable: hold, and do not accumulate
            // credit towards climbing.
            _goodWindows = 0;
            return Current;
        }

        _goodWindows++;
        // Stop at the auto ceiling rather than the top of the ladder. Going higher is
        // the operator's call, not the controller's.
        if (_goodWindows < UpshiftWindows || _index <= CeilingIndex) return Current;

        // Climbing is speculative: the next profile up costs more per frame than we
        // have evidence for. Reset the counter so a bad outcome cannot immediately
        // climb again.
        _goodWindows = 0;
        _index--;
        Adaptations++;
        return Current;
    }

    /// <summary>
    /// Pins a profile because the operator chose one explicitly.
    /// </summary>
    /// <remarks>
    /// The only way to reach a profile above <see cref="AutoCeiling"/>. Pinning is a
    /// preference, not a guarantee — a machine that cannot sustain the choice will
    /// still be stepped down by <see cref="Observe"/> rather than delivering a
    /// slideshow.
    /// </remarks>
    public void Force(VideoProfile profile)
    {
        var i = IndexOf(profile);
        if (i < 0) return;
        if (i != _index) Adaptations++;
        _index = i;
        _goodWindows = 0;
    }
}
