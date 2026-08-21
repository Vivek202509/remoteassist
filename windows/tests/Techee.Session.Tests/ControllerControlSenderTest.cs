using Techee.Protocol;
using Techee.Session;

namespace Techee.Session.Tests;

/// <summary>
/// The Windows controller's protocol layer.
/// </summary>
/// <remarks>
/// The same properties Android's sender is held to, asserted independently. Two
/// implementations of one specification are only worth having if both are actually
/// checked — otherwise the second is just a place for the first's bugs to be repeated.
/// </remarks>
public class ControllerControlSenderTests
{
    private readonly List<(Control Control, int Version)> _sent = [];
    private EndpointMeta? _peerMeta;
    private bool _open = true;

    private ControllerControlSender New() => new(
        (control, version) =>
        {
            if (!_open) return false;
            _sent.Add((control, version));
            return true;
        },
        () => _peerMeta);

    private static readonly EndpointMeta WindowsHost =
        new("windows", "w5", ["screen.share", "input.receive"]);

    private static readonly EndpointMeta AndroidHost =
        new("android", "1", ["screen.share", "input.receive", "nav.android"]);

    private List<string> Kinds() => [.. _sent.Select(s => s.Control.Kind)];

    // ---- dialect and capability filtering ----

    [Fact]
    public void An_unannounced_peer_is_addressed_in_the_legacy_dialect()
    {
        var sender = New();
        sender.Tap(0.5, 0.5);

        Assert.Equal(TecheeProtocol.LegacyVersion, _sent[0].Version);
    }

    [Fact]
    public void An_announced_peer_is_addressed_in_v1()
    {
        _peerMeta = WindowsHost;

        var sender = New();
        sender.Tap(0.5, 0.5);

        Assert.Equal(TecheeProtocol.ProtocolVersion, _sent[0].Version);
    }

    [Fact]
    public void The_dialect_follows_a_late_announcement()
    {
        // The announcement arrives after the channel opens, so a value captured at
        // construction would be null forever and every capability check would fail
        // closed for the whole session.
        var sender = New();
        Assert.Equal(TecheeProtocol.LegacyVersion, sender.PeerVersion);

        _peerMeta = WindowsHost;
        Assert.Equal(TecheeProtocol.ProtocolVersion, sender.PeerVersion);
    }

    [Fact]
    public void Capability_answers_come_from_what_the_peer_announced()
    {
        var sender = New();

        Assert.False(sender.PeerIsDesktop);
        Assert.False(sender.PeerAcceptsInput);
        Assert.True(sender.PeerAcceptsNavKeys);

        _peerMeta = WindowsHost;
        Assert.True(sender.PeerIsDesktop);
        Assert.True(sender.PeerAcceptsInput);
        Assert.False(sender.PeerAcceptsNavKeys);

        _peerMeta = AndroidHost;
        Assert.False(sender.PeerIsDesktop);
        Assert.True(sender.PeerAcceptsNavKeys);
    }

    [Fact]
    public void A_host_that_did_not_advertise_input_is_reported_as_not_accepting_it()
    {
        _peerMeta = new EndpointMeta("windows", "w5", ["screen.share"]);
        Assert.False(New().PeerAcceptsInput);
    }

    [Fact]
    public void Nav_keys_are_withheld_from_a_windows_peer()
    {
        _peerMeta = WindowsHost;

        Assert.False(New().NavKey("BACK"));
        Assert.Empty(_sent);
    }

    [Fact]
    public void Nav_keys_reach_an_android_peer()
    {
        _peerMeta = AndroidHost;

        Assert.True(New().NavKey("BACK"));
        Assert.Equal("nav.key", _sent[0].Control.Kind);
    }

    // ---- pointer ----

    [Fact]
    public void A_double_click_is_two_clicks()
    {
        _peerMeta = WindowsHost;
        New().DoubleTap(0.5, 0.5);

        Assert.Equal(["pointer.tap", "pointer.tap"], Kinds());
    }

    [Fact]
    public void A_drag_is_press_then_move_then_release()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        sender.ButtonDown(0.1, 0.1);
        sender.PointerMove(0.2, 0.2);
        sender.FlushPendingMove();
        sender.ButtonUp(0.3, 0.3);

