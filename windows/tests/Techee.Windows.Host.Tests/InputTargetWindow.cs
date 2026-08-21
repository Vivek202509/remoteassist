using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Techee.Windows.Host.Tests;

/// <summary>One window message the target actually received.</summary>
public readonly record struct TargetMessage(uint Message, int ClientX, int ClientY, nuint WParam)
{
    public override string ToString() => $"{Name} at ({ClientX},{ClientY})";

    public string Name => Message switch
    {
        InputTargetWindow.WM_MOUSEMOVE => "WM_MOUSEMOVE",
        InputTargetWindow.WM_LBUTTONDOWN => "WM_LBUTTONDOWN",
        InputTargetWindow.WM_LBUTTONUP => "WM_LBUTTONUP",
        InputTargetWindow.WM_RBUTTONDOWN => "WM_RBUTTONDOWN",
        InputTargetWindow.WM_RBUTTONUP => "WM_RBUTTONUP",
        InputTargetWindow.WM_MOUSEWHEEL => "WM_MOUSEWHEEL",
        InputTargetWindow.WM_KEYDOWN => "WM_KEYDOWN",
        InputTargetWindow.WM_KEYUP => "WM_KEYUP",
        InputTargetWindow.WM_CHAR => "WM_CHAR",
        _ => $"0x{Message:X4}",
    };
}

