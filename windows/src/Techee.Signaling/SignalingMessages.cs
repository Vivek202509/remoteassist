using System.Text.Json;

namespace Techee.Signaling;

/// <summary>Why a registration attempt ended.</summary>
public enum RegistrationOutcome
{
    Registered,

    /// <summary>The broker refused. <see cref="RegistrationResult.Reason"/> says why.</summary>
    Rejected,

    /// <summary>The socket closed or the attempt timed out before an answer arrived.</summary>
    Disconnected,
}

/// <summary>The result of one registration handshake.</summary>
public sealed record RegistrationResult(
    RegistrationOutcome Outcome,
    string? DeviceId = null,
    string? Reason = null,
    IReadOnlyList<IceServer>? IceServers = null,
    int? ProtocolVersion = null)
{
    public bool Ok => Outcome == RegistrationOutcome.Registered;
}

/// <summary>
/// An ICE server issued by the broker.
/// </summary>
/// <remarks>
/// TURN credentials are time-limited and re-issued on request, so these are cached
/// only for the life of a session. <see cref="Credential"/> is a secret and must never
/// be logged — see the redaction note on <see cref="ToString"/>.
/// </remarks>
public sealed record IceServer(string Urls, string? Username = null, string? Credential = null)
{
    /// <summary>
    /// Redacts the credential.
    /// </summary>
    /// <remarks>
    /// Overridden because the compiler-generated record <c>ToString</c> would print
    /// every property, and an interpolated log line is exactly how a TURN secret ends
    /// up on disk.
    /// </remarks>
    public override string ToString() =>
        $"IceServer {{ Urls = {Urls}, Username = {(Username is null ? "null" : "<set>")}, Credential = <redacted> }}";
}

/// <summary>A message received from the broker.</summary>
/// <remarks>
/// The raw <see cref="JsonElement"/> is exposed rather than a per-type model because
/// the broker forwards peer messages verbatim, and the set of fields is open by
/// design — unknown fields must survive rather than be dropped by a strict model.
/// </remarks>
public sealed record SignalingMessage(string Type, JsonElement Body)
{
    public string? GetString(string name) =>
        Body.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public bool GetBool(string name, bool fallback = false) =>
        Body.TryGetProperty(name, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean()
            : fallback;

    /// <summary>The sender's device ID, stamped by the broker and not spoofable by the sender.</summary>
    public string? From => GetString("from");
}
