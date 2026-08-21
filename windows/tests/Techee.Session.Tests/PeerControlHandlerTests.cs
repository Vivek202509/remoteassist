using System.Diagnostics;
using Techee.Protocol;
using Techee.Session;
using Techee.Windows.Host;
using Techee.Windows.Host.Tests;
using Xunit.Abstractions;

namespace Techee.Session.Tests;

/// <summary>
/// The gate between an authenticated control frame and the local desktop.
/// </summary>
/// <remarks>
/// Everything here runs against a recording injector, so the assertions are about what
/// <i>would</i> be injected and in what order. That is the part worth testing: whether
/// <c>SendInput</c> works is a question for the machine, but whether a view-only grant
/// can move the mouse is a question for this class, and it must be answerable without
/// hardware.
/// </remarks>
public class PeerControlHandlerTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>A 1920×1080 panel running at 125%, so DPI errors show up as wrong numbers.</summary>
    /// <remarks>
    /// A 1:1 display would let a missing scale division pass unnoticed — the bug
    /// <see cref="DisplayGeometry"/> exists to prevent is invisible at 100%.
    /// </remarks>
    private static readonly DisplayInfo Scaled125 =
        new("DISPLAY1", @"\\.\DISPLAY1", 0, 0, 1536, 864, 1920, 1080, IsPrimary: true);

    private readonly List<IDisposable> _tracked = [];
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public void Dispose()
    {
        foreach (var d in _tracked) d.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Grant GrantWith(params string[] permissions) => new()
    {
        GrantId = "g1",
        ControllerId = "c1",
        PermissionTokens = permissions,
    };

    private (PeerControlHandler Handler, FakeInputInjector Injector) Build(
        Grant? grant = null,
        DisplayInfo? display = null,
        ControlRateLimiter? limiter = null)
    {
        var injector = new FakeInputInjector();
        var effectiveGrant = grant ?? GrantWith("screen.view", "input.control");

        var handler = new PeerControlHandler(
            injector,
            () => effectiveGrant,
            () => display ?? Scaled125,
            () => _now,
            limiter,
            log: output.WriteLine);

        _tracked.Add(handler);
        return (handler, injector);
    }

    /// <summary>
    /// Waits for the worker to catch up.
    /// </summary>
    /// <remarks>
    /// Polling rather than a fixed sleep: the handler executes on its own thread by
    /// design, and a sleep long enough to be reliable on a loaded CI runner would make
    /// every test in this file slow.
    /// </remarks>
    private static void Drain(PeerControlHandler handler, long processed, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (handler.Processed < processed && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(1);

        // Processed rather than Executed, so a command that threw still counts as
        // drained — otherwise the test for a throwing injector waits for something that
        // by definition never happens.
        Assert.True(
            handler.Processed >= processed,
            $"worker finished {handler.Processed} of {processed} commands within {timeoutMs} ms");
    }

    /// <summary>Confirms nothing arrives, which needs a wait rather than an immediate look.</summary>
    private static void DrainNothing(FakeInputInjector injector)
    {
        Thread.Sleep(50);
        Assert.Empty(injector.Events);
    }

    // ---- coordinates ----

    [Fact]
    public void A_tap_becomes_a_press_and_a_release_at_the_same_mapped_point()
    {
        var (handler, injector) = Build();

        handler.Handle(new Control.PointerTap(0.5, 0.5));
        Drain(handler, 1);

        // 0.5 of 1535 logical pixels = 768; 0.5 of 863 = 432. Both are the DPI-divided
        // logical figures, not the 1920×1080 physical ones the capture produces.
        Assert.Collection(
            injector.Events,
            e => Assert.Equal(new InjectedEvent.Down(768, 432, PointerButton.Left), e),
            e => Assert.Equal(new InjectedEvent.Up(768, 432, PointerButton.Left), e));
    }

    [Fact]
    public void Coordinates_map_against_the_captured_display_not_the_primary()
    {
        // A monitor to the left of the primary has negative virtual-desktop coordinates.
        // Mapping against the primary instead would put every click on the wrong screen.
        var left = new DisplayInfo("DISPLAY2", @"\\.\DISPLAY2", -1920, 0, 1920, 1080, 1920, 1080, false);
        var (handler, injector) = Build(display: left);

        handler.Handle(new Control.PointerMove(0.0, 0.0));
        Drain(handler, 1);

        Assert.Equal(new InjectedEvent.Move(-1920, 0), injector.Events[0]);
    }

    [Fact]
    public void The_far_edge_lands_on_the_last_addressable_pixel()
    {
        var (handler, injector) = Build();

        handler.Handle(new Control.PointerMove(1.0, 1.0));
        Drain(handler, 1);

        // 1535 and 863, not 1536 and 864: one past the edge would spill onto the
        // neighbouring monitor.
        Assert.Equal(new InjectedEvent.Move(1535, 863), injector.Events[0]);
    }

    [Fact]
    public void With_no_captured_display_nothing_is_injected()
    {
        // Capture torn down, or not started. Falling back to the primary monitor would
        // put input on a screen the operator cannot see. Built directly, because Build()
        // substitutes the default display for a null one.
        var injector = new FakeInputInjector();
        using var handler = new PeerControlHandler(
            injector,
            () => GrantWith("input.control"),
            () => null,
            () => _now,
            log: output.WriteLine);

        handler.Handle(new Control.PointerTap(0.5, 0.5));
        Drain(handler, 1);

        DrainNothing(injector);
    }

    // ---- authorization ----

    [Fact]
    public void A_view_only_grant_cannot_move_the_mouse()
    {
        var (handler, injector) = Build(GrantWith("screen.view"));

        handler.Handle(new Control.PointerTap(0.5, 0.5));
        handler.Handle(new Control.KeyText("hello"));

        DrainNothing(injector);
        Assert.Equal(2, handler.Refused);
        Assert.Equal(0, handler.Accepted);
    }

    [Fact]
    public void A_grant_revoked_mid_session_stops_input_on_the_very_next_frame()
    {
        // The grant is a callback, not a captured value. This is the whole reason: a
        // controller revoked while the session is live must lose control immediately,
        // not at the next reconnect.
        Grant? grant = GrantWith("input.control");
        var injector = new FakeInputInjector();

        using var handler = new PeerControlHandler(
            injector,
            () => grant,
            () => Scaled125,
            () => _now,
            log: output.WriteLine);

        handler.Handle(new Control.PointerTap(0.5, 0.5));
        Drain(handler, 1);
        Assert.Equal(2, injector.Events.Count);

        grant = null;

        handler.Handle(new Control.PointerTap(0.5, 0.5));
        Thread.Sleep(50);

        Assert.Equal(2, injector.Events.Count);
        Assert.Equal(1, handler.Refused);
    }

    [Fact]
    public void An_expired_grant_authorises_nothing()
    {
        var expired = new Grant
        {
            GrantId = "g1",
            ControllerId = "c1",
            PermissionTokens = ["input.control"],
            ExpiresAt = _now.ToUnixTimeMilliseconds() - 1,
        };

        var (handler, injector) = Build(expired);

        handler.Handle(new Control.PointerTap(0.5, 0.5));

        DrainNothing(injector);
        Assert.Equal(1, handler.Refused);
    }

    [Fact]
    public void A_grant_with_no_permissions_at_all_is_refused_rather_than_defaulted()
    {
        var (handler, injector) = Build(new Grant { GrantId = "g1", ControllerId = "c1" });

        handler.Handle(new Control.PointerTap(0.5, 0.5));

        DrainNothing(injector);
        Assert.Equal(1, handler.Refused);
    }

    [Fact]
    public void A_legacy_CONTROL_scope_grant_still_authorises_input()
    {
        // Every grant created by the shipped Android app carries `scope`, not
        // `permissions`. If the widening did not apply here, W4 would work only for
        // grants created after W4 shipped.
        var legacy = new Grant { GrantId = "g1", ControllerId = "c1", LegacyScope = ["CONTROL"] };
        var (handler, injector) = Build(legacy);

        handler.Handle(new Control.PointerTap(0.5, 0.5));
        Drain(handler, 1);

        Assert.Equal(2, injector.Events.Count);
    }

    [Fact]
    public void A_legacy_VIEW_scope_grant_does_not()
    {
        var legacy = new Grant { GrantId = "g1", ControllerId = "c1", LegacyScope = ["VIEW"] };
        var (handler, injector) = Build(legacy);

        handler.Handle(new Control.PointerTap(0.5, 0.5));

        DrainNothing(injector);
        Assert.Equal(1, handler.Refused);
    }

    [Fact]
    public void Commands_this_handler_does_not_own_are_ignored_rather_than_refused()
    {
        // Clipboard, display and power commands share the channel and belong to other
        // milestones. Counting them as refusals would make the diagnostics lie about
        // what the controller was denied.
        var (handler, injector) = Build();

        handler.Handle(new Control.ClipboardRequest());
        handler.Handle(new Control.SystemAction("system.restart"));
        handler.Handle(new Control.DisplayList());
        handler.Handle(new Control.HostCallState("IDLE"));

        DrainNothing(injector);
        Assert.Equal(0, handler.Refused);
        Assert.Equal(0, handler.Accepted);
    }

    // ---- buttons and wheel ----

    [Fact]
    public void Each_protocol_button_maps_to_its_own_physical_button()
    {
        var (handler, injector) = Build();

        handler.Handle(new Control.PointerDown(0, 0, "right"));
        handler.Handle(new Control.PointerDown(0, 0, "middle"));
        handler.Handle(new Control.PointerDown(0, 0, "x1"));
        handler.Handle(new Control.PointerUp(0, 0, "x2"));
        Drain(handler, 4);

        Assert.Collection(
            injector.Events,
            e => Assert.Equal(PointerButton.Right, Assert.IsType<InjectedEvent.Down>(e).Button),
            e => Assert.Equal(PointerButton.Middle, Assert.IsType<InjectedEvent.Down>(e).Button),
            e => Assert.Equal(PointerButton.X1, Assert.IsType<InjectedEvent.Down>(e).Button),
            e => Assert.Equal(PointerButton.X2, Assert.IsType<InjectedEvent.Up>(e).Button));
    }

    [Fact]
    public void Wheel_notches_reach_the_injector_unchanged()
    {
        // Notches are the protocol's unit. Converting them to pixels here would apply
        // the conversion twice, since the injector does it too.
        var (handler, injector) = Build();

        handler.Handle(new Control.PointerWheel(0.5, 0.5, -1.0, 3.0));
        Drain(handler, 1);

        var scroll = Assert.IsType<InjectedEvent.Scroll>(injector.Events[0]);
        Assert.Equal(-1.0, scroll.Dx);
        Assert.Equal(3.0, scroll.Dy);
    }

    // ---- keyboard ----

    [Fact]
    public void Modifiers_go_down_before_the_key_and_up_after_it_in_reverse()
    {
        var (handler, injector) = Build();

        handler.Handle(new Control.KeyDown("KeyC", ["ControlLeft", "ShiftLeft"]));
        Drain(handler, 1);

        Assert.Collection(
            injector.Events,
            e => Assert.Equal(KeyMap.Resolve("ControlLeft"), Assert.IsType<InjectedEvent.KeyPress>(e).Stroke),
            e => Assert.Equal(KeyMap.Resolve("ShiftLeft"), Assert.IsType<InjectedEvent.KeyPress>(e).Stroke),
            e => Assert.Equal(KeyMap.Resolve("KeyC"), Assert.IsType<InjectedEvent.KeyPress>(e).Stroke));

        handler.Handle(new Control.KeyUp("KeyC", ["ControlLeft", "ShiftLeft"]));
        Drain(handler, 2);

        // Released innermost-first, the way a human unwinds Ctrl+Shift+C.
        var releases = injector.OfType<InjectedEvent.KeyRelease>();
        Assert.Equal(KeyMap.Resolve("KeyC"), releases[0].Stroke);
        Assert.Equal(KeyMap.Resolve("ShiftLeft"), releases[1].Stroke);
        Assert.Equal(KeyMap.Resolve("ControlLeft"), releases[2].Stroke);
    }

    [Fact]
    public void A_non_modifier_listed_as_a_modifier_is_ignored()
    {
        // Otherwise a controller could hold Delete down around every keystroke.
        var (handler, injector) = Build();

        handler.Handle(new Control.KeyDown("KeyA", ["Delete", "ControlLeft"]));
        Drain(handler, 1);

        Assert.Collection(
            injector.Events,
            e => Assert.Equal(KeyMap.Resolve("ControlLeft"), Assert.IsType<InjectedEvent.KeyPress>(e).Stroke),
            e => Assert.Equal(KeyMap.Resolve("KeyA"), Assert.IsType<InjectedEvent.KeyPress>(e).Stroke));
    }

    [Theory]
    [InlineData("KeyZ", "ControlLeft")]   // undo
    [InlineData("KeyC", "ControlLeft")]   // copy
    [InlineData("KeyV", "ControlLeft")]   // paste
    [InlineData("ArrowRight", "ShiftLeft")] // extend selection
    [InlineData("Tab", "AltLeft")]        // window switch
    public void A_shortcut_presses_its_modifier_first_and_the_key_second(string code, string modifier)
    {
        // Asserted at the mapping layer rather than against the desktop: sending Alt+Tab
        // to the real machine would switch the tester's windows mid-run, and what is in
        // question here is the order and identity of the strokes, not whether Windows
        // honours them.
        var (handler, injector) = Build();

        handler.Handle(new Control.KeyDown(code, [modifier]));
        Drain(handler, 1);

        var presses = injector.OfType<InjectedEvent.KeyPress>();
        Assert.Equal(2, presses.Count);
        Assert.Equal(KeyMap.Resolve(modifier), presses[0].Stroke);
        Assert.Equal(KeyMap.Resolve(code), presses[1].Stroke);

        handler.Handle(new Control.KeyUp(code, [modifier]));
        Drain(handler, 2);

        // Released key-first, so the shortcut is not re-triggered on the way out and
        // nothing is left held.
        var releases = injector.OfType<InjectedEvent.KeyRelease>();
        Assert.Equal(KeyMap.Resolve(code), releases[0].Stroke);
        Assert.Equal(KeyMap.Resolve(modifier), releases[1].Stroke);
        Assert.Equal(0, handler.Held);
    }

    [Fact]
    public void A_three_key_shortcut_unwinds_in_reverse()
    {
        var (handler, injector) = Build();

        handler.Handle(new Control.KeyDown("Escape", ["ControlLeft", "ShiftLeft"]));
        handler.Handle(new Control.KeyUp("Escape", ["ControlLeft", "ShiftLeft"]));
        Drain(handler, 2);

        var presses = injector.OfType<InjectedEvent.KeyPress>();
        Assert.Equal(KeyMap.Resolve("ControlLeft"), presses[0].Stroke);
        Assert.Equal(KeyMap.Resolve("ShiftLeft"), presses[1].Stroke);
        Assert.Equal(KeyMap.Resolve("Escape"), presses[2].Stroke);

        var releases = injector.OfType<InjectedEvent.KeyRelease>();
        Assert.Equal(KeyMap.Resolve("Escape"), releases[0].Stroke);
        Assert.Equal(KeyMap.Resolve("ShiftLeft"), releases[1].Stroke);
        Assert.Equal(KeyMap.Resolve("ControlLeft"), releases[2].Stroke);

        Assert.Equal(0, handler.Held);
    }

    [Fact]
    public void Every_key_the_acceptance_procedure_names_resolves_and_injects()
    {
        // The D8 vocabulary, driven through the handler rather than read off the table,
        // so a key that maps but is dropped somewhere downstream still fails here.
        string[] required =
        [
            "KeyA", "KeyM", "KeyZ", "Digit0", "Digit9",
            "Enter", "Escape", "Tab", "Backspace", "Space",
            "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
            "Home", "End", "PageUp", "PageDown",
            "F1", "F12",
            "ControlLeft", "ControlRight", "ShiftLeft", "ShiftRight",
            "AltLeft", "AltRight", "MetaLeft", "MetaRight",
        ];

        var (handler, injector) = Build();

        foreach (var code in required) handler.Handle(new Control.KeyDown(code, null));
        Drain(handler, required.Length);

        Assert.Equal(required.Length, injector.OfType<InjectedEvent.KeyPress>().Count);
        Assert.Equal(0, handler.Unsupported);

        for (var i = 0; i < required.Length; i++)
            Assert.Equal(KeyMap.Resolve(required[i]), injector.OfType<InjectedEvent.KeyPress>()[i].Stroke);
    }

    [Fact]
    public void An_unmappable_key_code_injects_nothing_and_is_counted()
    {
        var (handler, injector) = Build();

        handler.Handle(new Control.KeyDown("LaunchMailbox", null));
        Drain(handler, 1);

        Assert.Empty(injector.Events);
        Assert.Equal(1, handler.Unsupported);
    }

    [Fact]
    public void Text_is_typed_as_a_whole_string_rather_than_synthesised_keystrokes()
    {
        // Unicode injection, so the text arrives identically whatever layout the host
        // has active. Synthesising keystrokes would mangle anything outside the layout.
        var (handler, injector) = Build();

        handler.Handle(new Control.KeyText("héllo 🌍"));
        Drain(handler, 1);

        Assert.Equal(new InjectedEvent.Text("héllo 🌍"), injector.Events[0]);
    }

    [Fact]
    public void Android_navigation_keys_are_counted_and_not_approximated()
    {
        // BACK, HOME and RECENTS have no Windows equivalent. Mapping them onto real
        // shortcuts would fire something unpredictable into the focused application.
        var (handler, injector) = Build();

        handler.Handle(new Control.NavKey("BACK"));
        handler.Handle(new Control.NavKey("HOME"));
        handler.Handle(new Control.NavKey("RECENTS"));
        Drain(handler, 3);

        Assert.Empty(injector.Events);
        Assert.Equal(3, handler.Unsupported);
    }

    // ---- gestures ----

    [Fact]
    public void A_swipe_presses_moves_through_intermediate_points_and_releases()
    {
        var (handler, injector) = Build();

        handler.Handle(new Control.PointerSwipe(0.0, 0.0, 1.0, 1.0, 40));
        Drain(handler, 1);

        var events = injector.Events;

        // Opens with a move to the start, then the press.
        Assert.Equal(new InjectedEvent.Move(0, 0), events[0]);
        Assert.Equal(new InjectedEvent.Down(0, 0, PointerButton.Left), events[1]);

        // Ends at the far corner with the release.
        Assert.Equal(new InjectedEvent.Up(1535, 863, PointerButton.Left), events[^1]);

        // And genuinely interpolates. A drag that teleports is frequently not
        // registered as a drag at all — applications distinguish it from a click by the
        // moves in between.
        var moves = injector.OfType<InjectedEvent.Move>();
        Assert.True(moves.Count >= 4, $"only {moves.Count} moves in a 40 ms swipe");
        Assert.Contains(moves, m => m.X > 0 && m.X < 1535);
    }

    [Fact]
    public void A_swipe_ends_with_the_button_released_even_at_the_shortest_duration()
    {
        // A swipe that left the button down would leave the host's mouse stuck.
        var (handler, injector) = Build();

        handler.Handle(new Control.PointerSwipe(0.1, 0.1, 0.2, 0.2, 1));
        Drain(handler, 1);

        Assert.IsType<InjectedEvent.Up>(injector.Events[^1]);
    }

    // ---- the secure desktop ----

    [Fact]
    public void Nothing_is_injected_while_the_secure_desktop_is_up()
    {
        var (handler, injector) = Build();
        injector.Available = false;

        handler.Handle(new Control.PointerTap(0.5, 0.5));
        Drain(handler, 1);

        Assert.Empty(injector.Events);
    }

    [Fact]
    public void Availability_is_rechecked_per_command_not_sampled_once()
    {
        // A UAC prompt appears and goes away mid-session. A value cached at session
        // start would be wrong for most of the session in both directions.
        var (handler, injector) = Build();

        injector.Available = false;
        handler.Handle(new Control.PointerTap(0.5, 0.5));
        Drain(handler, 1);
        Assert.Empty(injector.Events);

        injector.Available = true;
        handler.Handle(new Control.PointerTap(0.5, 0.5));
        Drain(handler, 2);
        Assert.Equal(2, injector.Events.Count);
    }

    // ---- rate limiting ----

    [Fact]
    public void A_flood_is_clamped_to_the_ceiling_and_the_session_survives()
    {
        var limiter = new ControlRateLimiter(() => _now);
        var (handler, _) = Build(limiter: limiter);

        for (var i = 0; i < 400; i++) handler.Handle(new Control.PointerMove(0.5, 0.5));

        Assert.Equal(250, handler.Accepted);
        Assert.Equal(150, handler.RateLimited);

        // Still usable a second later: a rate limit drops frames, it does not tear down.
        _now = _now.AddSeconds(1);
        handler.Handle(new Control.PointerMove(0.5, 0.5));
        Assert.Equal(251, handler.Accepted);
    }

    // ---- never throwing ----

    [Fact]
    public void A_null_frame_is_ignored()
    {
        var (handler, injector) = Build();

        handler.Handle(null);

        DrainNothing(injector);
    }

    [Fact]
    public void An_injector_that_throws_does_not_kill_the_worker()
    {
        // The worker is the only thing executing input. If one bad event ended it, the
        // session would look alive and accept nothing for the rest of its life.
        var (handler, injector) = Build();

        injector.ThrowOnce = new InvalidOperationException("device gone");
        handler.Handle(new Control.PointerTap(0.5, 0.5));
        Drain(handler, 1);
        Assert.Equal(1, handler.Failed);

        handler.Handle(new Control.PointerTap(0.25, 0.25));
        Drain(handler, 2);

        Assert.Equal(1, handler.Executed);
        // round(0.25 * 1535) = 384.
        Assert.Contains(injector.Events, e => e is InjectedEvent.Down { X: 384 });
    }

    [Fact]
    public void Disposing_twice_is_safe_and_stops_accepting_work()
    {
        var (handler, injector) = Build();

        handler.Dispose();
        handler.Dispose();

        handler.Handle(new Control.PointerTap(0.5, 0.5));
        Assert.Empty(injector.Events);
    }

    // ---- stuck keys and buttons ----

    [Fact]
    public void A_modifier_held_when_the_session_ends_is_released()
    {
        // The case this exists for: the controller presses Ctrl, the network dies before
        // the key-up, and the person at the machine is left with Ctrl logically held by
        // nothing. They cannot diagnose it and have no obvious way to clear it.
        var (handler, injector) = Build();

        handler.Handle(new Control.KeyDown("ControlLeft", null));
        Drain(handler, 1);
        Assert.Equal(1, handler.Held);

        handler.Dispose();

        Assert.Equal(0, handler.Held);
        Assert.Equal(KeyMap.Resolve("ControlLeft"), injector.OfType<InjectedEvent.KeyRelease>()[^1].Stroke);
    }

    [Fact]
    public void A_mouse_button_held_when_the_session_ends_is_released()
    {
        var (handler, injector) = Build();

        handler.Handle(new Control.PointerDown(0.5, 0.5, "left"));
        Drain(handler, 1);
        Assert.Equal(1, handler.Held);

        handler.Dispose();

        Assert.Equal(0, handler.Held);
        Assert.Equal(PointerButton.Left, injector.OfType<InjectedEvent.Up>()[^1].Button);
    }

    [Fact]
    public void Every_held_key_and_button_is_released_not_just_the_last()
    {
        var (handler, injector) = Build();

        handler.Handle(new Control.KeyDown("ControlLeft", null));
        handler.Handle(new Control.KeyDown("ShiftLeft", null));
        handler.Handle(new Control.PointerDown(0.5, 0.5, "right"));
        Drain(handler, 3);
        Assert.Equal(3, handler.Held);

        handler.Dispose();

        Assert.Equal(0, handler.Held);
        Assert.Contains(injector.OfType<InjectedEvent.KeyRelease>(),
            e => e.Stroke == KeyMap.Resolve("ControlLeft"));
        Assert.Contains(injector.OfType<InjectedEvent.KeyRelease>(),
            e => e.Stroke == KeyMap.Resolve("ShiftLeft"));
        Assert.Contains(injector.OfType<InjectedEvent.Up>(), e => e.Button == PointerButton.Right);
    }

    [Fact]
    public void Losing_authorization_while_holding_a_key_releases_it_without_ending_the_session()
    {
        // Revocation mid-hold. The matching key-up would itself be refused, so the
        // release has to be initiated by the refusal rather than waited for.
        Grant? grant = GrantWith("input.control");
        var injector = new FakeInputInjector();

        using var handler = new PeerControlHandler(
            injector,
            () => grant,
            () => Scaled125,
            () => _now,
            log: output.WriteLine);

        handler.Handle(new Control.KeyDown("ControlLeft", null));
        Drain(handler, 1);
        Assert.Equal(1, handler.Held);

        grant = null;
        handler.Handle(new Control.KeyUp("ControlLeft", null));

        var sw = Stopwatch.StartNew();
        while (handler.Held > 0 && sw.ElapsedMilliseconds < 5000) Thread.Sleep(1);

        Assert.Equal(0, handler.Held);
        Assert.Equal(1, handler.Refused);
    }

    [Fact]
    public void A_flood_of_refusals_queues_one_release_not_thousands()
    {
        // Otherwise a revoked controller that keeps sending fills the 256-slot queue
        // with release sentinels and starves anything else.
        Grant? grant = GrantWith("input.control");
        var injector = new FakeInputInjector();

        using var handler = new PeerControlHandler(
            injector,
            () => grant,
            () => Scaled125,
            () => _now,
            log: output.WriteLine);

        handler.Handle(new Control.PointerDown(0.5, 0.5, "left"));
        Drain(handler, 1);

        grant = null;
        for (var i = 0; i < 50; i++) handler.Handle(new Control.PointerMove(0.5, 0.5));

        var sw = Stopwatch.StartNew();
        while (handler.Held > 0 && sw.ElapsedMilliseconds < 5000) Thread.Sleep(1);

        Assert.Equal(0, handler.Held);
        Assert.Equal(50, handler.Refused);
        Assert.Equal(0, handler.QueueDropped);

        // Exactly one release, not one per refused frame.
        Assert.Single(injector.OfType<InjectedEvent.Up>());
    }

    [Fact]
    public void A_refusal_while_holding_nothing_queues_no_release_at_all()
    {
        var (handler, injector) = Build(GrantWith("screen.view"));

        for (var i = 0; i < 20; i++) handler.Handle(new Control.PointerTap(0.5, 0.5));

        DrainNothing(injector);
        Assert.Equal(20, handler.Refused);
    }

    [Fact]
    public void A_completed_tap_leaves_nothing_held()
    {
        // The steady state. If a tap left the button tracked as down, every session
        // would fire a spurious release at teardown.
        var (handler, _) = Build();

        handler.Handle(new Control.PointerTap(0.5, 0.5));
        handler.Handle(new Control.KeyText("hello"));
        Drain(handler, 2);

        Assert.Equal(0, handler.Held);
    }

    [Fact]
    public void A_completed_swipe_leaves_nothing_held()
    {
        var (handler, _) = Build();

        handler.Handle(new Control.PointerSwipe(0.1, 0.1, 0.9, 0.9, 20));
        Drain(handler, 1);

        Assert.Equal(0, handler.Held);
    }

    [Fact]
    public void A_matched_keyUp_clears_the_hold_without_waiting_for_teardown()
    {
        var (handler, _) = Build();

        handler.Handle(new Control.KeyDown("ShiftLeft", null));
        Drain(handler, 1);
        Assert.Equal(1, handler.Held);

        handler.Handle(new Control.KeyUp("ShiftLeft", null));
        Drain(handler, 2);
        Assert.Equal(0, handler.Held);
    }

    // ---- rate limiting reaches everything, including refusals ----

    [Fact]
    public void An_unauthorized_flood_is_rate_limited_and_not_merely_refused()
    {
        // The limiter runs before authorization deliberately: it is the denial-of-service
        // guard, and a peer with no grant must not be able to route around it by being
        // refused very quickly.
        var limiter = new ControlRateLimiter(() => _now);
        var (handler, injector) = Build(GrantWith("screen.view"), limiter: limiter);

        for (var i = 0; i < 400; i++) handler.Handle(new Control.PointerMove(0.5, 0.5));

        Assert.Equal(250, handler.Refused);
        Assert.Equal(150, handler.RateLimited);
        DrainNothing(injector);
    }

    // ---- malformed and hostile frames that still decode ----

    [Fact]
    public void A_hostile_wheel_delta_reaches_the_injector_as_a_finite_number()
    {
        // The codec accepts any finite double. Bounding it is the injector's job, and
        // the handler must not silently drop a legal frame on the way.
        var (handler, injector) = Build();

        handler.Handle(new Control.PointerWheel(0.5, 0.5, 0, 1e300));
        Drain(handler, 1);

        // Bounding it happens inside SendInputInjector, where the overflow would be —
        // pinned by SendInputInjectorTests.A_hostile_wheel_delta_cannot_overflow_the_conversion.
        var scroll = Assert.IsType<InjectedEvent.Scroll>(injector.Events[0]);
        Assert.Equal(1e300, scroll.Dy);
    }

    [Fact]
    public void An_unknown_button_injects_nothing_rather_than_defaulting_to_left()
    {
        // Unreachable through the codec today, which rejects unknown buttons. Asserted
        // anyway so a future decoder change cannot quietly turn a bad button into a
        // left-click.
        var (handler, injector) = Build();

        handler.Handle(new Control.PointerDown(0.5, 0.5, "pedal"));
        Drain(handler, 1);

        DrainNothing(injector);
        Assert.Equal(0, handler.Held);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("7")]
    [InlineData("")]
    [InlineData("{\"t\":\"tap\",\"x\":0.5")]
    [InlineData("{}")]
    [InlineData("{\"t\":\"selfDestruct\"}")]
    [InlineData("{\"t\":\"tap\",\"x\":0.5}")]
    [InlineData("{\"t\":\"tap\",\"x\":\"NaN\",\"y\":0.5}")]
    [InlineData("{\"t\":\"tap\",\"x\":null,\"y\":0.5}")]
    [InlineData("{\"v\":1,\"t\":\"pointer.wheel\",\"x\":0.5,\"y\":0.5,\"dx\":\"Infinity\",\"dy\":0}")]
    [InlineData("{\"v\":1,\"t\":\"pointer.up\",\"x\":0.1,\"y\":0.1,\"b\":\"pedal\"}")]
    [InlineData("{\"v\":1,\"t\":\"keyboard.keyDown\",\"code\":65}")]
    [InlineData("{\"v\":2,\"t\":\"pointer.tap\",\"x\":0.5,\"y\":0.5}")]
    [InlineData("{\"v\":0,\"t\":\"pointer.tap\",\"x\":0.5,\"y\":0.5}")]
    [InlineData("{\"t\":\"pointer.tap\",\"x\":0.5,\"y\":0.5}")]
    [InlineData("{\"v\":1,\"t\":\"tap\",\"x\":0.5,\"y\":0.5}")]
    public void A_malformed_frame_cannot_reach_the_injector(string raw)
    {
        // The whole inbound path from raw bytes, so this covers the decoder and the
        // handler together rather than assuming the decoder catches everything.
        var (handler, injector) = Build();

        var decoded = ControlCodec.DecodeRaw(raw);
        handler.Handle(decoded);

        DrainNothing(injector);
        Assert.Equal(0, handler.Held);
        Assert.Equal(0, handler.Executed);
    }

    [Fact]
    public void A_parser_bomb_is_refused_without_reaching_the_injector()
    {
        var bomb = string.Concat(Enumerable.Repeat("[", 2000))
                   + string.Concat(Enumerable.Repeat("]", 2000));

        var (handler, injector) = Build();

        handler.Handle(ControlCodec.DecodeRaw(bomb));

        DrainNothing(injector);
    }

    [Fact]
    public void An_oversized_frame_is_refused_on_length_before_it_is_parsed()
    {
        var oversized = "{\"v\":1,\"t\":\"keyboard.text\",\"s\":\"" + new string('a', 70000) + "\"}";

        var (handler, injector) = Build();

        handler.Handle(ControlCodec.DecodeRaw(oversized));

        DrainNothing(injector);
    }

    [Fact]
    public void Out_of_range_coordinates_are_clamped_onto_the_display_not_past_it()
    {
        // The codec clamps rather than rejecting, so the handler receives 1.0 and 0.0.
        // What must not happen is a coordinate landing outside the captured display.
        var (handler, injector) = Build();

        handler.Handle(ControlCodec.DecodeRaw("{\"t\":\"tap\",\"x\":1.7,\"y\":-0.4}"));
        Drain(handler, 1);

        var down = Assert.IsType<InjectedEvent.Down>(injector.Events[0]);
        Assert.InRange(down.X, 0, 1535);
        Assert.InRange(down.Y, 0, 863);
    }
}
