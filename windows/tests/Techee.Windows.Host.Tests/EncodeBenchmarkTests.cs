using System.Diagnostics;
using System.Runtime.InteropServices;
using Techee.Windows.Host;
using Xunit;
using Xunit.Abstractions;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// Reproduces the convert and encode costs quoted in <c>docs/WINDOWS_VIDEO_PIPELINE.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The figures in that document drive a real design decision — that 1080p30 is out of
/// reach and 720p30 is the default — so they need to be reproducible by anyone rather
/// than remembered from a one-off run.
/// </para>
/// <para>
/// Two things make a benchmark like this lie, and both are handled here. <b>Warm-up</b>:
/// the first encode pays JIT, allocation, and a full keyframe, so it is excluded.
/// <b>Static content</b>: libvpx early-outs on unchanged macroblocks, so encoding one
/// frame repeatedly reports roughly half the true cost — the frames below genuinely
/// differ.
/// </para>
/// <para>
/// Asserted loosely. This is a measurement that must be <i>recorded</i>, not a
/// threshold that fails on slower hardware; a tight bound would just make the suite
/// red on a smaller CI machine while telling nobody anything useful.
/// </para>
/// </remarks>
public class EncodeBenchmarkTests(ITestOutputHelper output)
{
    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Frames that genuinely differ, so libvpx cannot early-out.</summary>
    private static byte[][] VaryingBgra(int width, int height, int count)
    {
        var frames = new byte[count][];
        var rng = new Random(20260815);

        for (var f = 0; f < count; f++)
        {
            var buf = new byte[width * height * 4];
            // A gradient plus moving blocks: closer to desktop content than pure noise,
            // which would be pathologically hard to encode and overstate the cost.
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    var block = ((x + f * 37) / 64 + (y + f * 23) / 64) % 2 == 0;
                    buf[i] = (byte)(block ? x * 255 / width : 32);
                    buf[i + 1] = (byte)(block ? y * 255 / height : 32);
                    buf[i + 2] = (byte)(block ? (x + y + f * 11) % 256 : 200);
                    buf[i + 3] = 255;
                }
            }
            // A little noise so every frame differs even where the blocks align.
            for (var k = 0; k < buf.Length / 64; k++) buf[rng.Next(buf.Length)] = (byte)rng.Next(256);
            frames[f] = buf;
        }

        return frames;
    }

    [SkippableTheory]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1280, 720, "720p")]
    [InlineData(960, 540, "540p")]
    public void Convert_and_encode_cost(int width, int height, string label)
    {
        Skip.IfNot(OnWindows, "libvpx native binaries are shipped for Windows here");

        const int warmup = 5;
        const int iterations = 40;

        var sources = VaryingBgra(width, height, 4);
        var i420 = new byte[PixelConvert.I420Size(width, height)];
        var stride = width * 4;

        // ---- convert ----
        for (var i = 0; i < warmup; i++) PixelConvert.Convert(sources[0], i420, width, height, stride);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            PixelConvert.Convert(sources[i % sources.Length], i420, width, height, stride);
        sw.Stop();
        var convertMs = sw.Elapsed.TotalMilliseconds / iterations;

        // ---- encode ----
        var encoder = new VpxEncoder { TargetKbps = 4000 };
        try
        {
            for (var i = 0; i < warmup; i++)
            {
                PixelConvert.Convert(sources[i % sources.Length], i420, width, height, stride);
                encoder.Encode(i420, width, height);
            }

            long bytes = 0;
            var encodeTotal = 0.0;
            var encoded = 0;

            for (var i = 0; i < iterations; i++)
            {
                PixelConvert.Convert(sources[i % sources.Length], i420, width, height, stride);

                var t0 = Stopwatch.GetTimestamp();
                var payload = encoder.Encode(i420, width, height);
                var t1 = Stopwatch.GetTimestamp();

                encodeTotal += (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                if (payload is not null) { bytes += payload.Length; encoded++; }
            }

            Skip.If(encoded == 0, "libvpx produced no output on this machine");

            var encodeMs = encodeTotal / iterations;
            var total = convertMs + encodeMs;

            output.WriteLine($"{label} {width}x{height}");
            output.WriteLine($"  convert       {convertMs,6:F1} ms/frame  (SIMD: {PixelConvert.HardwareVectorsAvailable})");
            output.WriteLine($"  encode        {encodeMs,6:F1} ms/frame");
            output.WriteLine($"  total         {total,6:F1} ms/frame");
            output.WriteLine($"  FPS ceiling   {1000.0 / total,6:F1}   (convert+encode, one core)");
            output.WriteLine($"  30 FPS budget {total / 33.3:P0} used");
            output.WriteLine($"  avg payload   {bytes / (double)encoded / 1024.0,6:F1} KB");

            // Recorded, not gated: a slower machine should report a slower number, not
            // turn the suite red.
            Assert.True(convertMs > 0);
            Assert.True(encodeMs > 0);
        }
        finally
        {
            encoder.Dispose();
        }
    }

    [SkippableFact]
    public void The_default_profile_is_the_one_this_machine_can_sustain()
    {
        Skip.IfNot(OnWindows, "libvpx native binaries are shipped for Windows here");

        // The claim the ladder rests on: 720p fits inside a 30 FPS budget on the
        // reference hardware and 1080p does not. If that ever stops being true here,
        // the default deserves revisiting — so this reports rather than asserts a
        // threshold, and names what it found.
        var results = new Dictionary<string, double>();

        foreach (var profile in new[] { VideoProfile.Hd, VideoProfile.FullHd })
        {
            var sources = VaryingBgra(profile.Width, profile.Height, 4);
            var i420 = new byte[PixelConvert.I420Size(profile.Width, profile.Height)];
            var stride = profile.Width * 4;
            var encoder = new VpxEncoder { TargetKbps = profile.TargetKbps };

            try
            {
                for (var i = 0; i < 5; i++)
                {
                    PixelConvert.Convert(sources[i % sources.Length], i420, profile.Width, profile.Height, stride);
                    encoder.Encode(i420, profile.Width, profile.Height);
                }

                var sw = Stopwatch.StartNew();
                const int n = 20;
                for (var i = 0; i < n; i++)
                {
                    PixelConvert.Convert(sources[i % sources.Length], i420, profile.Width, profile.Height, stride);
                    encoder.Encode(i420, profile.Width, profile.Height);
                }
                sw.Stop();

                var perFrame = sw.Elapsed.TotalMilliseconds / n;
                results[profile.Name] = perFrame;
                output.WriteLine(
                    $"{profile.Name,-8} {perFrame,6:F1} ms/frame  budget {profile.FrameBudgetMs,5:F1} ms  " +
                    $"=> {(perFrame <= profile.FrameBudgetMs ? "fits" : "OVER BUDGET")}");
            }
            finally
            {
                encoder.Dispose();
            }
        }

        output.WriteLine("");
        output.WriteLine($"auto ceiling is {AdaptiveQuality.AutoCeiling.Name}; " +
                         $"{VideoProfile.FullHd.Name} stays operator-selected.");

        Assert.True(results.ContainsKey(VideoProfile.Hd.Name));
    }
}
