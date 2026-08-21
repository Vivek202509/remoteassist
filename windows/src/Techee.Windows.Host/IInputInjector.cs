namespace Techee.Windows.Host;

/// <summary>A mouse button, in the protocol's vocabulary rather than Win32's.</summary>
public enum PointerButton
{
    Left,
    Right,
    Middle,
    X1,
    X2,
}

/// <summary>
/// One keyboard key, already resolved to what <c>SendInput</c> needs.
/// </summary>
/// <remarks>
/// Produced by <see cref="KeyMap"/> from a W3C <c>KeyboardEvent.code</c>. Carrying the
/// scan code alongside the virtual key is not redundant: some applications — notably
/// games and remote-desktop clients — read the scan code and ignore the virtual key, and
/// an injected event with a zero scan code is invisible to them.
/// </remarks>
public readonly record struct KeyStroke(ushort VirtualKey, ushort ScanCode, bool Extended);

/// <summary>
/// Injects mouse and keyboard input into the local desktop.
/// </summary>
/// <remarks>
/// <para>
/// Coordinates are <b>logical virtual-desktop pixels</b> — the space
/// <see cref="DisplayGeometry.ToVirtual"/> produces, which may be negative on a monitor
/// left of or above the primary. Converting those to the 0..65535 range <c>SendInput</c>
/// wants is the implementation's business, because it is a quirk of the injection API
/// and not of the protocol.
/// </para>
/// <para>
/// <b>No protocol types appear here.</b> Like <see cref="IScreenSource"/>, this seam
/// keeps <c>Techee.Windows.Host</c> free of any reference to <c>Techee.Protocol</c>,
/// WebRTC, or the broker: it is told to click at a point, not told that a paired
/// controller sent a <c>pointer.tap</c>. Translating one into the other belongs to the
/// session layer, which is the only place that sees both.
/// </para>
/// <para>
/// <b>Implementations must not throw.</b> Injection failures are reported through
/// <see cref="LastError"/> and counted, never raised: the caller is draining a queue fed
/// by an attacker-influenced data channel, and an exception on that path takes down an
/// unattended machine.
/// </para>
/// </remarks>
public interface IInputInjector : IDisposable
{
    /// <summary>
    /// Whether input can currently reach the desktop.
    /// </summary>
    /// <remarks>
    /// False while the secure desktop (UAC elevation, the logon screen, Ctrl+Alt+Del) is
    /// in front. That is not a fault to be worked around — see
    /// <see cref="UnavailableReason"/>.
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>
    /// Why input cannot be delivered, or null when it can.
    /// </summary>
    /// <remarks>
    /// Intended to be shown to the operator verbatim. "Nothing happens when I click" is
    /// indistinguishable from a crash; "secure desktop active" is diagnosable.
    /// </remarks>
    string? UnavailableReason { get; }

    /// <summary>The most recent injection failure, or null. Diagnostic only.</summary>
    string? LastError { get; }

    /// <summary>
    /// Buttons and keys this injector has pressed and not yet released.
    /// </summary>
    /// <remarks>
    /// Exposed so a session can see whether it is holding anything before it goes away.
    /// Zero is the healthy steady state between commands.
    /// </remarks>
    int PressedCount { get; }

    /// <summary>
    /// Releases everything this injector pressed and has not released.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A remote session can end at any moment — the network drops, the grant is revoked,
    /// the operator closes the window — and it can do so between a
    /// <c>keyboard.keyDown</c> and its matching <c>keyboard.keyUp</c>. Without this, the
    /// person sitting at the machine is left with Ctrl or Shift logically held down by
    /// nothing, or the left mouse button stuck mid-drag. There is no way for them to
    /// diagnose it and no obvious way to clear it.
    /// </para>
    /// <para>
    /// <b>Only what this injector pressed.</b> Releasing every modifier unconditionally
    /// would clobber a key the local user is physically holding at that moment, turning
    /// a remote-session cleanup into interference with someone else's typing. The
    /// tracked set is exactly the set Techee is responsible for.
    /// </para>
    /// <para>
    /// Idempotent, and safe to call after disposal.
    /// </para>
    /// </remarks>
    void ReleaseAllPressed();

    /// <summary>Moves the cursor without pressing anything.</summary>
    void MoveTo(int x, int y);

    /// <summary>Presses a button at a point. The move and the press are one atomic batch.</summary>
    void ButtonDown(int x, int y, PointerButton button);

    void ButtonUp(int x, int y, PointerButton button);

    /// <summary>
    /// Scrolls at a point.
    /// </summary>
    /// <param name="notchesX">Horizontal notches; positive is right.</param>
    /// <param name="notchesY">Vertical notches; positive is away from the user.</param>
    /// <remarks>
    /// Notches, not pixels — the protocol says so, and Windows agrees: one notch is
    /// <c>WHEEL_DELTA</c> (120), and the receiving application decides how far that
    /// scrolls.
    /// </remarks>
    void Wheel(int x, int y, double notchesX, double notchesY);

    void KeyDown(KeyStroke stroke);

    void KeyUp(KeyStroke stroke);

    /// <summary>
    /// Types a string as literal Unicode, bypassing the keyboard layout.
    /// </summary>
    /// <remarks>
    /// Not the same as synthesising keystrokes: this delivers the characters themselves,
    /// so text types identically whatever layout the host happens to have active. It
    /// deliberately does <b>not</b> replace the focused control's contents, which is what
    /// the Android host does via <c>ACTION_SET_TEXT</c> — on a desktop, where the focus
    /// may be a shell or an editor rather than a text field, replacing everything would
    /// be destructive.
    /// </remarks>
    void TypeText(string text);
}