/// <summary>
/// A real top-level window, created for one test, that records what Windows delivers to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not Notepad.</b> Driving a shipped application and looking at it proves the
/// right thing but proves it to a human, and the assertion becomes "the tester says a
/// menu opened". Windows 11's Notepad is also a WinUI application with no classic edit
/// control to read back, so even the text check would be unreliable. A window created by
/// the test is an ordinary <c>HWND</c> receiving ordinary <c>WM_*</c> messages through the
/// ordinary input queue — the same path Notepad's messages take — and it can say exactly
/// which message arrived at which client coordinate.
/// </para>
/// <para>
/// The child is a real <c>EDIT</c> control from <c>comctl32</c>, not a custom one, so
/// typed text is validated by the same control Win32 applications have always used.
/// </para>
/// <para>
/// <b>This window steals focus.</b> Every test that uses it is gated behind
/// <c>TECHEE_REAL_INPUT=1</c>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class InputTargetWindow : IDisposable
{
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_QUIT = 0x0012;
    internal const uint WM_SETTEXT = 0x000C;
    internal const uint WM_GETTEXT = 0x000D;
    internal const uint WM_GETTEXTLENGTH = 0x000E;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;
    internal const uint WM_CHAR = 0x0102;
    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_MOUSEWHEEL = 0x020A;

    private const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CHILD = 0x40000000;
    private const int WS_BORDER = 0x00800000;
    private const int ES_MULTILINE = 0x0004;
    private const int SW_SHOW = 5;
    private const int HWND_TOPMOST = -1;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly ConcurrentQueue<TargetMessage> _messages = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _pump;

    // Held in a field for the window's whole life. If it were collected, the window
    // procedure pointer would dangle and the process would die on the next message.
    private readonly WndProc _wndProc;

    private IntPtr _hwnd;
    private IntPtr _edit;
    private bool _disposed;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, nuint wParam, nint lParam);

    public InputTargetWindow(string title = "Techee input target")
    {
        _wndProc = WindowProcedure;

        _pump = new Thread(() => Run(title))
        {
            IsBackground = true,
            Name = "techee-input-target",
        };

        // A window belongs to the thread that created it, and that thread must run the
        // message loop. STA because the shell is happier with it and costs nothing here.
        _pump.SetApartmentState(ApartmentState.STA);
        _pump.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("the input target window did not come up");
    }

    /// <summary>The window handle. Valid until disposal.</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>Everything the window has received, in order.</summary>
    public IReadOnlyList<TargetMessage> Messages => [.. _messages];

    /// <summary>Messages of one type.</summary>
    public IReadOnlyList<TargetMessage> Received(uint message) =>
        [.. _messages.Where(m => m.Message == message)];

    public void ClearMessages() => _messages.Clear();

    /// <summary>Client height reserved for the parent window, above the edit control.</summary>
    private const int CanvasClientHeight = 300;

    /// <summary>The window's rectangle in screen coordinates.</summary>
    public (int Left, int Top, int Right, int Bottom) ScreenBounds
    {
        get
        {
            GetWindowRect(_hwnd, out var r);
            return (r.Left, r.Top, r.Right, r.Bottom);
        }
    }

    /// <summary>
    /// The screen rectangle of the parent's own client area, excluding the edit control.
    /// </summary>
    /// <remarks>
    /// The only region where a click is guaranteed to reach this window's procedure. A
    /// click over the child edit control is delivered to the child and is invisible here,
    /// which looks exactly like input having failed.
    /// </remarks>
    public (int Left, int Top, int Right, int Bottom) CanvasScreenBounds
    {
        get
        {
            GetClientRect(_hwnd, out var client);
            var origin = new POINT { X = 0, Y = 0 };
            ClientToScreen(_hwnd, ref origin);

            return (origin.X, origin.Y,
                    origin.X + client.Right,
                    origin.Y + Math.Min(client.Bottom, CanvasClientHeight));
        }
    }

    /// <summary>The screen rectangle of the child edit control.</summary>
    public (int Left, int Top, int Right, int Bottom) EditScreenBounds
    {
        get
        {
            GetWindowRect(_edit, out var r);
            return (r.Left, r.Top, r.Right, r.Bottom);
        }
    }

    /// <summary>Brings the window to the front and gives it the keyboard focus.</summary>
    /// <remarks>
    /// Windows refuses foreground activation to a process that does not own the
    /// foreground, so this is best-effort. Tests that depend on focus check for it
    /// rather than assuming it.
    /// </remarks>
    public bool BringToFront()
    {
        // Topmost, keeping its current position and size (SWP_NOSIZE | SWP_NOMOVE), so
        // nothing the tester has open can sit in front of the click target.
        SetWindowPos(_hwnd, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, SWP_SHOWWINDOW | 0x0001 | 0x0002);
        ShowWindow(_hwnd, SW_SHOW);
        var ok = SetForegroundWindow(_hwnd);
        SetFocus(_edit);
        Thread.Sleep(150);
        return ok;
    }

    /// <summary>Whether this window is the one Windows will deliver input to.</summary>
    public bool IsForeground => GetForegroundWindow() == _hwnd;

    /// <summary>The text currently in the child edit control.</summary>
    public string EditText
    {
        get
        {
            var length = (int)SendMessageW(_edit, WM_GETTEXTLENGTH, 0, 0);
            if (length <= 0) return string.Empty;

            var buffer = Marshal.AllocHGlobal((length + 1) * sizeof(char));
            try
            {
                SendMessageW(_edit, WM_GETTEXT, (nuint)(length + 1), buffer);
                return Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public void ClearEditText() => SendMessageW(_edit, WM_SETTEXT, 0, Marshal.StringToHGlobalUni(""));

    /// <summary>Waits for a message to arrive, so tests never sleep a fixed amount.</summary>
    public bool WaitFor(uint message, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_messages.Any(m => m.Message == message)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    private void Run(string title)
    {
        var className = $"TecheeInputTarget_{Guid.NewGuid():N}";

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(IntPtr.Zero),
            lpszClassName = Marshal.StringToHGlobalUni(className),
            hCursor = LoadCursorW(IntPtr.Zero, 32512), // IDC_ARROW
            hbrBackground = (IntPtr)6,                 // COLOR_WINDOW + 1
        };

        if (RegisterClassExW(ref wc) == 0)
        {
            _ready.Set();
            return;
        }

        _hwnd = CreateWindowExW(
            0, className, title, WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            100, 100, 600, 500, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _ready.Set();
            return;
        }

        // The edit control sits at the BOTTOM, leaving the upper client area clear.
        // A child window receives its own mouse messages, so anything clicked over the
        // edit never reaches the parent's procedure — putting it here keeps the pointer
        // tests and the typing test from fighting over the same pixels.
        _edit = CreateWindowExW(
            0, "EDIT", "", WS_CHILD | WS_VISIBLE | WS_BORDER | ES_MULTILINE,
            10, CanvasClientHeight + 10, 560, 100, _hwnd, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        ShowWindow(_hwnd, SW_SHOW);
        _ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private IntPtr WindowProcedure(IntPtr hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_MOUSEMOVE:
            case WM_LBUTTONDOWN:
            case WM_LBUTTONUP:
            case WM_RBUTTONDOWN:
            case WM_RBUTTONUP:
            case WM_MOUSEWHEEL:
            case WM_KEYDOWN:
            case WM_KEYUP:
            case WM_CHAR:
                // lParam packs the client-relative position for mouse messages as two
                // signed 16-bit halves. Sign matters: a click above or left of the
                // client area is negative, and reading it unsigned reports 65000-ish.
                _messages.Enqueue(new TargetMessage(
                    msg,
                    unchecked((short)(lParam & 0xFFFF)),
                    unchecked((short)((lParam >> 16) & 0xFFFF)),
                    wParam));
                break;

            case WM_DESTROY:
                PostQuitMessage(0);
                break;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, 0, 0);

        _pump.Join(TimeSpan.FromSeconds(3));
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref MSG msg);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DispatchMessageW(ref MSG msg);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr hwnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SendMessageW(IntPtr hwnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetFocus(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr LoadCursorW(IntPtr instance, int cursor);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetModuleHandleW(IntPtr name);
}
