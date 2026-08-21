using Techee.Session;
using Xunit;

namespace Techee.Session.Tests;

/// <summary>
/// The controller's coordinate mapping.
/// </summary>
/// <remarks>
/// <para>
/// These are the same cases Android's <c>VideoGeometryTest</c> covers, held to the same
/// answers. The two controllers are independent implementations, and a click at the same
/// place in either must produce the same protocol coordinate — otherwise "the pointer is
/// slightly off" becomes a platform-specific bug hunt rather than one arithmetic error.
/// </para>
/// <para>
/// Worth stating what these protect: a mapping that is wrong by a few percent still looks
/// like a working session. Nothing errors, nothing disconnects, and the operator's clicks
/// simply land somewhere they did not aim. That is why it is unit-tested rather than left
/// to the acceptance run to notice.
/// </para>
/// </remarks>
public class VideoGeometryTests
{
    private const double Tolerance = 1e-9;

    // ---- content rectangle ----

    [Fact]
    public void A_matching_aspect_ratio_fills_the_view_with_no_bars()
    {
        var rect = VideoGeometry.ContentRect(1600, 900, 1280, 720);

        Assert.NotNull(rect);
        Assert.Equal(0, rect!.Value.Left, Tolerance);
        Assert.Equal(0, rect.Value.Top, Tolerance);
        Assert.Equal(1600, rect.Value.Width, Tolerance);
        Assert.Equal(900, rect.Value.Height, Tolerance);
    }

    [Fact]
    public void A_wide_video_in_a_tall_view_is_letterboxed_top_and_bottom()
    {
        // 16:9 in a square view: full width, and the bars take the rest of the height.
        var rect = VideoGeometry.ContentRect(1000, 1000, 1920, 1080);

        Assert.NotNull(rect);
        Assert.Equal(0, rect!.Value.Left, Tolerance);
        Assert.Equal(1000, rect.Value.Width, Tolerance);
        Assert.Equal(562.5, rect.Value.Height, Tolerance);
        Assert.Equal((1000 - 562.5) / 2, rect.Value.Top, Tolerance);
    }

    [Fact]
    public void A_tall_video_in_a_wide_view_is_pillarboxed_at_the_sides()
    {
        // A portrait phone screen shared onto a landscape window.
        var rect = VideoGeometry.ContentRect(1600, 900, 1080, 1920);

        Assert.NotNull(rect);
        Assert.Equal(0, rect!.Value.Top, Tolerance);
        Assert.Equal(900, rect.Value.Height, Tolerance);
        Assert.Equal(900 * (1080.0 / 1920.0), rect.Value.Width, Tolerance);
        Assert.Equal((1600 - rect.Value.Width) / 2, rect.Value.Left, Tolerance);
    }

    [Theory]
    [InlineData(0, 900, 1280, 720)]
    [InlineData(1600, 0, 1280, 720)]
    [InlineData(-1, 900, 1280, 720)]
    [InlineData(1600, 900, 0, 720)]
    [InlineData(1600, 900, 1280, 0)]
    [InlineData(1600, 900, -1280, 720)]
    public void A_degenerate_size_has_no_mapping_at_all(
        double viewWidth, double viewHeight, int videoWidth, int videoHeight)
    {
        // Null rather than a zero-sized rectangle. "No mapping yet" is a real state —
        // it is every frame before the first one — and it must not be confused with
        // "the pointer was in a bar".
        Assert.Null(VideoGeometry.ContentRect(viewWidth, viewHeight, videoWidth, videoHeight));
    }

    // ---- normalisation ----

    [Fact]
    public void The_centre_of_the_picture_is_the_centre_of_the_remote_screen()
    {
        var p = VideoGeometry.ToNormalized(800, 500, 1600, 1000, 1920, 1080);

        Assert.NotNull(p);
        Assert.Equal(0.5, p!.Value.X, Tolerance);
        Assert.Equal(0.5, p.Value.Y, Tolerance);
    }

    [Fact]
    public void The_centre_is_still_the_centre_when_the_view_is_letterboxed()
    {
        // The bars are symmetric, so the view centre and the picture centre coincide.
        // If this ever failed, every click would be offset by half a bar.
        var p = VideoGeometry.ToNormalized(500, 500, 1000, 1000, 1920, 1080);

        Assert.NotNull(p);
        Assert.Equal(0.5, p!.Value.X, Tolerance);
        Assert.Equal(0.5, p.Value.Y, Tolerance);
    }

    [Fact]
    public void The_picture_corners_map_to_the_unit_square_corners()
    {
        var rect = VideoGeometry.ContentRect(1000, 1000, 1920, 1080)!.Value;

        var topLeft = VideoGeometry.ToNormalized(rect.Left, rect.Top, rect);
        var bottomRight = VideoGeometry.ToNormalized(rect.Right, rect.Bottom, rect);

        Assert.NotNull(topLeft);
        Assert.Equal(0, topLeft!.Value.X, Tolerance);
        Assert.Equal(0, topLeft.Value.Y, Tolerance);

        Assert.NotNull(bottomRight);
        Assert.Equal(1, bottomRight!.Value.X, Tolerance);
        Assert.Equal(1, bottomRight.Value.Y, Tolerance);
    }

