using System.Text;
using System.Text.Json;
using Xunit;

namespace Techee.Protocol.Tests;

/// <summary>
/// Holds the C# protocol implementation to the shared golden vectors — the same files
/// Node and Kotlin are held to.
/// </summary>
/// <remarks>
/// The point is not that C# agrees with itself. It is that three independent
/// implementations satisfy one artifact, so a divergence surfaces as a red test in
/// whichever language drifted rather than as a Windows machine that mysteriously
/// cannot connect to a phone.
/// </remarks>
public class ProtocolFixtureTests
{
    // ---- vocabulary -------------------------------------------------------

    [Fact]
    public void Vocabulary_matches_the_shared_fixture()
    {
        using var fx = Fixtures.Load("capabilities.json");
        var root = fx.RootElement;

        Assert.Equal(root.GetProperty("protocolVersion").GetProperty("current").GetInt32(),
            TecheeProtocol.ProtocolVersion);
        Assert.Equal(root.GetProperty("protocolVersion").GetProperty("minSupported").GetInt32(),
            TecheeProtocol.MinProtocolVersion);

        Assert.Equal(root.GetProperty("platforms").Strings().Order(),
            TecheeProtocol.Platforms.Order());
        Assert.Equal(root.GetProperty("knownCapabilities").Strings().Order(),
            TecheeProtocol.Capabilities.Order());
        Assert.Equal(root.GetProperty("knownPermissions").Strings().Order(),
            TecheeProtocol.Permissions.Order());

        var limits = root.GetProperty("limits");
        Assert.Equal(limits.GetProperty("maxCapabilities").GetInt32(), TecheeProtocol.MaxCapabilities);
        Assert.Equal(limits.GetProperty("maxCapabilityLength").GetInt32(), TecheeProtocol.MaxCapabilityLength);
        Assert.Equal(limits.GetProperty("maxVersionLength").GetInt32(), TecheeProtocol.MaxVersionLength);
        Assert.Equal(limits.GetProperty("maxPermissions").GetInt32(), TecheeProtocol.MaxPermissions);
    }

    [Fact]
    public void Control_limits_match_the_shared_fixture()
    {
        using var fx = Fixtures.Load("control-v1.json");
        var limits = fx.RootElement.GetProperty("limits");

        Assert.Equal(limits.GetProperty("maxFrameBytes").GetInt32(), TecheeProtocol.MaxFrameBytes);
        Assert.Equal(limits.GetProperty("maxTextBytes").GetInt32(), TecheeProtocol.MaxTextBytes);
        Assert.Equal(limits.GetProperty("maxClipboardBytes").GetInt32(), TecheeProtocol.MaxClipboardBytes);
        Assert.Equal(limits.GetProperty("maxSwipeMs").GetInt64(), TecheeProtocol.MaxSwipeMs);
        Assert.Equal(limits.GetProperty("defaultSwipeMs").GetInt64(), TecheeProtocol.DefaultSwipeMs);
    }

    // ---- endpoint metadata ------------------------------------------------

