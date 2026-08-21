using Techee.Crypto;
using Techee.Protocol;
using Techee.Session;
using Techee.Signaling;
using Techee.Store;
using Techee.Windows.Host;

namespace Techee.Windows.HostApp;

/// <summary>
/// The runnable Techee Windows host.
/// </summary>
/// <remarks>
/// <para>
/// A console harness, not the production service — that is W6. It exists so W3's two
/// remaining acceptance criteria can be executed at all: an Android controller rendering
/// this desktop, and a session forced through TURN. Every Windows assembly before this
/// was a library, so there was nothing to launch.
/// </para>
/// <para>
/// <b>Trust is seeded from the command line here.</b> <c>trust</c> writes a controller's
/// public key straight into the trust store, bypassing the interactive pairing exchange.
/// That is deliberate: it isolates the video acceptance test from the pairing UX so a
/// failure means one thing rather than two. It does <b>not</b> validate pairing — that is
/// a separate matrix row and still needs two real devices.
/// </para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
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
                "run" => await RunAsync(options),
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
            techee-host — Techee Windows host (W3/W4 acceptance harness)

              identity  [--store DIR]
                  Print this machine's device ID and public key. Give the public key to
                  the controller so it can verify this host's SDP.

              trust     --pub BASE64 [--name NAME] [--grant-id ID] [--control] [--store DIR]
                  Trust a controller and grant it screen viewing. Seeds the trust and
                  grant stores directly, bypassing interactive pairing.

                  --control       Also grant input.control, so the controller may drive
                                  the mouse and keyboard. Off by default: a grant that
                                  silently conferred control would make view-only
                                  impossible to test.

              list      [--store DIR]
                  Show trusted peers and grants.

              run       --broker URL [--force-relay] [--profile NAME] [--no-input] [--store DIR]
                  Register, announce availability, and serve one controller at a time.

                  --force-relay   Restrict ICE to relay candidates. The session can then
                                  only succeed through TURN, which is what makes the TURN
                                  acceptance test conclusive rather than incidental.
                  --profile       1080p20 | 720p30 | 540p30 | 360p20   (default 720p30)
                  --no-input      Refuse input entirely, whatever the grant says. The
                                  host becomes view-only at the transport level rather
                                  than by permission, which is the safe way to run the
                                  video acceptance tests on a machine you are using.

            Stores default to %LOCALAPPDATA%\Techee and are DPAPI-protected.
            """);
        return 1;
    }

    // ---- verbs ----

    private static int Identity(IReadOnlyDictionary<string, string> options)
    {
        using var identity = CngDeviceIdentity.OpenOrCreate();

        Console.WriteLine($"device id  : {identity.DeviceId}");
        Console.WriteLine($"public key : {identity.PublicKeyB64}");
        Console.WriteLine($"short fp   : {TecheeCrypto.ShortFingerprint(identity.PublicKeySpkiDer)}");
        Console.WriteLine();
        Console.WriteLine("Give the SHORT FINGERPRINT to the operator to compare on the controller.");
        _ = options;
        return 0;
    }

    private static int Trust(IReadOnlyDictionary<string, string> options)
    {
        var pub = Required(options, "pub");
        var name = options.GetValueOrDefault("name", "Acceptance controller");
        var grantId = options.GetValueOrDefault("grant-id", "acceptance-grant");

        // Opt-in. Screen viewing and screen control are different things to consent to,
        // and defaulting to both would leave no way to exercise the view-only path that
        // most of the security model's interesting behaviour lives on.
        var control = options.ContainsKey("control");

        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(pub);
        }
        catch (FormatException)
        {
            Console.Error.WriteLine("--pub must be the controller's base64 SPKI public key.");
            return 2;
        }

        var store = OpenStore(options);
        var trust = new TrustStore(store);
        var grants = new GrantStore(store);

        var deviceId = TecheeCrypto.DeviceIdFor(spki);

        trust.Save(new PeerIdentity
        {
            PublicKeySpkiB64 = pub,
            Name = name,
            // No pairing exchange happened, so there is no ECDH secret to record. The
            // safety number is therefore not meaningful for a peer added this way, which
            // is precisely why this path is a test affordance and not a pairing flow.
            SharedSecretB64 = Convert.ToBase64String(new byte[32]),
            State = TrustState.PendingConfirm,
            PairedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            // Recorded in the store itself, not just printed once. Anything reading this
            // peer later can see it never went through pairing.
            Platform = TestSeededMarker,
        });
        trust.Confirm(pub);

        WriteSeededTrustWarning();

        string[] permissions = control ? ["screen.view", "input.control"] : ["screen.view"];

        grants.Save(new Grant
        {
            GrantId = grantId,
            ControllerId = deviceId,
            ControllerName = name,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PermissionTokens = permissions,
        });

        Console.WriteLine(
            $"trusted {deviceId} as '{name}' with grant '{grantId}' ({string.Join(", ", permissions)})");

        if (control)
        {
            Console.WriteLine();
            Console.WriteLine("This controller can now move the mouse and type on this machine.");
        }

        return 0;
    }

    private static int List(IReadOnlyDictionary<string, string> options)
    {
        var store = OpenStore(options);
        var trust = new TrustStore(store);
        var grants = new GrantStore(store);

        Console.WriteLine("trusted peers:");
        foreach (var peer in trust.All)
        {
            var seeded = peer.Platform == TestSeededMarker ? "  <<< TEST-SEEDED, NOT PAIRED" : "";
            Console.WriteLine($"  {peer}{seeded}");
        }

        if (trust.All.Any(p => p.Platform == TestSeededMarker)) WriteSeededTrustWarning();

        Console.WriteLine("grants:");
        foreach (var grant in grants.All)
        {
            Console.WriteLine($"  {grant.GrantId} -> {grant.ControllerId} " +
                              $"[{string.Join(",", grant.EffectivePermissions)}] active={grant.Active}");
        }

        return 0;
    }

    private static async Task<int> RunAsync(IReadOnlyDictionary<string, string> options)
    {
        var broker = new Uri(Required(options, "broker"));
        var forceRelay = options.ContainsKey("force-relay");
        var acceptInput = !options.ContainsKey("no-input");
        var profile = ResolveProfile(options.GetValueOrDefault("profile", VideoProfile.Hd.Name));

        if (profile is null)
        {
            Console.Error.WriteLine("--profile must be one of: " +
                                    string.Join(", ", VideoProfile.Ladder.Select(p => p.Name)));
            return 2;
        }

        var store = OpenStore(options);
        var trust = new TrustStore(store);
        var grants = new GrantStore(store);

        using var identity = CngDeviceIdentity.OpenOrCreate();
        // Advertised only when this host will actually act on input. Capability is
        // description, not authorisation — but describing something we have switched off
        // would have the controller offer an affordance that silently does nothing.
        string[] capabilities = acceptInput ? ["screen.share", "input.receive"] : ["screen.share"];

        await using var signaling = new SignalingClient(
            broker, identity,
            new EndpointMeta("windows", "w4-acceptance", capabilities),
            Log);

        await using var session = new WindowsHostSession(
            identity, signaling, trust, grants,
            pipelineFactory: () => BuildPipeline(profile),
            log: Log,
            injectorFactory: acceptInput ? BuildInjector : null)
        {
            ForceRelay = forceRelay,
        };

        if (!acceptInput) Console.WriteLine("input injection disabled (--no-input); this host is view-only.");

        if (trust.All.Any(p => p.Platform == TestSeededMarker)) WriteSeededTrustWarning();

        Console.WriteLine($"device id : {identity.DeviceId}");
        Console.WriteLine($"short fp  : {TecheeCrypto.ShortFingerprint(identity.PublicKeySpkiDer)}");
        Console.WriteLine($"broker    : {broker}");
        Console.WriteLine($"profile   : {profile}");
        Console.WriteLine($"ICE policy: {(forceRelay ? "RELAY ONLY (TURN must carry this session)" : "all candidates")}");
        Console.WriteLine();

        var registration = await session.RegisterAsync();
        if (!registration.Ok)
        {
            Console.Error.WriteLine($"registration failed: {registration.Reason}");
            return 3;
        }

        // TURN credentials are time-limited and issued on request. Without them a
        // relay-only session has nowhere to go.
        await signaling.RequestTurnCredentialsAsync();
        await Task.Delay(500);

        // TURN URLs are not secret; the credentials that come with them are, and
        // IceServer.ToString redacts them. Counting relay servers separately is what the
        // acceptance procedure actually needs to know.
        var relayServers = signaling.IceServers
            .Count(s => s.Urls.StartsWith("turn", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine();
        Console.WriteLine("---- host status ----");
        Console.WriteLine($"device id        : {identity.DeviceId}");
        Console.WriteLine($"broker connected : {(signaling.IsConnected ? "yes" : "NO")}");
        Console.WriteLine($"registration     : OK (broker echoed this device id)");
        Console.WriteLine($"broker protocol  : {signaling.BrokerProtocolVersion?.ToString() ?? "not advertised"}");
        Console.WriteLine($"host-open        : {(session.State == HostSessionState.Listening ? "OK" : "FAILED")}");
        Console.WriteLine($"TURN credentials : {(relayServers > 0 ? "yes" : "no")}");
        Console.WriteLine($"ICE servers      : {signaling.IceServers.Count} ({relayServers} relay)");
        Console.WriteLine($"transport policy : {(forceRelay ? "relay only" : "all")}");
        Console.WriteLine($"session state    : {session.State}");
        Console.WriteLine("---------------------");

        foreach (var server in signaling.IceServers) Console.WriteLine($"  {server}");

        if (forceRelay && relayServers == 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("--force-relay was requested but the broker offered no TURN server.");
            Console.Error.WriteLine("The session could not possibly connect. Fix TURN_HOST/TURN_SECRET first.");
            return 4;
        }

        if (session.State != HostSessionState.Listening)
        {
            Console.Error.WriteLine($"host-open did not take effect; state is {session.State}.");
            return 5;
        }

        Console.WriteLine();
        Console.WriteLine("waiting for a controller. Ctrl+C to stop.");

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        var pump = session.RunAsync(cancellation.Token);
        var reporter = ReportAsync(session, signaling, cancellation);

        await Task.WhenAll(pump, reporter);

        var lostRegistration = await reporter;

        // Ordered teardown. DisposeAsync ends the session — detaching the sink, stopping
        // and disposing the pipeline, and closing the peer — before the signaling socket
        // goes, so the controller receives the hangup rather than inferring it from a
        // dropped connection.
        Console.WriteLine();
        Console.WriteLine("shutting down…");
        WriteCounters(session);

        await session.DisposeAsync();
        await signaling.DisposeAsync();

        Console.WriteLine($"session state    : {session.State}");
        Console.WriteLine($"broker connected : {(signaling.IsConnected ? "yes" : "no")}");
        Console.WriteLine("stopped cleanly.");

        return lostRegistration ? 6 : 0;
    }

    /// <summary>
    /// Prints the numbers the acceptance procedure asks the operator to record.
    /// </summary>
    /// <remarks>
    /// Emits a line even with no controller attached. A host that prints nothing for five
    /// minutes is indistinguishable from a hung one, and the idle soak in the acceptance
    /// procedure depends on being able to tell those apart.
    /// </remarks>
    private static async Task<bool> ReportAsync(
        WindowsHostSession session, SignalingClient signaling, CancellationTokenSource cancellation)
    {
        var started = DateTimeOffset.UtcNow;
        var ct = cancellation.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);

                // A host that has lost its broker socket is not a host. It cannot be
                // reached, it will never receive a join-request, and no controller will
                // ever see it — but it looks perfectly healthy from the outside, which is
                // worse than crashing. Observed for real when a second instance
                // registered the same identity and the broker replaced this socket:
                // this process idled in Closed/DISCONNECTED indefinitely.
                //
                // Reconnection belongs to the production service, not this harness, so
                // the correct behaviour here is to stop and say why.
                if (!signaling.IsConnected)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] LOST BROKER REGISTRATION — session state {session.State}.");
                    Console.Error.WriteLine(
                        "  The broker connection is gone, so this host is unreachable and will never");
                    Console.Error.WriteLine(
                        "  receive a controller. Common causes: the broker restarted, or another");
                    Console.Error.WriteLine(
                        "  process registered the same device identity and displaced this socket.");
                    Console.Error.WriteLine("  Exiting rather than idling in an unreachable state.");

                    await cancellation.CancelAsync();
                    return true;
                }

                if (session.Pipeline is null)
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] idle | state {session.State} " +
                        $"| broker connected " +
                        $"| ice servers {signaling.IceServers.Count} " +
                        $"| uptime {DateTimeOffset.UtcNow - started:hh\\:mm\\:ss}");
                    continue;
                }

                WriteCounters(session);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }

        return false;
    }

    private static void WriteCounters(WindowsHostSession session)
    {
        var pipeline = session.Pipeline;
        var peer = session.Peer;
        if (pipeline is null) return;

        var stats = pipeline.Stats;
        var telemetry = peer?.GetTelemetry();

        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] {session.State} | {stats.Profile.Name} " +
            $"| fps {stats.AchievedFps:0.0} " +
            $"| convert {stats.AverageConvertMs:0.0}ms encode {stats.AverageEncodeMs:0.0}ms " +
            $"total {stats.ProcessingMsPerFrame:0.0}ms ({stats.ProcessingPressure:P0} of budget) " +
            $"| cap {stats.FramesCaptured} enc {stats.FramesEncoded} sent {stats.FramesSent} " +
            $"superseded {stats.FramesSuperseded} dropped {stats.FramesDropped} depth {stats.SendQueueDepth} " +
            $"| relay-gathered {telemetry?.UsingRelay} loss {telemetry?.PacketLossFraction:P1} " +
            $"| candidates {telemetry?.LocalCandidateTypes ?? "-"} " +
            $"| recreations {session.PeerRecreations} " +
            $"| vp8 pt {session.NegotiatedVideoPayloadType?.ToString() ?? "NOT NEGOTIATED"}");

        // On its own line and only once there is something to report, so the video
        // counters stay readable during the W3 tests. The breakdown matters because
        // "my clicks do nothing" has four different causes and they are only
        // distinguishable here: no grant, too fast, worker behind, or nothing to do.
        var input = session.ControlHandler;
        if (input is null || input.Accepted + input.Refused == 0) return;

        Console.WriteLine(
            $"           input | accepted {input.Accepted} executed {input.Executed} " +
            $"| refused {input.Refused} rate-limited {input.RateLimited} " +
            $"queue-dropped {input.QueueDropped} " +
            $"| unsupported {input.Unsupported} failed {input.Failed}");
    }

    private static VideoPipeline BuildPipeline(VideoProfile profile)
    {
        var displays = DesktopDuplicationSource.EnumerateDisplays();
        var display = displays.FirstOrDefault(d => d.IsPrimary)
                      ?? displays.FirstOrDefault()
                      ?? throw new InvalidOperationException("no display found to capture");

        // Physical is what duplication delivers; logical is what the desktop reports. On a
        // DPI-scaled monitor they differ, and printing both makes a scaling surprise
        // obvious in the acceptance log rather than three steps later.
        Console.WriteLine(
            $"capturing {display.DeviceName}: " +
            $"{display.PhysicalWidth}x{display.PhysicalHeight} physical, " +
            $"{display.LogicalWidth}x{display.LogicalHeight} logical (scale {display.Scale:0.00})");

        return new VideoPipeline(
            new DesktopDuplicationSource(display),
            new VpxEncoder { TargetKbps = profile.TargetKbps },
            new AdaptiveQuality(profile),
            Log);
    }

    /// <summary>
    /// Builds the input injector for a session.
    /// </summary>
    /// <remarks>
    /// The display layout is snapshotted here rather than per event, because enumerating
    /// it is a DXGI call and input arrives up to 250 times a second. The cost of the
    /// snapshot is that hot-plugging a monitor mid-session leaves the layout stale until
    /// the next session — acceptable for an acceptance harness, and something the W6
    /// service will want to handle by watching for display-change notifications.
    /// </remarks>
    private static IInputInjector BuildInjector()
    {
        var displays = DesktopDuplicationSource.EnumerateDisplays();
        var injector = new SendInputInjector(displays, Log);

        Console.WriteLine($"input enabled across {displays.Count} display(s)");
        if (injector.UnavailableReason is { } reason) Console.WriteLine($"  note: {reason}");

        return injector;
    }

    // ---- helpers ----

    /// <summary>
    /// Marks a trust entry that was seeded from the command line rather than paired.
    /// </summary>
    /// <remarks>
    /// Stored in <see cref="PeerIdentity.Platform"/>, which is a free-text descriptive
    /// field, so this needs no change to the store model. It survives restarts, which a
    /// printed warning does not — the point is that a seeded peer cannot later be
    /// mistaken for one that completed pairing.
    /// </remarks>
    private const string TestSeededMarker = "TEST-SEEDED";

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
        Console.WriteLine("  *  DTLS fingerprint binding, peer-identity and grant checks all *");
        Console.WriteLine("  *  still run against this key exactly as for a paired peer.     *");
        Console.WriteLine("  ****************************************************************");
        Console.WriteLine();

        Console.ForegroundColor = previous;
    }

    private static VideoProfile? ResolveProfile(string name) =>
        VideoProfile.Ladder.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IProtectedStore OpenStore(IReadOnlyDictionary<string, string> options) =>
        new DpapiFileStore(options.GetValueOrDefault("store", DpapiFileStore.UserDirectory));

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"--{name} is required");

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>Parses <c>--key value</c> and bare <c>--flag</c> arguments.</summary>
    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pending = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null) result[pending] = "true";
                pending = arg[2..];
            }
            else if (pending is not null)
            {
                result[pending] = arg;
                pending = null;
            }
        }

        if (pending is not null) result[pending] = "true";
        return result;
    }
}
