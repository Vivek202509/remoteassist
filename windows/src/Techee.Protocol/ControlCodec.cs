using System.Text;
using System.Text.Json;

namespace Techee.Protocol;

/// <summary>
/// Encodes and decodes Techee control frames.
/// </summary>
/// <remarks>
/// <para>
/// Two dialects share one DataChannel: <b>v0</b> (<c>{"t":"tap"}</c>), which every
/// shipped Android build speaks, and <b>v1</b> (<c>{"v":1,"t":"pointer.tap"}</c>).
/// Both decode to the same <see cref="Control"/>, so a Windows host serves an Android
/// controller without either side special-casing the other.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> A control frame is attacker-influenced input arriving
/// on a media callback thread; on an unattended office host, an exception there takes
/// down the machine nobody is standing next to. Every rejection is a null return.
/// </para>
/// </remarks>
public static class ControlCodec
{
    private static readonly HashSet<string> NavKeys = ["BACK", "HOME", "RECENTS"];
    private static readonly HashSet<string> PointerButtons = ["left", "right", "middle", "x1", "x2"];
    private static readonly HashSet<string> ClipboardMimes = ["text/plain"];

    /// <summary>v0 type name -> v1 type name.</summary>
    private static readonly Dictionary<string, string> V0ToV1 = new()
    {
        ["tap"] = "pointer.tap",
        ["swipe"] = "pointer.swipe",
        ["key"] = "nav.key",
        ["text"] = "keyboard.text",
        ["callstate"] = "host.callState",
    };

    /// <summary>v1 type name -> v0 type name, for messages the legacy dialect can express.</summary>
    private static readonly Dictionary<string, string> V1ToV0 =
        V0ToV1.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>True when a command can be expressed to a peer that only speaks v0.</summary>
    public static bool HasLegacyForm(string kind) => V1ToV0.ContainsKey(kind);

    // ---- decoding ---------------------------------------------------------

    /// <summary>
    /// Parses a raw DataChannel payload.
    /// </summary>
    /// <remarks>
    /// The size ceiling is checked <b>before</b> parsing, so an oversized payload
    /// costs a length comparison rather than a full JSON parse.
    /// </remarks>
    public static Control? DecodeRaw(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > TecheeProtocol.MaxFrameBytes) return null;

