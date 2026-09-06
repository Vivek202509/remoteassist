using Techee.Windows.Host;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// A screen source driven by the test rather than by a GPU.
/// </summary>
/// <remarks>
/// Exists so the pump's pacing, adaptation, keyframe and drop behaviour are ordinary
/// assertions. On real hardware those are only observable under real load on a real
/// network, which is not a reproducible test.
/// </remarks>
public sealed class FakeScreenSource : IScreenSource
{
    private readonly object _gate = new();
    private byte[] _buffer = [];
    private int _width, _height;

    public FakeScreenSource(DisplayInfo? display = null)
    {
        Display = display ?? new DisplayInfo(
            "DISPLAY1", @"\\.\DISPLAY1", 0, 0, 1920, 1080, 1920, 1080, IsPrimary: true);
        Resize(1280, 720);
    }

    public DisplayInfo Display { get; }

    public (int Width, int Height) OutputSize
    {
        get { lock (_gate) return (_width, _height); }
    }

    public bool ProtectedContentMasked { get; set; }

    /// <summary>When true, <see cref="TryCapture"/> reports a timeout, as an idle desktop does.</summary>
    public bool SimulateTimeout { get; set; }

    /// <summary>When set, <see cref="TryCapture"/> throws it once, then clears.</summary>
    public Exception? ThrowOnce { get; set; }

    /// <summary>Extra row padding, to exercise the stride path a GPU staging texture has.</summary>
    public int ExtraStrideBytes { get; set; }

    public int ResizeCount { get; private set; }
    public int CaptureCount { get; private set; }

    public void Resize(int width, int height)
    {
        lock (_gate)
        {
            _width = width;
            _height = height;
            ResizeCount++;
            var stride = width * 4 + ExtraStrideBytes;
            _buffer = new byte[stride * height];

            // A recognisable pattern rather than zeroes, so a frame that silently
            // becomes all-black in the pipeline is visible in an assertion.
            for (var i = 0; i < _buffer.Length; i++) _buffer[i] = (byte)(i * 31 % 251);
        }
    }

    public bool TryCapture(TimeSpan timeout, FrameHandler onFrame)
    {
        if (ThrowOnce is { } e)
        {
            ThrowOnce = null;
            throw e;
        }

        if (SimulateTimeout)
        {
            Thread.Sleep(1);
            return false;
        }

        lock (_gate)
        {
            CaptureCount++;
            onFrame(_buffer, _width, _height, _width * 4 + ExtraStrideBytes);
            return true;
        }
    }

    public void Dispose() { }
}

/// <summary>An encoder that records what it was asked to do.</summary>
public sealed class FakeEncoder : IVideoEncoder
{
    private readonly object _gate = new();
    private bool _keyFrameRequested = true;

    public int TargetKbps { get; set; }

    public int EncodeCount { get; private set; }
    public int KeyFrameRequests { get; private set; }
    public List<(int Width, int Height)> Sizes { get; } = [];
    public List<int> TargetKbpsHistory { get; } = [];

    /// <summary>When true, <see cref="Encode"/> returns null, as a stalled encoder does.</summary>
    public bool ReturnNull { get; set; }

    /// <summary>Simulated per-frame cost, so encode-pressure adaptation can be driven.</summary>
    public TimeSpan EncodeDelay { get; set; }

    public void ForceKeyFrame()
    {
        lock (_gate)
        {
            _keyFrameRequested = true;
            KeyFrameRequests++;
        }
    }

    public byte[]? Encode(ReadOnlySpan<byte> i420, int width, int height)
    {
        if (EncodeDelay > TimeSpan.Zero) Thread.Sleep(EncodeDelay);

        lock (_gate)
        {
            EncodeCount++;
            Sizes.Add((width, height));
            TargetKbpsHistory.Add(TargetKbps);

            if (ReturnNull) return null;

            // Mimic VP8's frame-type bit: bit 0 of byte 0 is 0 for a keyframe.
            var isKey = _keyFrameRequested;
            _keyFrameRequested = false;

            var payload = new byte[64];
            payload[0] = (byte)(isKey ? 0x00 : 0x01);
            return payload;
        }
    }

    public void Dispose() { }
}

/// <summary>One thing an injector was asked to do.</summary>
/// <remarks>
/// Records rather than a log of strings so assertions compare values — a test that
/// matches on formatted text passes when the format changes and the behaviour breaks.
/// </remarks>
public abstract record InjectedEvent
{
    public sealed record Move(int X, int Y) : InjectedEvent;