    [Fact]
    public void The_mapping_is_monotonic_across_the_picture()
    {
        // The property that catches a sign or origin error, which a centre-only test
        // cannot: the centre of a symmetric rectangle is correct even upside down.
        var rect = VideoGeometry.ContentRect(1000, 1000, 1920, 1080)!.Value;

        var left = VideoGeometry.ToNormalized(rect.Left + 10, rect.Top + 10, rect)!.Value;
        var middle = VideoGeometry.ToNormalized(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2, rect)!.Value;
        var right = VideoGeometry.ToNormalized(rect.Right - 10, rect.Bottom - 10, rect)!.Value;

        Assert.True(left.X < middle.X && middle.X < right.X, "x is not ordered left to right");
        Assert.True(left.Y < middle.Y && middle.Y < right.Y, "y is not ordered top to bottom");
    }

    // ---- the bug this type exists to prevent ----

    [Fact]
    public void A_click_in_a_letterbox_bar_produces_no_coordinate()
    {
        // PROTOCOL.md §5.3. The shipped controller normalised against the whole view, so
        // this produced a plausible 0..1 pair and clicked a real pixel on the host.
        var rect = VideoGeometry.ContentRect(1000, 1000, 1920, 1080)!.Value;

        Assert.True(rect.Top > 0, "this case needs a letterboxed layout to be meaningful");

        Assert.Null(VideoGeometry.ToNormalized(500, rect.Top - 1, rect));
        Assert.Null(VideoGeometry.ToNormalized(500, rect.Bottom + 1, rect));
    }

    [Fact]
    public void A_click_in_a_pillarbox_bar_produces_no_coordinate()
    {
        var rect = VideoGeometry.ContentRect(1600, 900, 1080, 1920)!.Value;

        Assert.True(rect.Left > 0, "this case needs a pillarboxed layout to be meaningful");

        Assert.Null(VideoGeometry.ToNormalized(rect.Left - 1, 450, rect));
        Assert.Null(VideoGeometry.ToNormalized(rect.Right + 1, 450, rect));
    }

    // ---- continuing a gesture ----

    [Fact]
    public void A_drag_into_a_bar_snaps_to_the_edge_rather_than_stopping()
    {
        // The opposite policy from starting a gesture, and deliberately so: a drag that
        // silently stopped at the boundary would leave the button held on the host.
        var rect = VideoGeometry.ContentRect(1000, 1000, 1920, 1080)!.Value;

        var above = VideoGeometry.ToNormalizedClamped(500, rect.Top - 50, rect);
        var below = VideoGeometry.ToNormalizedClamped(500, rect.Bottom + 50, rect);

        Assert.NotNull(above);
        Assert.Equal(0, above!.Value.Y, Tolerance);

        Assert.NotNull(below);
        Assert.Equal(1, below!.Value.Y, Tolerance);

        // The axis that was still inside is unaffected.
        Assert.Equal(0.5, above.Value.X, Tolerance);
    }

    [Fact]
    public void Clamping_never_escapes_the_unit_square()
    {
        var rect = VideoGeometry.ContentRect(1000, 1000, 1920, 1080)!.Value;

        foreach (var (x, y) in new[] { (-10_000.0, -10_000.0), (10_000.0, 10_000.0) })
        {
            var p = VideoGeometry.ToNormalizedClamped(x, y, rect);

            Assert.NotNull(p);
            Assert.InRange(p!.Value.X, 0, 1);
            Assert.InRange(p.Value.Y, 0, 1);
        }
    }

    [Fact]
    public void A_degenerate_rectangle_maps_nothing_even_when_clamping()
    {
        var empty = new VideoContentRect(0, 0, 0, 0);

        Assert.Null(VideoGeometry.ToNormalized(0, 0, empty));
        Assert.Null(VideoGeometry.ToNormalizedClamped(0, 0, empty));
    }

    // ---- DPI, which is matrix row D9 ----

    [Theory]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    public void The_mapping_is_independent_of_the_view_scale(double scale)
    {
        // D9 is about the host's physical-to-logical conversion, but the controller half
        // has to be neutral for that test to mean anything: the same point in a window
        // scaled by any factor must produce the same remote coordinate. A viewer that
        // drifted with DPI would make the host's mapping look wrong at 125%.
        var width = 1600 * scale;
        var height = 1000 * scale;

        var p = VideoGeometry.ToNormalized(width * 0.3, height * 0.7, width, height, 1920, 1080);

        Assert.NotNull(p);
        Assert.Equal(0.3, p!.Value.X, 1e-6);

        // y is checked against the letterboxed geometry rather than assumed, because a
        // 16:9 picture in a 16:10 view genuinely has bars.
        var rect = VideoGeometry.ContentRect(width, height, 1920, 1080)!.Value;
        var expectedY = (height * 0.7 - rect.Top) / rect.Height;
        Assert.Equal(expectedY, p.Value.Y, 1e-6);
    }
}
