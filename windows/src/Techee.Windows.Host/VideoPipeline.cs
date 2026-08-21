using System.Diagnostics;

namespace Techee.Windows.Host;

/// <summary>Rolling counters for one pipeline, for diagnostics and adaptation.</summary>
/// <remarks>
/// Deliberately about <i>this machine's</i> pipeline only. Nothing here leaves the
/// device except to the paired controller's diagnostics view — it is never reported to
/// any third party.
/// </remarks>
public sealed record PipelineStats(
    long FramesCaptured,
    long FramesEncoded,
    long FramesDropped,
    long CaptureTimeouts,
    double AverageEncodeMs,
    double AverageConvertMs,
    double AchievedFps,
    long BytesEncoded,
    VideoProfile Profile,
    bool ProtectedContentMasked,
    long FramesSent = 0,
    long FramesSuperseded = 0,
    long SendQueueDepth = 0)
{

    /// <summary>Average encoded size. A useful sanity check when a link looks saturated.</summary>
    public double AverageFrameBytes => FramesEncoded == 0 ? 0 : (double)BytesEncoded / FramesEncoded;

    /// <summary>Encode cost as a fraction of the current profile's frame budget.</summary>
    /// <remarks>
    /// Encode only. <see cref="ProcessingPressure"/> is what the adaptive controller
    /// acts on, and is the number to look at when asking whether the machine is keeping
    /// up — this one is for attributing cost between the two stages.
    /// </remarks>
    public double EncodePressure => AverageEncodeMs / Profile.FrameBudgetMs;

    /// <summary>Total per-frame CPU cost: conversion plus encoding.</summary>
    public double ProcessingMsPerFrame => AverageConvertMs + AverageEncodeMs;

    /// <summary>
    /// Total processing cost as a fraction of the frame budget.
    /// </summary>
    /// <remarks>
    /// At or above 1.0 the machine cannot produce frames as fast as the profile asks
    /// for, and the achieved frame rate will be below target no matter how good the
    /// network is.
    /// </remarks>
    public double ProcessingPressure => ProcessingMsPerFrame / Profile.FrameBudgetMs;
}

/// <summary>
/// The frame pump: capture → convert → encode → out.
/// </summary>
/// <remarks>
/// <para>
/// Runs one dedicated thread. Not a <see cref="System.Threading.Tasks.Task"/> loop,
/// because this is a continuous real-time pump rather than a unit of work — putting it
/// on the thread pool would let an unrelated blocking work item delay frames, and the
/// symptom would be intermittent stutter that is very hard to attribute.
/// </para>
/// <para>
/// <b>Back-pressure is drop-oldest.</b> If the consumer cannot keep up, frames are
/// dropped rather than queued. Video is only useful live: a queued frame is a frame the
/// operator will see late, and an unbounded queue is how a remote desktop turns into a
/// memory leak. Phase 23 asks for no uncontrolled queue growth, and this is where that
/// is enforced.
/// </para>
/// </remarks>
public sealed class VideoPipeline : IDisposable
{
    private readonly IScreenSource _source;
    private readonly IVideoEncoder _encoder;
    private readonly AdaptiveQuality _quality;
    private readonly Action<string>? _log;

    private readonly object _gate = new();

    /// <summary>
    /// The live pump thread, or null when none exists.
    /// </summary>
    /// <remarks>
    /// Cleared by the pump itself on the way out, <b>not</b> by <see cref="Stop"/>.
    /// Clearing it in Stop would let a subsequent Start spawn a second thread while the
    /// first was still winding down, and two threads sharing one Desktop Duplication
    /// session is a use-after-free waiting to happen.
    /// </remarks>
    private Thread? _thread;

    private volatile bool _running;

    /// <summary>Set whenever no pump thread is alive.</summary>
    private readonly ManualResetEventSlim _exited = new(initialState: true);

