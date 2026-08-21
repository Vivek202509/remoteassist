namespace Techee.Protocol;

/// <summary>
/// Enforces the per-session control-frame ceilings from <c>docs/PROTOCOL.md</c> §5.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exceeding a ceiling drops the frame. It never ends the session.</b> That is the
/// specified behaviour and the reason is in the protocol: a controller on a bad network
/// must not be able to disconnect itself by burst-sending, and a hostile one must not be
/// able to wedge the input queue. Tearing down on excess would turn a rate limit into a
/// denial-of-service primitive pointed at the wrong party.
/// </para>
/// <para>
/// A token bucket rather than a fixed window, so a burst of a few frames after an idle
/// moment is absorbed — which is what a real drag looks like when a packet cluster
/// arrives together — while a sustained flood is still clamped to the ceiling.
/// </para>
/// <para>
/// Three buckets, matching the three ceilings the protocol names. Everything that is not
/// <c>pointer.move</c> or <c>system.*</c> shares one bucket, because the spec says
/// "other commands 100/s" of the aggregate rather than 100/s each — the stricter reading,
/// chosen deliberately: a hostile peer should not be able to multiply its budget by
/// rotating through command types.
/// </para>
/// <para>
/// The clock is injected, so the interesting cases — a burst at the ceiling, recovery
/// after idling, a <c>system.*</c> command five seconds later — are ordinary unit tests
/// rather than tests that sleep.
/// </para>
/// </remarks>
public sealed class ControlRateLimiter
{
    /// <summary>Frames per second for <c>pointer.move</c>.</summary>
    public const double PointerMovePerSecond = 250;

    /// <summary>Frames per second for every other non-power command, in aggregate.</summary>
    public const double DefaultPerSecond = 100;

    /// <summary>Power commands: one per five seconds.</summary>
    public const double SystemPerSecond = 0.2;

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    private readonly Bucket _move;
    private readonly Bucket _default;
    private readonly Bucket _system;

    private long _dropped;

    public ControlRateLimiter(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        var now = _clock();

        _move = new Bucket(PointerMovePerSecond, now);
        _default = new Bucket(DefaultPerSecond, now);
        _system = new Bucket(SystemPerSecond, now);
    }

    /// <summary>How many frames this session has dropped for exceeding a ceiling.</summary>
    /// <remarks>
    /// Surfaced in diagnostics. A steadily climbing count on a session that feels
    /// sluggish distinguishes "the controller is flooding" from "the network is bad",
    /// which otherwise look identical from the host.
    /// </remarks>
    public long Dropped
    {
        get { lock (_gate) return _dropped; }
    }

    /// <summary>Whether a command of this kind may be executed now.</summary>
    public bool TryConsume(string kind)
    {
        var now = _clock();

        lock (_gate)
        {
            var bucket = BucketFor(kind);
            if (bucket.TryConsume(now)) return true;

            _dropped++;
            return false;
        }
    }

    private Bucket BucketFor(string kind) => kind switch
    {
        "pointer.move" => _move,
        _ when kind.StartsWith("system.", StringComparison.Ordinal) => _system,
        _ => _default,
    };

    private sealed class Bucket(double ratePerSecond, DateTimeOffset start)
    {
        // One second's worth of allowance, but never less than a single token: at
        // 0.2/s a capacity equal to the rate would be less than one token and the
        // bucket could never permit anything at all.
        private readonly double _capacity = Math.Max(1.0, ratePerSecond);

        private double _tokens = Math.Max(1.0, ratePerSecond);
        private DateTimeOffset _last = start;

        internal bool TryConsume(DateTimeOffset now)
        {
            // A clock that goes backwards refills nothing rather than draining the
            // bucket into a negative balance it can never climb out of.
            var elapsed = (now - _last).TotalSeconds;
            if (elapsed > 0)
            {
                _tokens = Math.Min(_capacity, _tokens + elapsed * ratePerSecond);
                _last = now;
            }

            if (_tokens < 1.0) return false;

            _tokens -= 1.0;
            return true;
        }
    }
}
