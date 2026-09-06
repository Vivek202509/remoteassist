using System.Buffers.Binary;
using System.Text;
using Techee.Session;
using Xunit;

namespace Techee.Session.Tests;

/// <summary>
/// The IVF container the acceptance recording is written into.
/// </summary>
/// <remarks>
/// <para>
/// Byte-layout code, so it gets byte-level assertions. A recording is the artefact an
/// acceptance run is judged on, and a header written even slightly wrong produces a file
/// that opens, reports a plausible duration, and plays nothing — which reads exactly like
/// a session that carried no video. Diagnosing that as a container bug rather than a
/// transport one costs an afternoon.
/// </para>
/// <para>
/// The layout is checked against the spec's field offsets rather than against what the
/// writer happens to emit, so this fails if the writer changes rather than agreeing with
/// it.
/// </para>
/// </remarks>
public class IvfWriterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"techee-ivf-{Guid.NewGuid():N}.ivf");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static byte[] Frame(byte fill, int length) => Enumerable.Repeat(fill, length).ToArray();

    [Fact]
    public void The_file_header_is_a_well_formed_VP8_IVF_header()
    {
        using (var writer = new IvfWriter(_path))
        {
            writer.SetDimensions(1280, 720);
            writer.Write(Frame(0xAA, 16), rtpTimestamp: 1000);
        }

        var bytes = File.ReadAllBytes(_path);

        Assert.Equal("DKIF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)));
        Assert.Equal("VP80", Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(1280, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2)));
        Assert.Equal(720, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2)));

        // Timebase 1/90000 — the RTP video clock, so timestamps need no conversion.
        Assert.Equal(90_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20, 4)));

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)));
    }

    [Fact]
    public void Each_frame_is_preceded_by_its_length_and_timestamp()
    {
        using (var writer = new IvfWriter(_path))
        {
            writer.SetDimensions(640, 480);
            writer.Write(Frame(0x01, 4), rtpTimestamp: 90_000);
            writer.Write(Frame(0x02, 7), rtpTimestamp: 93_000);
        }

        var bytes = File.ReadAllBytes(_path);
        var offset = 32;

        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 4, 8)));
        Assert.Equal(new byte[] { 1, 1, 1, 1 }, bytes.AsSpan(offset + 12, 4).ToArray());

        offset += 12 + 4;

        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)));

        // Rebased on the first frame, so the file starts at zero rather than at whatever
        // random offset the RTP clock happened to begin on.
        Assert.Equal(3_000ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 4, 8)));

        Assert.Equal(32 + (12 + 4) + (12 + 7), bytes.Length);
    }

    [Fact]
    public void A_wrapping_rtp_clock_stays_monotonic()
    {
        // The 32-bit RTP timestamp wraps roughly every 13 hours at 90 kHz. Signed
        // arithmetic here would turn the wrap into an enormous negative jump, which
        // players render as the recording ending at that point.
        using (var writer = new IvfWriter(_path))
        {
            writer.SetDimensions(320, 240);
            writer.Write(Frame(0x01, 2), rtpTimestamp: uint.MaxValue - 1);
            writer.Write(Frame(0x02, 2), rtpTimestamp: 8);   // wrapped
        }

        var bytes = File.ReadAllBytes(_path);

        const int firstFrameHeader = 32;
        const int secondFrameHeader = firstFrameHeader + 12 + 2;   // header + 2 bytes of payload
        const int timestampOffset = 4;                             // after the 4-byte length

        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(firstFrameHeader + timestampOffset, 8)));

        Assert.Equal(10ul, BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(secondFrameHeader + timestampOffset, 8)));
    }

    [Fact]
    public void The_frame_count_is_patched_on_close()
    {
        using (var writer = new IvfWriter(_path))
        {
            writer.SetDimensions(320, 240);
            for (var i = 0; i < 5; i++) writer.Write(Frame(0x03, 3), (uint)(i * 3000));

            // Not yet written to the header — the file is still open.
            Assert.Equal(5, writer.Frames);
        }

        var bytes = File.ReadAllBytes(_path);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)));
    }

    [Fact]
    public void The_first_dimensions_win()
    {
        // IVF has one header for the whole file, so a mid-session profile change cannot
        // be represented. Recording the size the file starts at is the honest choice;
        // silently relabelling it as the newest would misdescribe every earlier frame.
        using (var writer = new IvfWriter(_path))
        {
            writer.SetDimensions(1280, 720);
            writer.SetDimensions(640, 360);
            writer.Write(Frame(0x04, 2), 0);
        }

        var bytes = File.ReadAllBytes(_path);
        Assert.Equal(1280, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2)));
        Assert.Equal(720, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2)));
    }

    [Theory]
    [InlineData(0, 720)]
    [InlineData(1280, 0)]
    [InlineData(-1, 720)]
    [InlineData(70_000, 720)]
    public void A_nonsensical_size_is_ignored_rather_than_truncated(int width, int height)
    {
        // A size that does not fit the field would otherwise wrap: 70000 becomes 4464,
        // which is a plausible-looking number and a completely wrong one.
        using (var writer = new IvfWriter(_path))
        {
            writer.SetDimensions(width, height);
            writer.Write(Frame(0x05, 2), 0);
        }

        var bytes = File.ReadAllBytes(_path);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2)));
    }

    [Fact]
    public void An_empty_frame_is_not_recorded()
    {
        using (var writer = new IvfWriter(_path))
        {
            writer.SetDimensions(320, 240);
            writer.Write([], 0);
        }

        var bytes = File.ReadAllBytes(_path);
        Assert.Equal(32, bytes.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)));
    }

    [Fact]
    public void Writing_after_close_is_ignored_rather_than_throwing()
    {
        // The receive thread and the shutdown path genuinely race at the end of every
        // session. A frame arriving one instant late must not become an unhandled
        // exception on a network thread.
        var writer = new IvfWriter(_path);
        writer.SetDimensions(320, 240);
        writer.Write(Frame(0x06, 2), 0);
        writer.Dispose();

        var exception = Record.Exception(() => writer.Write(Frame(0x07, 2), 3000));

        Assert.Null(exception);
        Assert.Equal(1, writer.Frames);
    }
}