    [Fact]
    public void Endpoint_metadata_vectors()
    {
        using var fx = Fixtures.Load("capabilities.json");

        foreach (var v in fx.RootElement.GetProperty("metaVectors").EnumerateArray())
        {
            var name = v.GetProperty("name").GetString()!;
            var input = v.GetProperty("input");

            JsonDocument? synthesized = null;
            JsonElement? actual = input.ValueKind == JsonValueKind.Null ? null : input;

            // One vector asks for 64 synthetic tokens rather than inlining them.
            if (actual is { } el
                && el.TryGetProperty("capabilities", out var caps)
                && caps.ValueKind == JsonValueKind.String
                && caps.GetString() == "GENERATE:64")
            {
                var buffer = new MemoryStream();
                using (var w = new Utf8JsonWriter(buffer))
                {
                    w.WriteStartObject();
                    foreach (var p in el.EnumerateObject())
                    {
                        if (p.Name == "capabilities")
                        {
                            w.WriteStartArray("capabilities");
                            for (var i = 0; i < 64; i++) w.WriteStringValue($"synthetic.cap.{i}");
                            w.WriteEndArray();
                        }
                        else p.WriteTo(w);
                    }
                    w.WriteEndObject();
                }
                synthesized = JsonDocument.Parse(buffer.ToArray());
                actual = synthesized.RootElement;
            }

            var got = TecheeProtocol.ParseEndpointMeta(actual);
            var expect = v.GetProperty("expect");

            if (expect.ValueKind == JsonValueKind.Null)
            {
                Assert.True(got is null, $"{name}: expected rejection, got {got}");
            }
            else
            {
                Assert.True(got is not null, $"{name}: expected acceptance");
                Assert.Equal(expect.GetProperty("platform").GetString(), got!.Platform);
                Assert.Equal(expect.GetProperty("version").GetString(), got.Version);
                Assert.Equal(expect.GetProperty("capabilities").Strings(), got.Capabilities);
            }

            synthesized?.Dispose();
        }
    }

    [Fact]
    public void A_capability_advertisement_can_never_authorize_anything()
    {
        // The invariant the descriptive/authoritative split exists to protect.
        using var doc = JsonDocument.Parse(
            """{"platform":"windows","version":"1.0","capabilities":["power.shutdown","power.restart"]}""");

        var meta = TecheeProtocol.ParseEndpointMeta(doc.RootElement);
        Assert.NotNull(meta);
        Assert.True(meta!.Has("power.shutdown"), "the advertisement is recorded");

        var now = DateTimeOffset.UnixEpoch;
        var empty = new Grant { GrantId = "g", ControllerId = "c", PermissionTokens = [] };
        Assert.False(TecheeProtocol.GrantPermits(empty, "system.shutdown", now));

        // And a capability token is not interchangeable with a permission token —
        // which is why the two vocabularies were deliberately made different.
        var confused = new Grant { GrantId = "g", ControllerId = "c", PermissionTokens = ["power.shutdown"] };
        Assert.False(TecheeProtocol.GrantPermits(confused, "system.shutdown", now));
    }

    // ---- permissions ------------------------------------------------------

    [Fact]
    public void Legacy_scope_mapping_vectors()
    {
        using var fx = Fixtures.Load("capabilities.json");

        foreach (var v in fx.RootElement.GetProperty("legacyScopeMapping").GetProperty("vectors").EnumerateArray())
        {
            var scope = v.GetProperty("scope").Strings();
            var want = v.GetProperty("permissions").Strings();
            Assert.Equal(want, TecheeProtocol.NormalizePermissions(null, scope));
        }
    }

