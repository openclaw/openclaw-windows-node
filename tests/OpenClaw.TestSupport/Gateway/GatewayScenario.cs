using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.TestSupport.Gateway;

/// <summary>
/// Single synthetic source for the interactive host and protocol/UI tests.
/// This is a browse fixture, not a Gateway emulator or a recording of user data.
/// </summary>
public sealed class GatewayScenario
{
    public const string BrowseName = "multi-session-browse";
    public const string MainSessionKey = "agent:main:main";
    public const string LongSessionKey = "agent:main:fixture-long";
    public const string OtherSessionKey = "agent:research:fixture-other";
    public const string EmptySessionKey = "agent:main:fixture-empty";
    public const string EdgeSessionKey = "agent:research:fixture-edge";
    public const string MainTitle = "Fixture: main conversation";
    public const string LongTitle = "Fixture: 240-message conversation";
    public const string OtherTitle = "Fixture: research conversation";
    public const string LongSessionTitle = LongTitle;
    public const string OtherSessionTitle = OtherTitle;
    public const string EmptyTitle = "Fixture: empty conversation";
    public const string EdgeTitle = "Fixture: a deliberately long conversation title with repeated message identities across agents";
    public const string MainSentinel = "FIXTURE MAIN: the lighthouse is green.";
    public const string LongEarlySentinel = "FIXTURE LONG BEGIN: message 001.";
    public const string LongMiddleSentinel = "FIXTURE LONG MIDDLE: message 120.";
    public const string LongFinalSentinel = "FIXTURE LONG FINAL MESSAGE 240";
    public const string LongHistoryFinalMarker = "FIXTURE LONG END 240";
    public const string LongFinalLine = LongHistoryFinalMarker;
    public const string OtherSentinel = "FIXTURE RESEARCH: the violet compass points north.";
    public const string OtherHistoryMarker = OtherSentinel;
    public const string EdgeSentinel = "FIXTURE EDGE: shared IDs belong to this conversation only.";
    public const int LongMessageCount = 240;

    private static readonly DateTimeOffset Epoch = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly Session[] _sessions;
    private readonly IReadOnlyDictionary<string, JsonElement> _reads;

    public string Name => BrowseName;
    public int Version => 1;
    public int ProtocolVersion => GatewayProtocolContract.CurrentVersion;
    public string ContractProvenance =>
        "OpenClaw.Shared GatewayProtocolContract/OpenClawGatewayClient and Protocol/gateway-protocol-snapshot.json";
    public string Sha256 { get; }
    public IReadOnlyList<string> SessionKeys { get; }
    public IReadOnlyList<string> ReadMethods { get; }

