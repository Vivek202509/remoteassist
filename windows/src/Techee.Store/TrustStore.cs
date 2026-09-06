using System.Text.Json;
using System.Text.Json.Serialization;
using Techee.Crypto;

namespace Techee.Store;

/// <summary>How far a paired peer is trusted.</summary>
/// <remarks>Mirrors Android's <c>TrustState</c> so the two stores describe the same thing.</remarks>
public enum TrustState
{
    /// <summary>Pairing completed cryptographically but the safety number is unconfirmed.</summary>
    PendingConfirm,

    /// <summary>The user compared the safety number and accepted it.</summary>
    Trusted,

    /// <summary>Explicitly withdrawn. Retained so the revocation itself is remembered.</summary>
    Revoked,
}

/// <summary>
/// A paired peer.
/// </summary>
/// <remarks>
/// <see cref="PublicKeySpkiB64"/> is the real identity and the primary key; the device
/// ID is derived from it. Everything downstream — grants, SDP verification — resolves
/// through this record.
/// </remarks>
public sealed record PeerIdentity
{
    [JsonPropertyName("pub")] public required string PublicKeySpkiB64 { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>
    /// The raw ECDH secret from pairing, used only to recompute the safety number.
    /// </summary>
    /// <remarks>
    /// This is key material. The file it lives in is DPAPI-protected, and it must
    /// never be logged or included in diagnostics.
    /// </remarks>
    [JsonPropertyName("secret")] public required string SharedSecretB64 { get; init; }

    [JsonPropertyName("state")] public TrustState State { get; init; } = TrustState.PendingConfirm;
    [JsonPropertyName("platform")] public string? Platform { get; init; }
    [JsonPropertyName("pairedAt")] public long PairedAt { get; init; }
    [JsonPropertyName("lastSeenAt")] public long? LastSeenAt { get; init; }

    /// <summary>The peer's device ID, derived rather than stored so the two cannot disagree.</summary>
    [JsonIgnore]
    public string DeviceId => TecheeCrypto.DeviceIdFor(Convert.FromBase64String(PublicKeySpkiB64));

    [JsonIgnore]
    public string ShortFingerprint =>
        TecheeCrypto.ShortFingerprint(Convert.FromBase64String(PublicKeySpkiB64));

    /// <summary>
    /// Redacts the shared secret.
    /// </summary>
    /// <remarks>
    /// The compiler-generated record <c>ToString</c> would print every property, and
    /// an interpolated log line is exactly how pairing key material reaches a disk.
    /// </remarks>
    public override string ToString() =>
        $"PeerIdentity {{ DeviceId = {DeviceId[..12]}…, Name = {Name}, State = {State}, Secret = <redacted> }}";
}

/// <summary>
/// The set of peers this endpoint has paired with.
/// </summary>
/// <remarks>
/// <para>
/// The Windows counterpart of Android's <c>TrustStore</c>, with one deliberate
/// behavioural difference: <see cref="Save"/> refuses to downgrade an already-trusted
/// peer. Android's <c>savePending</c> is an unconditional overwrite, so any accepted
/// <c>pair-complete</c> can knock a trusted peer back to <c>PENDING_CONFIRM</c> —
/// a denial of service against unattended access. This does not reproduce that.
/// </para>
/// <para>
/// Not thread-safe by itself; callers hold the instance behind their own
/// synchronisation. The service uses a single instance on one loop.
/// </para>
/// </remarks>
public sealed class TrustStore(IProtectedStore backing)
{
    private const string StoreKey = "peers";

    private readonly Dictionary<string, PeerIdentity> _peers = Load(backing);

    private static Dictionary<string, PeerIdentity> Load(IProtectedStore backing)
    {
        var json = backing.Read(StoreKey);
        if (json is null) return [];

        try
        {
            var list = JsonSerializer.Deserialize<List<PeerIdentity>>(json) ?? [];
            return list.ToDictionary(p => p.PublicKeySpkiB64);
        }
        catch (JsonException)
        {
            // A corrupt store must not prevent the service from starting — an office
            // host that will not boot is worse than one that has forgotten its
            // pairings and can be re-paired. The file is left in place for inspection.
            return [];
        }
    }