    [Fact]
    public void No_legacy_scope_confers_system_power()
    {
        // The upgrade-safety property, asserted directly rather than left as a
        // consequence of the table. Installing a Techee Windows host must not hand an
        // existing paired controller the ability to shut it down.
        foreach (var scope in new[] { "VIEW", "CONTROL", "CLIPBOARD", "FILES" })
        {
            var perms = TecheeProtocol.NormalizePermissions(null, [scope]);
            Assert.DoesNotContain(perms, p => p.StartsWith("system.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Grant_evaluation_vectors()
    {
        using var fx = Fixtures.Load("capabilities.json");

        foreach (var v in fx.RootElement.GetProperty("grantVectors").EnumerateArray())
        {
            var name = v.GetProperty("name").GetString()!;
            var g = v.GetProperty("grant");

            var grant = new Grant
            {
                GrantId = g.GetProperty("grantId").GetString()!,
                ControllerId = g.GetProperty("controllerId").GetString()!,
                Active = g.GetProperty("active").GetBoolean(),
                ExpiresAt = g.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number
                    ? e.GetInt64()
                    : null,
                PermissionTokens = g.TryGetProperty("permissions", out var p) ? p.Strings() : null,
                LegacyScope = g.TryGetProperty("scope", out var s) ? s.Strings() : null,
            };

            var now = v.TryGetProperty("nowMs", out var n)
                ? DateTimeOffset.FromUnixTimeMilliseconds(n.GetInt64())
                : DateTimeOffset.UtcNow;

            foreach (var perm in v.GetProperty("permits").Strings())
                Assert.True(grant.Permits(perm, now), $"{name}: should permit {perm}");

            foreach (var perm in v.GetProperty("denies").Strings())
                Assert.False(grant.Permits(perm, now), $"{name}: should deny {perm}");
        }
    }

    [Fact]
    public void Permission_requirements_match_the_shared_fixture()
    {
        using var fx = Fixtures.Load("capabilities.json");

        foreach (var p in fx.RootElement.GetProperty("permissionRequiredFor").EnumerateObject())
        {
            if (p.Name.StartsWith('_')) continue;
            var want = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString();
            Assert.Equal(want, TecheeProtocol.RequiredPermission(p.Name));
            Assert.True(TecheeProtocol.IsKnownCommand(p.Name), $"{p.Name} should be a known command");
        }
    }

    [Fact]
    public void Authorization_fails_closed()
    {
        var now = DateTimeOffset.UnixEpoch;
        var control = new Grant { GrantId = "g", ControllerId = "c", PermissionTokens = ["screen.view", "input.control"] };
        var power = new Grant { GrantId = "g", ControllerId = "c", PermissionTokens = ["screen.view", "system.restart"] };
        var tap = new Control.PointerTap(0.5, 0.5);

        Assert.True(TecheeProtocol.AuthorizeCommand(tap, control, now));
        Assert.False(TecheeProtocol.AuthorizeCommand(new Control.SystemAction("system.restart"), control, now));
        Assert.True(TecheeProtocol.AuthorizeCommand(new Control.SystemAction("system.restart"), power, now));

        // Each power action is granted individually. Restart does not imply shutdown.
        Assert.False(TecheeProtocol.AuthorizeCommand(new Control.SystemAction("system.shutdown"), power, now));
        Assert.False(TecheeProtocol.AuthorizeCommand(tap, power, now));
        Assert.False(TecheeProtocol.AuthorizeCommand(tap, null, now));

        // A permission-free command needs no grant.
        Assert.True(TecheeProtocol.AuthorizeCommand(new Control.Opaque("host.status"), null, now));

        // A legacy grant is the realistic upgrade case.
        var legacy = new Grant { GrantId = "g", ControllerId = "c", LegacyScope = ["VIEW", "CONTROL"] };
        Assert.True(TecheeProtocol.AuthorizeCommand(tap, legacy, now));
        foreach (var action in new[]
                 { "system.lock", "system.sleep", "system.hibernate", "system.restart", "system.shutdown" })
        {
            Assert.False(TecheeProtocol.AuthorizeCommand(new Control.SystemAction(action), legacy, now),
                $"a legacy CONTROL grant must refuse {action}");
        }
    }

    [Fact]
    public void A_locked_workstation_downgrades_a_control_grant_to_view_only()
    {
        var now = DateTimeOffset.UnixEpoch;
        var grant = new Grant
        {
            GrantId = "g",
            ControllerId = "c",
            RequireUnlock = true,
            PermissionTokens = ["screen.view", "input.control", "system.sleep"],
        };

        Assert.True(grant.Permits("input.control", now));

        var locked = grant.DowngradeToViewOnly();
        Assert.True(locked.Permits("screen.view", now));
        Assert.False(locked.Permits("input.control", now));
        Assert.False(locked.Permits("system.sleep", now));
    }

    // ---- control frames ---------------------------------------------------

    [Fact]
    public void Decode_vectors()
    {
        using var fx = Fixtures.Load("control-v1.json");

        foreach (var v in fx.RootElement.GetProperty("decodeVectors").EnumerateArray())
        {
            var name = v.GetProperty("name").GetString()!;
            using var frame = Fixtures.MaterializeFrame(v.GetProperty("frame"));
            var got = ControlCodec.Decode(frame.RootElement);
            var want = v.GetProperty("expect");

            Assert.True(got is not null, $"{name}: should decode");
            Assert.Equal(want.GetProperty("kind").GetString(), got!.Kind);
            AssertControlMatches(name, want, got);
        }
    }

    [Fact]
    public void Reject_vectors()
    {
        using var fx = Fixtures.Load("control-v1.json");

        foreach (var v in fx.RootElement.GetProperty("rejectVectors").EnumerateArray())
        {
            var name = v.GetProperty("name").GetString()!;
            using var frame = Fixtures.MaterializeFrame(v.GetProperty("frame"));
            Assert.True(ControlCodec.Decode(frame.RootElement) is null, $"{name}: should be rejected");
        }
    }

    [Fact]
    public void Raw_reject_vectors_are_dropped_never_thrown()
    {
        using var fx = Fixtures.Load("control-v1.json");

        foreach (var v in fx.RootElement.GetProperty("rawRejectVectors").EnumerateArray())
        {
            var name = v.GetProperty("name").GetString()!;
            var raw = Fixtures.Materialize(v.GetProperty("raw").GetString()!);

            // Two separate properties. It must reject — and it must reject by
            // returning rather than throwing, because in production this runs on a
            // media callback thread on an unattended host.
            Control? got;
            try
            {
                got = ControlCodec.DecodeRaw(raw);
            }
            catch (Exception e)
            {
                Assert.Fail($"{name}: DecodeRaw threw {e.GetType().Name} instead of returning null");
                return;
            }

            Assert.True(got is null, $"{name}: should be rejected, got {got}");
        }
    }

    [Fact]
    public void An_oversized_frame_is_refused_on_length_alone()
    {
        var huge = $$"""{"v":1,"t":"keyboard.text","s":"{{new string('a', TecheeProtocol.MaxFrameBytes)}}"}""";
        Assert.Null(ControlCodec.DecodeRaw(huge));
    }

    [Fact]
    public void The_two_dialects_stay_disjoint()
    {
        // A v1-only name must not be reachable from a legacy frame...
        foreach (var t in new[] { "pointer.tap", "system.restart", "clipboard.set", "keyboard.keyDown" })
        {
            using var d = JsonDocument.Parse($$"""{"t":"{{t}}","x":0.5,"y":0.5,"code":"KeyA","s":"x"}""");
            Assert.True(ControlCodec.Decode(d.RootElement) is null, $"legacy frame must not accept v1 name {t}");
        }

        // ...and a legacy name must not be reachable from a v1 frame, so a frame's
        // dialect is never ambiguous.
        foreach (var t in new[] { "tap", "swipe", "key", "text", "callstate" })
        {
            using var d = JsonDocument.Parse(
                $$"""{"v":1,"t":"{{t}}","x":0.5,"y":0.5,"k":"BACK","s":"x","state":"IDLE"}""");
            Assert.True(ControlCodec.Decode(d.RootElement) is null, $"v1 frame must not accept legacy name {t}");
        }
    }

    [Fact]
    public void The_version_is_judged_by_value_not_by_spelling()
    {
        // JSON has no integer type: 1 and 1.0 are the same number. This decoder used
        // to additionally require that the raw text carried no '.', so a v1 peer
        // whose encoder wrote "1.0" was silently dropped — and JS could not have
        // implemented that rule at all, since JSON.parse collapses both spellings to
        // one value before the decoder ever sees them. Judge the number, not the text.
        foreach (var spelling in new[] { "1", "1.0", "1e0", "1.0e0" })
        {
            using var d = JsonDocument.Parse("{\"v\":" + spelling + ",\"t\":\"pointer.tap\",\"x\":0.5,\"y\":0.25}");
            Assert.Equal(new Control.PointerTap(0.5, 0.25), ControlCodec.Decode(d.RootElement));
        }

        // A value that is not integral, not in range, or not a number is still refused.
        foreach (var bad in new[] { "1.5", "0", "2", "-1", "0.5", "\"1\"", "true", "null" })
        {
            using var d = JsonDocument.Parse("{\"v\":" + bad + ",\"t\":\"pointer.tap\",\"x\":0.5,\"y\":0.25}");
            Assert.True(ControlCodec.Decode(d.RootElement) is null, $"version {bad} must be refused");
        }
    }

    [Fact]
    public void Encoding_refuses_an_unknown_command()
    {
        // SystemAction and Opaque carry their kind as a string, so an unknown one is
        // representable. It must not reach the wire.
        Assert.Null(ControlCodec.Encode(new Control.SystemAction("system.selfDestruct")));
        Assert.Null(ControlCodec.Encode(new Control.Opaque("not.a.command")));
        Assert.NotNull(ControlCodec.Encode(new Control.SystemAction("system.restart")));
    }

    [Fact]
    public void Encode_vectors_and_dialect_downgrade()
    {
        using var fx = Fixtures.Load("control-v1.json");

        foreach (var v in fx.RootElement.GetProperty("encodeVectors").EnumerateArray())
        {
            var name = v.GetProperty("name").GetString()!;
            var cmd = CommandFrom(v.GetProperty("command"));

            var gotV1 = ControlCodec.Encode(cmd, TecheeProtocol.ProtocolVersion);
            Assert.True(gotV1 is not null, $"{name}: v1 encoding");
            AssertJsonEquals(name, v.GetProperty("v1"), gotV1!);

            var gotV0 = ControlCodec.Encode(cmd, TecheeProtocol.LegacyVersion);
            if (v.GetProperty("v0").ValueKind == JsonValueKind.Null)
            {
                Assert.True(gotV0 is null, $"{name}: must have no legacy form");
            }
            else
            {
                Assert.True(gotV0 is not null, $"{name}: v0 encoding");
                AssertJsonEquals(name, v.GetProperty("v0"), gotV0!);

                // Round trip proves the dialects are equivalent, not merely similar.
                Assert.Equal(cmd, ControlCodec.DecodeRaw(gotV0!));
            }

            Assert.Equal(cmd, ControlCodec.DecodeRaw(gotV1!));
        }
    }

    // ---- helpers ----------------------------------------------------------

    private static Control CommandFrom(JsonElement o)
    {
        var kind = o.GetProperty("kind").GetString()!;
        return kind switch
        {
            "pointer.tap" => new Control.PointerTap(o.GetProperty("x").GetDouble(), o.GetProperty("y").GetDouble()),
            "pointer.swipe" => new Control.PointerSwipe(
                o.GetProperty("x1").GetDouble(), o.GetProperty("y1").GetDouble(),
                o.GetProperty("x2").GetDouble(), o.GetProperty("y2").GetDouble(),
                o.GetProperty("ms").GetInt64()),
            "pointer.wheel" => new Control.PointerWheel(
                o.GetProperty("x").GetDouble(), o.GetProperty("y").GetDouble(),
                o.GetProperty("dx").GetDouble(), o.GetProperty("dy").GetDouble()),
            "nav.key" => new Control.NavKey(o.GetProperty("key").GetString()!),
            "keyboard.text" => new Control.KeyText(o.GetProperty("text").GetString()!),
            "host.callState" => new Control.HostCallState(o.GetProperty("state").GetString()!),
            "system.lock" or "system.sleep" or "system.hibernate" or "system.restart" or "system.shutdown" =>
                new Control.SystemAction(kind),
            _ => throw new InvalidOperationException($"fixture uses a command this test does not build: {kind}"),
        };
    }

    private static void AssertControlMatches(string name, JsonElement want, Control got)
    {
        foreach (var p in want.EnumerateObject())
        {
            if (p.Name is "kind") continue;

            object? actual = p.Name switch
            {
                "x" => got switch
                {
                    Control.PointerTap c => c.X,
                    Control.PointerMove c => c.X,
                    Control.PointerDown c => c.X,
                    Control.PointerUp c => c.X,
                    Control.PointerWheel c => c.X,
                    _ => null,
                },
                "y" => got switch
                {
                    Control.PointerTap c => c.Y,
                    Control.PointerMove c => c.Y,
                    Control.PointerDown c => c.Y,
                    Control.PointerUp c => c.Y,
                    Control.PointerWheel c => c.Y,
                    _ => null,
                },
                "x1" => ((Control.PointerSwipe)got).X1,
                "y1" => ((Control.PointerSwipe)got).Y1,
                "x2" => ((Control.PointerSwipe)got).X2,
                "y2" => ((Control.PointerSwipe)got).Y2,
                "ms" => ((Control.PointerSwipe)got).Ms,
                "dx" => ((Control.PointerWheel)got).Dx,
                "dy" => ((Control.PointerWheel)got).Dy,
                "button" => got switch
                {
                    Control.PointerDown c => c.Button,
                    Control.PointerUp c => c.Button,
                    _ => null,
                },
                "key" => ((Control.NavKey)got).Key,
                "code" => got switch
                {
                    Control.KeyDown c => c.Code,
                    Control.KeyUp c => c.Code,
                    _ => null,
                },
                "mods" => got switch
                {
                    Control.KeyDown c => c.Mods,
                    Control.KeyUp c => c.Mods,
                    _ => null,
                },
                "text" => got switch
                {
                    Control.KeyText c => c.Text,
                    Control.ClipboardSet c => c.Text,
                    Control.ClipboardData c => c.Text,
                    _ => null,
                },
                "mime" => got switch
                {
                    Control.ClipboardSet c => c.Mime,
                    Control.ClipboardData c => c.Mime,
                    _ => null,
                },
                "id" => ((Control.DisplaySelect)got).Id,
                "state" => ((Control.HostCallState)got).State,
                "platform" => ((Control.Hello)got).Meta.Platform,
                "version" => ((Control.Hello)got).Meta.Version,
                "capabilities" => ((Control.Hello)got).Meta.Capabilities,
                _ => throw new InvalidOperationException($"{name}: fixture field '{p.Name}' has no accessor"),
            };

            switch (p.Value.ValueKind)
            {
                case JsonValueKind.Array:
                    Assert.Equal(p.Value.Strings(), (IEnumerable<string>)actual!);
                    break;
                case JsonValueKind.Number:
                    Assert.Equal(p.Value.GetDouble(), Convert.ToDouble(actual), 9);
                    break;
                default:
                    Assert.Equal(p.Value.GetString(), actual);
                    break;
            }
        }
    }

    private static void AssertJsonEquals(string name, JsonElement want, string gotJson)
    {
        using var got = JsonDocument.Parse(gotJson);

        var wantKeys = want.EnumerateObject().Select(p => p.Name).Order().ToList();
        var gotKeys = got.RootElement.EnumerateObject().Select(p => p.Name).Order().ToList();
        Assert.Equal(wantKeys, gotKeys);

        foreach (var p in want.EnumerateObject())
        {
            var g = got.RootElement.GetProperty(p.Name);
            if (p.Value.ValueKind == JsonValueKind.Number)
                Assert.Equal(p.Value.GetDouble(), g.GetDouble(), 9);
            else if (p.Value.ValueKind == JsonValueKind.Array)
                Assert.Equal(p.Value.Strings(), g.Strings());
            else
                Assert.Equal(p.Value.GetString(), g.GetString());
        }
    }
}
