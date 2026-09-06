using Techee.Windows.Host;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// The W3C-code to scan-code table.
/// </summary>
/// <remarks>
/// Pure data, so every case that would otherwise need a second keyboard layout or a
/// numpad is an ordinary assertion. The cases chosen here are the ones that are wrong in
/// naive implementations rather than a transcription of the whole table.
/// </remarks>
public class KeyMapTests
{
    // ---- positions, not characters ----

    [Fact]
    public void A_letter_resolves_to_its_physical_scan_code()
    {
        // KeyA is the key where A sits on US QWERTY: set-1 scan code 0x1E. On AZERTY the
        // same physical key types 'q', and sending the scan code is what lets the host's
        // own layout decide that.
        var a = KeyMap.Resolve("KeyA");

        Assert.NotNull(a);
        Assert.Equal(0x1E, a!.Value.ScanCode);
        Assert.Equal(0x41, a.Value.VirtualKey);
        Assert.False(a.Value.Extended);
    }

    [Fact]
    public void The_letter_row_is_in_scan_code_order_not_alphabetical_order()
    {
        // Q W E R T Y are consecutive scan codes because they are physically adjacent.
        // A table built alphabetically would look plausible and be wrong for every key.
        Assert.Equal(0x10, KeyMap.Resolve("KeyQ")!.Value.ScanCode);
        Assert.Equal(0x11, KeyMap.Resolve("KeyW")!.Value.ScanCode);
        Assert.Equal(0x12, KeyMap.Resolve("KeyE")!.Value.ScanCode);
        Assert.Equal(0x13, KeyMap.Resolve("KeyR")!.Value.ScanCode);
        Assert.Equal(0x14, KeyMap.Resolve("KeyT")!.Value.ScanCode);
        Assert.Equal(0x15, KeyMap.Resolve("KeyY")!.Value.ScanCode);
    }

    [Fact]
    public void Digit0_follows_Digit9_because_that_is_where_it_sits_on_the_row()
    {
        // The digit row runs 1..9 then 0, so Digit0's scan code is the highest, not the
        // lowest. Deriving it as 0x02 + n puts every digit one place left.
        Assert.Equal(0x02, KeyMap.Resolve("Digit1")!.Value.ScanCode);
        Assert.Equal(0x0A, KeyMap.Resolve("Digit9")!.Value.ScanCode);
        Assert.Equal(0x0B, KeyMap.Resolve("Digit0")!.Value.ScanCode);
    }

    // ---- the extended-key flag ----

    [Fact]
    public void The_arrow_cluster_is_extended_and_the_numpad_is_not()
    {
        // This is the bug the flag exists to prevent: the arrows and the numpad share
        // scan codes and are told apart only by the E0 prefix. Without it, pressing Up
        // types '8' whenever NumLock is on.
        var up = KeyMap.Resolve("ArrowUp")!.Value;
        var numpad8 = KeyMap.Resolve("Numpad8")!.Value;

        Assert.Equal(0x48, up.ScanCode);
        Assert.Equal(0x48, numpad8.ScanCode);
        Assert.True(up.Extended);
        Assert.False(numpad8.Extended);
    }

    [Fact]
    public void Insert_Delete_Home_End_and_the_page_keys_are_all_extended()
    {
        foreach (var code in new[] { "Insert", "Delete", "Home", "End", "PageUp", "PageDown" })
            Assert.True(KeyMap.Resolve(code)!.Value.Extended, code);
    }

    [Fact]
    public void The_right_hand_modifiers_are_extended_and_the_left_hand_ones_are_not()
    {
        // Left and right Control share scan code 0x1D; only the prefix separates them.
        var left = KeyMap.Resolve("ControlLeft")!.Value;
        var right = KeyMap.Resolve("ControlRight")!.Value;

        Assert.Equal(left.ScanCode, right.ScanCode);
        Assert.False(left.Extended);
        Assert.True(right.Extended);

        Assert.False(KeyMap.Resolve("AltLeft")!.Value.Extended);
        Assert.True(KeyMap.Resolve("AltRight")!.Value.Extended);
    }

    [Fact]
    public void Numpad_enter_and_divide_are_extended_though_the_rest_of_the_numpad_is_not()
    {
        // NumpadEnter shares 0x1C with the main Enter and NumpadDivide shares 0x35 with
        // the Slash key. Both need the prefix; the digits do not.
        var enter = KeyMap.Resolve("NumpadEnter")!.Value;
        var divide = KeyMap.Resolve("NumpadDivide")!.Value;

        Assert.Equal(KeyMap.Resolve("Enter")!.Value.ScanCode, enter.ScanCode);
        Assert.True(enter.Extended);

        Assert.Equal(KeyMap.Resolve("Slash")!.Value.ScanCode, divide.ScanCode);
        Assert.True(divide.Extended);

        Assert.False(KeyMap.Resolve("NumpadAdd")!.Value.Extended);
        Assert.False(KeyMap.Resolve("Numpad5")!.Value.Extended);
    }

