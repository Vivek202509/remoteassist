using Techee.Windows.Host;
using Xunit.Abstractions;

namespace Techee.Windows.Host.Tests;

/// <summary>
/// The frame pump's lifecycle: exactly one capture loop, deterministic disposal, and no
/// resource left behind across stop/start cycles.
/// </summary>
/// <remarks>
/// <para>
/// These are the properties an unattended host depends on. A duplicate capture loop puts
/// two threads on one Desktop Duplication session; a disposal that races the pump frees
/// a libvpx context underneath it. Both are access violations rather than exceptions, so
/// neither is caught by ordinary error handling — they have to be designed out and
/// tested for.
/// </para>
/// <para>
/// Capture and encoding are faked. What is under test is ownership and ordering, which
/// is identical whether the frames come from a GPU or an array.
/// </para>
/// </remarks>
public class PipelineLifecycleTests(ITestOutputHelper output)
{
    private static VideoPipeline Build(IScreenSource source, IVideoEncoder encoder, ITestOutputHelper output) =>
        new(source, encoder, new AdaptiveQuality(VideoProfile.Hd), m => output.WriteLine($"video {m}"));

    /// <summary>A source and encoder that record whether they were disposed, and when.</summary>
    private sealed class CountingSource : IScreenSource
    {
        private readonly FakeScreenSource _inner = new();
        public int DisposeCount { get; private set; }
        public volatile bool InCapture;
        public int MaxConcurrentCaptures;
        private int _concurrent;

        public DisplayInfo Display => _inner.Display;
        public (int Width, int Height) OutputSize => _inner.OutputSize;
        public bool ProtectedContentMasked => _inner.ProtectedContentMasked;
        public void Resize(int width, int height) => _inner.Resize(width, height);

        public bool TryCapture(TimeSpan timeout, FrameHandler onFrame)
        {
            // Detects a second pump thread entering capture concurrently, which is the
            // failure a duplicate capture loop would actually produce.
            var now = Interlocked.Increment(ref _concurrent);
            MaxConcurrentCaptures = Math.Max(MaxConcurrentCaptures, now);
            InCapture = true;
            try
            {
                return _inner.TryCapture(timeout, onFrame);
            }
            finally
            {
                InCapture = false;
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public void Dispose()
        {
            DisposeCount++;
            _inner.Dispose();
        }
    }

    private sealed class CountingEncoder : IVideoEncoder
    {
        private readonly FakeEncoder _inner = new();
        public int DisposeCount { get; private set; }

        public int TargetKbps { get => _inner.TargetKbps; set => _inner.TargetKbps = value; }
        public byte[]? Encode(ReadOnlySpan<byte> i420, int w, int h) => _inner.Encode(i420, w, h);
        public void ForceKeyFrame() => _inner.ForceKeyFrame();
        public void Dispose() => DisposeCount++;
    }

    private static bool SpinUntil(Func<bool> condition, int seconds = 5) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(seconds));

    // ---- no viewer, no cost ----

    [Fact]
    public void A_pipeline_that_was_never_started_captures_nothing()
    {
        var source = new CountingSource();
        var encoder = new CountingEncoder();
        using var pipeline = Build(source, encoder, output);

        Thread.Sleep(50);

        Assert.False(pipeline.IsRunning);
        Assert.Equal(0, pipeline.PumpGenerations);
        Assert.Equal(0, pipeline.Stats.FramesCaptured);
    }

    // ---- exactly one capture loop ----

    [Fact]
    public void Starting_twice_does_not_create_a_second_capture_loop()
    {
        var source = new CountingSource();
        var encoder = new CountingEncoder();
        using var pipeline = Build(source, encoder, output);

        pipeline.Start();
        pipeline.Start();
        pipeline.Start();

        Assert.True(SpinUntil(() => pipeline.Stats.FramesCaptured > 5));
        pipeline.Stop();

        Assert.Equal(1, pipeline.PumpGenerations);
        Assert.Equal(1, source.MaxConcurrentCaptures);
    }

    [Fact]
    public void Stop_and_start_cycles_never_overlap_capture()
    {
        // The reconnect shape: pause the pump, resume it, repeatedly.
        var source = new CountingSource();
        var encoder = new CountingEncoder();
        using var pipeline = Build(source, encoder, output);

        for (var i = 0; i < 10; i++)
        {
            pipeline.Start();
            Assert.True(SpinUntil(() => pipeline.Stats.FramesCaptured > i * 2));
            pipeline.Stop();

            // Stop is synchronous with the thread actually leaving the loop.
            Assert.False(pipeline.IsRunning);
            Assert.False(source.InCapture);
        }

        output.WriteLine($"{pipeline.PumpGenerations} generations, " +
                         $"max concurrent captures {source.MaxConcurrentCaptures}");

        Assert.Equal(10, pipeline.PumpGenerations);
        Assert.Equal(1, source.MaxConcurrentCaptures);

        // Cycling the pump must not have touched the expensive state.
        Assert.Equal(0, source.DisposeCount);
        Assert.Equal(0, encoder.DisposeCount);
    }

