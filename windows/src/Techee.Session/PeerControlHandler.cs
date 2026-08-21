using System.Collections.Concurrent;
using Techee.Protocol;
using Techee.Windows.Host;

namespace Techee.Session;

/// <summary>
/// Turns authenticated control frames into desktop input.
/// </summary>
/// <remarks>
/// <para>
/// The inbound mirror of <see cref="PeerVideoSink"/>, and the same kind of adapter: the
/// injector knows nothing about grants, dialects or WebRTC, and the peer connection knows
/// nothing about <c>SendInput</c>. This class is the only place both are visible, which
/// is what keeps <c>Techee.Windows.Host</c> free of protocol types.
/// </para>
/// <para>
/// <b>Two threads, deliberately.</b> <see cref="Handle"/> runs on the SIPSorcery network
/// thread and does only cheap work — authorize, rate-limit, enqueue. Execution happens on
/// a private worker, because a <c>pointer.swipe</c> is a gesture with a duration and
/// running it inline would stall the transport for up to ten seconds. It also serialises
/// input: a button-up that overtook its button-down would leave the mouse stuck down.
/// </para>
/// <para>
/// <b>Authorization is per command, not per session.</b> The grant is re-read on every
/// frame rather than captured at join. That is what makes the
/// <c>docs/WINDOWS_SECURITY.md</c> promise — "lock state re-evaluated per call" — true of
/// input and not merely of the join handshake: a machine that locks mid-session loses
/// control rights on the very next frame, and a grant revoked while the session is live
/// stops working without waiting for a reconnect.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> Every path returns. See <c>docs/PROTOCOL.md</c> §5.5:
/// this is attacker-influenced input arriving on a media callback, and on an unattended
/// host an exception there kills the machine nobody is standing next to.
/// </para>
/// </remarks>
public sealed class PeerControlHandler : IDisposable
{
    /// <summary>
    /// How many commands may be waiting for the worker.
    /// </summary>
    /// <remarks>
    /// Bounded so a flood cannot grow the queue without limit. The rate limiter should
    /// stop that first; this is the backstop for the case where it does not, and a
    /// dropped input event is a far better outcome than an unbounded queue on a machine
    /// nobody is watching.
    /// </remarks>
    private const int QueueCapacity = 256;

    /// <summary>Interpolation step for a swipe. About 125 Hz — smoother than most displays.</summary>
    private const int SwipeStepMs = 8;

    private readonly IInputInjector _injector;
    private readonly Func<Grant?> _grant;
    private readonly Func<DisplayInfo?> _capturedDisplay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ControlRateLimiter _limiter;
    private readonly Action<string>? _log;

    private readonly BlockingCollection<Work> _queue = new(QueueCapacity);
    private readonly Thread _worker;

    private long _accepted;
    private long _refused;
    private long _queueDropped;
    private long _unsupported;
    private long _executed;
    private long _failed;
    private volatile bool _disposed;
    private bool _reportedUnavailable;
    private int _releasePending;

    /// <summary>
    /// One item of work for the injector thread.
    /// </summary>
    /// <remarks>
    /// A wrapper rather than a bare <see cref="Control"/> because losing authorization
    /// has to be able to reach the worker too, and the worker is the only thread allowed
    /// to touch the injector. Expressing it as queued work keeps that rule intact
    /// instead of adding a second path in.
    /// </remarks>
    private abstract record Work
    {
        internal sealed record Command(Control Control) : Work;

        /// <summary>Release whatever is held, without ending the session.</summary>
        internal sealed record ReleaseHeld : Work;
    }

    /// <param name="injector">Where accepted input goes.</param>
    /// <param name="grant">
    /// Re-reads the controller's current grant. Called once per command, and expected to
    /// return null the moment the grant stops being usable.
    /// </param>
    /// <param name="capturedDisplay">
    /// The display the controller is actually looking at. Normalized coordinates are
    /// relative to the captured surface, so mapping them against any other monitor puts
    /// every click somewhere else entirely.
    /// </param>
    public PeerControlHandler(
        IInputInjector injector,
        Func<Grant?> grant,
        Func<DisplayInfo?> capturedDisplay,
        Func<DateTimeOffset>? clock = null,
        ControlRateLimiter? limiter = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(injector);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(capturedDisplay);

        _injector = injector;
        _grant = grant;
        _capturedDisplay = capturedDisplay;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _limiter = limiter ?? new ControlRateLimiter(_clock);
        _log = log;

        _worker = new Thread(Pump)
        {
            IsBackground = true,
            Name = "techee-input",
        };
        _worker.Start();
    }

