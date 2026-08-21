using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace Techee.Windows.Host;

/// <summary>
/// VP8 encoding via libvpx, through SIPSorceryMedia.Encoders.
/// </summary>
/// <remarks>
/// <para>
/// Software encoding. Measured on the reference machine at ~16 ms/frame for 720p and
/// ~31 ms for 1080p, which is what puts 1080p30 out of reach — see
/// <c>docs/WINDOWS_VIDEO_PIPELINE.md</c>.
/// </para>
/// <para>
/// It takes an I420 <c>byte[]</c> because <c>EncodeVideoFaster</c>, which would have
/// accepted a BGRA pointer and converted natively, throws
/// <c>NotImplementedException</c> in SIPSorceryMedia.Encoders 10.0.4 for every pixel
/// format. That is why <see cref="PixelConvert"/> exists.
/// </para>
/// <para>
/// Behind <see cref="IVideoEncoder"/> so a hardware encoder can replace it without the
/// pump noticing — the fallback ADR 0001 reserves if 720p proves insufficient.
/// </para>
/// </remarks>
public sealed class VpxEncoder : IVideoEncoder
{
    private readonly object _gate = new();
    private readonly VpxVideoEncoder _encoder = new();

    private byte[] _scratch = [];
    private bool _keyFrameRequested = true;
    private bool _disposed;

    public int TargetKbps
    {
        get => (int)(_encoder.TargetKbps ?? 0u);
        set
        {
            lock (_gate)
            {
                if (_disposed) return;
                _encoder.TargetKbps = (uint)Math.Max(0, value);
            }
        }
    }

    public void ForceKeyFrame()
    {
        lock (_gate) _keyFrameRequested = true;
    }

    public byte[]? Encode(ReadOnlySpan<byte> i420, int width, int height)
    {
        lock (_gate)
        {
            if (_disposed) return null;

            if (_keyFrameRequested)
            {
                _encoder.ForceKeyFrame();
                _keyFrameRequested = false;
            }

            // The libvpx wrapper takes an array, not a span. Reuse one buffer rather
            // than allocating ~1.4 MB per frame at 720p — at 30 FPS that would be
            // 40 MB/s of pure garbage, and GC pauses show up directly as dropped frames.
            if (_scratch.Length < i420.Length) _scratch = new byte[i420.Length];
            i420.CopyTo(_scratch);

            try
            {
                return _encoder.EncodeVideo(
                    width, height,
                    i420.Length == _scratch.Length ? _scratch : _scratch[..i420.Length],
                    VideoPixelFormatsEnum.I420,
                    VideoCodecsEnum.VP8);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A single bad frame must not kill the pump. The next frame retries,
                // and a persistent failure shows up as a dropped-frame count rather
                // than an unattended host going dark.
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _encoder.Dispose();
        }
    }
}