    /// <summary>How long <see cref="Stop"/> waits for the pump to actually leave its loop.</summary>
    /// <remarks>
    /// Generous enough to cover a capture already blocked in <c>TryCapture</c> at the
    /// longest profile timeout, short enough that shutdown is not perceptibly stuck.
    /// </remarks>
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    private bool _disposed;

    /// <summary>
    /// True when a pump thread failed to exit within <see cref="ExitTimeout"/>.
    /// </summary>
    /// <remarks>
    /// A wedged pump is almost always a capture blocked inside the graphics driver.
    /// Once set, this pipeline will neither start another thread nor free its native
    /// resources: leaking a duplication session is recoverable, freeing one out from
    /// under a live thread is not.
    /// </remarks>
    private bool _wedged;

    private int _pumpGenerations;

    // ---- send stage ----
    //
    // A one-deep, latest-frame-wins slot between encoding and the transport. Capture and
    // encoding run at their own pace; if the sink cannot keep up, the frame waiting in
    // the slot is discarded in favour of the newer one. For a remote desktop that is
    // always the right trade: a stale frame is worth less than a fresh one, and the
    // operator would rather see "now" than a backlog of "then".
    //
    // Depth one is the whole bound. Memory cannot grow with session duration because
    // there is nowhere for a second frame to accumulate.
    private readonly object _sendGate = new();
    private EncodedFrame? _pending;
    private int _pendingFps;
    private Thread? _sendThread;
    private readonly ManualResetEventSlim _senderExited = new(initialState: true);
    private bool _sending;
    private long _sent, _supersededBeforeSend;

    private byte[] _i420 = [];
    private VideoProfile _appliedProfile;

    // Rolling counters. Read under _gate by Stats.
    private long _captured, _encoded, _dropped, _timeouts, _bytes;
    private double _encodeMsTotal, _convertMsTotal;
    private long _encodeSamples, _convertSamples;
    private long _windowStartTicks;
    private long _windowFrames;
    private double _windowFps;

    /// <summary>
    /// Raised for each encoded frame, on the pump thread.
    /// </summary>
    /// <remarks>
    /// Handlers must not block: this is the capture thread, and time spent here is time
    /// not spent capturing. Exceptions are caught and logged rather than being allowed
    /// to kill the pump — on an unattended host, losing the pump loses the machine.
    /// </remarks>
    public event Action<EncodedFrame>? FrameEncoded;

    // The transport. Volatile rather than locked: it is swapped on connect/disconnect but
    // read once per frame on the pump thread, and taking _gate there would put the
    // capture loop behind every Stats call.
    private volatile IEncodedVideoSink? _sink;

    /// <summary>
    /// Directs encoded frames to a transport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attaching replaces any previous sink, so a reconnect that installs a replacement
    /// peer cannot leave the old one still receiving — there is exactly one destination
    /// at a time, by construction rather than by discipline.
    /// </para>
    /// <para>
    /// Safe to call while the pump is running. Frames encoded before the swap may still
    /// be in flight on the capture thread; the sink must tolerate that.
    /// </para>
    /// </remarks>
    public void AttachSink(IEncodedVideoSink? sink) => _sink = sink;

    /// <summary>Stops delivering frames, without stopping capture.</summary>
    /// <remarks>
    /// Used when a transport is being torn down but the pipeline is about to be handed
    /// to a replacement, so the expensive DXGI and encoder state survives the gap.
    /// </remarks>
    public void DetachSink() => _sink = null;

    /// <summary>The transport currently attached, if any.</summary>
    public bool HasSink => _sink is not null;

    public VideoPipeline(
        IScreenSource source,
        IVideoEncoder encoder,
        AdaptiveQuality? quality = null,
        Action<string>? log = null)
    {
        _source = source;
        _encoder = encoder;
        _quality = quality ?? new AdaptiveQuality();
        _log = log;
        _appliedProfile = _quality.Current;
    }

    public VideoProfile Profile => _quality.Current;

