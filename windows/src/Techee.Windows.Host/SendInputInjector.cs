using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Techee.Windows.Host;

/// <summary>
/// Injects input through <c>user32!SendInput</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absolute positioning, always.</b> Relative mouse movement is subject to pointer
/// acceleration, so a remote drag would land somewhere other than where the operator
/// released it and the error would accumulate over a session. Every move carries
/// <c>MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK</c>, and the 0..65535 conversion
/// normalises against the union of all monitors — see
/// <see cref="DisplayGeometry.ToAbsolute"/>.
/// </para>
/// <para>
/// <b>A press is one batch.</b> Move and button-down go to a single <c>SendInput</c>
/// call, because two calls can be interleaved with real hardware input and with each
/// other; a click can then land at the position of whatever moved the cursor in between.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> A failed injection sets <see cref="LastError"/> and
/// increments a counter. The caller is draining commands that originated on a data
/// channel, and an exception on that path would end the session — or, on an unattended
/// host, the only route back into the machine.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class SendInputInjector : IInputInjector
{
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private IReadOnlyList<DisplayInfo> _displays;
    private long _injected;
    private long _failed;
    private volatile bool _disposed;

    /// <summary>
    /// What this injector is currently holding down.
    /// </summary>
    /// <remarks>
    /// Tracked so <see cref="ReleaseAllPressed"/> can release exactly what Techee
    /// pressed and nothing else. Both sets are guarded by <see cref="_gate"/> along with
    /// the injection that mutates them, so a release cannot interleave with a press and
    /// miss it.
    /// </remarks>
    private readonly HashSet<PointerButton> _buttonsDown = [];

    private readonly HashSet<KeyStroke> _keysDown = [];

    public SendInputInjector(IReadOnlyList<DisplayInfo> displays, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(displays);
        _displays = displays;
        _log = log;
    }

    /// <summary>
    /// The monitor layout absolute coordinates are normalised against.
    /// </summary>
    /// <remarks>
    /// Settable because monitors are hot-pluggable and a stale layout silently sends
    /// every click to the wrong place. Not re-enumerated per event: that is a DXGI call,
    /// and doing it 250 times a second during a drag would cost more than the injection.
    /// </remarks>
    public IReadOnlyList<DisplayInfo> Displays
    {
        get { lock (_gate) return _displays; }
        set { lock (_gate) _displays = value ?? throw new ArgumentNullException(nameof(value)); }
    }

    /// <summary>Events accepted by the OS.</summary>
    public long Injected => Interlocked.Read(ref _injected);

    /// <summary>Events the OS refused. Non-zero almost always means UIPI or the secure desktop.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    public string? LastError { get; private set; }

    public bool IsAvailable => UnavailableReason is null;

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately <b>not</b> cached. The secure desktop appears and disappears with
    /// every UAC prompt, and a value sampled at session start would be wrong for most of
    /// the session.
    /// </remarks>
    public string? UnavailableReason => SecureDesktop.InputBlockedReason();

    // ---- pointer ----

    public void MoveTo(int x, int y)
    {
        var (ax, ay) = Absolute(x, y);
        Send([Mouse(ax, ay, MOUSEEVENTF_MOVE, 0)]);
    }

    public void ButtonDown(int x, int y, PointerButton button) => Press(x, y, button, down: true);

    public void ButtonUp(int x, int y, PointerButton button) => Press(x, y, button, down: false);

    private void Press(int x, int y, PointerButton button, bool down)
    {
        var (flag, data) = ButtonFlags(button, down);
        if (flag == 0) return;

        var (ax, ay) = Absolute(x, y);

        lock (_gate)
        {
            // Move and press in one call. Splitting them is the bug that makes a remote
            // click land wherever the local mouse happened to be a millisecond later.
            if (!Send([Mouse(ax, ay, MOUSEEVENTF_MOVE | flag, data)])) return;

            // Recorded only after the OS accepted it. Tracking a press Windows refused
            // would make teardown send a release for a button that was never down.
            if (down) _buttonsDown.Add(button);
            else _buttonsDown.Remove(button);
        }
    }

    public void Wheel(int x, int y, double notchesX, double notchesY)
    {
        var (ax, ay) = Absolute(x, y);

        var vertical = Notches(notchesY);
        var horizontal = Notches(notchesX);
        if (vertical == 0 && horizontal == 0) return;

        var events = new List<INPUT>(2);
        if (vertical != 0) events.Add(Mouse(ax, ay, MOUSEEVENTF_MOVE | MOUSEEVENTF_WHEEL, unchecked((uint)vertical)));
        if (horizontal != 0) events.Add(Mouse(ax, ay, MOUSEEVENTF_MOVE | MOUSEEVENTF_HWHEEL, unchecked((uint)horizontal)));

        Send([.. events]);
    }

    /// <summary>
    /// The largest scroll one frame may request, in notches.
    /// </summary>
    /// <remarks>
    /// Far more than any real gesture — a fast trackpad flick is a handful of notches —
    /// and small enough that the <c>WHEEL_DELTA</c> multiply cannot leave the range of
    /// an <c>int</c>.
    /// </remarks>
    private const double MaxNotchesPerEvent = 120;

    /// <summary>Converts protocol notches to Windows' <c>WHEEL_DELTA</c> units.</summary>
    /// <remarks>
    /// <para>
    /// Rounded away from zero, so a controller sending a very small delta still scrolls
    /// rather than being silently swallowed — a trackpad flick that does nothing reads
    /// as a broken session.
    /// </para>
    /// <para>
    /// <b>Clamped before the multiply, not after.</b> The codec only requires
    /// <c>dx</c>/<c>dy</c> to be finite, so a peer may legitimately send <c>1e300</c>.
    /// An out-of-range <c>double</c>→<c>int</c> conversion saturates on .NET Core 3.0
    /// and later, so that arrives as <c>int.MaxValue</c> — about seventeen million
    /// notches, handed to <c>SendInput</c> in response to a frame the peer fully
    /// controls. Clamping the notch count first keeps the conversion in range and the
    /// scroll proportional to what was asked for.
    /// </para>
    /// </remarks>
    internal static int Notches(double notches)
    {
        if (!double.IsFinite(notches) || notches == 0) return 0;

        var clamped = Math.Clamp(notches, -MaxNotchesPerEvent, MaxNotchesPerEvent);
        var rounded = (int)Math.Round(Math.Abs(clamped) * WHEEL_DELTA, MidpointRounding.AwayFromZero);
        if (rounded == 0) rounded = 1;

        return clamped < 0 ? -rounded : rounded;
    }

    private static (uint Flag, uint Data) ButtonFlags(PointerButton button, bool down) => button switch
    {
        PointerButton.Left => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
        PointerButton.Right => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
        PointerButton.Middle => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
        PointerButton.X1 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON1),
        PointerButton.X2 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON2),
        _ => (0u, 0u),
    };

    private (int X, int Y) Absolute(int virtualX, int virtualY)
    {
        IReadOnlyList<DisplayInfo> displays;
        lock (_gate) displays = _displays;
        return DisplayGeometry.ToAbsolute(displays, virtualX, virtualY);
    }

    // ---- keyboard ----

    public void KeyDown(KeyStroke stroke) => Key(stroke, up: false);

    public void KeyUp(KeyStroke stroke) => Key(stroke, up: true);

    private void Key(KeyStroke stroke, bool up)
    {
        lock (_gate)
        {
            if (!SendKey(stroke, up)) return;

            if (up) _keysDown.Remove(stroke);
            else _keysDown.Add(stroke);
        }
    }

    /// <summary>Emits one key transition. Assumes <see cref="_gate"/> is held.</summary>
    private bool SendKey(KeyStroke stroke, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0u;
        if (stroke.Extended) flags |= KEYEVENTF_EXTENDEDKEY;

        // Prefer the scan code: it names a physical key position, which is what the
        // protocol carries, and it lets the host's own layout decide the character.
        // Only the media keys, which have no scan code, fall back to the virtual key.
        var byScan = stroke.ScanCode != 0;
        if (byScan) flags |= KEYEVENTF_SCANCODE;

        return Send([Keyboard(byScan ? (ushort)0 : stroke.VirtualKey, stroke.ScanCode, flags)]);
    }

    public int PressedCount
    {
        get { lock (_gate) return _buttonsDown.Count + _keysDown.Count; }
    }

    public void ReleaseAllPressed()
    {
        lock (_gate)
        {
            // Snapshotted before releasing, because releasing mutates the sets.
            var keys = _keysDown.ToArray();
            var buttons = _buttonsDown.ToArray();

            _keysDown.Clear();
            _buttonsDown.Clear();

            if (keys.Length + buttons.Length == 0) return;

            // Cleared before the sends rather than after, so a send that throws or is
            // refused cannot leave this method looping on the next call. A release we
            // failed to deliver is not recoverable by trying again with the same
            // desktop refusing us.
            foreach (var key in keys) SendKey(key, up: true);

            // Buttons released at the cursor's current position. There is nowhere else
            // sensible: the session that knew where the drag was going is gone.
            var (ax, ay) = Absolute(CurrentCursorFallback, CurrentCursorFallback);
            foreach (var button in buttons)
            {
                var (flag, data) = ButtonFlags(button, down: false);
                if (flag != 0) Send([Mouse(ax, ay, flag, data)]);
            }

            _log?.Invoke($"[input] released {keys.Length} key(s) and {buttons.Length} button(s) on teardown");
        }
    }

    /// <summary>
    /// Sentinel meaning "wherever the cursor already is".
    /// </summary>
    /// <remarks>
    /// A button release is emitted without <c>MOUSEEVENTF_MOVE</c>, so the coordinates
    /// in the event are ignored by Windows entirely. Passing zero would still be
    /// correct; the named constant exists so the next reader does not think a release
    /// warps the pointer to the top-left corner.
    /// </remarks>
    private const int CurrentCursorFallback = 0;

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // One down/up pair per UTF-16 code unit. Surrogate pairs are delivered as two
        // consecutive units in the same batch, which is how Windows expects an
        // astral-plane character — splitting them across calls produces two replacement
        // characters instead of one emoji.
        var events = new INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            events[i * 2] = Keyboard(0, text[i], KEYEVENTF_UNICODE);
            events[i * 2 + 1] = Keyboard(0, text[i], KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
        }

        // Batched in chunks so one very long string cannot monopolise the input queue.
        const int chunk = 128;
        for (var offset = 0; offset < events.Length; offset += chunk)
        {
            var slice = events[offset..Math.Min(offset + chunk, events.Length)];
            if (!Send(slice)) return;
        }
    }

    // ---- the one call ----

    private bool Send(INPUT[] events)
    {
        if (events.Length == 0 || _disposed) return false;

        var accepted = SendInput((uint)events.Length, events, Marshal.SizeOf<INPUT>());
        if (accepted == events.Length)
        {
            Interlocked.Add(ref _injected, accepted);
            return true;
        }

        Interlocked.Increment(ref _failed);

        var error = Marshal.GetLastPInvokeError();
        // ERROR_ACCESS_DENIED here is not a permissions bug to be fixed by elevating:
        // it is UIPI refusing input to a higher-integrity window, or the secure desktop
        // holding the input queue. Both are Windows working as intended.
        var reason = UnavailableReason
                     ?? (error == ERROR_ACCESS_DENIED
                         ? "blocked by UIPI — the foreground window runs at a higher integrity level"
                         : $"SendInput accepted {accepted} of {events.Length} events (error {error})");

        LastError = reason;
        _log?.Invoke($"[input] {reason}");
        return false;
    }

    private static INPUT Mouse(int x, int y, uint flags, uint data) => new()
    {
        Type = INPUT_MOUSE,
        Union = new InputUnion
        {
            Mouse = new MOUSEINPUT
            {
                Dx = x,
                Dy = y,
                MouseData = data,
                Flags = flags | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                Time = 0,
                ExtraInfo = ExtraInfo,
            },
        },
    };

    private static INPUT Keyboard(ushort vk, ushort scan, uint flags) => new()
    {
        Type = INPUT_KEYBOARD,
        Union = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                Vk = vk,
                Scan = scan,
                Flags = flags,
                Time = 0,
                ExtraInfo = ExtraInfo,
            },
        },
    };

    /// <summary>
    /// Releases anything still held, then stops accepting work.
    /// </summary>
    /// <remarks>
    /// The release happens <b>before</b> the disposal flag is set, because
    /// <see cref="Send"/> refuses once disposed — reversing the two would silently skip
    /// the cleanup this method exists for.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;

        ReleaseAllPressed();
        _disposed = true;
    }

    public override string ToString() =>
        $"SendInputInjector(displays={Displays.Count}, injected={Injected}, " +
        $"failed={Failed}, held={PressedCount})";

    // ---- interop ----

    /// <summary>
    /// Tags every event Techee injects.
    /// </summary>
    /// <remarks>
    /// A low-level hook can read this from <c>dwExtraInfo</c> and tell remote input from
    /// local. Techee does not install such a hook; the tag costs nothing and makes the
    /// events attributable to anything that does, including a future loop-prevention
    /// check if Techee ever drives a second Techee.
    /// </remarks>
    private const nuint ExtraInfo = 0x7EC4EE;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint XBUTTON1 = 0x0001;
    private const uint XBUTTON2 = 0x0002;
    private const int WHEEL_DELTA = 120;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const int ERROR_ACCESS_DENIED = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    /// <remarks>
    /// A real C union. The explicit layout is not an optimisation — <c>INPUT</c> is
    /// declared this way in <c>winuser.h</c>, and its size is what <c>cbSize</c> must
    /// report for <c>SendInput</c> to accept the array at all.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    /// <summary>
    /// The size <c>cbSize</c> reports to <c>SendInput</c>.
    /// </summary>
    /// <remarks>
    /// 40 bytes on x64, 28 on x86. Exposed so a test can pin it: if the managed
    /// <c>INPUT</c> ever stops matching <c>winuser.h</c>, <c>SendInput</c> rejects the
    /// whole array and injects nothing, and there is no other symptom.
    /// </remarks>
    internal static int NativeInputSize => Marshal.SizeOf<INPUT>();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] INPUT[] inputs, int size);
}
