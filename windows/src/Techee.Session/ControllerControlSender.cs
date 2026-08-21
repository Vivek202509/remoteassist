using Techee.Protocol;
using Techee.WebRtc;

namespace Techee.Session;

/// <summary>
/// The controller's half of the control protocol, on Windows.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of Android's <c>RemoteControlSender</c>, and deliberately the same shape:
/// one narrow object that owns dialect selection, capability filtering, pointer
/// coalescing and release bookkeeping, so no UI code ever builds a frame. Sharing the
/// shape rather than the code is the point — the two platforms have independent
/// implementations of the same specification, which is what makes the fixtures worth
/// anything.
/// </para>
/// <para>
/// <b>This is the protocol layer only.</b> There is no window, no video rendering and
/// no input capture yet; those are W5.1. What exists here is the piece a UI would sit
/// on, and it is testable without one.
/// </para>
/// <para>
/// Not thread-safe. A UI drives it from one thread, and a lock would suggest otherwise.
/// </para>
/// </remarks>
public sealed class ControllerControlSender
{
    private readonly Func<Control, int, bool> _send;
    private readonly Func<EndpointMeta?> _peerMeta;

    private readonly List<string> _heldModifiers = [];
    private string? _heldButton;
    private Control.PointerMove? _pendingMove;

    /// <param name="send">Puts one command on the wire in a given dialect.</param>
    /// <param name="peerMeta">
    /// What the peer announced, re-read rather than captured: the announcement arrives
    /// after the channel opens, so a value snapshotted at construction would always be
    /// null and every capability check would fail closed forever.
    /// </param>
    public ControllerControlSender(Func<Control, int, bool> send, Func<EndpointMeta?> peerMeta)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(peerMeta);

