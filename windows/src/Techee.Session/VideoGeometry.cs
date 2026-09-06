namespace Techee.Session;

/// <summary>Where the video sits inside a view, in view coordinates.</summary>
public readonly record struct VideoContentRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    /// <summary>True when the point falls on video rather than on a bar.</summary>
    public bool Contains(double x, double y) =>
        x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>
/// Where the remote picture actually sits inside a view, and how to turn a pointer
/// position into a protocol coordinate.
/// </summary>
/// <remarks>
/// <para>
/// The C# counterpart of Android's <c>VideoGeometry</c>, deliberately the same arithmetic
/// so a click at the same place in either controller produces the same coordinate. The
/// two are independent implementations of one specification, which is the only reason
/// comparing them is worth anything.
/// </para>
/// <para>
/// <c>PROTOCOL.md</c> §5.3 records the bug this exists to prevent: a controller that
/// normalised against the whole view produced a valid-looking 0..1 coordinate for a click
/// in a letterbox bar, and clicked a real pixel on the host. On a 16:9 desktop shown in a
/// window of a different shape, a large fraction of every gesture lands somewhere the
/// operator was not pointing — and it looks like a host-side mapping fault, which is
/// where the time then goes.
/// </para>
/// <para>
/// Pure arithmetic with no UI types, so every interesting case — wide, tall, square,
/// degenerate — is an ordinary unit test rather than something needing two machines and
/// somebody watching.
/// </para>
/// </remarks>
public static class VideoGeometry
{
    /// <summary>
    /// The rectangle the video occupies inside a view, under aspect-fit scaling.
    /// </summary>
    /// <remarks>
    /// Null when either the view or the video has no usable size. That happens
    /// legitimately — before the first frame, or during a layout pass — and it must not
    /// be confused with "the pointer was in a bar": there is simply no mapping yet, and
    /// guessing one would send coordinates derived from a placeholder.
    /// </remarks>
    public static VideoContentRect? ContentRect(
        double viewWidth, double viewHeight, int videoWidth, int videoHeight)
    {
        if (viewWidth <= 0 || viewHeight <= 0) return null;
        if (videoWidth <= 0 || videoHeight <= 0) return null;

        var viewAspect = viewWidth / viewHeight;
        var videoAspect = (double)videoWidth / videoHeight;

        if (videoAspect > viewAspect)
        {
            // Relatively wider: full width, bars above and below.
            var height = viewWidth / videoAspect;
            return new VideoContentRect(0, (viewHeight - height) / 2, viewWidth, height);
        }

        // Relatively taller, or identical: full height, bars at the sides.
        var width = viewHeight * videoAspect;
        return new VideoContentRect((viewWidth - width) / 2, 0, width, viewHeight);
    }

    /// <summary>
    /// Maps a pointer position in view coordinates to a protocol coordinate in 0..1.
    /// </summary>
    /// <remarks>
    /// Null for a position outside the video — in a bar, or before the geometry is known.
    /// <b>Null means do not send.</b> Clamping a bar click onto the nearest edge would be
    /// worse than dropping it: the operator would see clicks landing on the extreme edge
    /// of the remote desktop whenever they missed, and in a corner that is how windows
    /// get snapped and dialogs dismissed.
    /// </remarks>
    public static (double X, double Y)? ToNormalized(
        double pointerX, double pointerY,
        double viewWidth, double viewHeight,
        int videoWidth, int videoHeight)
    {
        var rect = ContentRect(viewWidth, viewHeight, videoWidth, videoHeight);
        return rect is null ? null : ToNormalized(pointerX, pointerY, rect.Value);
    }

    /// <summary>As above, against an already-computed rectangle.</summary>
    public static (double X, double Y)? ToNormalized(
        double pointerX, double pointerY, VideoContentRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        if (!rect.Contains(pointerX, pointerY)) return null;

        // Clamped after the containment check, purely against floating-point error at
        // the boundary: a click exactly on the edge must not come out at 1.0000001.
        return (
            Math.Clamp((pointerX - rect.Left) / rect.Width, 0, 1),
            Math.Clamp((pointerY - rect.Top) / rect.Height, 0, 1));
    }

    /// <summary>
    /// Maps a pointer position, snapping bar positions onto the nearest edge rather than
    /// rejecting them.
    /// </summary>
    /// <remarks>
    /// For <b>continuous</b> gestures only. Once a drag has begun on the video, a pointer
    /// that strays into a bar should keep dragging along the edge rather than have the
    /// drag silently stop and leave the remote button held. The distinction is the whole
    /// point: rejecting is right for starting a gesture, snapping is right for continuing
    /// one.
    /// </remarks>
    public static (double X, double Y)? ToNormalizedClamped(
        double pointerX, double pointerY, VideoContentRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return null;

        return (
            Math.Clamp((pointerX - rect.Left) / rect.Width, 0, 1),
            Math.Clamp((pointerY - rect.Top) / rect.Height, 0, 1));
    }
}
