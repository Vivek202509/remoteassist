using System.Drawing;
using System.Windows.Forms;
using Techee.Protocol;
using Techee.Session;
using Techee.WebRtc;

namespace Techee.Windows.ControllerApp;

/// <summary>
/// The acceptance viewer: renders the host's desktop and forwards local input to it.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that turns the matrix's "controller acceptance NYT" rows into
/// something executable. D7 and D8 were never blocked on the host — a real Windows
/// application has been receiving real <c>WM_*</c> messages since A32 — they were blocked
/// on there being no controller able to send a wheel notch or a keystroke at all. The
/// shipped Android controller has no keyboard sender, and <c>pointer.wheel</c> has no v0
/// form, so both were unreachable from the only controller that existed.
/// </para>
/// <para>
/// <b>A harness, not a product UI.</b> There is no toolbar, no reconnection, no session
/// browser and no attempt at a good first-run experience. What it does have is the thing
/// an acceptance run needs: every number that distinguishes one failure from another,
/// visible while the session is live.
/// </para>
/// </remarks>
public sealed class ViewerForm : Form
{
    private readonly ControllerSession _session;
    private readonly Vp8Decoder _decoder;
    private readonly IvfWriter? _recorder;
    private readonly bool _viewOnly;
    private readonly Action<string> _log;

