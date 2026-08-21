using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Techee.Windows.Host;
using Xunit.Abstractions;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// Proof that an ordinary Windows application reacts to injected input.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RealInputTests"/> proves <c>SendInput</c> accepted the events. That is a
/// weaker claim than it sounds: the API can accept an event that never reaches an
/// application, and a return value of 1 is exactly what a wrongly-positioned or
/// wrongly-flagged event also produces. These tests close that gap by asserting on the
/// <c>WM_*</c> messages a real window actually received, at the client coordinates it
/// received them.
/// </para>
/// <para>
/// <b>Every test here is intrusive</b> — it creates a topmost window, takes the
/// foreground, and moves the pointer. All are gated behind <c>TECHEE_REAL_INPUT=1</c>
/// and restore the cursor afterwards.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class RealApplicationInputTests(ITestOutputHelper output)
{
    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static bool IntrusiveAllowed =>
        Environment.GetEnvironmentVariable("TECHEE_REAL_INPUT") == "1";

    /// <summary>Skips unless a real desktop is present and the tester opted in.</summary>
    private static void RequireDesktop(SendInputInjector injector)
    {
        Skip.IfNot(OnWindows, "SendInput is Windows-only");
        Skip.IfNot(IntrusiveAllowed, "drives the real desktop; set TECHEE_REAL_INPUT=1 to run");

        var reason = injector.UnavailableReason;
        Skip.If(reason is not null, $"input unavailable: {reason}");
    }

    private static IReadOnlyList<DisplayInfo> Displays()
    {
        try
        {
            return DesktopDuplicationSource.EnumerateDisplays();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Turns a point inside the target window into the normalized pair a controller sends.
    /// </summary>
    /// <remarks>
    /// This is the controller's half of the contract, done by hand: the protocol carries
    /// 0..1 against the captured surface, so a test that wants to click a specific pixel
    /// has to normalize it exactly as an Android controller would.
    /// </remarks>
    private static (double X, double Y) Normalize(DisplayInfo display, int screenX, int screenY) =>
        ((screenX - display.Left) / (double)Math.Max(1, display.LogicalWidth - 1),
         (screenY - display.Top) / (double)Math.Max(1, display.LogicalHeight - 1));

    private static DisplayInfo Primary(IReadOnlyList<DisplayInfo> displays) =>
        displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];

    // ---- clicks ----

    [SkippableFact]
    public void A_remote_click_is_delivered_to_a_real_window_as_a_real_button_message()
    {
        var displays = Displays();
        Skip.If(displays.Count == 0, "no displays");

        using var injector = new SendInputInjector(displays, output.WriteLine);
        RequireDesktop(injector);
        Skip.IfNot(GetCursorPos(out var before), "cursor position unavailable");

        using var target = new InputTargetWindow();
        Skip.IfNot(target.BringToFront(), "could not take the foreground");

        var display = Primary(displays);
        var (left, top, right, bottom) = target.CanvasScreenBounds;
        var centreX = (left + right) / 2;
        var centreY = (top + bottom) / 2;

        output.WriteLine($"target canvas at ({left},{top})-({right},{bottom})");
        output.WriteLine($"BEFORE: messages received = {target.Messages.Count}");

        try
        {
            // The full controller-to-desktop arithmetic: normalize as a controller would,
            // map back through DisplayGeometry as the handler does, then inject.
            var (nx, ny) = Normalize(display, centreX, centreY);
            var (vx, vy) = DisplayGeometry.ToVirtual(display, nx, ny);

            output.WriteLine($"aiming at screen ({centreX},{centreY}) via normalized ({nx:F4},{ny:F4}) -> virtual ({vx},{vy})");

            injector.ButtonDown(vx, vy, PointerButton.Left);
            injector.ButtonUp(vx, vy, PointerButton.Left);

            Assert.True(target.WaitFor(InputTargetWindow.WM_LBUTTONDOWN), "no WM_LBUTTONDOWN arrived");
            Assert.True(target.WaitFor(InputTargetWindow.WM_LBUTTONUP), "no WM_LBUTTONUP arrived");

            var down = target.Received(InputTargetWindow.WM_LBUTTONDOWN)[0];
            var up = target.Received(InputTargetWindow.WM_LBUTTONUP)[0];

            output.WriteLine($"AFTER: {string.Join(", ", target.Messages)}");

            // The canvas is the parent's client area above the edit control. Asserting
            // the click landed in its middle band proves it was positioned, not merely
            // delivered — a click stuck at the origin would also produce a message.
            Assert.InRange(down.ClientX, 150, 450);
            Assert.InRange(down.ClientY, 50, 250);
            Assert.Equal(down.ClientX, up.ClientX);
            Assert.Equal(down.ClientY, up.ClientY);
        }
        finally
        {
            injector.MoveTo(before.X, before.Y);
        }
    }

    [SkippableFact]
    public void A_right_click_arrives_as_a_right_button_message_not_a_left_one()
    {
        var displays = Displays();
        Skip.If(displays.Count == 0, "no displays");

        using var injector = new SendInputInjector(displays, output.WriteLine);
        RequireDesktop(injector);
        Skip.IfNot(GetCursorPos(out var before), "cursor position unavailable");

        using var target = new InputTargetWindow();
        Skip.IfNot(target.BringToFront(), "could not take the foreground");

        var display = Primary(displays);
        var (left, top, right, bottom) = target.CanvasScreenBounds;

        try
        {
            var (nx, ny) = Normalize(display, (left + right) / 2, (top + bottom) / 2);
            var (vx, vy) = DisplayGeometry.ToVirtual(display, nx, ny);

            injector.ButtonDown(vx, vy, PointerButton.Right);
            injector.ButtonUp(vx, vy, PointerButton.Right);

            Assert.True(target.WaitFor(InputTargetWindow.WM_RBUTTONDOWN), "no WM_RBUTTONDOWN arrived");
            Assert.Empty(target.Received(InputTargetWindow.WM_LBUTTONDOWN));

            output.WriteLine($"received: {string.Join(", ", target.Messages)}");
        }
        finally
        {
            injector.MoveTo(before.X, before.Y);
        }
    }

    // ---- coordinate mapping across the screen ----

    [SkippableFact]
    public void Normalized_coordinates_land_in_the_expected_region_of_a_real_window()
    {
        var displays = Displays();
        Skip.If(displays.Count == 0, "no displays");

        using var injector = new SendInputInjector(displays, output.WriteLine);
        RequireDesktop(injector);
        Skip.IfNot(GetCursorPos(out var before), "cursor position unavailable");

        using var target = new InputTargetWindow();
        Skip.IfNot(target.BringToFront(), "could not take the foreground");

        var display = Primary(displays);

        // The parent's own client area, excluding the child edit control: a click over
        // the child is delivered to the child and never reaches this window's procedure.
        var (left, top, right, bottom) = target.CanvasScreenBounds;
        output.WriteLine($"canvas region ({left},{top})-({right},{bottom})");

        var width = right - left;
        var height = bottom - top;
        var probes = new[]
        {
            ("top-left", left + width / 5, top + height / 5),
            ("centre", left + width / 2, top + height / 2),
            ("bottom-right", left + 4 * width / 5, top + 4 * height / 5),
        };

        try
        {
            var landed = new List<(string Name, int X, int Y)>();

            foreach (var (name, sx, sy) in probes)
            {
                target.ClearMessages();

                var (nx, ny) = Normalize(display, sx, sy);
                var (vx, vy) = DisplayGeometry.ToVirtual(display, nx, ny);
                injector.ButtonDown(vx, vy, PointerButton.Left);
                injector.ButtonUp(vx, vy, PointerButton.Left);

                Assert.True(target.WaitFor(InputTargetWindow.WM_LBUTTONDOWN), $"{name}: no click arrived");

                var down = target.Received(InputTargetWindow.WM_LBUTTONDOWN)[0];
                landed.Add((name, down.ClientX, down.ClientY));
                output.WriteLine($"{name,-13} aimed screen ({sx},{sy}) -> client ({down.ClientX},{down.ClientY})");
            }

            // Ordering is the assertion that actually catches a mapping bug: an inverted
            // axis or a bad origin still produces three clicks, but not three in the
            // right order along both axes.
            Assert.True(landed[0].X < landed[1].X, "top-left did not land left of centre");
            Assert.True(landed[1].X < landed[2].X, "centre did not land left of bottom-right");
            Assert.True(landed[0].Y < landed[1].Y, "top-left did not land above centre");
            Assert.True(landed[1].Y < landed[2].Y, "centre did not land above bottom-right");
        }
        finally
        {
            injector.MoveTo(before.X, before.Y);
        }
    }

    // ---- drag ----

    [SkippableFact]
    public void A_drag_produces_a_press_then_movement_then_a_release_in_that_order()
    {
        var displays = Displays();
        Skip.If(displays.Count == 0, "no displays");

        using var injector = new SendInputInjector(displays, output.WriteLine);
        RequireDesktop(injector);
        Skip.IfNot(GetCursorPos(out var before), "cursor position unavailable");

        using var target = new InputTargetWindow();
        Skip.IfNot(target.BringToFront(), "could not take the foreground");

        var display = Primary(displays);
        var (left, top, right, bottom) = target.CanvasScreenBounds;
        var width = right - left;
        var height = bottom - top;

        try
        {
            var (nx1, ny1) = Normalize(display, left + width / 4, top + height / 2);
            var (nx2, ny2) = Normalize(display, left + 3 * width / 4, top + height / 2);
            var (fromX, fromY) = DisplayGeometry.ToVirtual(display, nx1, ny1);
            var (toX, toY) = DisplayGeometry.ToVirtual(display, nx2, ny2);

            // The same shape PeerControlHandler.Swipe emits: move, press, interpolated
            // moves, release. Reproduced here rather than driven through the handler so
            // this file stays a test of the injector against a real window.
            injector.MoveTo(fromX, fromY);
            injector.ButtonDown(fromX, fromY, PointerButton.Left);

            const int steps = 10;
            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps;
                injector.MoveTo(
                    fromX + (int)Math.Round((toX - fromX) * t),
                    fromY + (int)Math.Round((toY - fromY) * t));
                Thread.Sleep(8);
            }

            injector.ButtonUp(toX, toY, PointerButton.Left);

            Assert.True(target.WaitFor(InputTargetWindow.WM_LBUTTONUP), "the drag never released");

            var all = target.Messages;
            var downIndex = all.ToList().FindIndex(m => m.Message == InputTargetWindow.WM_LBUTTONDOWN);
            var upIndex = all.ToList().FindIndex(m => m.Message == InputTargetWindow.WM_LBUTTONUP);

            Assert.True(downIndex >= 0, "the drag never pressed");
            Assert.True(upIndex > downIndex, "the release did not follow the press");

            // Movement between the two is what distinguishes a drag from a click. An
            // application that sees no intermediate move treats it as a click, which is
            // why a teleporting drag fails to select text or start a drag-and-drop.
            var movesDuringDrag = all
                .Skip(downIndex + 1).Take(upIndex - downIndex - 1)
                .Count(m => m.Message == InputTargetWindow.WM_MOUSEMOVE);

            output.WriteLine($"press at index {downIndex}, release at {upIndex}, " +
                             $"{movesDuringDrag} move(s) between");

            Assert.True(movesDuringDrag >= 3, $"only {movesDuringDrag} moves during the drag");

            // And it travelled left to right.
            var moves = all
                .Skip(downIndex).Take(upIndex - downIndex + 1)
                .Where(m => m.Message == InputTargetWindow.WM_MOUSEMOVE)
                .ToList();

            Assert.True(moves[^1].ClientX > moves[0].ClientX, "the drag did not travel rightwards");
        }
        finally
        {
            injector.MoveTo(before.X, before.Y);
        }
    }

    // ---- keyboard against a real Windows control ----

    [SkippableFact]
    public void Typed_text_appears_in_a_real_edit_control()
    {
        var displays = Displays();
        Skip.If(displays.Count == 0, "no displays");

        using var injector = new SendInputInjector(displays, output.WriteLine);
        RequireDesktop(injector);
        Skip.IfNot(GetCursorPos(out var before), "cursor position unavailable");

        using var target = new InputTargetWindow();
        Skip.IfNot(target.BringToFront(), "could not take the foreground");
        Skip.IfNot(target.IsForeground, "another window holds the foreground");

        var display = Primary(displays);

        try
        {
            output.WriteLine($"BEFORE: edit control contains '{target.EditText}'");

            // Click into the field first, exactly as a remote operator would. Focus
            // cannot be handed to the control with SetFocus from this thread — focus is
            // per input-queue, and the window belongs to the pump thread — so the click
            // is both more realistic and the only thing that actually works.
            var (eLeft, eTop, eRight, eBottom) = target.EditScreenBounds;
            var (nx, ny) = Normalize(display, (eLeft + eRight) / 2, (eTop + eBottom) / 2);
            var (vx, vy) = DisplayGeometry.ToVirtual(display, nx, ny);

            injector.ButtonDown(vx, vy, PointerButton.Left);
            injector.ButtonUp(vx, vy, PointerButton.Left);
            Thread.Sleep(150);

            const string expected = "techee w4 héllo";
            injector.TypeText(expected);

            var deadline = Environment.TickCount64 + 3000;
            while (target.EditText.Length < expected.Length && Environment.TickCount64 < deadline)
                Thread.Sleep(25);

            output.WriteLine($"AFTER:  edit control contains '{target.EditText}'");

            // A genuine comctl32 EDIT control, the same one Win32 applications have
            // always used, reporting its contents through WM_GETTEXT. Unicode included,
            // because KEYEVENTF_UNICODE is the part that a scan-code path would break.
            Assert.Equal(expected, target.EditText);
        }
        finally
        {
            injector.MoveTo(before.X, before.Y);
        }
    }

    [SkippableFact]
    public void A_key_press_and_release_arrive_as_distinct_messages()
    {
        var displays = Displays();
        Skip.If(displays.Count == 0, "no displays");

        using var injector = new SendInputInjector(displays, output.WriteLine);
        RequireDesktop(injector);

        using var target = new InputTargetWindow();
        Skip.IfNot(target.BringToFront(), "could not take the foreground");
        Skip.IfNot(target.IsForeground, "another window holds the foreground");

        // F13: a real key with a real scan code that no application binds and that types
        // nothing. Distinct down and up messages are the property under test.
        var f13 = KeyMap.Resolve("F13")!.Value;

        injector.KeyDown(f13);
        Thread.Sleep(50);
        injector.KeyUp(f13);

        Assert.True(target.WaitFor(InputTargetWindow.WM_KEYDOWN), "no WM_KEYDOWN arrived");
        Assert.True(target.WaitFor(InputTargetWindow.WM_KEYUP), "no WM_KEYUP arrived");

        var down = target.Received(InputTargetWindow.WM_KEYDOWN);
        var up = target.Received(InputTargetWindow.WM_KEYUP);

        output.WriteLine($"WM_KEYDOWN vk=0x{down[0].WParam:X2}, WM_KEYUP vk=0x{up[0].WParam:X2}");

        // Windows resolves the scan code back to a virtual key. That it comes out as
        // VK_F13 (0x7C) is the round-trip proof that the scan-code table is right.
        Assert.Equal((nuint)0x7C, down[0].WParam);
        Assert.Equal((nuint)0x7C, up[0].WParam);
    }

    [SkippableFact]
    public void An_extended_key_round_trips_to_the_right_virtual_key()
    {
        var displays = Displays();
        Skip.If(displays.Count == 0, "no displays");

        using var injector = new SendInputInjector(displays, output.WriteLine);
        RequireDesktop(injector);

        using var target = new InputTargetWindow();
        Skip.IfNot(target.BringToFront(), "could not take the foreground");
        Skip.IfNot(target.IsForeground, "another window holds the foreground");

        // ArrowUp and Numpad8 share scan code 0x48 and differ only by the E0 prefix.
        // If the extended flag were dropped, this would arrive as VK_NUMPAD8 (0x68)
        // instead of VK_UP (0x26) — the classic symptom of the bug.
        var arrowUp = KeyMap.Resolve("ArrowUp")!.Value;
        Assert.True(arrowUp.Extended);

        injector.KeyDown(arrowUp);
        Thread.Sleep(50);
        injector.KeyUp(arrowUp);

        Assert.True(target.WaitFor(InputTargetWindow.WM_KEYDOWN), "no WM_KEYDOWN arrived");

        var down = target.Received(InputTargetWindow.WM_KEYDOWN)[0];
        output.WriteLine($"ArrowUp arrived as vk=0x{down.WParam:X2} (VK_UP is 0x26, VK_NUMPAD8 is 0x68)");

        Assert.Equal((nuint)0x26, down.WParam);
    }

    // ---- stuck-key protection, against the real OS ----

    [SkippableFact]
    public void Releasing_held_input_clears_a_modifier_the_session_left_down()
    {
        var displays = Displays();
        Skip.If(displays.Count == 0, "no displays");

        using var injector = new SendInputInjector(displays, output.WriteLine);
        RequireDesktop(injector);

        using var target = new InputTargetWindow();
        Skip.IfNot(target.BringToFront(), "could not take the foreground");
        Skip.IfNot(target.IsForeground, "another window holds the foreground");

        var shift = KeyMap.Resolve("ShiftLeft")!.Value;

        injector.KeyDown(shift);
        Thread.Sleep(50);

        Assert.Equal(1, injector.PressedCount);
        Assert.True(IsKeyPhysicallyDown(0xA0), "Windows does not report left Shift as down");

        // What teardown does. The OS must agree the key came back up.
        injector.ReleaseAllPressed();
        Thread.Sleep(100);

        Assert.Equal(0, injector.PressedCount);
        Assert.False(IsKeyPhysicallyDown(0xA0), "left Shift is still down after the release");

        output.WriteLine("left Shift was held, then released by ReleaseAllPressed; OS agrees it is up");
    }

    /// <summary>Asks Windows whether a virtual key is currently down.</summary>
    /// <remarks>
    /// The high bit of <c>GetAsyncKeyState</c> is the physical state, which is the only
    /// authority on whether a stuck key is actually stuck.
    /// </remarks>
    private static bool IsKeyPhysicallyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);
}
