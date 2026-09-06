using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Techee.Store;

/// <summary>
/// Encrypted key-value persistence for trust records and grants.
/// </summary>
/// <remarks>
/// An interface so the stores above it are testable without touching the filesystem
/// or DPAPI, and so a future backend (a service-scoped store, a different OS) can be
/// substituted without changing anything that uses it.
/// </remarks>
public interface IProtectedStore
{
    /// <summary>Returns the stored value, or null if absent or unreadable.</summary>
    string? Read(string key);

    void Write(string key, string value);

    void Delete(string key);
}

/// <summary>
/// The Windows implementation: JSON files encrypted with DPAPI.
/// </summary>
/// <remarks>
/// <para>
/// The Windows counterpart of Android's <c>EncryptedSharedPreferences</c>. DPAPI keys
/// the encryption to the machine or the user, so the file cannot be decrypted after
/// being copied to another computer — which is what makes it meaningful protection
/// for pairing secrets rather than obfuscation.
/// </para>
/// <para>
/// <b>Scope must match the identity's scope.</b> An unattended host's service runs as
/// SYSTEM and holds a machine-scoped identity, so its trust store must be
/// <see cref="DataProtectionScope.LocalMachine"/> too — a per-user store would be
/// unreadable to the service. Getting this wrong produces a host that starts, cannot
/// read its own pairings, and appears to have forgotten every device.
/// </para>
/// <para>
/// DPAPI at <c>LocalMachine</c> scope protects against <i>offline</i> attack — a
/// stolen disk or a copied file — not against an attacker who already has code
/// execution on the machine. That is the honest boundary, and it is the same one
/// Windows itself offers for machine-scoped secrets.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiFileStore : IProtectedStore
{
    private readonly string _directory;
    private readonly DataProtectionScope _scope;

    /// <summary>
    /// Extra entropy mixed into the DPAPI operation.
    /// </summary>
    /// <remarks>
    /// Not a secret — it is in the binary — but it does mean another application
    /// running under the same account cannot decrypt Techee's store by handing the
    /// bytes to <c>Unprotect</c> with default parameters.
    /// </remarks>
    private static readonly byte[] Entropy = "techee-store-v1"u8.ToArray();

    public DpapiFileStore(string directory, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        _directory = directory;
        _scope = scope;
        Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// The per-machine data directory for an unattended host.
    /// </summary>
    /// <remarks>
    /// Under <c>ProgramData</c> so it survives user profile changes and is readable by
    /// the SYSTEM-run service. Never a development path.
    /// </remarks>
    public static string MachineDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Techee");

    /// <summary>The per-user data directory for a controller-only installation.</summary>
    public static string UserDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Techee");

    private string PathFor(string key) => Path.Combine(_directory, $"{key}.dat");

    public string? Read(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;

        try
        {
            var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, _scope);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Written by a different user or machine, truncated, or locked. Treated as
            // absent: an office host that refuses to start because one file is
            // unreadable is worse than one that has forgotten its pairings and can be
            // re-paired. The file is left in place rather than deleted, so the cause
            // stays diagnosable.
            return null;
        }
    }

    public void Write(string key, string value)
    {
        var ciphertext = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, _scope);
        var path = PathFor(key);

        // Write-then-replace, so an interrupted write cannot leave a half-file where
        // the trust store used to be. Losing power mid-save must not un-pair a device.
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ciphertext);

        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    public void Delete(string key)
    {
        var path = PathFor(key);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort, same as the CNG key deletion path.
        }
    }
}

/// <summary>In-memory store for tests. Deliberately has no persistence path.</summary>
public sealed class InMemoryStore : IProtectedStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Read(string key) => _values.GetValueOrDefault(key);
    public void Write(string key, string value) => _values[key] = value;
    public void Delete(string key) => _values.Remove(key);
}
