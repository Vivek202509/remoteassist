using System.Text.Json;
using Techee.Protocol;

namespace Techee.Store;

/// <summary>
/// The standing unattended grants this host has issued.
/// </summary>
/// <remarks>
/// <para>
/// The authority for unattended access. The broker's <c>unattended</c> flag is
/// advisory — a compromised broker could set it freely — so the host consults this
/// store and nothing else before auto-accepting a session.
/// </para>
/// <para>
/// <see cref="FindUsableFor"/> deliberately requires the trust store as well as a
/// grant. A grant says <i>what</i> is allowed; the trust store says <i>who</i>. Android
/// checks only the grant and resolves identity later, at SDP verification, which means
/// screen capture has already started by the time the peer is authenticated. This
/// checks both up front.
/// </para>
/// </remarks>
public sealed class GrantStore(IProtectedStore backing)
{
    private const string StoreKey = "grants";

    private readonly Dictionary<string, Grant> _grants = Load(backing);

    private static Dictionary<string, Grant> Load(IProtectedStore backing)
    {
        var json = backing.Read(StoreKey);
        if (json is null) return [];

        try
        {
            var list = JsonSerializer.Deserialize<List<Grant>>(json) ?? [];
            return list.ToDictionary(g => g.GrantId);
        }
        catch (JsonException)
        {
            // Fail closed: an unreadable grant store means no unattended access, which
            // is the safe direction. The host still runs and still accepts attended
            // sessions, so an operator can recover without physical access.
            return [];
        }
    }

    private void Persist() =>
        backing.Write(StoreKey, JsonSerializer.Serialize(_grants.Values.ToList()));

    public IReadOnlyCollection<Grant> All => _grants.Values;

    public Grant? Find(string grantId) => _grants.GetValueOrDefault(grantId);

    public void Save(Grant grant)
    {
        _grants[grant.GrantId] = grant;
        Persist();
    }

    /// <summary>
    /// The grant authorising an unattended session for this controller, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fails closed on every path. All four conditions must hold:
    /// </para>
    /// <list type="number">
    ///   <item>the controller is in the trust store and is <c>Trusted</c>, not merely
    ///   pending or revoked;</item>
    ///   <item>a grant names that controller;</item>
    ///   <item>the grant is active and unexpired;</item>
    ///   <item>the grant confers at least <c>screen.view</c> — a grant that permits
    ///   nothing is not a reason to start capturing a screen.</item>
    /// </list>
    /// <para>
    /// When <paramref name="workstationLocked"/> and the grant sets
    /// <c>RequireUnlock</c>, the returned grant is downgraded to view-only. Unlike
    /// Android, which samples the keyguard once at session start, this is re-evaluated
    /// on every call, so a machine that locks mid-session loses control rights.
    /// </para>
    /// </remarks>
    public Grant? FindUsableFor(
        string controllerDeviceId,
        TrustStore trust,
        DateTimeOffset now,
        bool workstationLocked = false)
    {
        // Identity first. A grant naming an untrusted device authorises nothing —
        // the grant is what may happen, the trust store is who may cause it.
        if (!trust.IsTrusted(controllerDeviceId)) return null;

        var grant = _grants.Values.FirstOrDefault(g =>
            g.ControllerId == controllerDeviceId && g.IsUsable(now));

        if (grant is null) return null;
        if (!grant.EffectivePermissions.Contains("screen.view")) return null;

        return grant.RequireUnlock && workstationLocked ? grant.DowngradeToViewOnly() : grant;
    }

    /// <summary>Deactivates a grant, keeping the record so the revocation is remembered.</summary>
    public void Revoke(string grantId)
    {
        if (!_grants.TryGetValue(grantId, out var grant)) return;
        _grants[grantId] = grant with { Active = false };
        Persist();
    }

    /// <summary>
    /// Revokes every grant naming a controller.
    /// </summary>
    /// <remarks>
    /// Called when a peer is un-paired. Trust and grants are separate stores, so
    /// removing a peer without this would leave grants that quietly become live again
    /// if the same identity ever re-paired.
    /// </remarks>
    public void RevokeAllFor(string controllerDeviceId)
    {
        var affected = _grants.Values.Where(g => g.ControllerId == controllerDeviceId).ToList();
        if (affected.Count == 0) return;

        foreach (var g in affected) _grants[g.GrantId] = g with { Active = false };
        Persist();
    }

    public void MarkUsed(string grantId, DateTimeOffset now)
    {
        if (!_grants.TryGetValue(grantId, out var grant)) return;
        _grants[grantId] = grant with { LastUsedAt = now.ToUnixTimeMilliseconds() };
        Persist();
    }
}
