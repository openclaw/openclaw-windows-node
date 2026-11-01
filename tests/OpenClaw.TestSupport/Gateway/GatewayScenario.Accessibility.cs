using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.TestSupport.Gateway;

public sealed partial class GatewayScenario
{
    // A separate opt-in wire scenario. Write scopes enable local editing in the UI,
    // but config mutation requests remain rejected by the common read-only guard.
    public static GatewayScenario CreateAccessibilityBaseline()
    {
        var browse = CreateBrowse();
        var reads = new Dictionary<string, JsonElement>(browse._reads, StringComparer.Ordinal);
        var get = JsonNode.Parse(reads["config.get"].GetRawText())!.AsObject();
        var config = get["config"]!.DeepClone().AsObject();
        config["bindings"] = JsonNode.Parse("""[{"agentId":"main","match":{"channel":"webchat","accountId":"fixture"}}]""");
        config["messages"]!["fixtureLabels"] = JsonNode.Parse("""["alpha","bravo"]""");
        var raw = config.ToJsonString();
        get["raw"] = raw;
        get["config"] = config.DeepClone();
        get["parsed"] = config.DeepClone();
        get["hash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        reads["config.get"] = JsonSerializer.SerializeToElement(get);
        var schema = JsonNode.Parse(reads["config.schema"].GetRawText())!.AsObject();
        schema["schema"]!["properties"]!["messages"]!["properties"]!["fixtureLabels"] =
            JsonNode.Parse("""{"type":"array","title":"Fixture labels","items":{"type":"string"}}""");
        reads["config.schema"] = JsonSerializer.SerializeToElement(schema);
        reads["usage.status"] = JsonSerializer.SerializeToElement(new
        {
            updatedAt = Epoch.ToUnixTimeMilliseconds(),
            providers = new object[]
            {
                new { provider = "fixture", displayName = "Fixture Provider", plan = "Pro", error = "Fixture quota warning", windows = new[] { new { label = "Quota", usedPercent = 37 } } },
                new { provider = "fixture-empty", displayName = "Fixture Empty" }
            }
        });
        return new GatewayScenario(browse._sessions, reads, accessibilityBaseline: true);
    }

    private static object AccessibilityCost(JsonElement parameters)
    {
        ValidateProperties(parameters, "days");
        var days = PositiveInt(parameters, "days", 30);
        return new
        {
            updatedAt = Epoch.ToUnixTimeMilliseconds(), days,
            daily = new[]
            {
                new { date = "2026-10-31", totalCost = 1.25 },
                new { date = "2026-10-30", totalCost = 0.50 }
            },
            totals = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = 0, totalCost = 1.75, missingCostEntries = 0 }
        };
    }
}