    /// <summary>Commands authorized and queued.</summary>
    public long Accepted => Interlocked.Read(ref _accepted);

    /// <summary>Commands refused by the grant. Expected to be non-zero on a view-only session.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>Commands dropped because the worker was behind.</summary>
    public long QueueDropped => Interlocked.Read(ref _queueDropped);

    /// <summary>Commands this platform has no action for — <c>nav.key</c>, mostly.</summary>
    public long Unsupported => Interlocked.Read(ref _unsupported);

    /// <summary>Commands the worker has run to completion.</summary>
    public long Executed => Interlocked.Read(ref _executed);

    /// <summary>
    /// Commands that reached the worker and threw.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Refused"/> and <see cref="RateLimited"/>, which never
    /// reached the injector at all. Without this counter an injection that throws is
    /// invisible outside the log, and "my clicks do nothing" has one more
    /// indistinguishable cause than it needs.
    /// </remarks>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Commands the worker has finished with, successfully or not.</summary>
    public long Processed => Executed + Failed;

    /// <summary>Commands dropped for exceeding a protocol rate ceiling.</summary>
    public long RateLimited => _limiter.Dropped;

    // ---- inbound, on the network thread ----

    /// <summary>
    /// Accepts one decoded control frame.
    /// </summary>
    /// <remarks>
    /// Validation already happened in <c>ControlCodec</c>; this is the authorize step
    /// that <c>docs/PROTOCOL.md</c> §5.5 requires to come after it. A refusal is silent
    /// and local — the sender is not told, because telling it would turn the host into an
    /// oracle for what a stolen grant still permits.
    /// </remarks>
    public void Handle(Control? control)
    {
        if (control is null || _disposed) return;

        try
        {
            if (!IsInput(control))
            {
                // Not ours. Clipboard, display and power commands arrive on the same
                // channel and belong to W7/W8; ignoring them is what the protocol asks
                // for and is not an error.
                return;
            }

            // Rate limiting comes FIRST, before authorization.
            //
            // The limiter is the denial-of-service guard, so nothing an unauthorized
            // peer does may route around it. Authorizing first would leave a peer with
            // no grant able to burn an unbounded number of grant lookups per second
            // simply by being refused very quickly — the one path where being denied
            // costs the host more than being allowed. The cost of the ordering is that
            // a refused flood is counted as rate-limited rather than refused; both
            // counters are reported, so the diagnosis is unchanged.
            if (!_limiter.TryConsume(control.Kind)) return;

            if (!TecheeProtocol.AuthorizeCommand(control, _grant(), _clock()))
            {
                Interlocked.Increment(ref _refused);

                // Losing control while holding something must not leave it held. This is
                // the revocation case: Ctrl goes down, the grant is revoked, and the
                // matching key-up is refused — so the release has to come from here.
                RequestRelease();
                return;
            }

            if (!_queue.TryAdd(new Work.Command(control)))
            {
                Interlocked.Increment(ref _queueDropped);
                return;
            }

            Interlocked.Increment(ref _accepted);
        }
        catch (Exception e)
        {
            // The kind is safe to log; the payload never is. A keyboard.text frame
            // carries whatever the user typed, which may be a password.
            _log?.Invoke($"[input] dropped '{control.Kind}': {e.GetType().Name}");
        }
    }

    /// <summary>
    /// Asks the worker to drop anything it is holding.
    /// </summary>
    /// <remarks>
    /// Coalesced through <see cref="_releasePending"/>, so a controller that keeps
    /// sending after being revoked queues one release rather than filling the queue with
    /// them. Nothing is enqueued when the injector holds nothing, which is the common
    /// case for a refusal.
    /// </remarks>
    private void RequestRelease()
    {
        if (_injector.PressedCount == 0) return;
        if (Interlocked.Exchange(ref _releasePending, 1) != 0) return;

        if (!_queue.TryAdd(new Work.ReleaseHeld())) Volatile.Write(ref _releasePending, 0);
    }

