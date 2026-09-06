namespace Techee.Windows.Host;

/// <summary>
/// One monitor, in the coordinate systems that actually matter.
/// </summary>
/// <remarks>
/// <para>
/// The distinction between physical and logical pixels is not pedantry — it is the
/// bug this type exists to prevent. On the development machine, DXGI reports the
/// desktop rectangle as 1536×864 (logical, DPI-scaled at 125%) while Desktop
/// Duplication delivers 1920×1080 (physical). Capture happens in physical pixels;
/// <c>SendInput</c> works in logical virtual-desktop coordinates. Conflating them puts
/// every remote click 25% away from where the operator aimed.
/// </para>
/// <para>
/// <see cref="Left"/>/<see cref="Top"/> can be negative: a monitor placed left of or
/// above the primary has negative virtual-desktop coordinates, which is the case naive
/// implementations get wrong.
/// </para>
/// </remarks>
public sealed record DisplayInfo(
    string Id,
    string DeviceName,
    int Left,
    int Top,
    int LogicalWidth,
    int LogicalHeight,
    int PhysicalWidth,
    int PhysicalHeight,
    bool IsPrimary)
{
    /// <summary>DPI scale factor, e.g. 1.25 at 125%. Derived rather than queried separately.</summary>
    public double Scale => LogicalWidth == 0 ? 1.0 : (double)PhysicalWidth / LogicalWidth;

    public int Right => Left + LogicalWidth;
    public int Bottom => Top + LogicalHeight;

    /// <summary>
    /// The form sent in <c>display.list</c>.
    /// </summary>
    /// <remarks>
    /// Reports <b>physical</b> pixels, because that is the size of the image the
    /// controller will actually receive, plus the scale so a controller can explain why
    /// those numbers differ from what the user's display settings show.
    /// </remarks>
    public DisplayWire ToWire() => new(Id, PhysicalWidth, PhysicalHeight, IsPrimary, Math.Round(Scale, 4));
}

/// <summary>
/// A display as it appears in the <c>display.list</c> control message.
/// </summary>
/// <remarks>
/// A named type rather than an anonymous one: anonymous types are internal to their
/// assembly, so tests in another assembly cannot read them without reflection — which
/// is a poor reason to leave a wire format untested.
/// </remarks>
public sealed record DisplayWire(string Id, int Width, int Height, bool Primary, double Scale);

/// <summary>
/// Maps normalized protocol coordinates onto Windows virtual-desktop coordinates.
/// </summary>
/// <remarks>
/// <para>
/// The protocol carries normalized 0..1 coordinates in the <b>captured surface</b>
/// space (<c>docs/PROTOCOL.md</c> §5.3). This turns them into the logical
/// virtual-desktop pixels <c>SendInput</c> expects.
/// </para>
/// <para>
/// Pure arithmetic with no Windows dependency, so multi-monitor and DPI correctness is
/// unit-testable without the monitors — which matters, because the interesting cases
/// (negative coordinates, mixed DPI) need hardware most developers do not have.
/// </para>
/// </remarks>
public static class DisplayGeometry
{
    /// <summary>The bounding box of every display, in logical coordinates.</summary>
    public static (int Left, int Top, int Width, int Height) VirtualBounds(IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0) return (0, 0, 0, 0);

        var left = displays.Min(d => d.Left);
        var top = displays.Min(d => d.Top);
        var right = displays.Max(d => d.Right);
        var bottom = displays.Max(d => d.Bottom);

        return (left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Maps a normalized point on one display to logical virtual-desktop coordinates.
    /// </summary>
    /// <remarks>
    /// The normalized point is relative to the captured surface, which is that
    /// display's <i>physical</i> pixels. The result is <i>logical</i>, so the DPI scale
    /// divides out — which is exactly the step that goes missing when someone assumes
    /// the two are the same.
    /// </remarks>
    public static (int X, int Y) ToVirtual(DisplayInfo display, double normalizedX, double normalizedY)
    {
        var x = Math.Clamp(normalizedX, 0.0, 1.0);
        var y = Math.Clamp(normalizedY, 0.0, 1.0);

        // Multiply by (logical size - 1) so normalized 1.0 lands on the last addressable
        // pixel rather than one past the edge, which would spill onto the next monitor.
        return (
            display.Left + (int)Math.Round(x * Math.Max(0, display.LogicalWidth - 1)),
            display.Top + (int)Math.Round(y * Math.Max(0, display.LogicalHeight - 1)));
    }

    /// <summary>
    /// Maps a normalized point on the whole virtual desktop to logical coordinates.
    /// </summary>
    /// <remarks>Used when the host shares all displays as one surface.</remarks>
    public static (int X, int Y) ToVirtualDesktop(
        IReadOnlyList<DisplayInfo> displays, double normalizedX, double normalizedY)
    {
        var (left, top, width, height) = VirtualBounds(displays);
        var x = Math.Clamp(normalizedX, 0.0, 1.0);
        var y = Math.Clamp(normalizedY, 0.0, 1.0);

        return (
            left + (int)Math.Round(x * Math.Max(0, width - 1)),
            top + (int)Math.Round(y * Math.Max(0, height - 1)));
    }

    /// <summary>
    /// Converts logical virtual-desktop coordinates to the 0..65535 absolute range
    /// <c>SendInput</c> uses with <c>MOUSEEVENTF_VIRTUALDESK</c>.
    /// </summary>
    /// <remarks>
    /// <c>SendInput</c>'s absolute mode normalizes against the whole virtual desktop, so
    /// this must use the union bounds and not the primary monitor. Getting that wrong
    /// works perfectly on a single-monitor machine and fails the moment a second one
    /// appears — which is why it is pinned by a test.
    /// </remarks>
    public static (int X, int Y) ToAbsolute(IReadOnlyList<DisplayInfo> displays, int virtualX, int virtualY)
    {
        var (left, top, width, height) = VirtualBounds(displays);
        if (width <= 1 || height <= 1) return (0, 0);

        var x = (virtualX - left) * 65535.0 / (width - 1);
        var y = (virtualY - top) * 65535.0 / (height - 1);

        return (
            (int)Math.Round(Math.Clamp(x, 0, 65535)),
            (int)Math.Round(Math.Clamp(y, 0, 65535)));
    }

    /// <summary>The display containing a logical point, or null if it falls in a gap.</summary>
    /// <remarks>
    /// Non-rectangular arrangements genuinely have gaps, and a point in one belongs to
    /// no monitor. Returning null rather than guessing keeps that visible.
    /// </remarks>
    public static DisplayInfo? DisplayAt(IReadOnlyList<DisplayInfo> displays, int x, int y) =>
        displays.FirstOrDefault(d => x >= d.Left && x < d.Right && y >= d.Top && y < d.Bottom);
}
