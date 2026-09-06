namespace Techee.Windows.Host;

/// <summary>
/// Receives one captured frame. The span is valid only for the duration of the call.
/// </summary>
/// <remarks>
/// A callback rather than a returned buffer so the frame can be handed over while the
/// GPU staging texture is still mapped. Returning a <c>byte[]</c> would force a copy of
/// roughly 8 MB per frame at 1080p — 240 MB/s at 30 FPS, purely to change ownership.
/// </remarks>
public delegate void FrameHandler(ReadOnlySpan<byte> bgra, int width, int height, int strideBytes);

/// <summary>A source of desktop frames.</summary>
/// <remarks>
/// An interface so <see cref="VideoPipeline"/> can be tested end to end without a GPU.
/// The pacing, adaptation, keyframe and telemetry behaviour are all things that would
/// otherwise only be observable on real hardware under real load.
/// </remarks>
public interface IScreenSource : IDisposable
{
    /// <summary>The display being captured.</summary>
    DisplayInfo Display { get; }

    /// <summary>The size of the frames this source currently produces.</summary>
    (int Width, int Height) OutputSize { get; }

    /// <summary>
    /// Reconfigures the output size, typically because the quality profile changed.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TryCapture"/> because reallocating GPU textures mid-pump
    /// is expensive and must not happen per frame.
    /// </remarks>
    void Resize(int width, int height);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a frame and hands it to <paramref name="onFrame"/>.
    /// </summary>
    /// <returns>
    /// False on timeout, which is <b>not</b> an error: Desktop Duplication only delivers
    /// a frame when the screen actually changes, so an idle desktop times out constantly
    /// and that is the API working correctly.
    /// </returns>
    bool TryCapture(TimeSpan timeout, FrameHandler onFrame);

    /// <summary>
    /// True when the OS blanked protected content in the most recent frame.
    /// </summary>
    /// <remarks>
    /// DRM-protected windows are blacked out by Windows itself, not by Techee. Surfaced
    /// so an operator sees "protected content hidden" rather than an unexplained black
    /// rectangle.
    /// </remarks>
    bool ProtectedContentMasked { get; }
}

/// <summary>A video encoder.</summary>
/// <remarks>
/// Abstracted so the pump can be tested without libvpx, and so a hardware encoder can
/// be substituted later without touching the pump — the fallback ADR 0001 reserves.
/// </remarks>
public interface IVideoEncoder : IDisposable
{
    /// <summary>Target bitrate. Changing it mid-stream is how bandwidth adaptation takes effect.</summary>
    int TargetKbps { get; set; }

    /// <summary>
    /// Encodes one I420 frame, or returns null when the encoder produced no output.
    /// </summary>
    byte[]? Encode(ReadOnlySpan<byte> i420, int width, int height);

    /// <summary>
    /// Requests a keyframe on the next encode.
    /// </summary>
    /// <remarks>
    /// Required after a resolution change and after a decoder reports loss. Without it,
    /// a receiver that missed the switch decodes deltas against a frame of the wrong
    /// size and shows garbage until the next natural keyframe.
    /// </remarks>
    void ForceKeyFrame();
}

/// <summary>One encoded frame leaving the pipeline.</summary>
public sealed record EncodedFrame(
    byte[] Payload,
    int Width,
    int Height,
    bool IsKeyFrame,
    TimeSpan CaptureToEncode);

/// <summary>
/// Where encoded frames go. The pipeline's only outward dependency.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that keeps capture independent of transport.
/// <c>Techee.Windows.Host</c> deliberately references no Techee project and knows
/// nothing about WebRTC, SDP, or the broker: it produces VP8 and hands it to whoever
/// asked. The peer connection is adapted onto this interface from the session layer,
/// so signaling concerns never reach the capture thread and the pump stays testable
/// with a fake sink and no GPU.
/// </para>
/// <para>
/// Implementations are called <b>on the capture thread</b> and must not block — time
/// spent here is time not spent capturing. They must not throw either, though the pump
/// defends itself anyway.
/// </para>
/// </remarks>
public interface IEncodedVideoSink
{
    /// <summary>
    /// Sends one encoded frame.
    /// </summary>
    /// <param name="frame">The encoded frame. The payload must not be retained past the call.</param>
    /// <param name="fps">
    /// The frame rate the current profile is pacing to, so the transport can derive an
    /// RTP frame duration without knowing anything about quality profiles.
    /// </param>
    void SendEncodedFrame(EncodedFrame frame, int fps);
}
