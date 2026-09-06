using System.Text.Json;

namespace Techee.Crypto.Tests;

/// <summary>
/// Loads the shared golden vectors from <c>protocol/fixtures/</c> — the same files
/// <c>server/test/protocol.js</c> and Android's <c>ProtocolFixtureTest</c> read.
/// </summary>
/// <remarks>
/// Read from the repository at test time rather than copied into this project. A copy
/// is exactly the thing that drifts, and drift is what these fixtures exist to
/// prevent.
/// </remarks>
public static class Fixtures
{
    private static readonly Lazy<string> Root = new(FindFixtureDirectory);

    public static JsonDocument Load(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Root.Value, name)));

    /// <summary>
    /// Walks up from the test assembly to the repository root.
    /// </summary>
    /// <remarks>
    /// Deliberately not a hard-coded relative path: those break the moment the output
    /// layout changes, and they break as a confusing "file not found" rather than as
    /// a clear message about what is actually wrong.
    /// </remarks>
    private static string FindFixtureDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "protocol", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate protocol/fixtures by walking up from " +
            $"{AppContext.BaseDirectory}. These tests must run inside the Techee repository; " +
            "the fixtures are the cross-language contract and cannot be substituted.");
    }

    /// <summary>
    /// Expands the placeholders the fixtures use instead of inlining huge payloads.
    /// </summary>
    public static string Materialize(string value) => value switch
    {
        _ when value.StartsWith("GENERATE:", StringComparison.Ordinal) =>
            new string('a', int.Parse(value["GENERATE:".Length..])),
        _ when value.StartsWith("GENERATE_NESTED:", StringComparison.Ordinal) =>
            Nested(int.Parse(value["GENERATE_NESTED:".Length..])),
        _ => value,
    };

    private static string Nested(int depth) =>
        new string('[', depth) + new string(']', depth);

    /// <summary>Re-serialises a fixture frame with any GENERATE placeholders expanded.</summary>
    public static JsonDocument MaterializeFrame(JsonElement frame)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var p in frame.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    w.WriteString(p.Name, Materialize(p.Value.GetString()!));
                else
                    p.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    public static List<string> Strings(this JsonElement array) =>
        [.. array.EnumerateArray().Select(e => e.GetString()!)];
}
