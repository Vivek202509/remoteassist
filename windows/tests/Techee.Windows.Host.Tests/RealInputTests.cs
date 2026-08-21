using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Techee.Windows.Host;
using Xunit.Abstractions;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// The parts of injection only a real machine can settle.
/// </summary>
/// <remarks>
/// <para>
/// Everything about <i>which</i> events Techee injects is covered without hardware, by
/// <see cref="KeyMapTests"/> and the handler suite. What those cannot prove is that the
/// interop declarations are right: an <c>INPUT</c> whose size or layout is wrong makes
/// <c>SendInput</c> return zero and inject nothing, and the managed code looks perfect
/// while the product does not work at all. That is the classic P/Invoke bug and it needs
/// the real API.
/// </para>
/// <para>
/// <b>These tests do not disturb the machine they run on.</b> The mouse is moved to the
/// position it is already at, which exercises the whole path — struct marshalling,
/// <c>cbSize</c>, absolute coordinate conversion, the call itself — with no visible
/// effect. Anything genuinely intrusive is behind <c>TECHEE_REAL_INPUT=1</c>, because a
/// test suite that types into whatever window happens to have focus is a test suite
/// nobody will run twice.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class RealInputTests(ITestOutputHelper output)
{
    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Whether the tester has opted in to input that is actually visible.</summary>
    private static bool IntrusiveAllowed =>
        Environment.GetEnvironmentVariable("TECHEE_REAL_INPUT") == "1";

    private static IReadOnlyList<DisplayInfo> Displays()
    {
        // The real layout, so absolute conversion is exercised against the machine's own
        // virtual desktop rather than a fabricated one.
        try
        {
            return DesktopDuplicationSource.EnumerateDisplays();
        }
        catch (Exception)
        {
            return [];
        }
    }

    // ---- the interop itself ----

    [SkippableFact]
    public void SendInput_accepts_a_move_to_the_cursors_current_position()
    {
        Skip.IfNot(OnWindows, "SendInput is Windows-only");

        var displays = Displays();
        Skip.If(displays.Count == 0, "no displays to normalise against");

        Skip.IfNot(GetCursorPos(out var before), "cursor position unavailable");

        using var injector = new SendInputInjector(displays, output.WriteLine);

        // A CI runner is often not on an interactive desktop, and input genuinely cannot
        // be delivered there. Asserting success would turn a correct refusal into a red
        // build; the interop is still proven on any machine that has a desktop.
        var unavailable = injector.UnavailableReason;
        Skip.If(unavailable is not null, $"input unavailable: {unavailable}");

        // Moving to where the cursor already is proves the whole path without the
        // tester noticing. If the INPUT layout or cbSize were wrong this would inject
        // nothing and Failed would climb.
        injector.MoveTo(before.X, before.Y);

        output.WriteLine($"cursor at ({before.X}, {before.Y}); {injector}");

        Assert.Equal(0, injector.Failed);
        Assert.True(injector.Injected > 0, "SendInput accepted no events");
    }

    [SkippableFact]
    public void An_absolute_move_lands_within_a_pixel_of_where_it_was_aimed()
    {
        Skip.IfNot(OnWindows, "SendInput is Windows-only");
        Skip.IfNot(IntrusiveAllowed, "moves the real cursor; set TECHEE_REAL_INPUT=1 to run");

        var displays = Displays();
        Skip.If(displays.Count == 0, "no displays to normalise against");
        Skip.IfNot(GetCursorPos(out var before), "cursor position unavailable");

        using var injector = new SendInputInjector(displays, output.WriteLine);

        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
        var (targetX, targetY) = DisplayGeometry.ToVirtual(primary, 0.5, 0.5);

        try
        {
            injector.MoveTo(targetX, targetY);
            Thread.Sleep(50);

            Assert.True(GetCursorPos(out var after), "cursor position unavailable after the move");
            output.WriteLine($"aimed at ({targetX}, {targetY}), landed at ({after.X}, {after.Y})");

            // The 0..65535 conversion is lossy on any desktop narrower than 65536
            // pixels, so exact equality is the wrong assertion. More than a pixel out
            // means the normalisation is wrong, which is the bug worth catching.
            Assert.InRange(Math.Abs(after.X - targetX), 0, 1);
            Assert.InRange(Math.Abs(after.Y - targetY), 0, 1);
        }
        finally
        {
            injector.MoveTo(before.X, before.Y);
        }
    }

    [SkippableFact]
    public void A_multi_monitor_desktop_normalises_against_the_union_not_the_primary()
    {
        Skip.IfNot(OnWindows, "SendInput is Windows-only");

        var displays = Displays();
        Skip.If(displays.Count < 2, "needs a second monitor");

        var bounds = DisplayGeometry.VirtualBounds(displays);
        output.WriteLine($"virtual desktop: {bounds}");

        // A conversion written against the primary monitor cannot reach a point on the
        // second one, which is the failure this catches. It works perfectly on a
        // single-monitor machine, which is why it survives so often.
        var secondary = displays.First(d => !d.IsPrimary);
        var (vx, vy) = DisplayGeometry.ToVirtual(secondary, 0.5, 0.5);
        var (ax, ay) = DisplayGeometry.ToAbsolute(displays, vx, vy);

        Assert.InRange(ax, 0, 65535);
        Assert.InRange(ay, 0, 65535);

        // And the point must round-trip back onto the secondary display rather than
        // being clamped onto the primary's edge.
        var backX = bounds.Left + (int)Math.Round(ax * (bounds.Width - 1) / 65535.0);
        var backY = bounds.Top + (int)Math.Round(ay * (bounds.Height - 1) / 65535.0);

        Assert.Equal(secondary.Id, DisplayGeometry.DisplayAt(displays, backX, backY)?.Id);
    }

    // ---- the secure desktop probe ----

    [SkippableFact]
    public void An_ordinary_interactive_session_reports_input_as_available()
    {
        Skip.IfNot(OnWindows, "the desktop API is Windows-only");

        using var injector = new SendInputInjector(Displays(), output.WriteLine);

        var reason = injector.UnavailableReason;
        output.WriteLine($"availability: {reason ?? "available"}");

        // A false positive here would refuse input that would in fact have worked, and
        // would do it silently for the whole session. Running under a service or over a
        // disconnected RDP session legitimately reports unavailable, so this is skipped
        // rather than failed in that case.
        Skip.If(reason is not null, $"not on an interactive desktop: {reason}");

        Assert.True(injector.IsAvailable);
        Assert.Null(injector.UnavailableReason);
    }

    [SkippableFact]
    public void The_probe_is_cheap_enough_to_run_per_command()
    {
        Skip.IfNot(OnWindows, "the desktop API is Windows-only");

        using var injector = new SendInputInjector(Displays(), output.WriteLine);

        // Warmed up first: the first call pays for JIT and for loading user32's desktop
        // API, which is a one-off cost the session does not repeat and which is large
        // enough on a cold runner to swamp the measurement.
        for (var i = 0; i < 10; i++) _ = injector.UnavailableReason;

        // Checked before every injected command, so at the pointer.move ceiling this
        // runs 250 times a second. Caching it would be wrong — a UAC prompt appears
        // mid-session — so it has to be affordable instead.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 250; i++) _ = injector.UnavailableReason;
        sw.Stop();

        output.WriteLine($"250 probes in {sw.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(
            sw.Elapsed.TotalMilliseconds < 250,
            $"a second's worth of probes took {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    // ---- keyboard, opt-in only ----

    [SkippableFact]
    public void A_keystroke_is_accepted_by_the_OS()
    {
        Skip.IfNot(OnWindows, "SendInput is Windows-only");
        Skip.IfNot(IntrusiveAllowed, "types into the focused window; set TECHEE_REAL_INPUT=1 to run");

        using var injector = new SendInputInjector(Displays(), output.WriteLine);

        // Shift on its own: it reaches the focused window as a real keystroke but types
        // nothing and triggers no shortcut, so an opted-in tester loses nothing.
        var shift = KeyMap.Resolve("ShiftLeft")!.Value;

        injector.KeyDown(shift);
        injector.KeyUp(shift);

        output.WriteLine(injector.ToString());
        Assert.Equal(0, injector.Failed);
        Assert.True(injector.Injected >= 2);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);
}