    private GatewayScenario(Session[] sessions, IReadOnlyDictionary<string, JsonElement> reads)
    {
        _sessions = sessions;
        _reads = reads;
        SessionKeys = Array.AsReadOnly(sessions.Select(s => s.Key).ToArray());
        ReadMethods = Array.AsReadOnly(new[]
        {
            "sessions.list", "sessions.subscribe", "sessions.preview", "chat.history",
            "models.list", "usage.cost"
        }.Concat(reads.Keys).Order(StringComparer.Ordinal).ToArray());
        var source = JsonSerializer.Serialize(new
        {
            Name, Version, ProtocolVersion, ContractProvenance,
            sessions = sessions.Select(s => new { s.Row, s.Messages }), reads
        });
        Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    public static GatewayScenario LoadBuiltin(string name) =>
        name == BrowseName ? CreateBrowse() : throw new ArgumentException("Unknown fixture scenario.", nameof(name));

    public static GatewayScenario CreateBrowse()
    {
        Session[] sessions =
        [
            CreateSession(MainSessionKey, MainTitle, "main", "browse", 0,
                ["Can we browse the synthetic Gateway?", MainSentinel]),
            CreateSession(LongSessionKey, LongTitle, "main", "browse", 1,
                Enumerable.Range(1, LongMessageCount).Select(LongMessage).ToArray()),
            CreateSession(OtherSessionKey, OtherTitle, "research", "research", 2,
                ["What did the synthetic research find?", OtherSentinel, "No provider was contacted.", OtherSentinel]),
            CreateSession(EmptySessionKey, EmptyTitle, "main", "browse", 3, []),
            CreateSession(EdgeSessionKey, EdgeTitle, "research", "research", 4,
                ["These message IDs also exist in other sessions.", EdgeSentinel])
        ];
        var config = JsonSerializer.SerializeToElement(new
        {
            agents = new
            {
                defaults = new { model = new { primary = "fixture/browse" }, workspace = "/fixture/workspace" },
                list = new[]
                {
                    new { id = "main", name = "Fixture Main", model = "fixture/browse" },
                    new { id = "research", name = "Fixture Research", model = "fixture/research" }
                }
            },
            gateway = new { mode = "local", bind = "loopback" },
            session = new { scope = "per-sender" },
            messages = new { responsePrefix = "Fixture" }
        });
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                agents = new
                {
                    type = "object",
                    properties = new
                    {
                        defaults = new
                        {
                            type = "object",
                            properties = new
                            {
                                model = new
                                {
                                    type = "object",
                                    properties = new { primary = new { type = "string", title = "Default model" } }
                                },
                                workspace = new { type = "string", title = "Synthetic workspace" }
                            }
                        },
                        list = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    id = new { type = "string" }, name = new { type = "string" },
                                    model = new { type = "string" }
                                }
                            }
                        }
                    }
                },
                gateway = new
                {
                    type = "object",
                    properties = new { mode = new { type = "string" }, bind = new { type = "string" } }
                },
                session = new { type = "object", properties = new { scope = new { type = "string" } } },
                messages = new { type = "object", properties = new { responsePrefix = new { type = "string" } } }
            }
        });
        var configHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.GetRawText()))).ToLowerInvariant();
        var reads = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["health"] = JsonSerializer.SerializeToElement(new
            {
                ok = true, ts = Epoch.ToUnixTimeMilliseconds(), durationMs = 0,
                channels = new { }, channelOrder = Array.Empty<string>(),
                defaultAgentId = "main",
                agents = new[] { new { agentId = "main", isDefault = true }, new { agentId = "research", isDefault = false } }
            }),
            ["agents.list"] = JsonSerializer.SerializeToElement(new
            {
                defaultId = "main", mainKey = "main", scope = "per-sender",
                agents = new[]
                {
                    new { id = "main", name = "Fixture Main", identity = new { name = "Fixture Main" } },
                    new { id = "research", name = "Fixture Research", identity = new { name = "Fixture Research" } }
                }
            }),
            ["node.list"] = JsonSerializer.SerializeToElement(new { ts = Epoch.ToUnixTimeMilliseconds(), nodes = Array.Empty<object>() }),
            ["node.pair.list"] = JsonSerializer.SerializeToElement(new { pending = Array.Empty<object>(), paired = Array.Empty<object>() }),
            ["device.pair.list"] = JsonSerializer.SerializeToElement(new { pending = Array.Empty<object>(), paired = Array.Empty<object>() }),
            ["usage.status"] = JsonSerializer.SerializeToElement(new
            {
                updatedAt = Epoch.ToUnixTimeMilliseconds(), providers = Array.Empty<object>()
            }),
            ["commands.list"] = JsonSerializer.SerializeToElement(new
            {
                commands = new[]
                {
                    new
                    {
                        name = "help", description = "Show available commands (fixture browsing only).",
                        source = "native", scope = "text", acceptsArgs = false, args = Array.Empty<object>()
                    }
                }
            }),
            ["config.get"] = JsonSerializer.SerializeToElement(new
            {
                path = "/fixture/openclaw.json", exists = true, raw = config.GetRawText(),
                parsed = config, config, valid = true, hash = configHash,
                issues = Array.Empty<object>(), warnings = Array.Empty<object>(), legacyIssues = Array.Empty<object>()
            }),
            ["config.schema"] = JsonSerializer.SerializeToElement(new
            {
                schema, uiHints = new Dictionary<string, object>
                {
                    ["agents"] = new { label = "Fixture agents", group = "Agents", order = 10 },
                    ["gateway"] = new { label = "Fixture Gateway", group = "Gateway", order = 20 }
                },
                version = "fixture-1", generatedAt = Epoch.ToString("O")
            })
        };
        return new GatewayScenario(sessions, reads);
    }

    internal object CreateHello(string connectionId) => new
    {
        type = GatewayProtocolContract.HelloOkType,
        protocol = ProtocolVersion,
        server = new { version = "fixture-1", connId = connectionId },
        features = new { methods = ReadMethods, events = new[] { "connect.challenge", "sessions.changed" } },
        snapshot = new
        {
            presence = Array.Empty<object>(), health = _reads["health"],
            sessionDefaults = new { defaultAgentId = "main", mainKey = "main", mainSessionKey = MainSessionKey, scope = "per-sender" }
        },
        auth = new { role = "operator", scopes = new[] { "operator.read" } },
        policy = new { maxPayload = 1_048_576, maxBufferedBytes = 1_048_576, tickIntervalMs = 30_000 }
    };

    internal bool ContainsSession(string key) => _sessions.Any(s => s.Key == key);

    internal object Respond(string method, JsonElement parameters)
    {
        if (IsWrite(method))
            throw new FixtureRequestException("FIXTURE_READ_ONLY", "Fixture Gateway is read-only. This operation is not executed.");
        return method switch
        {
            "sessions.list" => ListSessions(parameters),
            "sessions.subscribe" => Subscribe(parameters),
            "sessions.preview" => Preview(parameters),
            "chat.history" => History(parameters),
            "models.list" => Models(parameters),
            "usage.cost" => Cost(parameters),
            _ when _reads.ContainsKey(method) => Read(method, parameters),
            _ => throw new FixtureRequestException("METHOD_NOT_FOUND", "Unknown fixture Gateway method.", unexpected: true)
        };
    }

    private object ListSessions(JsonElement p)
    {
        ValidateProperties(p, "agentId", "limit", "activeMinutes", "includeGlobal", "includeUnknown", "includeDerivedTitles", "includeLastMessage");
        var agent = OptionalString(p, "agentId");
        if (agent is not null && agent is not ("main" or "research"))
            throw new FixtureRequestException("INVALID_PARAMS", "Unknown fixture agentId.");
        var limit = PositiveInt(p, "limit", _sessions.Length);
        var activeMinutes = PositiveInt(p, "activeMinutes", int.MaxValue);
        foreach (var flag in new[] { "includeGlobal", "includeUnknown", "includeDerivedTitles", "includeLastMessage" })
            OptionalBoolean(p, flag);
        var rows = _sessions.Where(s => agent is null || s.AgentId == agent)
            .Where(s => Epoch.ToUnixTimeMilliseconds() - s.UpdatedAt <= (long)activeMinutes * 60_000)
            .Take(limit).Select(s => s.Row).ToArray();
        return new
        {
            ts = Epoch.ToUnixTimeMilliseconds(), count = rows.Length,
            defaults = new { modelProvider = "fixture", model = "browse", contextTokens = 128_000 },
            sessions = rows
        };
    }

    private object History(JsonElement p)
    {
        ValidateProperties(p, "sessionKey", "limit");
        var session = FindSession(RequiredString(p, "sessionKey"));
        var limit = PositiveInt(p, "limit", LongMessageCount);
        return new
        {
            sessionKey = session.Key, sessionId = session.Id,
            messages = session.Messages.TakeLast(limit).ToArray(), thinkingLevel = "off"
        };
    }

    private object Preview(JsonElement p)
    {
        ValidateProperties(p, "keys", "limit", "maxChars");
        if (!p.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() == 0)
            throw new FixtureRequestException("INVALID_PARAMS", "keys must be a nonempty array.");
        var sessions = keys.EnumerateArray().Select(key => key.ValueKind == JsonValueKind.String
            ? FindSession(key.GetString()!)
            : throw new FixtureRequestException("INVALID_PARAMS", "keys must contain session key strings.")).ToArray();
        var limit = PositiveInt(p, "limit", 12);
        var maxChars = PositiveInt(p, "maxChars", 240);
        return new
        {
            ts = Epoch.ToUnixTimeMilliseconds(),
            previews = sessions.Select(s => new
            {
                key = s.Key, status = s.Messages.Length == 0 ? "empty" : "ok",
                items = s.Texts.TakeLast(limit).Select((text, index) => new
                {
                    role = s.Messages[s.Messages.Length - Math.Min(limit, s.Messages.Length) + index].GetProperty("role").GetString(),
                    text = text[..Math.Min(text.Length, maxChars)]
                }).ToArray()
            }).ToArray()
        };
    }

    private static object Subscribe(JsonElement p)
    {
        ValidateProperties(p);
        return new { ok = true };
    }

    private static object Models(JsonElement p)
    {
        ValidateProperties(p, "view");
        var view = OptionalString(p, "view") ?? "configured";
        if (view is not ("configured" or "all"))
            throw new FixtureRequestException("INVALID_PARAMS", "view must be configured or all.");
        return new
        {
            models = new[]
            {
                new { id = "browse", name = "Fixture Browse", provider = "fixture", contextWindow = 128_000, configured = true, available = true, @default = true },
                new { id = "research", name = "Fixture Research", provider = "fixture", contextWindow = 64_000, configured = true, available = true, @default = false }
            }
        };
    }

    private static object Cost(JsonElement p)
    {
        ValidateProperties(p, "days");
        var days = PositiveInt(p, "days", 30);
        return new
        {
            updatedAt = Epoch.ToUnixTimeMilliseconds(), days, daily = Array.Empty<object>(),
            totals = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = 0, totalCost = 0, missingCostEntries = 0 }
        };
    }

    private JsonElement Read(string method, JsonElement p)
    {
        ValidateProperties(p, method == "health" ? ["deep", "probe"] : []);
        if (method == "health")
        {
            OptionalBoolean(p, "deep");
            OptionalBoolean(p, "probe");
        }
        return _reads[method];
    }

    private Session FindSession(string key) => _sessions.FirstOrDefault(s => s.Key == key)
        ?? throw new FixtureRequestException("INVALID_PARAMS", "Unknown fixture session key.");

    private static bool IsWrite(string method) => method is
        "chat.send" or "chat.abort" or "chat.inject" or "config.set" or "config.patch" or "config.apply"
        or "sessions.patch" or "sessions.reset" or "sessions.delete" or "sessions.compact" or "sessions.create"
        or "sessions.compaction.branch" or "node.invoke" or "exec.approval.resolve" or "node.rename"
        or "node.pair.approve" or "node.pair.reject" or "node.pair.remove" or "device.pair.approve"
        or "device.pair.reject" or "device.token.rotate" or "device.token.revoke" or "update.run"
        or "cron.add" or "cron.update" or "cron.remove" or "cron.run" or "skills.install" or "skills.update";

    internal static void ValidateProperties(JsonElement p, params string[] names)
    {
        if (p.ValueKind != JsonValueKind.Object)
            throw new FixtureRequestException("INVALID_PARAMS", "params must be an object.");
        if (p.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)))
            throw new FixtureRequestException("INVALID_PARAMS", "Unsupported fixture request parameter.");
    }

    internal static string RequiredString(JsonElement p, string name) => OptionalString(p, name)
        ?? throw new FixtureRequestException("INVALID_PARAMS", $"Missing {name}.");

    private static string? OptionalString(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FixtureRequestException("INVALID_PARAMS", $"{name} must be a nonempty string.");
        return value.GetString();
    }

    private static int PositiveInt(JsonElement p, string name, int fallback)
    {
        if (!p.TryGetProperty(name, out var value))
            return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result <= 0)
            throw new FixtureRequestException("INVALID_PARAMS", $"{name} must be a positive integer.");
        return result;
    }

    private static void OptionalBoolean(JsonElement p, string name)
    {
        if (p.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FixtureRequestException("INVALID_PARAMS", $"{name} must be a boolean.");
    }

    private static Session CreateSession(string key, string title, string agent, string model, int index, string[] texts)
    {
        var id = $"00000000-0000-4000-8000-{index + 1:D12}";
        var updatedAt = Epoch.AddMinutes(-index).ToUnixTimeMilliseconds();
        var row = JsonSerializer.SerializeToElement(new
        {
            key, sessionId = id, agentId = agent, label = title, displayName = title, derivedTitle = title,
            kind = "direct", chatType = "direct", channel = "webchat", status = "idle", hasActiveRun = false,
            isBackground = false, isMain = key == MainSessionKey, model, modelProvider = "fixture",
            updatedAt, startedAt = Epoch.AddDays(-1).ToString("O"), thinkingLevel = "off",
            inputTokens = 120, outputTokens = 80, totalTokens = 200, contextTokens = 128_000
        });
        var messages = texts.Select((text, i) =>
        {
            var number = i + 1;
            var role = i % 2 == 0 ? "user" : "assistant";
            object content = text;
            if (key == LongSessionKey && number == 40)
            {
                content = new object[]
                {
                    new { type = "text", text },
                    new { type = "toolCall", id = "fixture-inert-tool", name = "fixture_inventory", arguments = new { shelf = "synthetic" } }
                };
            }
            if (key == LongSessionKey && number == 41)
            {
                role = "toolResult";
                content = new object[] { new { type = "text", text = text + "\nSynthetic inventory: 3 books. No command executed." } };
            }
            return JsonSerializer.SerializeToElement(new
            {
                role, content, timestamp = Epoch.AddHours(-6).AddMinutes(i).ToUnixTimeMilliseconds(),
                toolCallId = number == 41 && key == LongSessionKey ? "fixture-inert-tool" : null,
                toolName = number == 41 && key == LongSessionKey ? "fixture_inventory" : null,
                stopReason = number == 40 && key == LongSessionKey ? "toolUse" : "stop",
                __openclaw = new { id = i < 2 ? $"fixture-shared-{number:D3}" : $"{id}-message-{number:D3}", seq = number, kind = "message" }
            });
        }).ToArray();
        return new Session(key, id, agent, updatedAt, row, messages, texts);
    }

    private static string LongMessage(int number)
    {
        if (number == 1) return LongEarlySentinel;
        if (number == 120) return LongMiddleSentinel;
        if (number == LongMessageCount) return $"{LongFinalSentinel}\n\nThis is the actual last message, not a nearby row.\n\n{LongFinalLine}";
        var heading = $"Fixture long message {number:D3}";
        return (number % 5) switch
        {
            0 => $"{heading}\n\n- First synthetic observation\n- Second observation with a longer explanation that wraps at narrow widths\n- Third observation\n\nNothing here invokes a provider.",
            1 => $"{heading}\n\n```csharp\nvar fixture = \"inert text\";\nConsole.WriteLine(fixture);\n```\n\nThis code block is display-only.",
            2 => $"{heading}\n\n| Item | State |\n| --- | --- |\n| Copper telescope | Parked |\n| Violet compass | Ready |\n\nA small synthetic table.",
            3 => $"{heading}\n\nA longer paragraph for mixed-height virtualization. The quiet observatory has three windows and a copper telescope. All names and events are fictional.\n\nA second paragraph makes the narrow viewport wrap differently from the wide viewport.",
            _ => $"{heading}: short synthetic reply."
        };
    }

    private sealed record Session(string Key, string Id, string AgentId, long UpdatedAt, JsonElement Row, JsonElement[] Messages, string[] Texts);
}

internal sealed class FixtureRequestException(string code, string message, bool unexpected = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Unexpected { get; } = unexpected;
}
