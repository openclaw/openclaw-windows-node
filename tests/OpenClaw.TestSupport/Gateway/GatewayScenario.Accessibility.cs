using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

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
        return new GatewayScenario(browse._sessions, reads, accessibilityBaseline: true);
    }

    public void AdvanceAccessibilityUsage()
    {
        if (!_accessibilityBaseline) throw new InvalidOperationException("Usage snapshots are available only in the accessibility fixture.");
        Interlocked.Exchange(ref _accessibilityUsageRevision, 1);
    }

    private object AccessibilityStatus(JsonElement parameters)
    {
        ValidateProperties(parameters);
        var changed = Volatile.Read(ref _accessibilityUsageRevision) != 0;
        return new
        {
            updatedAt = Epoch.AddSeconds(changed ? 1 : 0).ToUnixTimeMilliseconds(),
            providers = new object[]
            {
                new { provider = "fixture", displayName = "Fixture Provider", plan = "Pro", error = changed ? "Fixture changed warning" : "Fixture quota warning", windows = new[] { new { label = "Quota", usedPercent = changed ? 53 : 37 } } },
                new { provider = "fixture-empty", displayName = "Fixture Empty" }
            }
        };
    }

    private object AccessibilityCost(JsonElement parameters)
    {
        ValidateProperties(parameters, "days");
        var days = PositiveInt(parameters, "days", 30);
        var changed = Volatile.Read(ref _accessibilityUsageRevision) != 0;
        var firstDayCost = (days == 7 ? 0.75 : 1.25) * (changed ? 2 : 1);
        return new
        {
            updatedAt = Epoch.AddSeconds(changed ? 1 : 0).ToUnixTimeMilliseconds(), days,
            daily = new[]
            {
                new { date = "2026-10-31", totalCost = firstDayCost },
                new { date = "2026-10-30", totalCost = 0.50 }
            },
            totals = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = 0, totalCost = firstDayCost + 0.50, missingCostEntries = 0 }
        };
    }

    private object AccessibilitySessionsUsage(JsonElement parameters)
    {
        ValidateProperties(parameters, "startDate", "endDate", "agentScope", "agentId", "mode", "timeZone", "utcOffset",
            "groupBy", "limit", "projection", "offset", "query", "sort", "sortDirection", "key");
        var start = DateOnly.ParseExact(RequiredString(parameters, "startDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(RequiredString(parameters, "endDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var days = end.DayNumber - start.DayNumber + 1;
        var changed = Volatile.Read(ref _accessibilityUsageRevision) != 0;
        var cost = (days == 7 ? 0.75 : 1.25) * (changed ? 2 : 1);
        return new
        {
            startDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            sessions = new[] { new { key = "agent:fixture:usage", label = "Fixture usage",
                usage = new { totalTokens = 0, totalCost = cost + 0.5 } } },
            totals = new { totalTokens = 0, totalCost = cost + 0.5 },
            aggregates = new
            {
                sessionCount = 1,
                costDaily = new[]
                {
                    new { date = end.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), totalTokens = 0, totalCost = cost },
                    new { date = end.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), totalTokens = 0, totalCost = 0.5 },
                },
            },
        };
    }
}
