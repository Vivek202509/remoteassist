using System.Text.Json;

namespace Techee.Protocol;

/// <summary>
/// The Techee protocol contract, in C#.
/// </summary>
/// <remarks>
/// One of three implementations of the same specification — the others are
/// <c>server/src/protocol.js</c> and <c>com.remoteassist.protocol</c> on Android.
/// None is derived from the others; all three are held to the golden vectors in
/// <c>protocol/fixtures/</c>. See <c>docs/PROTOCOL.md</c>.
/// <para>
/// Deliberately free of Windows types so it is testable on any runner and reusable
/// by both the host service and the controller UI.
/// </para>
/// </remarks>
public static class TecheeProtocol
{
    /// <summary>
    /// Bumping this means adding messages or fields. It never means changing what a
    /// v1 message already means: a peer that only speaks v1 must be able to go on
    /// speaking v1 to a newer peer indefinitely.
    /// </summary>
    public const int ProtocolVersion = 1;

    public const int MinProtocolVersion = 1;

    /// <summary>
    /// Frames with no <c>v</c> field are the pre-versioning dialect every shipped
    /// Android build speaks. A supported dialect, not an error to be tolerated.
    /// </summary>
    public const int LegacyVersion = 0;

    public static readonly IReadOnlyList<string> Platforms = ["android", "windows"];

    public static readonly IReadOnlyList<string> Capabilities =
    [
        "screen.share", "screen.receive",
        "input.send", "input.receive",
        "audio.microphone.send", "audio.desktop.send", "audio.receive",
        "clipboard",
        "display.multi",
        "nav.android",
        "power.lock", "power.sleep", "power.hibernate", "power.restart", "power.shutdown",
    ];

    public static readonly IReadOnlyList<string> Permissions =
    [
        "screen.view",
        "input.control",
        "clipboard.read", "clipboard.write",
        "system.lock", "system.sleep", "system.hibernate", "system.restart", "system.shutdown",
        "files.transfer",
    ];

    public const int MaxCapabilities = 32;
    public const int MaxCapabilityLength = 40;
    public const int MaxVersionLength = 32;
    public const int MaxPermissions = 32;
    public const int MaxFrameBytes = 65536;
    public const int MaxTextBytes = 4096;
    public const int MaxClipboardBytes = 65536;
    public const long MaxSwipeMs = 10_000;
    public const long DefaultSwipeMs = 200;

    private static readonly HashSet<string> CapabilitySet = [.. Capabilities];
    private static readonly HashSet<string> PermissionSet = [.. Permissions];

    /// <summary>
    /// How Android's shipped <c>enum Scope { VIEW, CONTROL, FILES, CLIPBOARD }</c>
    /// maps onto v1 permission tokens.
    /// </summary>
    /// <remarks>
    /// Note what is absent: nothing maps to any <c>system.*</c> permission. Every
    /// grant created before power control existed therefore confers no power over the
    /// machine, so installing a Techee Windows host cannot hand an existing paired
    /// controller the ability to shut it down. That is the reason this is a table
    /// with a fixture behind it rather than an inline switch.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string[]> LegacyScopeMap =
        new Dictionary<string, string[]>
        {
            ["VIEW"] = ["screen.view"],
            // Controlling a screen you may not see is not a coherent grant.
            ["CONTROL"] = ["input.control", "screen.view"],
            ["CLIPBOARD"] = ["clipboard.read", "clipboard.write"],
            ["FILES"] = ["files.transfer"],
        };

    /// <summary>Which grant permission each command requires. Null means none.</summary>
    private static readonly IReadOnlyDictionary<string, string?> RequiredPermissionMap =
        new Dictionary<string, string?>
        {
            ["pointer.tap"] = "input.control",
            ["pointer.swipe"] = "input.control",
            ["pointer.move"] = "input.control",
            ["pointer.down"] = "input.control",
            ["pointer.up"] = "input.control",
            ["pointer.wheel"] = "input.control",
            ["keyboard.keyDown"] = "input.control",
            ["keyboard.keyUp"] = "input.control",
            ["keyboard.text"] = "input.control",
            ["nav.key"] = "input.control",
            ["clipboard.set"] = "clipboard.write",
            ["clipboard.request"] = "clipboard.read",
            ["clipboard.data"] = null,
            ["system.lock"] = "system.lock",
            ["system.sleep"] = "system.sleep",
            ["system.hibernate"] = "system.hibernate",
            ["system.restart"] = "system.restart",
            ["system.shutdown"] = "system.shutdown",
            ["display.list"] = "screen.view",
            ["display.select"] = "screen.view",
            ["display.info"] = null,
            ["host.status"] = null,
            ["host.callState"] = null,
            ["hello"] = null,
            ["hello.ack"] = null,
            ["error"] = null,
        };

    public static bool IsKnownCommand(string kind) => RequiredPermissionMap.ContainsKey(kind);

    /// <summary>The permission a command requires, or null if it needs none.</summary>
    public static string? RequiredPermission(string kind) =>
        RequiredPermissionMap.TryGetValue(kind, out var p) ? p : null;

    // ---- endpoint metadata (descriptive) ----------------------------------

