using System.Windows.Forms;

namespace Techee.Windows.ControllerApp;

/// <summary>
/// Translates WinForms <see cref="Keys"/> into W3C <c>KeyboardEvent.code</c> names.
/// </summary>
/// <remarks>
/// <para>
/// The inverse of the host's <c>KeyMap</c>, and the reason a Windows controller can
/// exercise D8 at all: the protocol carries key <i>positions</i>, so the controller has
/// to name the position rather than the character. <c>Keys.A</c> is "the key where A sits
/// on US QWERTY" and travels as <c>KeyA</c>, which the host turns back into a scan code
/// and lets the remote layout interpret.
/// </para>
/// <para>
/// Nothing here consults the character a key would produce. Mapping through the typed
/// character would freeze this machine's layout onto the host — the exact bug the host's
/// <c>KeyMap</c> documentation warns about, arrived at from the other direction.
/// </para>
/// <para>
/// <b>Bare modifiers are reported as the left-hand key.</b> WinForms raises
/// <c>Keys.ShiftKey</c> rather than <c>LShiftKey</c>/<c>RShiftKey</c> for a plain press,
/// and the distinction is not recoverable from the event alone. Left is the assumption
/// because it is the one that matters: a modifier's side changes nothing about what a
/// shortcut does. Text is sent as text rather than as keystrokes, so this is never the
/// path a character takes.
/// </para>
/// </remarks>
public static class WinFormsKeyMap
{
    private static readonly IReadOnlyDictionary<Keys, string> Table =
        new Dictionary<Keys, string>
        {
            // ---- letters ----
            [Keys.A] = "KeyA", [Keys.B] = "KeyB", [Keys.C] = "KeyC", [Keys.D] = "KeyD",
            [Keys.E] = "KeyE", [Keys.F] = "KeyF", [Keys.G] = "KeyG", [Keys.H] = "KeyH",
            [Keys.I] = "KeyI", [Keys.J] = "KeyJ", [Keys.K] = "KeyK", [Keys.L] = "KeyL",
            [Keys.M] = "KeyM", [Keys.N] = "KeyN", [Keys.O] = "KeyO", [Keys.P] = "KeyP",
            [Keys.Q] = "KeyQ", [Keys.R] = "KeyR", [Keys.S] = "KeyS", [Keys.T] = "KeyT",
            [Keys.U] = "KeyU", [Keys.V] = "KeyV", [Keys.W] = "KeyW", [Keys.X] = "KeyX",
            [Keys.Y] = "KeyY", [Keys.Z] = "KeyZ",

            // ---- digit row ----
            [Keys.D0] = "Digit0", [Keys.D1] = "Digit1", [Keys.D2] = "Digit2",
            [Keys.D3] = "Digit3", [Keys.D4] = "Digit4", [Keys.D5] = "Digit5",
            [Keys.D6] = "Digit6", [Keys.D7] = "Digit7", [Keys.D8] = "Digit8",
            [Keys.D9] = "Digit9",

            // ---- editing and whitespace ----
            [Keys.Space] = "Space",
            [Keys.Enter] = "Enter",
            [Keys.Tab] = "Tab",
            [Keys.Back] = "Backspace",
            [Keys.Escape] = "Escape",
            [Keys.Delete] = "Delete",
            [Keys.Insert] = "Insert",

            // ---- navigation ----
            [Keys.Left] = "ArrowLeft",
            [Keys.Up] = "ArrowUp",
            [Keys.Right] = "ArrowRight",
            [Keys.Down] = "ArrowDown",
            [Keys.Home] = "Home",
            [Keys.End] = "End",
            [Keys.PageUp] = "PageUp",
            [Keys.PageDown] = "PageDown",

            // ---- modifiers ----
            [Keys.ShiftKey] = "ShiftLeft",
            [Keys.LShiftKey] = "ShiftLeft",
            [Keys.RShiftKey] = "ShiftRight",
            [Keys.ControlKey] = "ControlLeft",
            [Keys.LControlKey] = "ControlLeft",
            [Keys.RControlKey] = "ControlRight",
            [Keys.Menu] = "AltLeft",
            [Keys.LMenu] = "AltLeft",
            [Keys.RMenu] = "AltRight",
            [Keys.LWin] = "MetaLeft",
            [Keys.RWin] = "MetaRight",
            [Keys.Apps] = "ContextMenu",

            // ---- locks and system ----
            [Keys.CapsLock] = "CapsLock",
            [Keys.NumLock] = "NumLock",
            [Keys.Scroll] = "ScrollLock",
            [Keys.PrintScreen] = "PrintScreen",
            [Keys.Pause] = "Pause",

            // ---- function row ----
            [Keys.F1] = "F1", [Keys.F2] = "F2", [Keys.F3] = "F3", [Keys.F4] = "F4",
            [Keys.F5] = "F5", [Keys.F6] = "F6", [Keys.F7] = "F7", [Keys.F8] = "F8",
            [Keys.F9] = "F9", [Keys.F10] = "F10", [Keys.F11] = "F11", [Keys.F12] = "F12",
            [Keys.F13] = "F13", [Keys.F14] = "F14", [Keys.F15] = "F15", [Keys.F16] = "F16",
            [Keys.F17] = "F17", [Keys.F18] = "F18", [Keys.F19] = "F19", [Keys.F20] = "F20",
            [Keys.F21] = "F21", [Keys.F22] = "F22", [Keys.F23] = "F23", [Keys.F24] = "F24",

            // ---- numpad ----
            [Keys.NumPad0] = "Numpad0", [Keys.NumPad1] = "Numpad1", [Keys.NumPad2] = "Numpad2",
            [Keys.NumPad3] = "Numpad3", [Keys.NumPad4] = "Numpad4", [Keys.NumPad5] = "Numpad5",
            [Keys.NumPad6] = "Numpad6", [Keys.NumPad7] = "Numpad7", [Keys.NumPad8] = "Numpad8",
            [Keys.NumPad9] = "Numpad9",
            [Keys.Add] = "NumpadAdd",
            [Keys.Subtract] = "NumpadSubtract",
            [Keys.Multiply] = "NumpadMultiply",
            [Keys.Divide] = "NumpadDivide",
            [Keys.Decimal] = "NumpadDecimal",

            // ---- punctuation, by position on a US board ----
            [Keys.OemMinus] = "Minus",
            [Keys.Oemplus] = "Equal",
            [Keys.OemOpenBrackets] = "BracketLeft",
            [Keys.OemCloseBrackets] = "BracketRight",
            [Keys.OemPipe] = "Backslash",
            [Keys.OemSemicolon] = "Semicolon",
            [Keys.OemQuotes] = "Quote",
            [Keys.Oemcomma] = "Comma",
            [Keys.OemPeriod] = "Period",
            [Keys.OemQuestion] = "Slash",
            [Keys.Oemtilde] = "Backquote",
            [Keys.OemBackslash] = "IntlBackslash",
        };