        try
        {
            using var doc = JsonDocument.Parse(payload.ToArray());
            return Decode(doc.RootElement);
        }
        catch (JsonException)
        {
            // Not JSON, truncated, or a nesting bomb. All simply dropped.
            return null;
        }
    }

    /// <summary>Parses a raw UTF-8 JSON string.</summary>
    public static Control? DecodeRaw(string payload)
    {
        if (Encoding.UTF8.GetByteCount(payload) > TecheeProtocol.MaxFrameBytes) return null;
        return DecodeRaw(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>Decodes a parsed frame, or null for anything malformed or unknown.</summary>
    public static Control? Decode(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object) return null;

        // Version gate. Absent means the legacy dialect; present must be an integer
        // we support. A future version is refused rather than guessed at.
        int version;
        if (!frame.TryGetProperty("v", out var vEl))
        {
            version = TecheeProtocol.LegacyVersion;
        }
        else if (vEl.ValueKind == JsonValueKind.Number && vEl.TryGetInt32(out var v)
                 && vEl.GetRawText().IndexOf('.') < 0)
        {
            if (v < TecheeProtocol.MinProtocolVersion || v > TecheeProtocol.ProtocolVersion) return null;
            version = v;
        }
        else
        {
            return null;
        }

        if (!frame.TryGetProperty("t", out var tEl) || tEl.ValueKind != JsonValueKind.String) return null;
        var rawType = tEl.GetString()!;
        if (rawType.Length == 0) return null;

        // Legacy names are lifted into their v1 equivalents so everything downstream
        // reasons about one vocabulary. The two vocabularies stay disjoint — a legacy
        // frame may only use legacy names and vice versa — so a frame's dialect is
        // never ambiguous.
        string kind;
        if (version == TecheeProtocol.LegacyVersion)
        {
            if (!V0ToV1.TryGetValue(rawType, out var mapped)) return null;
            kind = mapped;
        }
        else
        {
            if (!TecheeProtocol.IsKnownCommand(rawType)) return null;
            kind = rawType;
        }

        switch (kind)
        {
            case "pointer.tap":
            {
                if (Coord(frame, "x") is not { } x || Coord(frame, "y") is not { } y) return null;
                return new Control.PointerTap(x, y);
            }
            case "pointer.move":
            {
                if (Coord(frame, "x") is not { } x || Coord(frame, "y") is not { } y) return null;
                return new Control.PointerMove(x, y);
            }
            case "pointer.down":
            case "pointer.up":
            {
                if (Coord(frame, "x") is not { } x || Coord(frame, "y") is not { } y) return null;
                var button = "left";
                if (frame.TryGetProperty("b", out var bEl))
                {
                    if (bEl.ValueKind != JsonValueKind.String) return null;
                    button = bEl.GetString()!;
                }
                if (!PointerButtons.Contains(button)) return null;
                return kind == "pointer.down"
                    ? new Control.PointerDown(x, y, button)
                    : new Control.PointerUp(x, y, button);
            }
            case "pointer.wheel":
            {
                if (Coord(frame, "x") is not { } x || Coord(frame, "y") is not { } y) return null;
                if (Finite(frame, "dx") is not { } dx || Finite(frame, "dy") is not { } dy) return null;
                return new Control.PointerWheel(x, y, dx, dy);
            }
            case "pointer.swipe":
            {
                if (Coord(frame, "x1") is not { } x1 || Coord(frame, "y1") is not { } y1) return null;
                if (Coord(frame, "x2") is not { } x2 || Coord(frame, "y2") is not { } y2) return null;

                long ms = TecheeProtocol.DefaultSwipeMs;
                if (frame.TryGetProperty("ms", out _))
                {
                    if (Finite(frame, "ms") is not { } raw) return null;
                    ms = (long)raw;
                }
                // Clamped rather than rejected, matching the shipped Android
                // behaviour: a hostile duration cannot wedge the gesture queue, but a
                // rounding artefact still produces a gesture.
                ms = Math.Clamp(ms, 1, TecheeProtocol.MaxSwipeMs);
                return new Control.PointerSwipe(x1, y1, x2, y2, ms);
            }
            case "nav.key":
            {
                if (Str(frame, "k") is not { } k || !NavKeys.Contains(k)) return null;
                return new Control.NavKey(k);
            }
            case "keyboard.keyDown":
            case "keyboard.keyUp":
            {
                if (Str(frame, "code") is not { } code || code.Length == 0 || code.Length > 32) return null;

                IReadOnlyList<string>? mods = null;
                if (frame.TryGetProperty("mods", out var modsEl))
                {
                    if (modsEl.ValueKind != JsonValueKind.Array) return null;
                    var list = new List<string>();
                    foreach (var m in modsEl.EnumerateArray())
                    {
                        // All-or-nothing: one bad modifier invalidates the frame
                        // rather than being silently dropped, because a partially
                        // applied chord is worse than none.
                        if (m.ValueKind != JsonValueKind.String) return null;
                        var s = m.GetString()!;
                        if (s.Length == 0 || s.Length > 32) return null;
                        list.Add(s);
                    }
                    mods = list;
                }

                return kind == "keyboard.keyDown"
                    ? new Control.KeyDown(code, mods)
                    : new Control.KeyUp(code, mods);
            }
            case "keyboard.text":
            {
                if (Str(frame, "s") is not { } s) return null;
                if (Encoding.UTF8.GetByteCount(s) > TecheeProtocol.MaxTextBytes) return null;
                return new Control.KeyText(s);
            }
            case "clipboard.set":
            case "clipboard.data":
            {
                if (Str(frame, "s") is not { } s) return null;
                if (Encoding.UTF8.GetByteCount(s) > TecheeProtocol.MaxClipboardBytes) return null;

                var mime = "text/plain";
                if (frame.TryGetProperty("mime", out var mimeEl))
                {
                    if (mimeEl.ValueKind != JsonValueKind.String) return null;
                    mime = mimeEl.GetString()!;
                }
                // v1 is text/plain only. Refusing unknown formats outright is
                // deliberate: HTML and file payloads are how a clipboard channel
                // becomes a delivery mechanism, and adding them needs its own
                // threat-model pass rather than a permissive default.
                if (!ClipboardMimes.Contains(mime)) return null;

                return kind == "clipboard.set"
                    ? new Control.ClipboardSet(mime, s)
                    : new Control.ClipboardData(mime, s);
            }
            case "clipboard.request":
                return new Control.ClipboardRequest();
            case "display.list":
                return new Control.DisplayList();
            case "display.select":
            {
                if (Str(frame, "id") is not { } id || id.Length == 0 || id.Length > 64) return null;
                return new Control.DisplaySelect(id);
            }
            case "system.lock":
            case "system.sleep":
            case "system.hibernate":
            case "system.restart":
            case "system.shutdown":
                return new Control.SystemAction(kind);
            case "host.callState":
            {
                if (Str(frame, "state") is not { } state || state.Length == 0 || state.Length > 32) return null;
                return new Control.HostCallState(state);
            }
            case "hello":
            case "hello.ack":
            {
                var meta = TecheeProtocol.ParseEndpointMeta(frame);
                return meta is null ? null : new Control.Hello(meta, kind == "hello.ack");
            }
            case "display.info":
            case "host.status":
            case "error":
                return new Control.Opaque(kind);
            default:
                return null;
        }
    }

    // ---- encoding ---------------------------------------------------------

    /// <summary>
    /// Renders a command into the dialect a peer understands.
    /// </summary>
    /// <remarks>
    /// Pass <see cref="TecheeProtocol.LegacyVersion"/> for a peer that never sent
    /// <c>hello</c>. Returns null when the command has no representation in that
    /// dialect — a Windows power action cannot be expressed to a peer that predates
    /// it, and inventing an encoding the peer would misread is worse than silence.
    /// <para>
    /// <b>This is a presentation-layer downgrade only.</b> It changes how a command
    /// is spelled, never whether it is authenticated. SDP identity binding is
    /// enforced separately, is never negotiated, and has no legacy fallback.
    /// </para>
    /// </remarks>
    public static string? Encode(Control cmd, int version = TecheeProtocol.ProtocolVersion)
    {
        if (!TecheeProtocol.IsKnownCommand(cmd.Kind)) return null;

        var legacy = version == TecheeProtocol.LegacyVersion;
        string type;
        if (legacy)
        {
            if (!V1ToV0.TryGetValue(cmd.Kind, out var mapped)) return null;
            type = mapped;
        }
        else
        {
            type = cmd.Kind;
        }

        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (!legacy) w.WriteNumber("v", TecheeProtocol.ProtocolVersion);
            w.WriteString("t", type);

            switch (cmd)
            {
                case Control.PointerTap c:
                    w.WriteNumber("x", c.X); w.WriteNumber("y", c.Y);
                    break;
                case Control.PointerMove c:
                    w.WriteNumber("x", c.X); w.WriteNumber("y", c.Y);
                    break;
                case Control.PointerDown c:
                    w.WriteNumber("x", c.X); w.WriteNumber("y", c.Y); w.WriteString("b", c.Button);
                    break;
                case Control.PointerUp c:
                    w.WriteNumber("x", c.X); w.WriteNumber("y", c.Y); w.WriteString("b", c.Button);
                    break;
                case Control.PointerWheel c:
                    w.WriteNumber("x", c.X); w.WriteNumber("y", c.Y);
                    w.WriteNumber("dx", c.Dx); w.WriteNumber("dy", c.Dy);
                    break;
                case Control.PointerSwipe c:
                    w.WriteNumber("x1", c.X1); w.WriteNumber("y1", c.Y1);
                    w.WriteNumber("x2", c.X2); w.WriteNumber("y2", c.Y2);
                    w.WriteNumber("ms", c.Ms);
                    break;
                case Control.NavKey c:
                    w.WriteString("k", c.Key);
                    break;
                case Control.KeyDown c:
                    w.WriteString("code", c.Code);
                    WriteMods(w, c.Mods);
                    break;
                case Control.KeyUp c:
                    w.WriteString("code", c.Code);
                    WriteMods(w, c.Mods);
                    break;
                case Control.KeyText c:
                    w.WriteString("s", c.Text);
                    break;
                case Control.ClipboardSet c:
                    w.WriteString("mime", c.Mime); w.WriteString("s", c.Text);
                    break;
                case Control.ClipboardData c:
                    w.WriteString("mime", c.Mime); w.WriteString("s", c.Text);
                    break;
                case Control.DisplaySelect c:
                    w.WriteString("id", c.Id);
                    break;
                case Control.HostCallState c:
                    w.WriteString("state", c.State);
                    break;
                case Control.Hello c:
                    w.WriteString("platform", c.Meta.Platform);
                    w.WriteString("version", c.Meta.Version);
                    w.WriteStartArray("capabilities");
                    foreach (var cap in c.Meta.Capabilities) w.WriteStringValue(cap);
                    w.WriteEndArray();
                    break;
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteMods(Utf8JsonWriter w, IReadOnlyList<string>? mods)
    {
        if (mods is null) return;
        w.WriteStartArray("mods");
        foreach (var m in mods) w.WriteStringValue(m);
        w.WriteEndArray();
    }

    // ---- field readers ----------------------------------------------------

    /// <summary>
    /// A normalized 0..1 surface coordinate.
    /// </summary>
    /// <remarks>
    /// Non-numeric and non-finite values are rejected; finite values are clamped, so
    /// a rounding artefact at the edge still lands on the edge rather than
    /// discarding the whole frame.
    /// </remarks>
    private static double? Coord(JsonElement frame, string key) =>
        Finite(frame, key) is { } v ? Math.Clamp(v, 0.0, 1.0) : null;

    /// <summary>A finite JSON number. Rejects strings, booleans, nulls, NaN and infinities.</summary>
    private static double? Finite(JsonElement frame, string key)
    {
        if (!frame.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Number) return null;
        if (!el.TryGetDouble(out var d)) return null;
        return double.IsFinite(d) ? d : null;
    }

    private static string? Str(JsonElement frame, string key) =>
        frame.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
