namespace Techee.Windows.Host;

/// <summary>
/// Translates W3C <c>KeyboardEvent.code</c> names into what <c>SendInput</c> needs.
/// </summary>
/// <remarks>
/// <para>
/// The protocol carries key <i>positions</i>, not characters: <c>KeyA</c> means "the key
/// where A sits on a US QWERTY board", which on a French AZERTY board is the one that
/// types <c>q</c>. A scan code means exactly the same thing, which is why this maps to
/// scan codes and lets Windows apply the active layout, rather than mapping to virtual
/// keys and freezing the sender's layout onto the host.
/// </para>
/// <para>
/// Mapping through <c>VkKeyScan</c> or the characters themselves would be the tempting
/// shortcut and is wrong in a way that only shows up on a machine whose layout differs
/// from the tester's: <c>Ctrl</c>+<c>KeyZ</c> would arrive as <c>Ctrl</c>+<c>W</c> on
/// AZERTY. Undo becoming close-window is a bad first impression of remote control.
/// </para>
/// <para>
/// Pure data with no Windows dependency, so the whole table is unit-testable on any
/// runner — the point of extracting it rather than inlining it in the injector.
/// </para>
/// </remarks>
public static class KeyMap
{
    /// <summary>
    /// Set-1 scan code plus the US virtual key, per W3C code.
    /// </summary>
    /// <remarks>
    /// The virtual key is carried for diagnostics and for the handful of keys that have
    /// no scan code at all; injection uses the scan code whenever there is one.
    /// <c>Extended</c> marks the keys that live behind an <c>E0</c> prefix — the "grey"
    /// navigation cluster, the right-hand modifiers, and the numpad's divide and enter.
    /// Omitting it is the classic bug that makes the arrow keys behave like the numpad.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, KeyStroke> Table =
        new Dictionary<string, KeyStroke>(StringComparer.Ordinal)
        {
            // ---- letters, in scan-code order rather than alphabetical ----
            ["KeyQ"] = new(0x51, 0x10, false),
            ["KeyW"] = new(0x57, 0x11, false),
            ["KeyE"] = new(0x45, 0x12, false),
            ["KeyR"] = new(0x52, 0x13, false),
            ["KeyT"] = new(0x54, 0x14, false),
            ["KeyY"] = new(0x59, 0x15, false),
            ["KeyU"] = new(0x55, 0x16, false),
            ["KeyI"] = new(0x49, 0x17, false),
            ["KeyO"] = new(0x4F, 0x18, false),
            ["KeyP"] = new(0x50, 0x19, false),
            ["KeyA"] = new(0x41, 0x1E, false),
            ["KeyS"] = new(0x53, 0x1F, false),
            ["KeyD"] = new(0x44, 0x20, false),
            ["KeyF"] = new(0x46, 0x21, false),
            ["KeyG"] = new(0x47, 0x22, false),
            ["KeyH"] = new(0x48, 0x23, false),
            ["KeyJ"] = new(0x4A, 0x24, false),
            ["KeyK"] = new(0x4B, 0x25, false),
            ["KeyL"] = new(0x4C, 0x26, false),
            ["KeyZ"] = new(0x5A, 0x2C, false),
            ["KeyX"] = new(0x58, 0x2D, false),
            ["KeyC"] = new(0x43, 0x2E, false),
            ["KeyV"] = new(0x56, 0x2F, false),
            ["KeyB"] = new(0x42, 0x30, false),
            ["KeyN"] = new(0x4E, 0x31, false),
            ["KeyM"] = new(0x4D, 0x32, false),

            // ---- digit row ----
            ["Digit1"] = new(0x31, 0x02, false),
            ["Digit2"] = new(0x32, 0x03, false),
            ["Digit3"] = new(0x33, 0x04, false),
            ["Digit4"] = new(0x34, 0x05, false),
            ["Digit5"] = new(0x35, 0x06, false),
            ["Digit6"] = new(0x36, 0x07, false),
            ["Digit7"] = new(0x37, 0x08, false),
            ["Digit8"] = new(0x38, 0x09, false),
            ["Digit9"] = new(0x39, 0x0A, false),
            ["Digit0"] = new(0x30, 0x0B, false),

            // ---- punctuation ----
            ["Minus"] = new(0xBD, 0x0C, false),
            ["Equal"] = new(0xBB, 0x0D, false),
            ["BracketLeft"] = new(0xDB, 0x1A, false),
            ["BracketRight"] = new(0xDD, 0x1B, false),
            ["Backslash"] = new(0xDC, 0x2B, false),
            ["Semicolon"] = new(0xBA, 0x27, false),
            ["Quote"] = new(0xDE, 0x28, false),
            ["Backquote"] = new(0xC0, 0x29, false),
            ["Comma"] = new(0xBC, 0x33, false),
            ["Period"] = new(0xBE, 0x34, false),
            ["Slash"] = new(0xBF, 0x35, false),
            ["IntlBackslash"] = new(0xE2, 0x56, false),
            ["IntlRo"] = new(0xC1, 0x73, false),
            ["IntlYen"] = new(0xFF, 0x7D, false),

            // ---- editing and whitespace ----
            ["Escape"] = new(0x1B, 0x01, false),
            ["Backspace"] = new(0x08, 0x0E, false),
            ["Tab"] = new(0x09, 0x0F, false),
            ["Enter"] = new(0x0D, 0x1C, false),
            ["Space"] = new(0x20, 0x39, false),
            ["CapsLock"] = new(0x14, 0x3A, false),

            // ---- modifiers. Right-hand Control and Alt are extended; the left pair are not ----
            ["ShiftLeft"] = new(0xA0, 0x2A, false),
            ["ShiftRight"] = new(0xA1, 0x36, false),
            ["ControlLeft"] = new(0xA2, 0x1D, false),
            ["ControlRight"] = new(0xA3, 0x1D, true),
            ["AltLeft"] = new(0xA4, 0x38, false),
            ["AltRight"] = new(0xA5, 0x38, true),
            ["MetaLeft"] = new(0x5B, 0x5B, true),
            ["MetaRight"] = new(0x5C, 0x5C, true),
            ["ContextMenu"] = new(0x5D, 0x5D, true),

            // ---- function row ----
            ["F1"] = new(0x70, 0x3B, false),
            ["F2"] = new(0x71, 0x3C, false),
            ["F3"] = new(0x72, 0x3D, false),
            ["F4"] = new(0x73, 0x3E, false),
            ["F5"] = new(0x74, 0x3F, false),
            ["F6"] = new(0x75, 0x40, false),
            ["F7"] = new(0x76, 0x41, false),
            ["F8"] = new(0x77, 0x42, false),
            ["F9"] = new(0x78, 0x43, false),
            ["F10"] = new(0x79, 0x44, false),
            ["F11"] = new(0x7A, 0x57, false),
            ["F12"] = new(0x7B, 0x58, false),
            ["F13"] = new(0x7C, 0x64, false),
            ["F14"] = new(0x7D, 0x65, false),
            ["F15"] = new(0x7E, 0x66, false),
            ["F16"] = new(0x7F, 0x67, false),
            ["F17"] = new(0x80, 0x68, false),
            ["F18"] = new(0x81, 0x69, false),
            ["F19"] = new(0x82, 0x6A, false),
            ["F20"] = new(0x83, 0x6B, false),
            ["F21"] = new(0x84, 0x6C, false),
            ["F22"] = new(0x85, 0x6D, false),
            ["F23"] = new(0x86, 0x6E, false),
            ["F24"] = new(0x87, 0x76, false),

            // ---- the grey navigation cluster. All extended: these share scan codes with
            //      the numpad, and the E0 prefix is the only thing telling them apart ----
            ["Insert"] = new(0x2D, 0x52, true),
            ["Delete"] = new(0x2E, 0x53, true),
            ["Home"] = new(0x24, 0x47, true),
            ["End"] = new(0x23, 0x4F, true),
            ["PageUp"] = new(0x21, 0x49, true),
            ["PageDown"] = new(0x22, 0x51, true),
            ["ArrowUp"] = new(0x26, 0x48, true),
            ["ArrowDown"] = new(0x28, 0x50, true),
            ["ArrowLeft"] = new(0x25, 0x4B, true),
            ["ArrowRight"] = new(0x27, 0x4D, true),

            ["PrintScreen"] = new(0x2C, 0x37, true),
            ["ScrollLock"] = new(0x91, 0x46, false),
            ["Pause"] = new(0x13, 0x45, false),

            // ---- numpad ----
            ["NumLock"] = new(0x90, 0x45, true),
            ["Numpad0"] = new(0x60, 0x52, false),
            ["Numpad1"] = new(0x61, 0x4F, false),
            ["Numpad2"] = new(0x62, 0x50, false),
            ["Numpad3"] = new(0x63, 0x51, false),
            ["Numpad4"] = new(0x64, 0x4B, false),
            ["Numpad5"] = new(0x65, 0x4C, false),
            ["Numpad6"] = new(0x66, 0x4D, false),
            ["Numpad7"] = new(0x67, 0x47, false),
            ["Numpad8"] = new(0x68, 0x48, false),
            ["Numpad9"] = new(0x69, 0x49, false),
            ["NumpadDecimal"] = new(0x6E, 0x53, false),
            ["NumpadAdd"] = new(0x6B, 0x4E, false),
            ["NumpadSubtract"] = new(0x6D, 0x4A, false),
            ["NumpadMultiply"] = new(0x6A, 0x37, false),
            ["NumpadDivide"] = new(0x6F, 0x35, true),
            ["NumpadEnter"] = new(0x0D, 0x1C, true),

            // ---- media keys. No scan code, so these inject by virtual key ----
            ["AudioVolumeMute"] = new(0xAD, 0x00, false),
            ["AudioVolumeDown"] = new(0xAE, 0x00, false),
            ["AudioVolumeUp"] = new(0xAF, 0x00, false),
            ["MediaTrackNext"] = new(0xB0, 0x00, false),
            ["MediaTrackPrevious"] = new(0xB1, 0x00, false),
            ["MediaStop"] = new(0xB2, 0x00, false),
            ["MediaPlayPause"] = new(0xB3, 0x00, false),
        };

    /// <summary>How many key positions this build can inject. Pinned by a test.</summary>
    public static int Count => Table.Count;

    /// <summary>
    /// Resolves a W3C code, or null if this build has no mapping for it.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary answer for a key Techee does not model, not an error. The
    /// caller drops the command, exactly as the protocol requires for anything it cannot
    /// enforce or execute — guessing a virtual key from an unrecognised name is how a
    /// stray keystroke ends up somewhere surprising.
    /// </remarks>
    public static KeyStroke? Resolve(string? code) =>
        code is not null && Table.TryGetValue(code, out var stroke) ? stroke : null;

    /// <summary>Whether a code names a modifier, and so may be held across another key.</summary>
    /// <remarks>
    /// Used to bound what a <c>mods</c> array may contain. Without this a controller
    /// could list <c>Delete</c> as a "modifier" and have it held down around every
    /// keystroke in the session.
    /// </remarks>
    public static bool IsModifier(string? code) => code is
        "ShiftLeft" or "ShiftRight" or
        "ControlLeft" or "ControlRight" or
        "AltLeft" or "AltRight" or
        "MetaLeft" or "MetaRight";
}