    /// <summary>
    /// Validates a peer's self-description.
    /// </summary>
    /// <remarks>
    /// Returns null when the metadata is absent or unusable. Null is not a failure to
    /// escalate — it means "this peer did not say what it is", which is exactly what
    /// every Android build shipped so far does. Hide capability-gated UI; never
    /// refuse the peer over it.
    /// <para>
    /// Unknown capability tokens are dropped rather than rejected, so a newer peer
    /// advertising something this build has never heard of still connects.
    /// </para>
    /// <para>
    /// <b>Nothing here is an authorization decision.</b> A peer claiming
    /// <c>power.shutdown</c> has described its own hardware, not acquired a right
    /// over ours. See <see cref="GrantPermits"/>.
    /// </para>
    /// </remarks>
    public static EndpointMeta? ParseEndpointMeta(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } obj) return null;

        if (!obj.TryGetProperty("platform", out var platformEl)
            || platformEl.ValueKind != JsonValueKind.String)
            return null;
        var platform = platformEl.GetString()!;
        if (!Platforms.Contains(platform)) return null;

        var version = "unknown";
        if (obj.TryGetProperty("version", out var versionEl) && versionEl.ValueKind != JsonValueKind.Null)
        {
            if (versionEl.ValueKind != JsonValueKind.String) return null;
            version = versionEl.GetString()!;
        }
        if (version.Length == 0 || version.Length > MaxVersionLength) return null;

        var capabilities = new List<string>();
        if (obj.TryGetProperty("capabilities", out var capsEl) && capsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in capsEl.EnumerateArray())
            {
                if (capabilities.Count >= MaxCapabilities) break;
                if (c.ValueKind != JsonValueKind.String) continue;
                var s = c.GetString()!;
                if (s.Length > MaxCapabilityLength) continue;
                if (!CapabilitySet.Contains(s)) continue;
                if (!capabilities.Contains(s)) capabilities.Add(s);
            }
        }

        // Sorted so implementations that build the list in different orders still
        // compare equal — the fixtures depend on this being deterministic.
        capabilities.Sort(StringComparer.Ordinal);

        return new EndpointMeta(platform, version, capabilities);
    }

    /// <summary>This endpoint's own advertisement.</summary>
    public static EndpointMeta LocalMeta(string version, IEnumerable<string> capabilities)
    {
        var caps = capabilities.Where(CapabilitySet.Contains).Distinct().ToList();
        caps.Sort(StringComparer.Ordinal);
        return new EndpointMeta("windows", version, caps);
    }

    // ---- permissions (authoritative) --------------------------------------

    /// <summary>
    /// The permissions a grant actually confers, sorted and de-duplicated.
    /// </summary>
    /// <remarks>
    /// Prefers an explicit <c>permissions</c> list; otherwise widens a legacy
    /// <c>scope</c> list. Unknown tokens in either are dropped — a permission this
    /// build does not understand is one it cannot enforce, so honouring it would be
    /// worse than ignoring it.
    /// </remarks>
    public static IReadOnlyList<string> NormalizePermissions(
        IEnumerable<string>? permissions,
        IEnumerable<string>? legacyScope)
    {
        var outp = new List<string>();

        void Add(string p)
        {
            if (outp.Count >= MaxPermissions) return;
            if (!PermissionSet.Contains(p)) return;
            if (!outp.Contains(p)) outp.Add(p);
        }

        if (permissions is not null)
        {
            foreach (var p in permissions) Add(p);
        }
        else if (legacyScope is not null)
        {
            foreach (var s in legacyScope)
            {
                if (LegacyScopeMap.TryGetValue(s, out var mapped))
                    foreach (var p in mapped) Add(p);
            }
        }

        outp.Sort(StringComparer.Ordinal);
        return outp;
    }

    /// <summary>Fail-closed authorization: the one question a host asks before acting.</summary>
    public static bool GrantPermits(Grant? grant, string permission, DateTimeOffset now)
    {
        if (!PermissionSet.Contains(permission)) return false;
        if (grant is null || !grant.IsUsable(now)) return false;
        return grant.EffectivePermissions.Contains(permission);
    }

    /// <summary>
    /// Whether a decoded command may be executed under a grant.
    /// </summary>
    /// <remarks>
    /// Fails closed on every path: an unknown command, an unusable grant, or a
    /// missing permission all return false.
    /// </remarks>
    public static bool AuthorizeCommand(Control cmd, Grant? grant, DateTimeOffset now)
    {
        if (!IsKnownCommand(cmd.Kind)) return false;
        var needed = RequiredPermission(cmd.Kind);
        return needed is null || GrantPermits(grant, needed, now);
    }
}

/// <summary>
/// What a peer says it is. Unverified self-description.
/// </summary>
/// <remarks>
/// Use <see cref="Has"/> to decide whether to <i>show</i> an affordance, never
/// whether to <i>allow</i> an action.
/// </remarks>
public sealed record EndpointMeta(string Platform, string Version, IReadOnlyList<string> Capabilities)
{
    /// <summary>Whether the peer claims to support something. Descriptive, not authorising.</summary>
    public bool Has(string capability) => Capabilities.Contains(capability);
}