    /// <summary>
    /// The display being captured.
    /// </summary>
    /// <remarks>
    /// Exposed for input injection, which must map normalized coordinates against the
    /// surface the controller is actually looking at rather than against the primary
    /// monitor. On a multi-monitor host those are routinely different.
    /// </remarks>
    public DisplayInfo Display => _source.Display;

    public bool IsRunning => _running;

    /// <summary>
    /// Starts the pump.
    /// </summary>
    /// <remarks>
    /// Idempotent, and refuses to start a second thread while a previous one is still
    /// winding down or wedged. Exactly one capture loop exists per pipeline at any
    /// moment — the property a reconnect depends on, since recovery stops and restarts
    /// the same pipeline rather than building a new one.
    /// </remarks>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running) return;

            if (_wedged)
            {
                _log?.Invoke("[video] refusing to start: a previous pump never exited");
                return;
            }

            if (_thread is not null)
            {
                // Stop() timed out or was never called. Starting here would put two
                // threads on one duplication session.
                _log?.Invoke("[video] refusing to start: the previous pump is still winding down");
                return;
            }

            _running = true;
            _exited.Reset();
            _pumpGenerations++;

            ApplyProfile(_quality.Current, force: true);
            _windowStartTicks = Stopwatch.GetTimestamp();
            _windowFrames = 0;

            // The sender starts first so the slot is being drained before the pump can
            // fill it, and no frame is superseded merely because nobody was reading yet.
            StartSender();