    /// <summary>The W3C code for a key, or null if this harness has no name for it.</summary>
    /// <remarks>
    /// Null rather than a guess. An unmapped key that travelled as something plausible
    /// would press the wrong key on someone else's machine, which is a far worse outcome
    /// than doing nothing and saying so.
    /// </remarks>
    public static string? CodeFor(Keys key) => Table.GetValueOrDefault(key & Keys.KeyCode);

    /// <summary>The modifier codes currently held, in the order a person would press them.</summary>
    /// <remarks>
    /// Read from <see cref="Control.ModifierKeys"/> rather than accumulated from key
    /// events, so a modifier released while the viewer did not have focus cannot leave a
    /// phantom latched here.
    /// </remarks>
    public static IReadOnlyList<string> HeldModifiers()
    {
        var mods = Control.ModifierKeys;
        var held = new List<string>(3);

        if (mods.HasFlag(Keys.Control)) held.Add("ControlLeft");
        if (mods.HasFlag(Keys.Alt)) held.Add("AltLeft");
        if (mods.HasFlag(Keys.Shift)) held.Add("ShiftLeft");

        return held;
    }

    /// <summary>Whether a key is itself a modifier, and so already covered by the modifier list.</summary>
    public static bool IsModifier(Keys key) => (key & Keys.KeyCode) switch
    {
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => true,
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => true,
        Keys.Menu or Keys.LMenu or Keys.RMenu => true,
        Keys.LWin or Keys.RWin => true,
        _ => false,
    };
}