    // ---- unmapped keys ----

    [Fact]
    public void An_unknown_code_resolves_to_null_rather_than_a_guess()
    {
        // Guessing would put a real keystroke somewhere unpredictable. Null makes the
        // caller drop the command, which is what the protocol requires for anything it
        // cannot execute.
        Assert.Null(KeyMap.Resolve("KeyThatDoesNotExist"));
        Assert.Null(KeyMap.Resolve(""));
        Assert.Null(KeyMap.Resolve(null));
    }

    [Fact]
    public void Codes_are_matched_case_sensitively_as_the_W3C_defines_them()
    {
        // "keya" is not a KeyboardEvent.code. Accepting it would mean accepting input
        // from a sender that is not speaking the specification.
        Assert.NotNull(KeyMap.Resolve("KeyA"));
        Assert.Null(KeyMap.Resolve("keya"));
        Assert.Null(KeyMap.Resolve("KEYA"));
    }

    [Fact]
    public void The_media_keys_have_no_scan_code_and_fall_back_to_a_virtual_key()
    {
        // Injection branches on this: a zero scan code is the signal to send the virtual
        // key instead, and a table that invented a scan code here would send nothing.
        var mute = KeyMap.Resolve("AudioVolumeMute")!.Value;

        Assert.Equal(0, mute.ScanCode);
        Assert.Equal(0xAD, mute.VirtualKey);
    }

    // ---- modifiers ----

    [Fact]
    public void Only_the_eight_real_modifiers_count_as_modifiers()
    {
        foreach (var code in new[]
                 {
                     "ShiftLeft", "ShiftRight", "ControlLeft", "ControlRight",
                     "AltLeft", "AltRight", "MetaLeft", "MetaRight",
                 })
            Assert.True(KeyMap.IsModifier(code), code);
    }

    [Fact]
    public void A_non_modifier_cannot_be_held_across_another_key()
    {
        // Without this, a controller could list Delete in `mods` and have it pressed
        // around every keystroke of the session.
        Assert.False(KeyMap.IsModifier("Delete"));
        Assert.False(KeyMap.IsModifier("KeyA"));
        Assert.False(KeyMap.IsModifier("CapsLock"));
        Assert.False(KeyMap.IsModifier("Enter"));
        Assert.False(KeyMap.IsModifier(null));
    }

    // ---- the table as a whole ----

    [Fact]
    public void Every_mapped_key_has_either_a_scan_code_or_a_virtual_key()
    {
        // An entry with neither would inject nothing at all and look like a dropped
        // frame rather than a gap in the table.
        foreach (var code in AllCodes())
        {
            var stroke = KeyMap.Resolve(code)!.Value;
            Assert.True(stroke.ScanCode != 0 || stroke.VirtualKey != 0, code);
        }
    }

    [Fact]
    public void The_table_covers_the_keys_a_desktop_session_needs()
    {
        // A regression guard on scope rather than on contents: losing the function row
        // or the numpad in a refactor should fail here.
        Assert.True(KeyMap.Count >= 120, $"only {KeyMap.Count} keys mapped");

        foreach (var code in AllCodes()) Assert.NotNull(KeyMap.Resolve(code));
    }

    private static IEnumerable<string> AllCodes()
    {
        for (var c = 'A'; c <= 'Z'; c++) yield return $"Key{c}";
        for (var d = 0; d <= 9; d++) yield return $"Digit{d}";
        for (var f = 1; f <= 24; f++) yield return $"F{f}";
        for (var n = 0; n <= 9; n++) yield return $"Numpad{n}";

        yield return "Escape";
        yield return "Enter";
        yield return "Tab";
        yield return "Space";
        yield return "Backspace";
        yield return "CapsLock";
        yield return "ArrowUp";
        yield return "ArrowDown";
        yield return "ArrowLeft";
        yield return "ArrowRight";
        yield return "ShiftLeft";
        yield return "ControlLeft";
        yield return "AltLeft";
        yield return "MetaLeft";
        yield return "NumpadEnter";
        yield return "NumpadDivide";
        yield return "NumpadMultiply";
        yield return "NumpadAdd";
        yield return "NumpadSubtract";
        yield return "NumpadDecimal";
    }
}
