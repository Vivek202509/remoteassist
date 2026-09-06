using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Techee.Session;

namespace Techee.Windows.ControllerApp;

/// <summary>
/// Draws the remote desktop and converts local pointer positions into protocol
/// coordinates.
/// </summary>
/// <remarks>
/// <para>
/// The C# counterpart of Android's <c>VideoGeometry</c>, and it exists for the same
/// reason: the protocol carries <b>normalised</b> coordinates in 0..1, so something has
/// to undo the letterboxing before a click means anything. Getting this wrong does not
/// break the session — it puts every click a little way from where the operator aimed,
/// which is the hardest class of remote-control bug to notice and the easiest to
/// misattribute to the host's input mapping.
/// </para>
/// <para>
/// The image is letterboxed rather than stretched. A stretched picture would make the
/// aspect ratio silently part of the coordinate mapping, so a host with an unusual
/// display would be systematically off in one axis.
/// </para>
/// </remarks>
public sealed class VideoSurface : Control
{
    private readonly object _gate = new();
    private Bitmap? _frame;

    public VideoSurface()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Selectable,
            true);

        TabStop = true;
        BackColor = Color.FromArgb(24, 24, 27);
        DoubleBuffered = true;
    }

    /// <summary>The remote picture size, or null before the first frame.</summary>
    public Size? RemoteSize { get; private set; }

    /// <summary>Replaces the displayed frame, disposing the one it supersedes.</summary>
    /// <remarks>
    /// Latest-wins. The previous bitmap is disposed here rather than left to the
    /// collector because at 30 fps a 720p leak is 80 MB/s, which becomes a stall long
    /// before it becomes an out-of-memory.
    /// </remarks>
    public void SetFrame(Bitmap frame)
    {
        Bitmap? previous;
        lock (_gate)
        {
            previous = _frame;
            _frame = frame;
            RemoteSize = frame.Size;
        }

        previous?.Dispose();

        // A lambda rather than the `Invalidate` method group: that group has several
        // overloads, so which delegate it binds to depends on overload resolution
        // between BeginInvoke(Action) and BeginInvoke(Delegate).
        if (IsHandleCreated) BeginInvoke(() => Invalidate());
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        lock (_gate)
        {
            if (_frame is null)
            {
                DrawWaiting(e.Graphics);
                return;
            }

            // NearestNeighbor: this is a screen share, and bilinear smoothing on text is
            // the difference between reading a remote error dialog and squinting at it.
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(_frame, PictureRect());
        }
    }

    private void DrawWaiting(Graphics g)
    {
        g.Clear(BackColor);

        using var brush = new SolidBrush(Color.FromArgb(140, 140, 150));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        g.DrawString("waiting for the first frame…", Font, brush, ClientRectangle, format);
    }

    /// <summary>
    /// Where the remote picture sits inside this control, or null before the geometry is
    /// known.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="VideoGeometry"/> rather than repeating the arithmetic, so
    /// this control and Android's surface letterbox identically and the fixtures cover
    /// both. Everything this rectangle excludes is padding and belongs to no remote
    /// coordinate at all.
    /// </remarks>
    public VideoContentRect? ContentRect()
    {
        var remote = RemoteSize;
        if (remote is null) return null;

        return VideoGeometry.ContentRect(
            ClientSize.Width, ClientSize.Height, remote.Value.Width, remote.Value.Height);
    }

    /// <summary>The picture's on-screen rectangle, for painting.</summary>
    public Rectangle PictureRect()
    {
        if (ContentRect() is not { } r) return ClientRectangle;

        return new Rectangle(
            (int)Math.Round(r.Left),
            (int)Math.Round(r.Top),
            Math.Max(1, (int)Math.Round(r.Width)),
            Math.Max(1, (int)Math.Round(r.Height)));
    }

    /// <summary>
    /// Converts a client-area point into protocol coordinates, or null if it is outside
    /// the picture.
    /// </summary>
    /// <param name="continuing">
    /// True while a gesture is already in progress, which snaps a pointer that has
    /// strayed into the padding onto the nearest edge instead of dropping it. A drag that
    /// silently stopped at the letterbox boundary would leave the remote button held.
    /// </param>
    public (double X, double Y)? ToRemote(Point client, bool continuing = false)
    {
        if (ContentRect() is not { } rect) return null;

        return continuing
            ? VideoGeometry.ToNormalizedClamped(client.X, client.Y, rect)
            : VideoGeometry.ToNormalized(client.X, client.Y, rect);
    }

    /// <summary>Builds a bitmap from a decoded BGR24 frame.</summary>
    /// <remarks>
    /// <c>Format24bppRgb</c> is BGR in memory, which is exactly what libvpx handed back,
    /// so this is a row-wise copy rather than a conversion. The row loop is not
    /// avoidable: GDI+ pads each scanline to a four-byte boundary and the decoder's
    /// output is packed.
    /// </remarks>
    public static Bitmap ToBitmap(DecodedFrame frame)
    {
        var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format24bppRgb);

        var data = bitmap.LockBits(
            new Rectangle(0, 0, frame.Width, frame.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format24bppRgb);

        try
        {
            var packedStride = frame.Width * 3;

            for (var row = 0; row < frame.Height; row++)
            {
                var source = row * packedStride;
                if (source + packedStride > frame.Bgr.Length) break;

                System.Runtime.InteropServices.Marshal.Copy(
                    frame.Bgr,
                    source,
                    data.Scan0 + row * data.Stride,
                    packedStride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    /// <summary>Arrows and Tab are remote input, not local focus navigation.</summary>
    /// <remarks>
    /// Without this WinForms eats them to move focus between controls, so the two keys
    /// most needed for driving a remote desktop would be the two that never arrive.
    /// </remarks>
    protected override bool IsInputKey(Keys keyData) => true;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _frame?.Dispose();
                _frame = null;
            }
        }

        base.Dispose(disposing);
    }
}