    [Fact]
    public void Stop_waits_for_the_capture_thread_to_actually_leave()
    {
        // The hazard this prevents: returning from Stop while the thread is still inside
        // TryCapture, then disposing the source underneath it.
        var source = new CountingSource();
        var encoder = new CountingEncoder();
        using var pipeline = Build(source, encoder, output);

        pipeline.Start();
        Assert.True(SpinUntil(() => pipeline.Stats.FramesCaptured > 3));

        pipeline.Stop();

        Assert.False(source.InCapture);
        Assert.False(pipeline.IsRunning);
        Assert.False(pipeline.IsWedged);
    }

    // ---- deterministic disposal ----

    [Fact]
    public void Disposing_releases_the_encoder_and_the_capture_device_exactly_once()
    {
        var source = new CountingSource();
        var encoder = new CountingEncoder();
        var pipeline = Build(source, encoder, output);

        pipeline.Start();
        Assert.True(SpinUntil(() => pipeline.Stats.FramesEncoded > 2));

        pipeline.Dispose();
        pipeline.Dispose();
        pipeline.Dispose();

        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(1, encoder.DisposeCount);
        Assert.True(pipeline.IsDisposed);
        Assert.False(pipeline.IsRunning);
    }

    [Fact]
    public void Disposing_without_ever_starting_is_clean()
    {
        var source = new CountingSource();
        var encoder = new CountingEncoder();
        var pipeline = Build(source, encoder, output);

        pipeline.Dispose();

        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(1, encoder.DisposeCount);
    }

    [Fact]
    public void Starting_after_disposal_is_refused_rather_than_silently_ignored()
    {
        var source = new CountingSource();
        var encoder = new CountingEncoder();
        var pipeline = Build(source, encoder, output);
        pipeline.Dispose();

        Assert.Throws<ObjectDisposedException>(pipeline.Start);
        Assert.Equal(0, pipeline.PumpGenerations);
    }

    // ---- the wedged case ----

    /// <summary>A source that blocks in capture until released, simulating a stuck driver.</summary>
    private sealed class BlockingSource : IScreenSource
    {
        private readonly FakeScreenSource _inner = new();
        public readonly ManualResetEventSlim Release = new(false);
        public readonly ManualResetEventSlim Entered = new(false);
        public int DisposeCount { get; private set; }

        public DisplayInfo Display => _inner.Display;
        public (int Width, int Height) OutputSize => _inner.OutputSize;
        public bool ProtectedContentMasked => false;
        public void Resize(int width, int height) => _inner.Resize(width, height);

        public bool TryCapture(TimeSpan timeout, FrameHandler onFrame)
        {
            Entered.Set();
            Release.Wait();
            return _inner.TryCapture(timeout, onFrame);
        }

        public void Dispose()
        {
            DisposeCount++;
            _inner.Dispose();
        }
    }

    [Fact]
    public void A_wedged_pump_leaks_its_resources_rather_than_freeing_them_under_a_live_thread()
    {
        // Freeing a duplication session or a libvpx context while a thread is still
        // inside them is an access violation, which no catch block would save an
        // unattended host from. Leaking is the survivable choice, and it is announced.
        var source = new BlockingSource();
        var encoder = new CountingEncoder();
        var pipeline = Build(source, encoder, output);

        pipeline.Start();
        Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(5)), "the pump never reached capture");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        pipeline.Dispose();
        sw.Stop();

        output.WriteLine($"dispose took {sw.Elapsed.TotalSeconds:0.0}s, wedged={pipeline.IsWedged}");

        Assert.True(pipeline.IsWedged);
        Assert.Equal(0, source.DisposeCount);
        Assert.Equal(0, encoder.DisposeCount);

        // And it stays unusable rather than starting a second thread onto the stuck one.
        Assert.Throws<ObjectDisposedException>(pipeline.Start);

        source.Release.Set();
        Assert.True(SpinUntil(() => !pipeline.IsRunning, 5));
    }

    [Fact]
    public void A_wedged_pipeline_refuses_to_start_another_pump()
    {
        var source = new BlockingSource();
        var encoder = new CountingEncoder();
        using var pipeline = Build(source, encoder, output);

        pipeline.Start();
        Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(5)));

        pipeline.Stop();
        Assert.True(pipeline.IsWedged);

        var generations = pipeline.PumpGenerations;
        pipeline.Start();

        Assert.Equal(generations, pipeline.PumpGenerations);

        source.Release.Set();
    }

    // ---- no runaway work ----

    [Fact]
    public void A_stopped_pump_stops_consuming_frames()
    {
        var source = new CountingSource();
        var encoder = new CountingEncoder();
        using var pipeline = Build(source, encoder, output);

        pipeline.Start();
        Assert.True(SpinUntil(() => pipeline.Stats.FramesCaptured > 3));
        pipeline.Stop();

        var atStop = pipeline.Stats.FramesCaptured;
        Thread.Sleep(200);

        output.WriteLine($"captured {atStop} at stop, {pipeline.Stats.FramesCaptured} after 200ms");

        Assert.Equal(atStop, pipeline.Stats.FramesCaptured);
    }
}
