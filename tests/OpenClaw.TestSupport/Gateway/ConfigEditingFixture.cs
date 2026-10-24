using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.TestSupport.Gateway;

/// <summary>
/// Opt-in, memory-only full-document replacement contract. Not a Gateway runtime:
/// no schema generation, secret resolution, disk writes or restart is performed.
/// Pinned contract: openclaw/openclaw 098e991549b7986bcfaebdbe427aeaaf95afb926,
/// packages/gateway-protocol/src/schema/config.ts and src/gateway/server-methods/config.ts.
/// </summary>
public sealed class ConfigEditingFixture
{
    private readonly object _sync = new();
    private JsonObject _config = JsonNode.Parse("""
        {
          "agents": { "list": [{ "id": "main" }, { "id": "research" }] },
          "models": { "providers": { "fixture": { "models": [
            { "id": "alpha", "name": "Alpha" }, { "id": "beta", "name": "Beta" }
          ] } } },
          "messages": { "responsePrefix": "Original" }
        }
        """)!.AsObject();

    public object Snapshot()
    {
        lock (_sync)
        {
            var raw = _config.ToJsonString();
            return new
            {
                path = "/fixture/openclaw.json", exists = true, valid = true,
                raw, parsed = _config.DeepClone(), config = _config.DeepClone(),
                hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw))),
                issues = Array.Empty<object>(), warnings = Array.Empty<object>()
            };
        }
    }

    internal object Respond(string method, JsonElement parameters)
    {
        lock (_sync)
        {
            if (method == "config.get")
            {
                ValidateProperties(parameters);
                return Snapshot();
            }
            ValidateProperties(parameters, "raw", "baseHash");
            var snapshot = JsonSerializer.SerializeToElement(Snapshot());
            if (!parameters.TryGetProperty("baseHash", out var hash) ||
                hash.ValueKind != JsonValueKind.String ||
                hash.GetString() != snapshot.GetProperty("hash").GetString())
                throw new FixtureRequestException("INVALID_REQUEST", "config baseHash conflict; reload config.get.");
            if (!parameters.TryGetProperty("raw", out var raw) || raw.ValueKind != JsonValueKind.String)
                throw new FixtureRequestException("INVALID_REQUEST", "config.apply requires raw JSON.");
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(raw.GetString()!); }
            catch (JsonException)
            {
                throw new FixtureRequestException("INVALID_REQUEST", "Invalid configuration JSON.");
            }
            if (parsed is not JsonObject config || config["agents"] is not JsonObject agentConfig ||
                agentConfig["list"] is not JsonArray agents ||
                !agents.OfType<JsonObject>().Any(agent => agent["id"] is JsonValue id &&
                    id.TryGetValue<string>(out var value) && value == "main"))
                throw new FixtureRequestException("INVALID_REQUEST", "The fixture configuration must retain the main agent.");
            _config = config;
            var committed = JsonSerializer.SerializeToElement(Snapshot());
            return new
            {
                ok = true, hash = committed.GetProperty("hash"), config = committed.GetProperty("config"),
                restart = new { ok = true, scheduled = true }
            };
        }
    }

    private static void ValidateProperties(JsonElement parameters, params string[] allowed)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            parameters.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
            throw new FixtureRequestException("INVALID_REQUEST", "Unexpected configuration parameters.");
    }
}