    private readonly VideoSurface _surface = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 22,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(6, 0, 6, 0),
        BackColor = Color.FromArgb(32, 32, 36),
        ForeColor = Color.FromArgb(200, 200, 210),
    };

    private readonly TextBox _typeBox = new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        PlaceholderText = "type Unicode text here and press Enter (exercises keyboard.text / D8)",
    };

    private readonly Panel _typeRow = new() { Dock = DockStyle.Top, Height = 26, Padding = new Padding(6, 3, 6, 3) };

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 16 };

    private readonly SemaphoreSlim _work = new(0, 1);
    private readonly object _slotGate = new();
    private byte[]? _pending;

    private CancellationTokenSource? _decodeLoop;
    private long _framesReceived;
    private long _framesSuperseded;
    private int _statusDivider;
    private bool _dragging;
    private string? _heldButton;
    private (double X, double Y) _lastRemote = (0.5, 0.5);

    /// <summary>
    /// The most recent decoded frame, for <c>--snapshot</c>.
    /// </summary>
    /// <remarks>
    /// Kept as pixels rather than as the presented bitmap because the surface disposes
    /// each bitmap as the next supersedes it, so by the time the window closes the last
    /// one is already gone.
    /// </remarks>
    public DecodedFrame? LastDecoded { get; private set; }

    public ViewerForm(
        ControllerSession session,
        Vp8Decoder decoder,
        IvfWriter? recorder,
        bool viewOnly,
        Action<string> log)
    {
        _session = session;
        _decoder = decoder;
        _recorder = recorder;
        _viewOnly = viewOnly;
        _log = log;

        Text = "techee-ctl";
        ClientSize = new Size(1280, 760);
        BackColor = Color.FromArgb(24, 24, 27);
        KeyPreview = false;

        _typeRow.Controls.Add(_typeBox);
        Controls.Add(_surface);
        Controls.Add(_typeRow);
        Controls.Add(_status);

        WireInput();

        _session.VideoFrameReceived += OnVideoFrame;
        _session.LinkStateChanged += _ => BeginInvokeSafe(UpdateStatus);
        _session.HostAnnounced += OnHostAnnounced;

        _tick.Tick += OnTick;
    }

    // ---- lifecycle ----

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        _decodeLoop = new CancellationTokenSource();
        _ = Task.Run(() => DecodeLoopAsync(_decodeLoop.Token));

        _tick.Start();
        _surface.Focus();

        if (_viewOnly) _log("view-only: this controller will not send input");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _tick.Stop();
        _session.VideoFrameReceived -= OnVideoFrame;

        _decodeLoop?.Cancel();

        // Whatever is held remotely is released before the window goes, rather than
        // leaving it to the host's stuck-input net to notice.
        //
        // The button goes first and at the last known position, so ReleaseAll is left
        // with only the modifiers to unwind — it reports (0,0), which for a held button
        // would drag it into the corner on the way out.
        if (!_viewOnly && _session.Sender is { } sender)
        {
            if (_heldButton is { } button)
            {
                sender.ButtonUp(_lastRemote.X, _lastRemote.Y, button);
                _heldButton = null;
            }

            sender.ReleaseAll();
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _decodeLoop?.Dispose();
            _work.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---- video ----

    /// <summary>
    /// Takes a frame off the receive thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bounded latest-wins slot, the same shape as the host's send-side back-pressure.
    /// Decoding is slower than reception on a busy screen, and a queue would trade
    /// latency for frames nobody will ever see: by the time a backlogged frame is drawn
    /// it is already wrong.
    /// </para>
    /// <para>
    /// The recording is written here rather than in the decode loop, so it captures every
    /// frame that arrived — including the ones dropped for latency and the ones the
    /// decoder could not read. A recording that only contains what decoded successfully
    /// is useless for diagnosing a decode failure.
    /// </para>
    /// </remarks>
    private void OnVideoFrame(byte[] frame, uint rtpTimestamp)
    {
        Interlocked.Increment(ref _framesReceived);
        _recorder?.Write(frame, rtpTimestamp);

        lock (_slotGate)
        {
            if (_pending is not null) Interlocked.Increment(ref _framesSuperseded);
            _pending = frame;
        }

        try
        {
            _work.Release();
        }
        catch (SemaphoreFullException)
        {
            // The worker has not taken the previous frame yet. It will take the newest
            // one when it does, which is the whole point of the slot.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private async Task DecodeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _work.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            byte[]? frame;
            lock (_slotGate)
            {
                frame = _pending;
                _pending = null;
            }

            if (frame is null) continue;

            var decoded = _decoder.Decode(frame);
            if (decoded is null) continue;

            LastDecoded = decoded;
            _recorder?.SetDimensions(decoded.Width, decoded.Height);

            try
            {
                _surface.SetFrame(VideoSurface.ToBitmap(decoded));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // A malformed size from the decoder must not end the session.
                _log($"could not present a frame: {e.GetType().Name}");
            }
        }
    }

    private void OnHostAnnounced(EndpointMeta meta)
    {
        _log($"host announced: {meta.Platform} {meta.Version} [{string.Join(", ", meta.Capabilities)}]");

        BeginInvokeSafe(() =>
        {
            if (!meta.Has("input.receive"))
            {
                _log("the host does not accept input (--no-input, or a view-only grant)");
            }

            UpdateStatus();
        });
    }

    // ---- input ----

    private void WireInput()
    {
        // A press must land on the picture: a click in the letterbox padding is a miss,
        // not an instruction to click the remote screen's edge.
        _surface.MouseDown += (_, e) =>
        {
            _surface.Focus();
            if (Blocked()) return;

            if (_surface.ToRemote(e.Location) is not { } p) return;

            _dragging = true;
            _heldButton = ButtonName(e.Button);
            _lastRemote = p;
            _session.Sender!.ButtonDown(p.X, p.Y, _heldButton);
        };

        // A release, by contrast, is clamped. Releasing outside the picture still has to
        // release: dropping it would leave the button held on the remote machine.
        _surface.MouseUp += (_, e) =>
        {
            if (Blocked()) return;

            var wasDragging = _dragging;
            _dragging = false;
            _heldButton = null;

            if (_surface.ToRemote(e.Location, continuing: wasDragging) is not { } p) return;

            _lastRemote = p;
            _session.Sender!.ButtonUp(p.X, p.Y, ButtonName(e.Button));
        };

        // Buffered, not sent. The sender coalesces and the tick flushes, so a fast drag
        // costs one frame per tick rather than one per mouse message.
        _surface.MouseMove += (_, e) =>
        {
            if (Blocked()) return;
            if (_surface.ToRemote(e.Location, continuing: _dragging) is not { } p) return;

            _lastRemote = p;
            _session.Sender!.PointerMove(p.X, p.Y);
        };

        // A drag out of the window still delivers MouseUp, because WinForms captures the
        // mouse for the duration — so no handler is needed for that case.
        //
        // What capture does not survive is losing activation: Alt+Tab or a UAC prompt
        // mid-drag, after which the release never arrives and the button stays down on
        // the host. Deactivate is the right signal for that and, unlike
        // MouseCaptureChanged, it does not also fire on every ordinary click. (Capture is
        // released by DefWndProc before MouseUp is raised, so a capture-based handler
        // would release the button on the way up from every single click, at the (0,0)
        // coordinates ReleaseAll uses.)
        Deactivate += (_, _) =>
        {
            if (!_dragging || Blocked()) return;

            _dragging = false;

            // Released where the pointer actually is, rather than through ReleaseAll,
            // which reports (0,0) and would drag whatever is held into the corner — on a
            // title bar that means moving a window, and on a desktop it means a selection
            // box across the whole screen.
            if (_heldButton is { } button)
            {
                _session.Sender!.ButtonUp(_lastRemote.X, _lastRemote.Y, button);
                _heldButton = null;
            }

            _log("lost activation mid-drag; released the held button");
        };

        _surface.MouseWheel += (_, e) =>
        {
            if (Blocked()) return;
            if (_surface.ToRemote(e.Location) is not { } p) return;

            // Windows reports 120 units per notch; the protocol carries notches.
            _session.Sender!.Wheel(p.X, p.Y, 0, (double)e.Delta / SystemInformation.MouseWheelScrollDelta);
        };

        _surface.KeyDown += (_, e) =>
        {
            if (Blocked()) return;

            var code = WinFormsKeyMap.CodeFor(e.KeyCode);
            if (code is null)
            {
                _log($"no protocol name for {e.KeyCode}; not sent");
                return;
            }

            // Suppressed locally whether or not it is sent, so Alt does not open the
            // window menu and F10 does not move focus to it.
            e.SuppressKeyPress = true;
            e.Handled = true;

            var mods = WinFormsKeyMap.IsModifier(e.KeyCode) ? null : WinFormsKeyMap.HeldModifiers();
            _session.Sender!.KeyDown(code, mods);
        };

        _surface.KeyUp += (_, e) =>
        {
            if (Blocked()) return;

            var code = WinFormsKeyMap.CodeFor(e.KeyCode);
            if (code is null) return;

            e.Handled = true;

            var mods = WinFormsKeyMap.IsModifier(e.KeyCode) ? null : WinFormsKeyMap.HeldModifiers();
            _session.Sender!.KeyUp(code, mods);
        };

        // Typing leaves the surface, so nothing here is forwarded as keystrokes. This is
        // the keyboard.text path — the one that carries characters a key position cannot
        // express, which is what "techee w4 héllo" in A32 was actually testing.
        _typeBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;

            e.SuppressKeyPress = true;
            e.Handled = true;

            var text = _typeBox.Text;
            if (string.IsNullOrEmpty(text)) return;

            if (Blocked())
            {
                _log("view-only: text not sent");
                return;
            }

            var ok = _session.Sender!.TypeText(text);
            _log(ok ? $"sent {text.Length} chars as keyboard.text" : "keyboard.text was refused by the transport");

            _typeBox.Clear();
            _surface.Focus();
        };
    }

    /// <summary>Whether input should be withheld right now.</summary>
    /// <remarks>
    /// Checked on every event rather than once at startup, because the channel opens
    /// after the window does. Sending before <see cref="ControllerSession.Sender"/>
    /// exists would be a null dereference on the first mouse move over the surface.
    /// </remarks>
    private bool Blocked() => _viewOnly || _session.Sender is null;

    private static string ButtonName(MouseButtons button) => button switch
    {
        MouseButtons.Right => "right",
        MouseButtons.Middle => "middle",
        _ => "left",
    };

    // ---- status ----

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_viewOnly) _session.Sender?.FlushPendingMove();

        // The flush wants every tick; the status line does not. Sixty text updates a
        // second is measurable CPU spent on something nobody can read.
        if (++_statusDivider < 30) return;
        _statusDivider = 0;

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var peer = _session.Peer;
        var telemetry = peer?.GetTelemetry();
        var sender = _session.Sender;

        var size = _surface.RemoteSize is { } s ? $"{s.Width}x{s.Height}" : "—";
        var link = _session.LinkState;

        _status.Text =
            $"{link} | {size} | " +
            $"rx frames {Interlocked.Read(ref _framesReceived)} " +
            $"(superseded {Interlocked.Read(ref _framesSuperseded)}) | " +
            $"pkts {peer?.VideoPacketsReceived ?? 0} | " +
            $"{_decoder} | " +
            $"rtt {telemetry?.RoundTripMs ?? 0:0}ms loss {telemetry?.PacketLossFraction ?? 0:P1} | " +
            $"{(telemetry?.UsingRelay == true ? "RELAY" : "direct")} | " +
            $"dialect v{sender?.PeerVersion ?? 0}" +
            (_viewOnly ? " | VIEW ONLY" : sender is { PeerAcceptsInput: false } ? " | host refuses input" : "") +
            (sender is { FailedSends: > 0 } ? $" | failed sends {sender.FailedSends}" : "");

        Text = $"techee-ctl — {link}" + (_recorder is not null ? $" — recording {_recorder.Frames} frames" : "");
    }

    private void BeginInvokeSafe(Action action)
    {
        if (!IsHandleCreated || IsDisposed) return;

        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // The window went away between the check and the post.
        }
    }
}
