using System.Runtime.InteropServices;
using Techee.Windows.Host;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// The interop details of <see cref="SendInputInjector"/> that fail silently.
/// </summary>
/// <remarks>
/// Both things pinned here have the same failure mode: the managed code compiles, every
/// other test passes, and the product injects nothing or injects nonsense. Neither is
/// observable from the outside without a machine and a person watching the screen, which
/// is exactly why they are worth a unit test.
/// </remarks>
public class SendInputInjectorTests
{
    // ---- the native struct ----

    [Fact]
    public void The_native_input_struct_is_the_size_winuser_h_declares()
    {
        // SendInput validates cbSize and rejects the entire array if it disagrees. A
        // mismatch injects nothing and reports no error worth the name.
        var expected = IntPtr.Size == 8 ? 40 : 28;
        Assert.Equal(expected, SendInputInjector.NativeInputSize);
    }

    [Fact]
    public void The_native_input_struct_is_blittable_so_LibraryImport_can_pin_it()
    {
        // If it were not, the source generator would marshal a copy and the layout
        // assertion above would be testing the wrong bytes.
        var array = new int[1];
        var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
        handle.Free();

        Assert.Equal(0, SendInputInjector.NativeInputSize % IntPtr.Size);
    }

    // ---- wheel conversion ----

    [Fact]
    public void One_notch_is_one_WHEEL_DELTA()
    {
        Assert.Equal(120, SendInputInjector.Notches(1.0));
        Assert.Equal(-120, SendInputInjector.Notches(-1.0));
        Assert.Equal(360, SendInputInjector.Notches(3.0));
    }

    [Fact]
    public void A_tiny_delta_still_scrolls_rather_than_being_swallowed()
    {
        // A trackpad flick that does nothing reads as a broken session.
        Assert.Equal(1, SendInputInjector.Notches(0.0001));
        Assert.Equal(-1, SendInputInjector.Notches(-0.0001));
    }

    [Fact]
    public void Zero_and_non_finite_deltas_produce_no_event()
    {
        Assert.Equal(0, SendInputInjector.Notches(0));
        Assert.Equal(0, SendInputInjector.Notches(double.NaN));
        Assert.Equal(0, SendInputInjector.Notches(double.PositiveInfinity));
        Assert.Equal(0, SendInputInjector.Notches(double.NegativeInfinity));
    }

    [Theory]
    [InlineData(1e300)]
    [InlineData(-1e300)]
    [InlineData(2.5e9)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    [InlineData(int.MaxValue)]
    public void A_hostile_wheel_delta_cannot_overflow_the_conversion(double hostile)
    {
        // The codec only requires dx/dy to be finite, so 1e300 is a legal frame. Casting
        // that to int is undefined in C# and yields int.MinValue in practice, which would
        // reach SendInput as roughly seventeen million notches in the wrong direction.
        var delta = SendInputInjector.Notches(hostile);

        Assert.True(
            Math.Abs(delta) <= 120 * 120,
            $"a delta of {hostile:E1} produced {delta}, which is outside the clamp");

        // And crucially the sign survives: an overflow to int.MinValue would invert a
        // large positive scroll into a large negative one.
        Assert.True(hostile > 0 ? delta > 0 : delta < 0, $"{hostile:E1} produced {delta}");
    }

    [Fact]
    public void The_arithmetic_this_replaced_really_did_overflow()
    {
        // Kept as a witness so the clamp above is not mistaken for defensive noise.
        // This is verbatim what the first W4 implementation computed: multiply first,
        // then cast.
        const double hostile = 1e300;

        var delta = hostile * 120;
        var oldResult = (int)Math.Round(Math.Abs(delta), MidpointRounding.AwayFromZero);

        // .NET Core 3.0 and later saturate an out-of-range double→int conversion rather
        // than leaving it undefined, so this is int.MaxValue and not the sign-flipped
        // int.MinValue older runtimes would produce. Saturating is the kinder failure,
        // but it is still roughly seventeen million notches handed to SendInput as
        // mouseData in response to a frame the peer fully controls.
        Assert.Equal(int.MaxValue, oldResult);
        Assert.True(oldResult / 120 > 17_000_000);

        // The current implementation is bounded.
        Assert.Equal(120 * 120, SendInputInjector.Notches(hostile));
    }

    [Fact]
    public void The_clamp_does_not_alter_any_realistic_gesture()
    {
        // A fast flick is a handful of notches. The clamp must be invisible to real use,
        // or it is a bug rather than a guard.
        foreach (var notches in new[] { 0.5, 1.0, 3.0, 10.0, -10.0, 100.0 })
            Assert.Equal((int)Math.Round(notches * 120), SendInputInjector.Notches(notches));
    }

    // ---- pressed-state tracking, without touching the desktop ----

    [Fact]
    public void A_fresh_injector_holds_nothing()
    {
        using var injector = new SendInputInjector([]);
        Assert.Equal(0, injector.PressedCount);
    }

    [Fact]
    public void Releasing_nothing_is_safe_and_silent()
    {
        // Called on every teardown, including the overwhelming majority where no key was
        // held. It must not fail, and must not inject a stray event.
        using var injector = new SendInputInjector([]);

        injector.ReleaseAllPressed();
        injector.ReleaseAllPressed();

        Assert.Equal(0, injector.PressedCount);
        Assert.Equal(0, injector.Injected);
    }

    [Fact]
    public void Disposal_is_idempotent()
    {
        var injector = new SendInputInjector([]);

        injector.Dispose();
        injector.Dispose();

        Assert.Equal(0, injector.PressedCount);
    }

    [Fact]
    public void An_empty_display_set_does_not_throw_on_injection()
    {
        // Reachable when capture has not started or every monitor was unplugged. The
        // coordinates are meaningless, but the call must not take the session down.
        using var injector = new SendInputInjector([]);

        injector.MoveTo(0, 0);
        injector.Wheel(0, 0, 0, 1);

        Assert.Equal(0, injector.PressedCount);
    }
}