    public sealed record Down(int X, int Y, PointerButton Button) : InjectedEvent;

    public sealed record Up(int X, int Y, PointerButton Button) : InjectedEvent;

    public sealed record Scroll(int X, int Y, double Dx, double Dy) : InjectedEvent;

    public sealed record KeyPress(KeyStroke Stroke) : InjectedEvent;

    public sealed record KeyRelease(KeyStroke Stroke) : InjectedEvent;

    public sealed record Text(string Value) : InjectedEvent;
}

/// <summary>
/// An injector that records instead of touching the desktop.
/// </summary>
/// <remarks>
/// The order of events is the whole point of recording them: a press that arrives before
/// its move clicks the wrong place, and a modifier released before its key produces the
/// unmodified character. Neither is visible from a count.
/// </remarks>
public sealed class FakeInputInjector : IInputInjector
{
    private readonly object _gate = new();
    private readonly List<InjectedEvent> _events = [];

    /// <summary>Everything injected, in order.</summary>
    public IReadOnlyList<InjectedEvent> Events
    {
        get { lock (_gate) return [.. _events]; }
    }

    /// <summary>Set false to simulate the secure desktop holding the input queue.</summary>
    public bool Available { get; set; } = true;

    public bool IsAvailable => Available;

    public string? UnavailableReason =>
        Available ? null : "secure desktop active — remote input temporarily unavailable";

    public string? LastError { get; private set; }

    /// <summary>When set, the next injection throws it, then it clears.</summary>
    /// <remarks>
    /// The handler must absorb this: an exception from injection on an unattended host
    /// must not escape to the worker and kill the session.
    /// </remarks>
    public Exception? ThrowOnce { get; set; }

    private readonly HashSet<PointerButton> _buttonsDown = [];
    private readonly HashSet<KeyStroke> _keysDown = [];

    public void MoveTo(int x, int y) => Record(new InjectedEvent.Move(x, y));

    public void ButtonDown(int x, int y, PointerButton button)
    {
        Record(new InjectedEvent.Down(x, y, button));
        lock (_gate) _buttonsDown.Add(button);
    }

    public void ButtonUp(int x, int y, PointerButton button)
    {
        Record(new InjectedEvent.Up(x, y, button));
        lock (_gate) _buttonsDown.Remove(button);
    }

    public void Wheel(int x, int y, double notchesX, double notchesY) =>
        Record(new InjectedEvent.Scroll(x, y, notchesX, notchesY));

    public void KeyDown(KeyStroke stroke)
    {
        Record(new InjectedEvent.KeyPress(stroke));
        lock (_gate) _keysDown.Add(stroke);
    }

    public void KeyUp(KeyStroke stroke)
    {
        Record(new InjectedEvent.KeyRelease(stroke));
        lock (_gate) _keysDown.Remove(stroke);
    }

    public void TypeText(string text) => Record(new InjectedEvent.Text(text));

    /// <summary>Mirrors the real injector's tracking, so teardown tests mean something.</summary>
    public int PressedCount
    {
        get { lock (_gate) return _buttonsDown.Count + _keysDown.Count; }
    }

    public void ReleaseAllPressed()
    {
        KeyStroke[] keys;
        PointerButton[] buttons;

        lock (_gate)
        {
            keys = [.. _keysDown];
            buttons = [.. _buttonsDown];
            _keysDown.Clear();
            _buttonsDown.Clear();
        }

        // Recorded through the normal event list so a test can assert the release
        // actually happened and in what order, rather than only that a counter moved.
        lock (_gate)
        {
            foreach (var key in keys) _events.Add(new InjectedEvent.KeyRelease(key));
            foreach (var button in buttons) _events.Add(new InjectedEvent.Up(0, 0, button));
        }
    }

    private void Record(InjectedEvent e)
    {
        if (ThrowOnce is { } ex)
        {
            ThrowOnce = null;
            LastError = ex.Message;
            throw ex;
        }

        lock (_gate) _events.Add(e);
    }

    /// <summary>The events of one type, for assertions that do not care about the rest.</summary>
    public IReadOnlyList<T> OfType<T>() where T : InjectedEvent
    {
        lock (_gate) return [.. _events.OfType<T>()];
    }

    public void Dispose() { }
}