        _send = send;
        _peerMeta = peerMeta;
    }

    /// <summary>Builds a sender bound to a peer connection.</summary>
    public static ControllerControlSender For(TecheePeerConnection peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return new ControllerControlSender(
            (control, version) => peer.SendControl(control, version),
            () => peer.PeerMeta);
    }

    /// <summary>What the peer announced, or null for a peer that never did.</summary>
    public EndpointMeta? PeerMeta => _peerMeta();

    /// <summary>
    /// The dialect the peer is addressed in.
    /// </summary>
    /// <remarks>
    /// Legacy until the peer announces itself. Defaulting the other way would send v1
    /// frames to a shipped Android host, which drops them.
    /// </remarks>
    public int PeerVersion =>
        PeerMeta is null ? TecheeProtocol.LegacyVersion : TecheeProtocol.ProtocolVersion;

    /// <summary>Whether the peer said it will act on input. Description, never authorization.</summary>
    public bool PeerAcceptsInput => PeerMeta?.Has("input.receive") == true;

    /// <summary>Whether the peer is a desktop, so a pointer model is meaningful.</summary>
    public bool PeerIsDesktop => PeerMeta?.Platform == "windows";

    /// <summary>Whether Android navigation keys mean anything to the peer.</summary>
    /// <remarks>
    /// True for an unannounced peer, which is almost certainly a legacy Android host —
    /// the one kind of peer for which these keys are the primary interface.
    /// </remarks>
    public bool PeerAcceptsNavKeys =>
        PeerMeta is null || PeerMeta.Platform == "android" || PeerMeta.Has("nav.android");

    /// <summary>Buttons and keys held on the remote side.</summary>
    public int HeldCount => _heldModifiers.Count + (_heldButton is null ? 0 : 1);

    /// <summary>The modifiers currently latched, in press order.</summary>
    public IReadOnlyList<string> HeldModifiers => _heldModifiers;

    /// <summary>Moves superseded by a newer position before they were sent.</summary>
    public long CoalescedMoves { get; private set; }

    /// <summary>Frames the transport refused.</summary>
    public long FailedSends { get; private set; }

    // ---- pointer ----

    public bool Tap(double x, double y) => Send(new Control.PointerTap(x, y));

    /// <summary>
    /// A double click, as two clicks.
    /// </summary>
    /// <remarks>
    /// The protocol has no double-click, deliberately: Windows decides what counts as
    /// one from the timing of two, and the double-click speed is the host's setting to
    /// honour rather than the controller's to override.
    /// </remarks>
    public bool DoubleTap(double x, double y) => Tap(x, y) && Tap(x, y);

    public bool ButtonDown(double x, double y, string button = "left")
    {
        FlushPendingMove();

        var ok = Send(new Control.PointerDown(x, y, button));
        if (ok) _heldButton = button;
        return ok;
    }

    public bool ButtonUp(double x, double y, string button = "left")
    {
        FlushPendingMove();

        var ok = Send(new Control.PointerUp(x, y, button));
        // Cleared regardless. If the channel has gone there is nothing to release, and
        // believing we still owe one would open the next session with a stray release.
        _heldButton = null;
        return ok;
    }

    /// <summary>
    /// Moves the pointer, superseding any unsent move.
    /// </summary>
    /// <remarks>
    /// Buffered rather than sent. A drag produces a move per frame and only the newest
    /// is worth anything; every state transition flushes first, so a press can never
    /// overtake the movement that positioned it.
    /// </remarks>
    public void PointerMove(double x, double y)
    {
        if (_pendingMove is not null) CoalescedMoves++;
        _pendingMove = new Control.PointerMove(x, y);
    }

    /// <summary>Sends the buffered move. False when there was nothing pending.</summary>
    public bool FlushPendingMove()
    {
        var move = _pendingMove;
        if (move is null) return false;

        _pendingMove = null;
        return Send(move);
    }

    public bool Wheel(double x, double y, double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return false;
        if (dx == 0 && dy == 0) return false;

        FlushPendingMove();

        return Send(new Control.PointerWheel(
            x, y,
            Math.Clamp(dx, -MaxWheelNotches, MaxWheelNotches),
            Math.Clamp(dy, -MaxWheelNotches, MaxWheelNotches)));
    }

    /// <summary>A swipe, for an Android host with no pointer model.</summary>
    public bool Swipe(double x1, double y1, double x2, double y2, long ms) =>
        Send(new Control.PointerSwipe(x1, y1, x2, y2, Math.Clamp(ms, 1, TecheeProtocol.MaxSwipeMs)));

    /// <summary>Android navigation. Refused for a peer that does not claim it.</summary>
    public bool NavKey(string key) => PeerAcceptsNavKeys && Send(new Control.NavKey(key));

    // ---- keyboard ----

    /// <summary>
    /// Types literal text.
    /// </summary>
    /// <remarks>
    /// Split on the protocol's byte ceiling rather than truncated, and never through a
    /// surrogate pair — half an emoji arrives as a replacement character.
    /// </remarks>
    public bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            var take = PrefixWithinByteLimit(remaining, TecheeProtocol.MaxTextBytes);
            if (take == 0) return false;
            if (!Send(new Control.KeyText(remaining[..take].ToString()))) return false;
            remaining = remaining[take..];
        }

        return true;
    }

    public bool KeyDown(string code, IReadOnlyList<string>? mods = null) =>
        Send(new Control.KeyDown(code, Trim(mods)));

    public bool KeyUp(string code, IReadOnlyList<string>? mods = null) =>
        Send(new Control.KeyUp(code, Trim(mods)));

    /// <summary>Presses a modifier and remembers it.</summary>
    public bool HoldModifier(string code)
    {
        if (_heldModifiers.Contains(code)) return true;

        var ok = KeyDown(code);
        if (ok) _heldModifiers.Add(code);
        return ok;
    }

    public bool ReleaseModifier(string code) =>
        _heldModifiers.Remove(code) && KeyUp(code);

    public bool IsModifierHeld(string code) => _heldModifiers.Contains(code);

    /// <summary>
    /// Sends a key with the latched modifiers applied, then clears the latches.
    /// </summary>
    /// <remarks>
    /// One-shot. A modifier that stayed latched after use would silently turn the next
    /// keystroke into a shortcut, which is a bad surprise in someone else's session.
    /// </remarks>
    public bool TapKeyWithModifiers(string code)
    {
        var mods = _heldModifiers.ToList();

        var down = KeyDown(code, mods);
        var up = KeyUp(code, mods);

        ReleaseHeldModifiers();
        return down && up;
    }

    // ---- cleanup ----

    /// <summary>
    /// Releases everything held remotely.
    /// </summary>
    /// <remarks>
    /// Called on teardown and whenever a gesture is lost. The host has its own
    /// stuck-input net; this is the near half, and the half that matters when the
    /// session survives and only the gesture did not.
    /// </remarks>
    public void ReleaseAll()
    {
        if (_heldButton is { } button) Send(new Control.PointerUp(0, 0, button));
        _heldButton = null;

        ReleaseHeldModifiers();
        _pendingMove = null;
    }

    private void ReleaseHeldModifiers()
    {
        // Reversed, so Ctrl+Shift unwinds the way a person would let go.
        for (var i = _heldModifiers.Count - 1; i >= 0; i--) KeyUp(_heldModifiers[i]);
        _heldModifiers.Clear();
    }

    // ---- the one send ----

    private bool Send(Control control)
    {
        bool ok;
        try
        {
            ok = _send(control, PeerVersion);
        }
        catch (Exception)
        {
            ok = false;
        }

        if (!ok) FailedSends++;
        return ok;
    }

    private static IReadOnlyList<string>? Trim(IReadOnlyList<string>? mods) =>
        mods is null || mods.Count == 0 ? null : mods;

    private const double MaxWheelNotches = 120;

    /// <summary>
    /// How many chars of <paramref name="s"/> fit in <paramref name="limit"/> UTF-8
    /// bytes without splitting a surrogate pair.
    /// </summary>
    private static int PrefixWithinByteLimit(ReadOnlySpan<char> s, int limit)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(s) <= limit) return s.Length;

        var taken = 0;
        var bytes = 0;

        while (taken < s.Length)
        {
            // Surrogate pairs move two chars at a time; splitting one produces two
            // replacement characters on the host instead of one character.
            var width = char.IsHighSurrogate(s[taken]) && taken + 1 < s.Length ? 2 : 1;
            var cost = System.Text.Encoding.UTF8.GetByteCount(s.Slice(taken, width));

            if (bytes + cost > limit) break;

            bytes += cost;
            taken += width;
        }

        return taken;
    }
}