    private void Persist() =>
        backing.Write(StoreKey, JsonSerializer.Serialize(_peers.Values.ToList()));

    public IReadOnlyCollection<PeerIdentity> All => _peers.Values;

    public PeerIdentity? Find(string publicKeySpkiB64) =>
        _peers.GetValueOrDefault(publicKeySpkiB64);

    /// <summary>Finds a peer by its device ID.</summary>
    public PeerIdentity? FindByDeviceId(string deviceId) =>
        _peers.Values.FirstOrDefault(p => p.DeviceId == deviceId);

    /// <summary>
    /// The public key to verify a peer's SDP against, or null if there is none to use.
    /// </summary>
    /// <remarks>
    /// <b>Returns null for anything not <see cref="TrustState.Trusted"/>.</b> Android's
    /// equivalent searches all peers regardless of state, so a <i>revoked</i> peer's
    /// SDP still authenticates — a live gap recorded in <c>docs/ARCHITECTURE.md</c>.
    /// Revocation that does not actually revoke is worse than no revocation, because
    /// the operator believes they have acted.
    /// </remarks>
    public byte[]? PublicKeyForSdp(string deviceId)
    {
        var peer = FindByDeviceId(deviceId);
        if (peer is null || peer.State != TrustState.Trusted) return null;
        return Convert.FromBase64String(peer.PublicKeySpkiB64);
    }

    public bool IsTrusted(string deviceId) => FindByDeviceId(deviceId)?.State == TrustState.Trusted;

    /// <summary>
    /// Records a newly paired peer.
    /// </summary>
    /// <remarks>
    /// Refuses to move an existing <see cref="TrustState.Trusted"/> peer backwards.
    /// Re-pairing an already-trusted device is a no-op rather than a downgrade, so a
    /// replayed or hostile <c>pair-complete</c> cannot revoke working unattended
    /// access. Use <see cref="Remove"/> first if a genuine re-pair is intended.
    /// </remarks>
    public bool Save(PeerIdentity peer)
    {
        if (_peers.TryGetValue(peer.PublicKeySpkiB64, out var existing)
            && existing.State == TrustState.Trusted)
        {
            return false;
        }

        _peers[peer.PublicKeySpkiB64] = peer;
        Persist();
        return true;
    }

    /// <summary>Promotes a peer to trusted once the user has confirmed the safety number.</summary>
    public void Confirm(string publicKeySpkiB64)
    {
        if (!_peers.TryGetValue(publicKeySpkiB64, out var peer)) return;
        _peers[publicKeySpkiB64] = peer with { State = TrustState.Trusted };
        Persist();
    }

    /// <summary>
    /// Revokes a peer, keeping the record.
    /// </summary>
    /// <remarks>
    /// Retained rather than deleted so the revocation is remembered: a peer that could
    /// re-pair silently would make revocation meaningless. Callers that want the peer
    /// gone entirely use <see cref="Remove"/>.
    /// </remarks>
    public void Revoke(string publicKeySpkiB64)
    {
        if (!_peers.TryGetValue(publicKeySpkiB64, out var peer)) return;
        _peers[publicKeySpkiB64] = peer with { State = TrustState.Revoked };
        Persist();
    }

    public void Remove(string publicKeySpkiB64)
    {
        if (_peers.Remove(publicKeySpkiB64)) Persist();
    }

    public void MarkSeen(string publicKeySpkiB64, DateTimeOffset now)
    {
        if (!_peers.TryGetValue(publicKeySpkiB64, out var peer)) return;
        _peers[publicKeySpkiB64] = peer with { LastSeenAt = now.ToUnixTimeMilliseconds() };
        Persist();
    }
}