            _thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "techee-capture",
                // Above normal so a busy desktop does not starve the capture loop, but
                // not real-time: a remote session must never make the machine itself
                // unusable for whoever is sitting at it.
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }
    }

    /// <summary>
    /// Stops the pump and waits for the capture thread to actually leave its loop.
    /// </summary>
    /// <remarks>
    /// Waiting is the point. Returning while the thread is still inside
    /// <see cref="IScreenSource.TryCapture"/> would let the caller dispose the source
    /// underneath it. If the wait expires the pipeline is marked wedged rather than
    /// pretending the thread is gone.
    /// </remarks>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
        }

        var pumpExited = _exited.Wait(ExitTimeout);

        // Stopped after the pump, so anything the pump encoded on its way out still gets
        // its chance to go: the sender drains a frame left in the slot before it leaves.
        // That is the flush, and it is why the last frame of a session is not lost.
        StopSender();

        if (pumpExited) return;

        lock (_gate) _wedged = true;
        _log?.Invoke($"[video] pump did not exit within {ExitTimeout.TotalSeconds:0}s; " +
                     "native resources will be leaked rather than freed under a live thread");
    }

    private void StartSender()
    {
        lock (_sendGate)
        {
            if (_sending || _sendThread is not null) return;
            _sending = true;
            _senderExited.Reset();

            _sendThread = new Thread(SendLoop)
            {
                IsBackground = true,
                Name = "techee-send",
                // Normal priority: this one waits on the network, and raising it above
                // the pump would only make it spin waiting for frames that do not exist.
            };
            _sendThread.Start();
        }
    }

    private void StopSender()
    {
        lock (_sendGate)
        {
            if (!_sending) return;
            _sending = false;
            Monitor.PulseAll(_sendGate);
        }

        if (_senderExited.Wait(ExitTimeout)) return;

        lock (_gate) _wedged = true;
        _log?.Invoke("[video] the send stage did not exit; treating the pipeline as wedged");
    }

    /// <summary>
    /// Drains the one-deep slot into the transport.
    /// </summary>
    /// <remarks>
    /// Separate from the pump so a stalled network throttles sending rather than
    /// encoding. If capture and encode were held behind a slow send, the adaptive
    /// controller's encode timings would silently start measuring the network instead of
    /// the CPU, and it would step the resolution down for the wrong reason.
    /// </remarks>
    private void SendLoop()
    {
        try
        {
            while (true)
            {
                EncodedFrame frame;
                int fps;

                lock (_sendGate)
                {
                    while (_sending && _pending is null) Monitor.Wait(_sendGate);

                    // Leaving, and nothing left to flush.
                    if (!_sending && _pending is null) return;

                    frame = _pending!;
                    fps = _pendingFps;
                    _pending = null;
                }

                var sink = _sink;
                if (sink is null) continue;

                try
                {
                    sink.SendEncodedFrame(frame, fps);
                    Interlocked.Increment(ref _sent);
                }
                catch (Exception e)
                {
                    // A transport that throws must not take the send stage down; the
                    // link state change is what drives recovery.
                    _log?.Invoke($"[video] sink threw: {e.GetType().Name}");
                }
            }
        }
        finally
        {
            lock (_sendGate) _sendThread = null;
            _senderExited.Set();
        }
    }

    /// <summary>
    /// Offers a frame to the send stage, displacing any frame still waiting.
    /// </summary>
    /// <remarks>
    /// The payload is copied because the slot outlives the call. <see cref="VpxEncoder"/>
    /// reuses its <i>input</i> buffer and the underlying libvpx wrapper makes no written
    /// guarantee about the lifetime of the array it returns, so holding that array across
    /// the next encode would be betting on an undocumented detail. The copy is tens of
    /// kilobytes of compressed VP8, not the ~1.4 MB an I420 frame would cost, and it buys
    /// certainty about a class of corruption that would otherwise appear as rare,
    /// unreproducible artefacts.
    /// </remarks>
    /// <summary>
    /// Frames waiting on the transport: 0 or 1, never more.
    /// </summary>
    /// <remarks>
    /// This is the bound, read from the slot itself. A frame currently inside the sink is
    /// deliberately not counted here — it is in flight, not backlogged.
    /// </remarks>
    private long SlotDepth
    {
        get { lock (_sendGate) return _pending is null ? 0 : 1; }
    }

    private void Offer(EncodedFrame frame, int fps)
    {
        var owned = frame with { Payload = frame.Payload.ToArray() };

        lock (_sendGate)
        {
            if (!_sending) return;

            // Latest wins. The displaced frame is counted, never queued.
            if (_pending is not null) Interlocked.Increment(ref _supersededBeforeSend);

            _pending = owned;
            _pendingFps = fps;
            Monitor.Pulse(_sendGate);
        }
    }

    /// <summary>How many pump threads this pipeline has started.</summary>
    /// <remarks>
    /// One per start. Used by the lifecycle tests to prove a reconnect reuses this
    /// pipeline instead of quietly accumulating capture loops.
    /// </remarks>
    public int PumpGenerations
    {
        get { lock (_gate) return _pumpGenerations; }
    }

    /// <summary>True when a pump thread failed to exit and this pipeline is unusable.</summary>
    public bool IsWedged
    {
        get { lock (_gate) return _wedged; }
    }

    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    /// <summary>Feeds a network observation into the adaptive controller.</summary>
    /// <remarks>
    /// Called by the session layer with RTCP-derived loss and RTT. Encode cost and
    /// achieved frame rate come from the pipeline's own counters, so the controller sees
    /// both halves of the picture.
    /// </remarks>
    public void ReportNetwork(double packetLossFraction, double roundTripMs)
    {
        PipelineStats stats;
        lock (_gate) stats = SnapshotLocked();

        var next = _quality.Observe(new QualitySample(
            stats.AverageEncodeMs,
            stats.AchievedFps,
            packetLossFraction,
            roundTripMs,
            // Conversion competes for the same frame budget as the encode, so the
            // controller sees the whole per-frame cost rather than half of it.
            stats.AverageConvertMs));

        if (next.Name != _appliedProfile.Name) ApplyProfile(next, force: false);
    }

    /// <summary>Pins a profile, e.g. because the operator chose one.</summary>
    public void ForceProfile(VideoProfile profile)
    {
        _quality.Force(profile);
        if (_quality.Current.Name != _appliedProfile.Name) ApplyProfile(_quality.Current, force: false);
    }

    /// <summary>Requests a keyframe, e.g. because a controller just joined or reported loss.</summary>
    public void RequestKeyFrame() => _encoder.ForceKeyFrame();

    public PipelineStats Stats
    {
        get { lock (_gate) return SnapshotLocked(); }
    }

    private PipelineStats SnapshotLocked() => new(
        _captured, _encoded, _dropped, _timeouts,
        _encodeSamples == 0 ? 0 : _encodeMsTotal / _encodeSamples,
        _convertSamples == 0 ? 0 : _convertMsTotal / _convertSamples,
        _windowFps,
        _bytes,
        _appliedProfile,
        _source.ProtectedContentMasked,
        Interlocked.Read(ref _sent),
        Interlocked.Read(ref _supersededBeforeSend),
        // Observed, not derived. An arithmetic depth would have to account for the frame
        // currently in flight inside the sink, which is neither queued nor sent yet, and
        // getting that subtraction subtly wrong is how a backlog metric starts lying.
        SlotDepth);

    private void ApplyProfile(VideoProfile profile, bool force)
    {
        lock (_gate)
        {
            if (!force && profile.Name == _appliedProfile.Name) return;

            _source.Resize(profile.Width, profile.Height);
            _encoder.TargetKbps = profile.TargetKbps;

            var needed = PixelConvert.I420Size(profile.Width, profile.Height);
            if (_i420.Length < needed) _i420 = new byte[needed];

            _appliedProfile = profile;

            // Mandatory after a resolution change. A receiver decoding deltas against a
            // frame of the wrong size shows garbage until the next natural keyframe,
            // which on a static desktop can be a very long time.
            _encoder.ForceKeyFrame();

            // Encode timings from the previous profile do not describe this one, and
            // leaving them in place would have the controller adapt on stale evidence.
            _encodeMsTotal = 0;
            _encodeSamples = 0;
            _convertMsTotal = 0;
            _convertSamples = 0;

            _log?.Invoke($"[video] profile -> {profile}");
        }
    }

    private void Pump()
    {
        try
        {
            PumpLoop();
        }
        finally
        {
            // The pump owns its own exit. Stop() waits on this rather than assuming,
            // and only once it is signalled may native resources be released.
            lock (_gate) _thread = null;
            _exited.Set();
        }
    }

    private void PumpLoop()
    {
        // Timeout is derived from the frame budget rather than fixed: at 20 FPS a 33 ms
        // timeout would give up before the next frame was even due.
        var timeout = TimeSpan.FromMilliseconds(Math.Max(50, _appliedProfile.FrameBudgetMs * 2));
        var frameTimer = new Stopwatch();

        while (_running)
        {
            VideoProfile profile;
            byte[] buffer;
            lock (_gate)
            {
                profile = _appliedProfile;
                buffer = _i420;
            }

            frameTimer.Restart();
            var gotFrame = false;
            byte[]? encoded = null;
            double convertMs = 0, encodeMs = 0;

            try
            {
                gotFrame = _source.TryCapture(timeout, (bgra, w, h, stride) =>
                {
                    // The source is configured to the profile size, but a display mode
                    // change can race a resize. Encoding a mismatched frame would
                    // corrupt the stream, so skip it and let the next resize settle.
                    if (w != profile.Width || h != profile.Height) return;

                    var t0 = Stopwatch.GetTimestamp();
                    PixelConvert.Convert(bgra, buffer, w, h, stride);
                    var t1 = Stopwatch.GetTimestamp();

                    encoded = _encoder.Encode(buffer.AsSpan(0, PixelConvert.I420Size(w, h)), w, h);
                    var t2 = Stopwatch.GetTimestamp();

                    convertMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                    encodeMs = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
                });
            }
            catch (Exception e)
            {
                // A capture or encode failure must not kill the pump. On an unattended
                // office host this thread is the only way anyone gets back in.
                _log?.Invoke($"[video] frame failed: {e.GetType().Name}: {e.Message}");
                lock (_gate) _dropped++;
                Thread.Sleep(100);
                continue;
            }

            if (!gotFrame)
            {
                // Not an error: Desktop Duplication only delivers on change, so an idle
                // desktop times out continuously.
                lock (_gate) _timeouts++;
                continue;
            }

            lock (_gate)
            {
                _captured++;
                if (convertMs > 0) { _convertMsTotal += convertMs; _convertSamples++; }
                if (encodeMs > 0) { _encodeMsTotal += encodeMs; _encodeSamples++; }

                if (encoded is null || encoded.Length == 0)
                {
                    _dropped++;
                }
                else
                {
                    _encoded++;
                    _bytes += encoded.Length;
                    _windowFrames++;
                }

                UpdateFpsWindowLocked();
            }

            if (encoded is { Length: > 0 })
            {
                Deliver(
                    new EncodedFrame(
                        encoded,
                        profile.Width,
                        profile.Height,
                        // libvpx does not surface the keyframe flag through this API.
                        // VP8 carries it in the payload: bit 0 of the first byte is 0
                        // for a keyframe.
                        IsKeyFrame: (encoded[0] & 0x01) == 0,
                        frameTimer.Elapsed),
                    profile.Fps);
            }

            // Pace to the profile. Capture can outrun the target on a busy screen, and
            // sending faster than asked wastes bandwidth the adaptive controller is
            // trying to manage.
            var budget = profile.FrameBudgetMs;
            var spent = frameTimer.Elapsed.TotalMilliseconds;
            if (spent < budget) Thread.Sleep((int)Math.Max(1, budget - spent));
        }
    }

    /// <summary>
    /// Hands one encoded frame to the transport, then to any diagnostic observers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transport goes first: it is the reason the pump exists, and a misbehaving
    /// diagnostic subscriber must not delay the picture.
    /// </para>
    /// <para>
    /// Observers see <b>every</b> encoded frame, including ones the send stage later
    /// discards as stale. That is deliberate: the event is a diagnostic tap on the
    /// encoder, and a counter that quietly skipped dropped frames would hide exactly the
    /// backlog it exists to reveal.
    /// </para>
    /// <para>
    /// An observer that throws does not rob the next one, and cannot kill the pump — on
    /// an unattended host, losing the pump loses the only way back into the machine.
    /// </para>
    /// </remarks>
    private void Deliver(EncodedFrame frame, int fps)
    {
        // Hand off rather than send. The pump returns to capturing immediately, and a
        // slow transport costs dropped frames instead of a stalled encoder.
        if (_sink is not null) Offer(frame, fps);

        var handler = FrameEncoded;
        if (handler is null) return;

        try
        {
            handler(frame);
        }
        catch (Exception e)
        {
            _log?.Invoke($"[video] frame handler threw: {e.GetType().Name}");
        }
    }

    private void UpdateFpsWindowLocked()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _windowStartTicks) / (double)Stopwatch.Frequency;
        if (elapsed < 1.0) return;

        _windowFps = _windowFrames / elapsed;
        _windowFrames = 0;
        _windowStartTicks = now;
    }

    /// <summary>
    /// Stops capture and releases the encoder and the capture device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent and deterministic: by the time this returns, either the native
    /// resources are freed or the pipeline is marked wedged and has said so.
    /// </para>
    /// <para>
    /// A wedged pump keeps its resources on purpose. Freeing a Desktop Duplication
    /// session or a libvpx context while a thread is still inside them is an access
    /// violation that takes down an unattended host; leaking them costs memory until the
    /// process exits, which is survivable and visible in diagnostics.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        // Detach before stopping so no frame can reach a transport that is being torn
        // down alongside this pipeline.
        _sink = null;
        Stop();

        bool wedged;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            wedged = _wedged;
        }

        if (wedged)
        {
            _log?.Invoke("[video] leaking the encoder and capture device: the pump never exited");
            return;
        }

        _encoder.Dispose();
        _source.Dispose();
        _exited.Dispose();
        _senderExited.Dispose();
    }
}
