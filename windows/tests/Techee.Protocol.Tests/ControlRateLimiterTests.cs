using Techee.Protocol;

namespace Techee.Protocol.Tests;

/// <summary>
/// The per-session control ceilings from <c>docs/PROTOCOL.md</c> §5.4.
/// </summary>
/// <remarks>
/// The clock is driven by the test, so "a burst at the ceiling", "recovery after idling"
/// and "a power command five seconds later" are assertions rather than sleeps. A suite
/// that slept would take a minute to prove the <c>system.*</c> ceiling alone, and would
/// still be flaky on a loaded runner.
/// </remarks>
public class ControlRateLimiterTests
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    private ControlRateLimiter New() => new(() => _now);

    private void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    // ---- ceilings ----

    [Fact]
    public void Pointer_move_is_allowed_a_full_second_of_burst_and_no_more()
    {
        var limiter = New();

        // The bucket starts full, so 250 moves land without the clock advancing at all.
        for (var i = 0; i < 250; i++)
            Assert.True(limiter.TryConsume("pointer.move"), $"move {i} refused");

        Assert.False(limiter.TryConsume("pointer.move"));
        Assert.Equal(1, limiter.Dropped);
    }

    [Fact]
    public void Other_commands_share_the_lower_default_ceiling()
    {
        var limiter = New();

        for (var i = 0; i < 100; i++)
            Assert.True(limiter.TryConsume("pointer.tap"), $"tap {i} refused");

        Assert.False(limiter.TryConsume("pointer.tap"));
    }

    [Fact]
    public void Rotating_through_command_types_does_not_multiply_the_budget()
    {
        // The spec says "other commands 100/s", not 100/s each. A per-kind bucket would
        // let a hostile peer send 100 taps, then 100 wheels, then 100 keystrokes in the
        // same second — three times the ceiling by spelling the flood differently.
        var limiter = New();

        for (var i = 0; i < 100; i++) Assert.True(limiter.TryConsume("pointer.tap"));

        Assert.False(limiter.TryConsume("pointer.wheel"));
        Assert.False(limiter.TryConsume("keyboard.text"));
        Assert.False(limiter.TryConsume("pointer.down"));
    }

    [Fact]
    public void Pointer_move_has_its_own_bucket_and_does_not_starve_clicks()
    {
        // A drag is a stream of moves. If it drained the shared bucket, the button-up
        // that ends the drag would be dropped and the mouse would stay down.
        var limiter = New();

        for (var i = 0; i < 250; i++) Assert.True(limiter.TryConsume("pointer.move"));

        Assert.False(limiter.TryConsume("pointer.move"));
        Assert.True(limiter.TryConsume("pointer.up"));
    }

    // ---- power commands ----

    [Fact]
    public void A_power_command_is_allowed_once_and_then_not_for_five_seconds()
    {
        var limiter = New();

        Assert.True(limiter.TryConsume("system.restart"));
        Assert.False(limiter.TryConsume("system.restart"));

        Advance(4.9);
        Assert.False(limiter.TryConsume("system.restart"));

        Advance(0.2);
        Assert.True(limiter.TryConsume("system.restart"));
    }

    [Fact]
    public void All_power_commands_share_one_bucket()
    {
        // Otherwise "one per five seconds" is really one of each per five seconds, and
        // shutdown is reachable immediately after restart.
        var limiter = New();

        Assert.True(limiter.TryConsume("system.lock"));
        Assert.False(limiter.TryConsume("system.shutdown"));
        Assert.False(limiter.TryConsume("system.sleep"));
    }

    [Fact]
    public void A_power_bucket_holds_at_least_one_token_despite_a_sub_unit_rate()
    {
        // 0.2 tokens per second: a capacity equal to the rate would be less than one
        // token and the bucket could never permit anything at all.
        var limiter = New();
        Assert.True(limiter.TryConsume("system.lock"));
    }

    // ---- refill ----

    [Fact]
    public void An_exhausted_bucket_refills_at_the_ceiling_rate()
    {
        var limiter = New();

        for (var i = 0; i < 100; i++) limiter.TryConsume("pointer.tap");
        Assert.False(limiter.TryConsume("pointer.tap"));

        // A tenth of a second buys ten more at 100/s.
        Advance(0.1);
        for (var i = 0; i < 10; i++)
            Assert.True(limiter.TryConsume("pointer.tap"), $"refill {i} refused");

        Assert.False(limiter.TryConsume("pointer.tap"));
    }

    [Fact]
    public void Idling_does_not_bank_more_than_one_seconds_worth()
    {
        // Without a capacity cap, a controller could stay quiet for a minute and then
        // release 6000 events at once — precisely the wedge the ceiling exists to stop.
        var limiter = New();

        Advance(60);

        for (var i = 0; i < 100; i++) Assert.True(limiter.TryConsume("pointer.tap"));
        Assert.False(limiter.TryConsume("pointer.tap"));
    }

    // ---- robustness ----

    [Fact]
    public void A_clock_that_goes_backwards_neither_refills_nor_drains_the_bucket()
    {
        // Wall-clock time moves backwards across an NTP correction or a DST change. A
        // naive elapsed-time calculation would produce a negative refill and leave the
        // bucket permanently in debt, silently killing input for the rest of the session.
        var limiter = New();

        for (var i = 0; i < 100; i++) limiter.TryConsume("pointer.tap");
        Assert.False(limiter.TryConsume("pointer.tap"));

        Advance(-30);
        Assert.False(limiter.TryConsume("pointer.tap"));

        // And it recovers normally once time moves forward again.
        Advance(31);
        Assert.True(limiter.TryConsume("pointer.tap"));
    }

    [Fact]
    public void An_unknown_command_kind_falls_into_the_default_bucket()
    {
        // Unknown kinds are dropped upstream by the codec, but a limiter that threw or
        // let them through unmetered would be the wrong backstop.
        var limiter = New();

        for (var i = 0; i < 100; i++) Assert.True(limiter.TryConsume("something.new"));
        Assert.False(limiter.TryConsume("something.new"));
    }

    [Fact]
    public void Dropped_counts_every_refusal_across_every_bucket()
    {
        var limiter = New();

        for (var i = 0; i < 250; i++) limiter.TryConsume("pointer.move");
        for (var i = 0; i < 100; i++) limiter.TryConsume("pointer.tap");
        limiter.TryConsume("system.lock");

        Assert.Equal(0, limiter.Dropped);

        limiter.TryConsume("pointer.move");
        limiter.TryConsume("pointer.tap");
        limiter.TryConsume("system.lock");

        Assert.Equal(3, limiter.Dropped);
    }

    [Fact]
    public void Exceeding_a_ceiling_drops_the_frame_and_leaves_the_limiter_usable()
    {
        // The protocol is explicit that a rate limit drops frames rather than ending the
        // session: a controller on a bad network must not be able to disconnect itself.
        var limiter = New();

        for (var i = 0; i < 300; i++) limiter.TryConsume("pointer.move");
        Assert.True(limiter.Dropped > 0);

        Advance(1);
        Assert.True(limiter.TryConsume("pointer.move"));
    }

    // ---- the constants themselves ----

    // ---- memory safety ----

    [Fact]
    public void Attacker_controlled_command_names_cannot_create_unbounded_buckets()
    {
        // The command kind reaching this method is attacker-influenced. A limiter that
        // kept a bucket per distinct name would let a peer allocate one dictionary entry
        // per frame — an unbounded memory leak reachable from the network, and one that
        // also hands the attacker an unmetered budget for every new name it invents.
        var limiter = New();

        var allowed = 0;
        for (var i = 0; i < 10_000; i++)
            if (limiter.TryConsume($"attacker.kind.{i}"))
                allowed++;

        // Ten thousand distinct names share the one default bucket, so the ceiling holds.
        Assert.Equal(100, allowed);
        Assert.Equal(9_900, limiter.Dropped);
    }

    [Fact]
    public void Attacker_controlled_power_command_names_share_the_one_system_bucket()
    {
        var limiter = New();

        var allowed = 0;
        for (var i = 0; i < 1_000; i++)
            if (limiter.TryConsume($"system.invented{i}"))
                allowed++;

        Assert.Equal(1, allowed);
    }

    [Fact]
    public void Bucket_cardinality_is_fixed_at_three_regardless_of_traffic()
    {
        // Structural, via reflection, because "no unbounded growth" is a property of the
        // shape rather than of any one behaviour. Three named buckets and no collection
        // keyed by anything the peer controls.
        var fields = typeof(ControlRateLimiter)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.Empty(fields.Where(f =>
            f.FieldType.IsGenericType &&
            f.FieldType.GetGenericTypeDefinition().Name.StartsWith("Dictionary", StringComparison.Ordinal)));
    }

    // ---- ordinary use ----

    [Fact]
    public void A_realistic_session_never_touches_a_ceiling()
    {
        // A limiter that throttled normal use would be worse than none. This is a busy
        // minute: continuous 60 Hz pointer movement with a click every second.
        var limiter = New();
        var dropped = 0L;

        for (var second = 0; second < 60; second++)
        {
            for (var frame = 0; frame < 60; frame++)
            {
                Advance(1.0 / 60);
                if (!limiter.TryConsume("pointer.move")) dropped++;
            }

            if (!limiter.TryConsume("pointer.tap")) dropped++;
        }

        Assert.Equal(0, dropped);
    }

    [Fact]
    public void The_ceilings_match_the_shared_fixture()
    {
        // These three numbers are in protocol/fixtures/control-v1.json and in the Node
        // and Kotlin implementations. Drift here is drift across the whole system.
        Assert.Equal(250, ControlRateLimiter.PointerMovePerSecond);
        Assert.Equal(100, ControlRateLimiter.DefaultPerSecond);
        Assert.Equal(0.2, ControlRateLimiter.SystemPerSecond);
    }
}
