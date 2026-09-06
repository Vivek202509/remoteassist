using Techee.Windows.Host;
using Xunit;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// Coordinate mapping across monitors and DPI scales.
/// </summary>
/// <remarks>
/// These are the cases that are wrong on every naive implementation and that most
/// developers cannot reproduce, because they need a second monitor placed to the left
/// of the primary and a mixed-DPI setup. Doing the arithmetic in a pure type makes them
/// testable without the hardware.
/// </remarks>
public class DisplayGeometryTests
{
    /// <summary>The development machine: 1920×1080 panel at 125%, so 1536×864 logical.</summary>
    private static readonly DisplayInfo Scaled125 =
        new("DISPLAY1", @"\\.\DISPLAY1", 0, 0, 1536, 864, 1920, 1080, IsPrimary: true);

    private static readonly DisplayInfo Primary1080 =
        new("DISPLAY1", @"\\.\DISPLAY1", 0, 0, 1920, 1080, 1920, 1080, IsPrimary: true);

    /// <summary>A second monitor placed to the LEFT of the primary — negative coordinates.</summary>
    private static readonly DisplayInfo LeftOfPrimary =
        new("DISPLAY2", @"\\.\DISPLAY2", -1280, 0, 1280, 720, 1280, 720, IsPrimary: false);

    /// <summary>A 4K monitor at 200%, above the primary. Mixed DPI and negative Y.</summary>
    private static readonly DisplayInfo AbovePrimary4K =
        new("DISPLAY3", @"\\.\DISPLAY3", 0, -1080, 1920, 1080, 3840, 2160, IsPrimary: false);

    // ---- DPI ----

    [Fact]
    public void Scale_is_derived_from_physical_over_logical()
    {
        Assert.Equal(1.25, Scaled125.Scale, 4);
        Assert.Equal(1.0, Primary1080.Scale, 4);
        Assert.Equal(2.0, AbovePrimary4K.Scale, 4);
    }

    [Fact]
    public void A_normalized_point_maps_through_the_dpi_scale_not_around_it()
    {
        // Capture is in PHYSICAL pixels (1920×1080); SendInput takes LOGICAL
        // coordinates (1536×864). The centre of the captured image must land at the
        // centre of the logical desktop, not at 1920/2 — which would be off-screen.
        var (x, y) = DisplayGeometry.ToVirtual(Scaled125, 0.5, 0.5);

        Assert.Equal(768, x); // round(0.5 * 1535) = 768
        Assert.Equal(432, y); // round(0.5 * 863)  = 432
        Assert.True(x < Scaled125.LogicalWidth, "must stay inside the logical desktop, not the physical one");
    }

    [Fact]
    public void The_far_edge_maps_to_the_last_addressable_pixel_not_past_it()
    {
        // Multiplying by the full width would put normalized 1.0 one pixel beyond the
        // monitor, which on a multi-monitor desktop silently lands on the neighbour.
        var (x, y) = DisplayGeometry.ToVirtual(Primary1080, 1.0, 1.0);

        Assert.Equal(1919, x);
        Assert.Equal(1079, y);
    }

    [Fact]
    public void Out_of_range_normalized_values_clamp_to_the_edge()
    {
        Assert.Equal((0, 0), DisplayGeometry.ToVirtual(Primary1080, -5.0, -0.001));
        Assert.Equal((1919, 1079), DisplayGeometry.ToVirtual(Primary1080, 1.5, 99.0));
    }

    // ---- multi-monitor ----

    [Fact]
    public void Virtual_bounds_span_monitors_placed_at_negative_coordinates()
    {
        var displays = new[] { Primary1080, LeftOfPrimary };
        var (left, top, width, height) = DisplayGeometry.VirtualBounds(displays);

        Assert.Equal(-1280, left);
        Assert.Equal(0, top);
        Assert.Equal(3200, width); // 1280 + 1920
        Assert.Equal(1080, height);
    }

    [Fact]
    public void Virtual_bounds_handle_a_monitor_above_the_primary()
    {
        var displays = new[] { Primary1080, AbovePrimary4K };
        var (left, top, width, height) = DisplayGeometry.VirtualBounds(displays);

        Assert.Equal(0, left);
        Assert.Equal(-1080, top);
        Assert.Equal(1920, width);
        Assert.Equal(2160, height);
    }