    /// <summary>Whether a command is one this handler executes.</summary>
    private static bool IsInput(Control control) => control is
        Control.PointerTap or Control.PointerMove or Control.PointerDown or
        Control.PointerUp or Control.PointerWheel or Control.PointerSwipe or
        Control.KeyDown or Control.KeyUp or Control.KeyText or Control.NavKey;

    // ---- execution, on the worker ----

    private void Pump()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    switch (work)
                    {
                        case Work.Command command:
                            Execute(command.Control);
                            Interlocked.Increment(ref _executed);
                            break;

                        case Work.ReleaseHeld:
                            Volatile.Write(ref _releasePending, 0);
                            _injector.ReleaseAllPressed();
                            break;
                    }
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _failed);
                    _log?.Invoke($"[input] work item failed: {e.GetType().Name}");
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposed while blocked on the queue. Normal shutdown.
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Execute(Control control)
    {
        // Checked here rather than at enqueue: a UAC prompt that appears between the two
        // is exactly the case this exists for, and the queue may hold a second of work.
        if (!_injector.IsAvailable)
        {
            ReportUnavailable();
            return;
        }

        _reportedUnavailable = false;

        switch (control)
        {
            case Control.PointerMove m:
                if (Point(m.X, m.Y) is { } mp) _injector.MoveTo(mp.X, mp.Y);
                break;

            case Control.PointerTap t:
                // A tap is a press and a release at one point. Unlike Android's 50 ms
                // stroke there is no dwell: Windows has no long-press semantics to
                // preserve, and an artificial delay would only make clicking feel slow.
                if (Point(t.X, t.Y) is { } tp)
                {
                    _injector.ButtonDown(tp.X, tp.Y, PointerButton.Left);
                    _injector.ButtonUp(tp.X, tp.Y, PointerButton.Left);
                }
                break;

            case Control.PointerDown d:
                if (Point(d.X, d.Y) is { } dp && Button(d.Button) is { } db)
                    _injector.ButtonDown(dp.X, dp.Y, db);
                break;

            case Control.PointerUp u:
                if (Point(u.X, u.Y) is { } up && Button(u.Button) is { } ub)
                    _injector.ButtonUp(up.X, up.Y, ub);
                break;

            case Control.PointerWheel w:
                if (Point(w.X, w.Y) is { } wp) _injector.Wheel(wp.X, wp.Y, w.Dx, w.Dy);
                break;

            case Control.PointerSwipe s:
                Swipe(s);
                break;

            case Control.KeyDown k:
                WithModifiers(k.Mods, press: true, () =>
                {
                    if (KeyMap.Resolve(k.Code) is { } stroke) _injector.KeyDown(stroke);
                    else Interlocked.Increment(ref _unsupported);
                });
                break;

            case Control.KeyUp k:
                WithModifiers(k.Mods, press: false, () =>
                {
                    if (KeyMap.Resolve(k.Code) is { } stroke) _injector.KeyUp(stroke);
                    else Interlocked.Increment(ref _unsupported);
                });
                break;

            case Control.KeyText t:
                _injector.TypeText(t.Text);
                break;

            case Control.NavKey:
                // BACK, HOME and RECENTS are Android navigation with no Windows
                // equivalent. Inventing one — Alt+Left for BACK, say — would fire a
                // real shortcut into whatever application has focus, which is worse
                // than doing nothing. Counted so the omission is visible rather than
                // looking like a dropped frame.
                Interlocked.Increment(ref _unsupported);
                break;
        }
    }

    /// <summary>
    /// Runs a keystroke with its modifiers held around it.
    /// </summary>
    /// <remarks>
    /// <c>mods</c> is the modifier state that must hold while the key transitions, so a
    /// press takes them down first and a release lets them up afterwards. Non-modifier
    /// entries are ignored: without that check a controller could list <c>Delete</c> as
    /// a modifier and have it pressed around every keystroke.
    /// </remarks>
    private void WithModifiers(IReadOnlyList<string>? mods, bool press, Action key)
    {
        var strokes = Modifiers(mods);

        if (press) foreach (var m in strokes) _injector.KeyDown(m);

        key();

        if (!press)
        {
            // Released in reverse, so Ctrl+Shift+X unwinds the way a human would.
            for (var i = strokes.Count - 1; i >= 0; i--) _injector.KeyUp(strokes[i]);
        }
    }

    private static List<KeyStroke> Modifiers(IReadOnlyList<string>? mods)
    {
        var strokes = new List<KeyStroke>();
        if (mods is null) return strokes;

        foreach (var m in mods)
        {
            if (!KeyMap.IsModifier(m)) continue;
            if (KeyMap.Resolve(m) is { } stroke) strokes.Add(stroke);
        }

        return strokes;
    }

    private void Swipe(Control.PointerSwipe s)
    {
        if (Point(s.X1, s.Y1) is not { } from) return;
        if (Point(s.X2, s.Y2) is not { } to) return;

        var (fromX, fromY) = from;
        var (toX, toY) = to;

        var ms = Math.Clamp(s.Ms, 1, TecheeProtocol.MaxSwipeMs);
        var steps = (int)Math.Clamp(ms / SwipeStepMs, 1, 1024);

        _injector.MoveTo(fromX, fromY);
        _injector.ButtonDown(fromX, fromY, PointerButton.Left);

        // Interpolated rather than a straight jump: applications distinguish a drag from
        // a click by the moves in between, and a text selection or a drag-and-drop that
        // teleports is frequently not registered at all.
        var started = _clock();
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var x = fromX + (int)Math.Round((toX - fromX) * t);
            var y = fromY + (int)Math.Round((toY - fromY) * t);
            _injector.MoveTo(x, y);

            if (_disposed) break;

            // Paced against the wall clock rather than by accumulating sleeps, so a slow
            // injection call does not stretch the gesture past its requested duration.
            var due = started.AddMilliseconds(ms * t);
            var remaining = due - _clock();
            if (remaining > TimeSpan.Zero) Thread.Sleep(remaining);
        }

        _injector.ButtonUp(toX, toY, PointerButton.Left);
    }

    /// <summary>
    /// Maps a normalized protocol point onto logical virtual-desktop pixels.
    /// </summary>
    /// <remarks>
    /// Against the <i>captured</i> display, because that is the surface the coordinates
    /// were normalized against. With no display — capture not started, or already torn
    /// down — the point cannot be placed at all, and guessing the primary monitor would
    /// put input on a screen the operator is not looking at.
    /// </remarks>
    private (int X, int Y)? Point(double nx, double ny)
    {
        var display = _capturedDisplay();
        if (display is null) return null;

        return DisplayGeometry.ToVirtual(display, nx, ny);
    }

    private static PointerButton? Button(string? button) => button switch
    {
        "left" or null => PointerButton.Left,
        "right" => PointerButton.Right,
        "middle" => PointerButton.Middle,
        "x1" => PointerButton.X1,
        "x2" => PointerButton.X2,
        // Unreachable through the codec, which rejects unknown buttons. Kept because
        // this method must stay total if it is ever called from anywhere else.
        _ => null,
    };

    private void ReportUnavailable()
    {
        // Logged on the transition only. A UAC prompt left on screen would otherwise
        // produce a line per queued event.
        if (_reportedUnavailable) return;

        _reportedUnavailable = true;
        _log?.Invoke($"[input] {_injector.UnavailableReason}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.CompleteAdding();

        // Bounded: a swipe in flight can hold the worker for its remaining duration, and
        // waiting forever on it would hang session teardown.
        var stopped = _worker.Join(TimeSpan.FromSeconds(2));

        // After the worker has stopped, so this cannot race a press it is about to make.
        // If the worker did not stop in time we release anyway: a stuck Ctrl on the
        // user's machine is worse than two threads briefly touching the injector, and
        // the injector's own lock makes that safe in any case.
        _injector.ReleaseAllPressed();

        if (!stopped) _log?.Invoke("[input] worker did not stop within 2s; released held input anyway");

        _queue.Dispose();
    }

    /// <summary>Buttons and keys currently held by injected input. Zero when idle.</summary>
    public int Held => _injector.PressedCount;

    public override string ToString() =>
        $"PeerControlHandler(accepted={Accepted}, executed={Executed}, failed={Failed}, " +
        $"refused={Refused}, rateLimited={RateLimited}, queueDropped={QueueDropped}, " +
        $"unsupported={Unsupported}, held={Held})";
}
