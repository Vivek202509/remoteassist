using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Techee.Windows.Host;
using Xunit;
using Xunit.Abstractions;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// The pipeline against real hardware: DXGI Desktop Duplication and libvpx.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in this suite uses fakes, which proves the pump's logic but not that
/// it works. These run the genuine article and report what the machine actually
/// achieved, so the numbers in <c>docs/WINDOWS_VIDEO_PIPELINE.md</c> stay honest rather
/// than becoming folklore.
/// </para>
/// <para>
/// Skipped where there is no display — a headless CI runner has no desktop to
/// duplicate, and failing there would say nothing about the code.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class RealCaptureTests(ITestOutputHelper output)
{
    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static IReadOnlyList<DisplayInfo>? TryEnumerate()
    {
        try
        {
            var displays = DesktopDuplicationSource.EnumerateDisplays();
            return displays.Count == 0 ? null : displays;
        }
        catch (Exception)
        {
            // No adapter, no output, or duplication unavailable (an RDP session owns it).
            return null;
        }
    }

    [SkippableFact]
    public void Displays_enumerate_with_both_coordinate_systems()
    {
        Skip.IfNot(OnWindows, "DXGI is Windows-only");
        var displays = TryEnumerate();
        Skip.If(displays is null, "no duplicable display on this machine");

        foreach (var d in displays!)
        {
            output.WriteLine(
                $"{d.Id} {d.DeviceName}  logical {d.LogicalWidth}x{d.LogicalHeight} at ({d.Left},{d.Top})  " +
                $"physical {d.PhysicalWidth}x{d.PhysicalHeight}  scale {d.Scale:F2}  primary={d.IsPrimary}");

            Assert.True(d.LogicalWidth > 0 && d.LogicalHeight > 0);
            Assert.True(d.PhysicalWidth > 0 && d.PhysicalHeight > 0);

            // Physical is never smaller than logical: DPI scaling makes the logical
            // desktop the smaller of the two.
            Assert.True(d.PhysicalWidth >= d.LogicalWidth);
            Assert.True(d.Scale >= 1.0);
        }

        Assert.Contains(displays!, d => d.IsPrimary);
    }

    [SkippableFact]
    public void The_full_pipeline_produces_real_encoded_frames()
    {
        Skip.IfNot(OnWindows, "DXGI is Windows-only");
        var displays = TryEnumerate();
        Skip.If(displays is null, "no duplicable display on this machine");

        var display = displays!.First(d => d.IsPrimary);

        using var source = new DesktopDuplicationSource(display);
        var encoder = new VpxEncoder();
        using var pipeline = new VideoPipeline(source, encoder, new AdaptiveQuality(VideoProfile.Hd),
            log: output.WriteLine);

        var frames = new List<EncodedFrame>();
        pipeline.FrameEncoded += f => { lock (frames) frames.Add(f); };

        pipeline.Start();

        // Desktop Duplication only delivers on change. An idle desktop can produce very
        // few frames, so this asserts the pipeline WORKS rather than asserting a rate —
        // a rate assertion here would be flaky for reasons unrelated to the code.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            lock (frames) if (frames.Count >= 3) break;
            Thread.Sleep(50);
        }

        pipeline.Stop();

        var stats = pipeline.Stats;
        output.WriteLine("");
        output.WriteLine($"profile            {stats.Profile}");
        output.WriteLine($"source size        {source.SourceSize.Width}x{source.SourceSize.Height}");
        output.WriteLine($"output size        {source.OutputSize.Width}x{source.OutputSize.Height}");
        output.WriteLine($"captured           {stats.FramesCaptured}");
        output.WriteLine($"encoded            {stats.FramesEncoded}");
        output.WriteLine($"dropped            {stats.FramesDropped}");
        output.WriteLine($"capture timeouts   {stats.CaptureTimeouts}  (idle desktop => expected)");
        output.WriteLine($"convert            {stats.AverageConvertMs:F2} ms/frame");
        output.WriteLine($"encode             {stats.AverageEncodeMs:F2} ms/frame");
        output.WriteLine($"encode pressure    {stats.EncodePressure:P0} of the {stats.Profile.FrameBudgetMs:F1} ms budget");
        output.WriteLine($"avg frame size     {stats.AverageFrameBytes / 1024.0:F1} KB");
        output.WriteLine($"protected masked   {stats.ProtectedContentMasked}");

        lock (frames)
        {
            Skip.If(frames.Count == 0,
                "the desktop did not change during the window, so no frames were delivered");

            Assert.All(frames, f => Assert.NotEmpty(f.Payload));
            Assert.All(frames, f => Assert.Equal(VideoProfile.Hd.Width, f.Width));
            Assert.All(frames, f => Assert.Equal(VideoProfile.Hd.Height, f.Height));

            // The first frame a receiver gets must be decodable on its own.
            Assert.True(frames[0].IsKeyFrame, "the first emitted frame must be a keyframe");
        }

        Assert.True(stats.FramesEncoded > 0);
        Assert.True(stats.AverageEncodeMs > 0, "real encoding should take measurable time");
    }

    [SkippableFact]
    public void Changing_profile_mid_capture_resizes_the_real_pipeline()
    {
        Skip.IfNot(OnWindows, "DXGI is Windows-only");
        var displays = TryEnumerate();
        Skip.If(displays is null, "no duplicable display on this machine");

        var display = displays!.First(d => d.IsPrimary);
        using var source = new DesktopDuplicationSource(display);
        var encoder = new VpxEncoder();
        using var pipeline = new VideoPipeline(source, encoder, log: output.WriteLine);

        var sizes = new HashSet<(int, int)>();
        pipeline.FrameEncoded += f => { lock (sizes) sizes.Add((f.Width, f.Height)); };

        pipeline.Start();
        Thread.Sleep(1500);

        pipeline.ForceProfile(VideoProfile.Sd);
        Thread.Sleep(1500);

        pipeline.Stop();

        output.WriteLine($"frame sizes seen: {string.Join(", ", sizes.Select(s => $"{s.Item1}x{s.Item2}"))}");
        Assert.Equal((960, 540), source.OutputSize);

        lock (sizes)
        {
            Skip.If(sizes.Count == 0, "the desktop did not change during the window");
            // The GPU textures were genuinely rebuilt, not just the profile field.
            Assert.Contains((960, 540), sizes);
        }
    }

    [SkippableFact]
    public void The_real_encoder_honours_a_keyframe_request()
    {
        Skip.IfNot(OnWindows, "DXGI is Windows-only");

        // No display needed: this exercises libvpx directly, which is the part that has
        // to get the VP8 frame-type bit right for the pipeline's IsKeyFrame to mean
        // anything.
        var encoder = new VpxEncoder { TargetKbps = 2000 };
        try
        {
            const int w = 320, h = 240;
            var i420 = new byte[PixelConvert.I420Size(w, h)];
            new Random(1).NextBytes(i420);

            var first = encoder.Encode(i420, w, h);
            Skip.If(first is null || first.Length == 0, "libvpx produced no output on this machine");

            output.WriteLine($"first frame: {first!.Length} bytes, frame-type bit = {first[0] & 0x01}");
            Assert.Equal(0, first[0] & 0x01); // keyframe

            // A second encode of similar content should be a delta.
            var second = encoder.Encode(i420, w, h);
            if (second is { Length: > 0 })
                output.WriteLine($"second frame: {second.Length} bytes, frame-type bit = {second[0] & 0x01}");

            encoder.ForceKeyFrame();
            var forced = encoder.Encode(i420, w, h);
            Assert.NotNull(forced);
            output.WriteLine($"forced frame: {forced!.Length} bytes, frame-type bit = {forced[0] & 0x01}");
            Assert.Equal(0, forced[0] & 0x01);
        }
        finally
        {
            encoder.Dispose();
        }
    }
}