        Assert.Equal(["pointer.down", "pointer.move", "pointer.up"], Kinds());
    }

    [Fact]
    public void Each_button_reaches_the_wire_as_itself()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        sender.ButtonDown(0, 0, "right");
        sender.ButtonUp(0, 0, "middle");

        Assert.Equal("right", ((Control.PointerDown)_sent[0].Control).Button);
        Assert.Equal("middle", ((Control.PointerUp)_sent[1].Control).Button);
    }

    // ---- coalescing ----

    [Fact]
    public void Pointer_moves_coalesce_to_the_newest_position()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        sender.PointerMove(0.1, 0.1);
        sender.PointerMove(0.2, 0.2);
        sender.PointerMove(0.3, 0.3);

        Assert.Empty(_sent);

        sender.FlushPendingMove();

        Assert.Single(_sent);
        Assert.Equal(0.3, ((Control.PointerMove)_sent[0].Control).X, 4);
        Assert.Equal(2, sender.CoalescedMoves);
    }

    [Fact]
    public void A_state_transition_flushes_the_pending_move_first()
    {
        // Otherwise the press arrives before the movement that positioned it, and the
        // click lands at the previous position.
        _peerMeta = WindowsHost;
        var sender = New();

        sender.PointerMove(0.9, 0.9);
        sender.ButtonDown(0.9, 0.9);

        Assert.Equal(["pointer.move", "pointer.down"], Kinds());
    }

    [Fact]
    public void A_release_is_never_coalesced_away()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        sender.ButtonDown(0.5, 0.5);
        for (var i = 0; i < 50; i++) sender.PointerMove(0.5, 0.5);
        sender.ButtonUp(0.5, 0.5);

        Assert.Equal(["pointer.down", "pointer.move", "pointer.up"], Kinds());
    }

    // ---- wheel ----

    [Fact]
    public void Wheel_deltas_pass_through_in_notches()
    {
        _peerMeta = WindowsHost;
        New().Wheel(0.5, 0.5, 0, 3);

        var wheel = (Control.PointerWheel)_sent[0].Control;
        Assert.Equal(3, wheel.Dy, 4);
        Assert.Equal(0, wheel.Dx, 4);
    }

    [Fact]
    public void Both_scroll_directions_work()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        sender.Wheel(0.5, 0.5, 0, 2);
        sender.Wheel(0.5, 0.5, 0, -2);

        Assert.True(((Control.PointerWheel)_sent[0].Control).Dy > 0);
        Assert.True(((Control.PointerWheel)_sent[1].Control).Dy < 0);
    }

    [Fact]
    public void A_zero_or_non_finite_wheel_delta_is_not_sent()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        Assert.False(sender.Wheel(0.5, 0.5, 0, 0));
        Assert.False(sender.Wheel(0.5, 0.5, 0, double.NaN));
        Assert.False(sender.Wheel(0.5, 0.5, double.PositiveInfinity, 0));

        Assert.Empty(_sent);
    }

    [Fact]
    public void A_flung_gesture_is_clamped()
    {
        _peerMeta = WindowsHost;
        New().Wheel(0.5, 0.5, 0, 1e6);

        Assert.Equal(120, ((Control.PointerWheel)_sent[0].Control).Dy, 4);
    }

    // ---- keyboard ----

    [Fact]
    public void Text_is_sent_as_text()
    {
        _peerMeta = WindowsHost;
        New().TypeText("Techee Windows → Windows 123");

        Assert.Equal("Techee Windows → Windows 123", ((Control.KeyText)_sent[0].Control).Text);
    }

    [Fact]
    public void Long_text_is_split_rather_than_truncated()
    {
        _peerMeta = WindowsHost;
        var long_ = new string('a', TecheeProtocol.MaxTextBytes + 500);

        Assert.True(New().TypeText(long_));
        Assert.True(_sent.Count > 1);
        Assert.Equal(long_, string.Concat(_sent.Select(s => ((Control.KeyText)s.Control).Text)));
    }

    [Fact]
    public void A_surrogate_pair_is_never_split_across_frames()
    {
        _peerMeta = WindowsHost;
        var emoji = string.Concat(Enumerable.Repeat("🌍", TecheeProtocol.MaxTextBytes / 4 + 50));

        Assert.True(New().TypeText(emoji));

        var reassembled = string.Concat(_sent.Select(s => ((Control.KeyText)s.Control).Text));
        Assert.Equal(emoji, reassembled);

        foreach (var (control, _) in _sent)
        {
            var text = ((Control.KeyText)control).Text;
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= TecheeProtocol.MaxTextBytes);
            Assert.False(char.IsHighSurrogate(text[^1]), "a frame ended on half a surrogate pair");
        }
    }

    [Fact]
    public void Key_down_and_up_are_distinct()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        sender.KeyDown("KeyA");
        sender.KeyUp("KeyA");

        Assert.Equal(["keyboard.keyDown", "keyboard.keyUp"], Kinds());
    }

    [Fact]
    public void An_empty_modifier_list_is_omitted()
    {
        _peerMeta = WindowsHost;
        New().KeyDown("KeyA", []);

        Assert.Null(((Control.KeyDown)_sent[0].Control).Mods);
    }

    // ---- modifier latching and release ----

    [Fact]
    public void A_latched_modifier_is_applied_then_released()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        sender.HoldModifier("ControlLeft");
        _sent.Clear();

        sender.TapKeyWithModifiers("KeyC");

        Assert.Equal(["keyboard.keyDown", "keyboard.keyUp", "keyboard.keyUp"], Kinds());
        Assert.Equal(["ControlLeft"], ((Control.KeyDown)_sent[0].Control).Mods);
        Assert.Equal("ControlLeft", ((Control.KeyUp)_sent[2].Control).Code);
        Assert.Equal(0, sender.HeldCount);
    }

    [Fact]
    public void Latched_modifiers_release_in_reverse()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        sender.HoldModifier("ControlLeft");
        sender.HoldModifier("ShiftLeft");
        _sent.Clear();

        sender.ReleaseAll();

        Assert.Equal("ShiftLeft", ((Control.KeyUp)_sent[0].Control).Code);
        Assert.Equal("ControlLeft", ((Control.KeyUp)_sent[1].Control).Code);
    }

    [Fact]
    public void Latching_the_same_modifier_twice_presses_once()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        sender.HoldModifier("AltLeft");
        sender.HoldModifier("AltLeft");

        Assert.Single(_sent);
        Assert.Equal(1, sender.HeldCount);
    }

    // ---- teardown ----

    [Fact]
    public void ReleaseAll_puts_back_a_button_and_the_modifiers()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        sender.ButtonDown(0.5, 0.5);
        sender.HoldModifier("ControlLeft");
        Assert.Equal(2, sender.HeldCount);
        _sent.Clear();

        sender.ReleaseAll();

        Assert.Equal(["pointer.up", "keyboard.keyUp"], Kinds());
        Assert.Equal(0, sender.HeldCount);
    }

    [Fact]
    public void ReleaseAll_with_nothing_held_sends_nothing()
    {
        _peerMeta = WindowsHost;
        New().ReleaseAll();

        Assert.Empty(_sent);
    }

    [Fact]
    public void ReleaseAll_is_idempotent()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        sender.ButtonDown(0.5, 0.5);
        _sent.Clear();

        sender.ReleaseAll();
        sender.ReleaseAll();

        Assert.Single(_sent);
    }

    [Fact]
    public void A_button_up_clears_the_held_state_even_when_the_send_fails()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        sender.ButtonDown(0.5, 0.5);
        _open = false;
        sender.ButtonUp(0.5, 0.5);

        Assert.Equal(0, sender.HeldCount);
    }

    [Fact]
    public void A_closed_channel_fails_every_send_and_counts_it()
    {
        _peerMeta = WindowsHost;
        _open = false;
        var sender = New();

        Assert.False(sender.Tap(0.5, 0.5));
        Assert.False(sender.KeyDown("KeyA"));
        Assert.Equal(2, sender.FailedSends);
    }

    [Fact]
    public void A_transport_that_throws_is_treated_as_a_failed_send()
    {
        var sender = new ControllerControlSender(
            (_, _) => throw new InvalidOperationException("channel exploded"),
            () => WindowsHost);

        Assert.False(sender.Tap(0.5, 0.5));
        Assert.Equal(1, sender.FailedSends);
    }

    [Fact]
    public void Nothing_accumulates_while_the_channel_is_closed()
    {
        _peerMeta = WindowsHost;
        var sender = New();

        _open = false;
        for (var i = 0; i < 20; i++) sender.PointerMove(0.5, 0.5);
        sender.FlushPendingMove();

        _open = true;
        sender.FlushPendingMove();

        Assert.Empty(_sent);
    }
}
