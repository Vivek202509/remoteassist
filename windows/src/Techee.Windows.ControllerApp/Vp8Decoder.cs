using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace Techee.Windows.ControllerApp;

/// <summary>One decoded frame: BGR24, tightly packed, top-down.</summary>
public sealed record DecodedFrame(int Width, int Height, byte[] Bgr);

/// <summary>
/// VP8 decoding via libvpx, through SIPSorceryMedia.Encoders.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of the host's <c>VpxEncoder</c>, and deliberately the same binding at
/// the same version. If the two ends ever disagree about VP8 it must not be because the
/// controller was built against a different libvpx — that would turn a real interop
/// finding into a packaging accident.
/// </para>
/// <para>
/// <b>What this does not settle.</b> Both ends of a techee-host → techee-ctl session run
/// this same library, so a decode that succeeds here proves the session works, not that
/// the packetisation is correct in general. Only a libwebrtc decoder — a real Android
/// controller — settles that, and it remains matrix row D4. See
/// <c>docs/W3_ACCEPTANCE_PROCEDURE.md</c> §0.
/// </para>
/// <para>
/// Not thread-safe; libvpx decoder instances are not. One decoder per session, driven
/// from the single decode worker in <see cref="ViewerForm"/>.
/// </para>
/// </remarks>
public sealed class Vp8Decoder : IDisposable
{
    private readonly object _gate = new();
    private readonly VpxVideoEncoder _codec = new();
    private bool _disposed;

    /// <summary>Frames libvpx accepted and returned pixels for.</summary>
    public long Decoded { get; private set; }

    /// <summary>
    /// Frames libvpx accepted but returned nothing for.
    /// </summary>
    /// <remarks>
    /// Normal at the start of a session: until the first key frame arrives there is no
    /// reference to decode against, so every inter frame produces nothing. A count that
    /// keeps climbing after the picture appears means frames are being lost, which is a
    /// transport symptom rather than a decoder one.
    /// </remarks>
    public long Empty { get; private set; }

    /// <summary>Frames the decoder threw on.</summary>
    public long Failed { get; private set; }

    /// <summary>
    /// Decodes one encoded frame, or null if it produced no picture.
    /// </summary>
    /// <remarks>
    /// Returns the <b>last</b> sample when libvpx yields several. A single VP8 frame
    /// decodes to at most one picture in practice; taking the last rather than the first
    /// means that if that ever stops being true, the viewer shows the newest picture
    /// instead of a stale one.
    /// </remarks>
    public DecodedFrame? Decode(byte[] encoded)
    {
        lock (_gate)
        {
            if (_disposed) return null;

            try
            {
                DecodedFrame? latest = null;

                foreach (var sample in _codec.DecodeVideo(
                             encoded, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8))
                {
                    if (sample.Sample is null || sample.Width <= 0 || sample.Height <= 0) continue;
                    latest = new DecodedFrame((int)sample.Width, (int)sample.Height, sample.Sample);
                }

                if (latest is null) Empty++;
                else Decoded++;

                return latest;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A corrupt frame must not end the session. VP8 resynchronises on the
                // next key frame, and the host sends one on request.
                Failed++;
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
            _codec.Dispose();
        }
    }

    public override string ToString() =>
        $"Vp8Decoder(decoded={Decoded}, empty={Empty}, failed={Failed})";
}