    [Fact]
    public void Mapping_onto_a_secondary_monitor_keeps_its_own_origin()
    {
        // A click at the top-left of DISPLAY2's captured image must land at
        // DISPLAY2's origin, which is negative — not at (0,0) on the primary.
        var (x, y) = DisplayGeometry.ToVirtual(LeftOfPrimary, 0.0, 0.0);

        Assert.Equal(-1280, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void Whole_desktop_mapping_spans_the_union()
    {
        var displays = new[] { Primary1080, LeftOfPrimary };

        Assert.Equal((-1280, 0), DisplayGeometry.ToVirtualDesktop(displays, 0.0, 0.0));
        Assert.Equal((1919, 1079), DisplayGeometry.ToVirtualDesktop(displays, 1.0, 1.0));

        var (midX, _) = DisplayGeometry.ToVirtualDesktop(displays, 0.5, 0.5);
        Assert.Equal(320, midX); // -1280 + round(0.5 * 3199)
    }

    // ---- SendInput absolute coordinates ----

    [Fact]
    public void Absolute_coordinates_normalize_against_the_virtual_desktop()
    {
        // SendInput's absolute mode with MOUSEEVENTF_VIRTUALDESK normalizes against
        // the WHOLE virtual desktop. Using the primary monitor instead works perfectly
        // on a single-monitor machine and breaks the moment a second one appears.
        var displays = new[] { Primary1080, LeftOfPrimary };

        Assert.Equal((0, 0), DisplayGeometry.ToAbsolute(displays, -1280, 0));
        Assert.Equal((65535, 65535), DisplayGeometry.ToAbsolute(displays, 1919, 1079));
    }

    [Fact]
    public void Absolute_coordinates_on_a_single_monitor_span_the_full_range()
    {
        var displays = new[] { Primary1080 };

        Assert.Equal((0, 0), DisplayGeometry.ToAbsolute(displays, 0, 0));
        Assert.Equal((65535, 65535), DisplayGeometry.ToAbsolute(displays, 1919, 1079));

        var (midX, midY) = DisplayGeometry.ToAbsolute(displays, 959, 539);
        Assert.InRange(midX, 32000, 33000);
        Assert.InRange(midY, 32000, 33000);
    }

    [Fact]
    public void Absolute_coordinates_clamp_rather_than_overflowing()
    {
        var displays = new[] { Primary1080 };

        Assert.Equal((0, 0), DisplayGeometry.ToAbsolute(displays, -9999, -9999));
        Assert.Equal((65535, 65535), DisplayGeometry.ToAbsolute(displays, 99999, 99999));
    }

    [Fact]
    public void A_degenerate_display_set_does_not_divide_by_zero()
    {
        Assert.Equal((0, 0, 0, 0), DisplayGeometry.VirtualBounds([]));
        Assert.Equal((0, 0), DisplayGeometry.ToAbsolute([], 0, 0));
    }

    // ---- hit testing ----

    [Fact]
    public void A_point_resolves_to_the_display_containing_it()
    {
        var displays = new[] { Primary1080, LeftOfPrimary };

        Assert.Equal("DISPLAY1", DisplayGeometry.DisplayAt(displays, 100, 100)?.Id);
        Assert.Equal("DISPLAY2", DisplayGeometry.DisplayAt(displays, -100, 100)?.Id);

        // Exactly on the boundary belongs to the monitor that starts there.
        Assert.Equal("DISPLAY1", DisplayGeometry.DisplayAt(displays, 0, 0)?.Id);
        Assert.Equal("DISPLAY2", DisplayGeometry.DisplayAt(displays, -1, 0)?.Id);
    }

    [Fact]
    public void A_point_in_a_gap_belongs_to_no_display()
    {
        // Non-rectangular arrangements genuinely have gaps. Returning null rather than
        // guessing keeps that visible instead of silently snapping to a monitor.
        var offset = new DisplayInfo("DISPLAY2", @"\\.\DISPLAY2", 1920, 2000, 1280, 720, 1280, 720, false);
        var displays = new[] { Primary1080, offset };

        Assert.Null(DisplayGeometry.DisplayAt(displays, 100, 1500));
    }

    // ---- wire form ----

    [Fact]
    public void The_wire_form_reports_physical_pixels_and_the_scale()
    {
        // A controller needs the physical size to size its renderer, and the scale to
        // explain why the numbers differ from what the user's display settings say.
        var wire = Scaled125.ToWire();

        Assert.Equal("DISPLAY1", wire.Id);
        Assert.Equal(1920, wire.Width);   // physical, not the 1536 logical width
        Assert.Equal(1080, wire.Height);
        Assert.True(wire.Primary);
        Assert.Equal(1.25, wire.Scale);
    }
}
