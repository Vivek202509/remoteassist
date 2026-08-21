using System.Text.Json;
using System.Text.Json.Serialization;

namespace Techee.Protocol;

/// <summary>
/// A standing grant from this host to one paired controller identity, created by the
/// machine's owner and revocable at any time.
/// </summary>
/// <remarks>
/// <para>
/// The authoritative half of the capability/permission split. A grant is evaluated
/// <b>locally, by the host that will execute the action</b> — never by the broker,
/// and never from the peer's own advertisement.
/// </para>
/// <para>
/// <see cref="ControllerId"/> is the peer's device ID, the same identity the trust
/// store keys on. A grant naming a controller that is not in the trust store must
/// never auto-accept: the grant says <i>what</i> is allowed, the trust store says
/// <i>who</i>, and both are required.
/// </para>
/// </remarks>
public sealed record Grant
{
    [JsonPropertyName("grantId")] public required string GrantId { get; init; }
    [JsonPropertyName("controllerId")] public required string ControllerId { get; init; }
    [JsonPropertyName("controllerName")] public string ControllerName { get; init; } = "Paired device";
    [JsonPropertyName("createdAt")] public long CreatedAt { get; init; }

    /// <summary>Epoch milliseconds, or null for no expiry.</summary>
    [JsonPropertyName("expiresAt")] public long? ExpiresAt { get; init; }

    [JsonPropertyName("active")] public bool Active { get; init; } = true;

    /// <summary>
    /// Downgrade to view-only while the workstation is locked.
    /// </summary>
    /// <remarks>
    /// The Windows analogue of Android's keyguard check. Unlike Android's, this is
    /// re-evaluated during the session rather than sampled once at start.
    /// </remarks>
    [JsonPropertyName("requireUnlock")] public bool RequireUnlock { get; init; }

    /// <summary>v1 permission tokens. Takes precedence over <see cref="LegacyScope"/>.</summary>
    [JsonPropertyName("permissions")] public IReadOnlyList<string>? PermissionTokens { get; init; }

    /// <summary>Legacy Android <c>Scope</c> names, widened when no v1 permissions are present.</summary>
    [JsonPropertyName("scope")] public IReadOnlyList<string>? LegacyScope { get; init; }

    [JsonPropertyName("lastUsedAt")] public long? LastUsedAt { get; init; }

    /// <summary>The permissions this grant confers after normalization.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectivePermissions =>
        TecheeProtocol.NormalizePermissions(PermissionTokens, LegacyScope);

    /// <summary>
    /// Whether the grant is usable at <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// <c>ExpiresAt</c> null means no expiry. A present value is honoured including
    /// <c>0</c> — the Unix epoch, i.e. maximally expired. The shipped
    /// <c>state.js</c> uses a truthiness guard and reads 0 as never-expiring; this
    /// does not, and the fixture pins the fail-closed behaviour.
    /// </remarks>
    public bool IsUsable(DateTimeOffset now) =>
        Active && (ExpiresAt is null || ExpiresAt.Value >= now.ToUnixTimeMilliseconds());

    /// <summary>The view-only form used while the workstation is locked.</summary>
    public Grant DowngradeToViewOnly() => this with
    {
        PermissionTokens = ["screen.view"],
        LegacyScope = null,
    };

    public bool Permits(string permission, DateTimeOffset now) =>
        TecheeProtocol.GrantPermits(this, permission, now);

    public static Grant? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Grant>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
