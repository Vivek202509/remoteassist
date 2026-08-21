using Techee.Windows.Host;
using Xunit;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// BGRA → I420 correctness.
/// </summary>
/// <remarks>
/// A colour conversion that is subtly wrong produces a picture that looks <i>almost</i>
/// right — a slight tint, or text that shimmers — which is far harder to notice in
/// review than one that is obviously broken. So the optimised path is asserted
/// byte-identical to the scalar reference, and the reference itself is pinned against
/// known colours.
/// </remarks>
public class PixelConvertTests
{
    private static byte[] SolidBgra(int width, int height, byte b, byte g, byte r, int stride = 0)
    {
        stride = stride == 0 ? width * 4 : stride;
        var buf = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                buf[i] = b; buf[i + 1] = g; buf[i + 2] = r; buf[i + 3] = 255;
            }
        }
        return buf;
    }

    private static byte[] NoiseBgra(int width, int height, int stride, int seed)
    {
        var rng = new Random(seed);
        var buf = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                buf[i] = (byte)rng.Next(256);
                buf[i + 1] = (byte)rng.Next(256);
                buf[i + 2] = (byte)rng.Next(256);
                buf[i + 3] = 255;
            }
        }
        return buf;
    }

    // ---- known colours ----

    [Theory]
    // BT.601 studio swing: black is 16, not 0, and white is 235, not 255.
    // Values are the exact fixed-point results, not the real-arithmetic ones —
    // ((66r + 129g + 25b + 128) >> 8) + 16 truncates, so pure red is 82 rather than
    // the 81.5 a floating-point derivation would give.
    [InlineData(0, 0, 0, 16)]
    [InlineData(255, 255, 255, 235)]
    [InlineData(0, 0, 255, 82)]    // pure red
    [InlineData(0, 255, 0, 144)]   // pure green
    [InlineData(255, 0, 0, 41)]    // pure blue
    public void Luma_matches_bt601_for_known_colours(byte b, byte g, byte r, byte expectedY)
    {
        const int w = 16, h = 16;
        var src = SolidBgra(w, h, b, g, r);
        var dst = new byte[PixelConvert.I420Size(w, h)];

        PixelConvert.Convert(src, dst, w, h, w * 4);

        for (var i = 0; i < w * h; i++)
            Assert.Equal(expectedY, dst[i]);
    }

    [Fact]
    public void A_neutral_grey_has_neutral_chroma()
    {
        const int w = 8, h = 8;
        var src = SolidBgra(w, h, 128, 128, 128);
        var dst = new byte[PixelConvert.I420Size(w, h)];

        PixelConvert.Convert(src, dst, w, h, w * 4);

        var uOffset = w * h;
        var vOffset = uOffset + w * h / 4;
        for (var i = 0; i < w * h / 4; i++)
        {
            Assert.InRange(dst[uOffset + i], 127, 129);
            Assert.InRange(dst[vOffset + i], 127, 129);
        }
    }

    // ---- the fast path must equal the reference ----

    [Theory]
    [InlineData(16, 16)]
    [InlineData(64, 32)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    // Odd-ish widths, to catch a fast path that assumes a convenient multiple.
    [InlineData(18, 4)]
    [InlineData(22, 6)]
    public void The_fast_path_is_byte_identical_to_the_scalar_reference(int w, int h)
    {
        var stride = w * 4;
        var src = NoiseBgra(w, h, stride, seed: w * 7919 + h);

        var fast = new byte[PixelConvert.I420Size(w, h)];
        var reference = new byte[PixelConvert.I420Size(w, h)];

        PixelConvert.Convert(src, fast, w, h, stride);
        PixelConvert.ConvertScalar(src, reference, w, h, stride);

        Assert.Equal(reference, fast);
    }

    // ---- stride ----

    [Fact]
    public void Row_padding_is_honoured()
    {
        // GPU staging textures are routinely padded beyond width*4. Ignoring the row
        // pitch shears the image diagonally — an unmistakable artefact in production,
        // but only if something actually exercises a padded buffer.
        const int w = 8, h = 8;
        var stride = w * 4 + 64;

        var src = SolidBgra(w, h, 0, 0, 255, stride);
        // Fill the padding with a colour that would be obvious if it leaked in.
        for (var y = 0; y < h; y++)
            for (var i = w * 4; i < stride; i++)
                src[y * stride + i] = 0xFF;

        var dst = new byte[PixelConvert.I420Size(w, h)];
        PixelConvert.Convert(src, dst, w, h, stride);

        for (var i = 0; i < w * h; i++)
            Assert.Equal(82, dst[i]); // pure red luma, unaffected by the padding
    }

    [Fact]
    public void A_stride_narrower_than_the_row_is_rejected()
    {
        var dst = new byte[PixelConvert.I420Size(8, 8)];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PixelConvert.Convert(new byte[8 * 8 * 4], dst, 8, 8, 8 * 3));
    }

    [Fact]
    public void An_undersized_destination_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PixelConvert.Convert(new byte[8 * 8 * 4], new byte[10], 8, 8, 8 * 4));
    }

    [Fact]
    public void An_undersized_source_is_rejected()
    {
        // Cheaper to reject than to read past the end of a mapped GPU texture.
        var dst = new byte[PixelConvert.I420Size(8, 8)];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PixelConvert.Convert(new byte[16], dst, 8, 8, 8 * 4));
    }

    // ---- shape ----

    [Fact]
    public void The_i420_buffer_is_three_halves_of_the_pixel_count()
    {
        Assert.Equal(1280 * 720 * 3 / 2, PixelConvert.I420Size(1280, 720));
        Assert.Equal(1920 * 1080 * 3 / 2, PixelConvert.I420Size(1920, 1080));
    }

    [Fact]
    public void Chroma_averages_the_block_rather_than_point_sampling_it()
    {
        // A 2×2 block of one red and three black pixels. Point-sampling the top-left
        // would yield full red chroma; averaging yields roughly a quarter. Averaging
        // is what stops coloured text shimmering as it scrolls.
        const int w = 2, h = 2;
        var src = new byte[w * h * 4];
        src[2] = 255; src[3] = 255;             // (0,0) red
        for (var i = 1; i < 4; i++) src[i * 4 + 3] = 255; // rest black

        var dst = new byte[PixelConvert.I420Size(w, h)];
        PixelConvert.Convert(src, dst, w, h, w * 4);

        var v = dst[w * h + w * h / 4];
        var pureRedV = 240;
        var neutral = 128;

        Assert.True(v > neutral, "a red quarter should push V above neutral");
        Assert.True(v < (neutral + pureRedV) / 2,
            $"V={v} suggests point sampling rather than averaging the 2x2 block");
    }

    [Fact]
    public void Acceleration_is_reported_so_a_slow_machine_is_diagnosable()
    {
        // Not an assertion about this machine — a check that the property exists and
        // is answerable, because "why is capture slow here" needs it.
        _ = PixelConvert.HardwareVectorsAvailable;
    }
}
