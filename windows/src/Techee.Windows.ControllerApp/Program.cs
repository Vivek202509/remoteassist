using System.Windows.Forms;
using Techee.Crypto;
using Techee.Protocol;
using Techee.Session;
using Techee.Signaling;
using Techee.Store;

namespace Techee.Windows.ControllerApp;

/// <summary>
/// The runnable Techee Windows controller.
/// </summary>
/// <remarks>
/// <para>
/// The W5 counterpart to <c>techee-host</c>, and a console harness for the same reason:
/// it exists so the acceptance rows that need a controller can be executed at all. Until
/// this project, the only controller in the tree was a private class inside
/// <c>ControlOverBrokerTests</c>, so D6 could not be run, and D7/D8 could only be proven
/// against the host's own injector rather than driven by a real remote peer.
/// </para>
/// <para>
/// <b>Trust is seeded from the command line here too.</b> <c>trust</c> writes the host's
/// public key straight into this machine's trust store, bypassing the interactive pairing
/// exchange, exactly as the host's <c>trust</c> verb does. It does <b>not</b> validate
/// pairing — that is matrix row D3 and still needs the real exchange. What it does not
/// bypass is SDP verification: a host whose offer is not signed by the key seeded here is
/// refused, which is the property D6 is actually about.
/// </para>
/// <para>
/// <b>The identity is deliberately not the host's.</b> Both apps can run on one machine,
/// and sharing <c>Techee.DeviceIdentity</c> between them would have the second
/// registration displace the first socket at the broker — the documented exit-6 failure,
/// arrived at by accident. The controller keeps its own key and its own store directory.
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var verb = args.FirstOrDefault();
        var options = ParseOptions(args.Skip(1));

        try
        {
            return verb switch
            {
                "identity" => Identity(options),
                "trust" => Trust(options),
                "list" => List(options),
                "connect" => Connect(options),
                _ => Usage(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            techee-ctl — Techee Windows controller (W5 / D6-D8 acceptance harness)

              identity  [--store DIR] [--key NAME]
                  Print this controller's device ID and public key. Give the public key
                  to the host so it can trust and grant this controller.

              trust     --pub BASE64 [--name NAME] [--store DIR]
                  Trust a host, so its offers will authenticate. Seeds the trust store
                  directly, bypassing interactive pairing.

              list      [--store DIR]
                  Show trusted hosts.

              connect   --broker URL --host DEVICEID [options]
                  Dial a paired host and open the viewer.

                  --pair          Register the pairing edge with the broker first. The
                                  broker links both directions, so only one side needs
                                  to do this. Required once before the first dial, or
                                  the broker answers join-failed: not-paired.
                  --view-only     Never send input, whatever the host would allow. The
                                  safe way to run a video acceptance test on a machine
                                  someone is using.
                  --headless      No window. Records and reports; proves media arrived
                                  without needing a desktop to draw on.
                  --record FILE   Write received frames to FILE as IVF-wrapped VP8,
                                  playable with ffplay or VLC.
                  --snapshot FILE Write a PNG of the last decoded frame on exit.
                  --seconds N     Stop after N seconds. Headless runs need this; an
                                  acceptance step that never returns is not a step.
                  --force-relay   Restrict ICE to relay candidates, so the session can
                                  only succeed through TURN. Both ends must set it for
                                  the result to mean anything.
                  --store DIR     Default %LOCALAPPDATA%\\Techee\\controller, DPAPI-protected.
                  --key NAME      CNG key name. Default Techee.ControllerIdentity.

            Exit codes: 0 clean · 1 no verb · 2 bad arguments · 3 registration failed
                        4 --force-relay with no TURN · 5 the dial was refused
                        6 the host's SDP did not authenticate · 7 no media arrived
            """);
        return 1;
    }

    // ---- verbs ----

    private static int Identity(IReadOnlyDictionary<string, string> options)
    {
        using var identity = OpenIdentity(options);

        Console.WriteLine($"device id  : {identity.DeviceId}");
        Console.WriteLine($"public key : {identity.PublicKeyB64}");
        Console.WriteLine($"short fp   : {TecheeCrypto.ShortFingerprint(identity.PublicKeySpkiDer)}");
        Console.WriteLine($"protection : {identity.Protection}");
        Console.WriteLine();
        Console.WriteLine("On the host, run:");
        Console.WriteLine($"  techee-host trust --pub {identity.PublicKeyB64} --name \"Windows controller\" --control");
        return 0;
    }

    private static int Trust(IReadOnlyDictionary<string, string> options)
    {
        var pub = Required(options, "pub");
        var name = options.GetValueOrDefault("name", "Acceptance host");

        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(pub);
        }
        catch (FormatException)
        {
            Console.Error.WriteLine("--pub must be the host's base64 SPKI public key.");
            return 2;
        }

        var trust = new TrustStore(OpenStore(options));

        trust.Save(new PeerIdentity
        {
            PublicKeySpkiB64 = pub,
            Name = name,
            // No pairing exchange happened, so there is no ECDH secret and the safety
            // number computed from this entry is meaningless. Recorded in the store
            // rather than only printed, so it cannot later pass for a paired peer.
            SharedSecretB64 = Convert.ToBase64String(new byte[32]),
            State = TrustState.PendingConfirm,
            PairedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Platform = TestSeededMarker,
        });
        trust.Confirm(pub);

        WriteSeededTrustWarning();

        Console.WriteLine($"trusted {TecheeCrypto.DeviceIdFor(spki)} as '{name}'");
        Console.WriteLine();
        Console.WriteLine("Dial it with:");
        Console.WriteLine($"  techee-ctl connect --broker ws://HOST:8080 --host {TecheeCrypto.DeviceIdFor(spki)} --pair");
        return 0;
    }

    private static int List(IReadOnlyDictionary<string, string> options)
    {
        var trust = new TrustStore(OpenStore(options));

        Console.WriteLine("trusted hosts:");
        foreach (var peer in trust.All)
        {
            var seeded = peer.Platform == TestSeededMarker ? "  <<< TEST-SEEDED, NOT PAIRED" : "";
            Console.WriteLine($"  {peer}{seeded}");
        }

        if (trust.All.Any(p => p.Platform == TestSeededMarker)) WriteSeededTrustWarning();

        return 0;
    }

    private static int Connect(IReadOnlyDictionary<string, string> options)
    {
        var broker = new Uri(Required(options, "broker"));
        var hostId = Required(options, "host");

        if (!IsDeviceId(hostId))
        {
            Console.Error.WriteLine("--host must be a 64-character lowercase hex device ID.");
            Console.Error.WriteLine("Get it from `techee-host identity` on the host machine.");
            return 2;
        }

        var viewOnly = options.ContainsKey("view-only");
        var headless = options.ContainsKey("headless");
        var forceRelay = options.ContainsKey("force-relay");
        var pair = options.ContainsKey("pair");
        var record = options.GetValueOrDefault("record");
        var snapshot = options.GetValueOrDefault("snapshot");

        int? seconds = null;
        if (options.TryGetValue("seconds", out var raw))
        {
            if (!int.TryParse(raw, out var parsed) || parsed <= 0)
            {
                Console.Error.WriteLine("--seconds must be a positive whole number.");
                return 2;
            }
            seconds = parsed;
        }

        return ConnectAsync(new ConnectOptions(
            broker, hostId, viewOnly, headless, forceRelay, pair, record, snapshot, seconds, options))
            .GetAwaiter().GetResult();
    }

    private sealed record ConnectOptions(
        Uri Broker,
        string HostId,
        bool ViewOnly,
        bool Headless,
        bool ForceRelay,
        bool Pair,
        string? Record,
        string? Snapshot,
        int? Seconds,
        IReadOnlyDictionary<string, string> Raw);

    private static async Task<int> ConnectAsync(ConnectOptions o)
    {
        var trust = new TrustStore(OpenStore(o.Raw));

        if (trust.PublicKeyForSdp(o.HostId) is null)
        {
            Console.Error.WriteLine($"no trusted key for host {o.HostId}.");
            Console.Error.WriteLine("Run `techee-ctl trust --pub <host public key>` first, or the host's");
            Console.Error.WriteLine("offer cannot be authenticated and the session would be refused.");
            return 2;
        }

        using var identity = OpenIdentity(o.Raw);

        await using var signaling = new SignalingClient(
            o.Broker, identity,
            new EndpointMeta("windows", ControllerSession.ControllerVersion, ControllerSession.ControllerCapabilities),
            Log);

        Console.WriteLine($"device id : {identity.DeviceId}");
        Console.WriteLine($"short fp  : {TecheeCrypto.ShortFingerprint(identity.PublicKeySpkiDer)}");
        Console.WriteLine($"protection: {identity.Protection}");
        Console.WriteLine($"broker    : {o.Broker}");
        Console.WriteLine($"host      : {o.HostId}");
        Console.WriteLine($"ICE policy: {(o.ForceRelay ? "RELAY ONLY (TURN must carry this session)" : "all candidates")}");
        Console.WriteLine();

        var registration = await signaling.ConnectAndRegisterAsync();
        if (!registration.Ok)
        {
            Console.Error.WriteLine($"registration failed: {registration.Reason}");
            return 3;
        }

        Console.WriteLine("registered.");

        // Both directions are linked by the broker from one call, so the host does not
        // need to run this too.
        if (o.Pair)
        {
            await signaling.RegisterPairingAsync(o.HostId);
            await Task.Delay(300);
            Console.WriteLine("pairing edge registered with the broker.");
        }

        await signaling.RequestTurnCredentialsAsync();
        await Task.Delay(500);

        var relayServers = signaling.IceServers
            .Count(s => s.Urls.StartsWith("turn", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"ICE servers      : {signaling.IceServers.Count} ({relayServers} relay)");
        foreach (var server in signaling.IceServers) Console.WriteLine($"  {server}");

        if (o.ForceRelay && relayServers == 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("--force-relay was requested but the broker offered no TURN server.");
            Console.Error.WriteLine("The session could not possibly connect. Fix TURN_HOST/TURN_SECRET first.");
            return 4;
        }

        using var recorder = o.Record is null ? null : new IvfWriter(o.Record);
        if (recorder is not null) Console.WriteLine($"recording to     : {recorder.Path}");

        using var decoder = new Vp8Decoder();

        await using var session = new ControllerSession(
            identity, signaling, trust, o.HostId, Log, o.ForceRelay);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        if (o.Seconds is { } limit) cancellation.CancelAfter(TimeSpan.FromSeconds(limit));

        Console.WriteLine();
        var run = session.RunAsync(cancellation.Token);

        DecodedFrame? lastDecoded = null;

        if (o.Headless)
        {
            lastDecoded = await RunHeadlessAsync(session, decoder, recorder, cancellation.Token);
        }
        else
        {
            lastDecoded = RunViewer(session, decoder, recorder, o.ViewOnly, cancellation);
        }

        await session.EndAsync();
        var outcome = await run;

        // Flushed before the counters are read, so the frame count printed below is the
        // count actually on disk.
        recorder?.Dispose();

        Console.WriteLine();
        Console.WriteLine("---- session summary ----");
        Console.WriteLine($"outcome          : {outcome}{(session.FailureReason is { } r ? $" — {r}" : "")}");
        Console.WriteLine($"link state       : {session.LinkState}");
        Console.WriteLine($"video packets    : {session.Peer?.VideoPacketsReceived ?? 0}");
        Console.WriteLine($"video bytes      : {session.Peer?.VideoBytesReceived ?? 0}");
        Console.WriteLine($"video frames     : {session.Peer?.VideoFramesReceived ?? 0}");
        Console.WriteLine($"decoder          : {decoder}");
        if (recorder is not null) Console.WriteLine($"recording        : {recorder.Path} ({recorder.Frames} frames)");

        if (session.Peer?.GetTelemetry() is { } telemetry)
        {
            Console.WriteLine($"candidates       : local {telemetry.LocalCandidateTypes ?? "-"} " +
                              $"remote {telemetry.RemoteCandidateType ?? "-"}");
            Console.WriteLine($"relay            : {telemetry.UsingRelay}");
            Console.WriteLine($"rtt / loss       : {telemetry.RoundTripMs:0}ms / {telemetry.PacketLossFraction:P1}");
        }

        Console.WriteLine("-------------------------");

        if (o.Snapshot is { } path && lastDecoded is not null) WriteSnapshot(lastDecoded, path);

        return outcome switch
        {
            ControllerOutcome.JoinFailed or ControllerOutcome.ConsentRefused => 5,
            ControllerOutcome.SdpRejected => 6,
            _ when decoder.Decoded == 0 => 7,
            _ => 0,
        };
    }

    /// <summary>
    /// Runs without a window, decoding purely to prove the frames are real.
    /// </summary>
    /// <remarks>
    /// Decoding matters even with nothing to draw on. Packet counters prove bytes
    /// arrived; only a decode proves they were a picture, which is the difference
    /// between a working session and one where both ends are confidently exchanging
    /// something neither can read.
    /// </remarks>
    private static async Task<DecodedFrame?> RunHeadlessAsync(
        ControllerSession session, Vp8Decoder decoder, IvfWriter? recorder, CancellationToken ct)
    {
        DecodedFrame? last = null;
        long received = 0;

        session.VideoFrameReceived += (frame, timestamp) =>
        {
            Interlocked.Increment(ref received);
            recorder?.Write(frame, timestamp);

            // On the receive thread on purpose: headless has no frame budget to protect
            // and no user waiting, and decoding inline keeps the ordering obvious.
            if (decoder.Decode(frame) is { } decoded)
            {
                last = decoded;
                recorder?.SetDimensions(decoded.Width, decoded.Height);
            }
        };

        Console.WriteLine("headless: waiting for media. Ctrl+C to stop.");

        try
        {
            while (!ct.IsCancellationRequested && !session.Completion.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);

                var peer = session.Peer;
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] {session.LinkState} " +
                    $"| rx frames {Interlocked.Read(ref received)} " +
                    $"| pkts {peer?.VideoPacketsReceived ?? 0} " +
                    $"| bytes {peer?.VideoBytesReceived ?? 0} " +
                    $"| {decoder}" +
                    (last is { } f ? $" | {f.Width}x{f.Height}" : ""));
            }
        }
        catch (OperationCanceledException)
        {
            // The time limit or Ctrl+C.
        }

        return last;
    }

    /// <summary>Runs the viewer, returning the last frame it decoded for the snapshot.</summary>
    private static DecodedFrame? RunViewer(
        ControllerSession session,
        Vp8Decoder decoder,
        IvfWriter? recorder,
        bool viewOnly,
        CancellationTokenSource cancellation)
    {
        // A dedicated STA thread, not whatever thread this method happens to be on.
        //
        // `[STAThread]` on Main only covers the main thread, and by the time the viewer
        // starts, the connect path has awaited registration and TURN — so it has almost
        // certainly resumed on a thread-pool thread, which is MTA. Running a message loop
        // there is unsupported: it breaks the clipboard, drag-and-drop and modal dialogs,
        // and it does so intermittently rather than immediately, which is the worst way
        // to find out.
        DecodedFrame? lastDecoded = null;

        var thread = new Thread(() =>
        {
            // Spelled out rather than ApplicationConfiguration.Initialize(), which is
            // generated code whose content depends on csproj properties. A harness should
            // not have its DPI behaviour — which D9 is specifically about — decided
            // somewhere invisible.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            using var form = new ViewerForm(session, decoder, recorder, viewOnly, Log);

            // Either end can finish first: the operator closes the window, or the host
            // hangs up. Whichever happens, the other has to stop too.
            _ = session.Completion.ContinueWith(_ => CloseOnUiThread(form), TaskScheduler.Default);
            using var registration = cancellation.Token.Register(() => CloseOnUiThread(form));

            Application.Run(form);

            lastDecoded = form.LastDecoded;
        })
        {
            IsBackground = false,
            Name = "techee-ctl viewer",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        cancellation.Cancel();

        return lastDecoded;
    }

    /// <summary>Closes the viewer from whichever thread noticed the session was over.</summary>
    private static void CloseOnUiThread(Form form)
    {
        if (!form.IsHandleCreated || form.IsDisposed) return;

        try
        {
            form.BeginInvoke(() => form.Close());
        }
        catch (ObjectDisposedException)
        {
            // The window went away between the check and the post.
        }
        catch (InvalidOperationException)
        {
            // The handle was destroyed while the post was in flight.
        }
    }

    private static void WriteSnapshot(DecodedFrame frame, string path)
    {
        try
        {
            using var bitmap = VideoSurface.ToBitmap(frame);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"snapshot         : {path} ({frame.Width}x{frame.Height})");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"could not write the snapshot: {e.Message}");
        }
    }

    // ---- helpers ----

    /// <summary>
    /// Marks a trust entry seeded from the command line rather than paired.
    /// </summary>
    /// <remarks>
    /// The same marker the host uses, in the same free-text field, so a store written by
    /// either app reads the same way.
    /// </remarks>
    private const string TestSeededMarker = "TEST-SEEDED";

    private const string ControllerKeyName = "Techee.ControllerIdentity";

    private static void WriteSeededTrustWarning()
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;

        Console.WriteLine();
        Console.WriteLine("  ****************************************************************");
        Console.WriteLine("  *  TEST / ACCEPTANCE HARNESS ONLY                              *");
        Console.WriteLine("  *                                                              *");
        Console.WriteLine("  *  This peer was seeded from the command line. It did NOT go    *");
        Console.WriteLine("  *  through Techee pairing, so it has no shared secret and its   *");
        Console.WriteLine("  *  SAFETY NUMBER IS MEANINGLESS. Do not compare it, and do not  *");
        Console.WriteLine("  *  treat this as evidence that pairing works (matrix row D3).   *");
        Console.WriteLine("  *                                                              *");
        Console.WriteLine("  *  Session security is UNAFFECTED: SDP signature verification,  *");
        Console.WriteLine("  *  DTLS fingerprint binding and peer-identity checks all still  *");
        Console.WriteLine("  *  run against this key exactly as for a paired peer.           *");
        Console.WriteLine("  ****************************************************************");
        Console.WriteLine();

        Console.ForegroundColor = previous;
    }

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    private static bool IsDeviceId(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static CngDeviceIdentity OpenIdentity(IReadOnlyDictionary<string, string> options) =>
        CngDeviceIdentity.OpenOrCreate(
            KeyScope.User,
            options.GetValueOrDefault("key", ControllerKeyName));

    /// <summary>
    /// The controller's own store, separate from the host's.
    /// </summary>
    /// <remarks>
    /// A subdirectory rather than the shared <c>Techee</c> folder, so running both apps
    /// on one machine — which is the quickest way to smoke-test a change — does not have
    /// them overwriting each other's trust store.
    /// </remarks>
    private static IProtectedStore OpenStore(IReadOnlyDictionary<string, string> options) =>
        new DpapiFileStore(options.GetValueOrDefault(
            "store",
            Path.Combine(DpapiFileStore.UserDirectory, "controller")));

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"--{name} is required");

    /// <summary>
    /// Parses <c>--name value</c> and bare <c>--flag</c> pairs.
    /// </summary>
    /// <remarks>
    /// A flag takes the empty string, so <c>ContainsKey</c> answers "was it given" and
    /// <c>Required</c> still rejects a switch used where a value was needed.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pending = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null) options[pending] = "";
                pending = arg[2..];
            }
            else if (pending is not null)
            {
                options[pending] = arg;
                pending = null;
            }
        }

        if (pending is not null) options[pending] = "";
        return options;
    }
}
