using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.TestSupport.Gateway;

public enum ContextUsageListing
{
    Initial,
    MissingTotal,
    Lower800,
    Delayed1200,
    Lower600,
}

/// <summary>
/// Synthetic wire data for the real client's usage reconciliation. Only two exact
/// prompts are accepted. Listing changes take effect on requests, never on timers.
/// </summary>
public sealed class ContextUsageFixture
{
    public const string Name = "context-usage-snapshots";
    public const string SessionKey = GatewayScenario.EmptySessionKey;
    public const string FirstPrompt = "Fixture usage: first request";
    public const string SecondPrompt = "Fixture usage: repeat request";
    public const string FirstReply = "FIXTURE USAGE FIRST: 1000 tokens, not an accumulator.";
    public const string FinalReply = "FIXTURE USAGE FINAL: the latest request is 1500 tokens.";
    public const long MeasurementTimestamp = 1_787_227_200_000;
    private readonly object _sync = new();
    private int _sends;
    private ContextUsageListing _listing;

    public static string Title(ContextUsageListing listing) => $"Fixture: context usage {listing}";

    public void SelectListing(ContextUsageListing listing)
    {
        if (!Enum.IsDefined(listing))
            throw new ArgumentOutOfRangeException(nameof(listing));
        lock (_sync) _listing = listing;
    }

    internal object List(JsonElement baseline)
    {
        ContextUsageListing listing;
        lock (_sync) listing = _listing;
        var result = JsonNode.Parse(baseline.GetRawText())!.AsObject();
        result["ts"] = MeasurementTimestamp + (listing switch
        {
            ContextUsageListing.Initial => -100,
            ContextUsageListing.MissingTotal => 10,
            ContextUsageListing.Lower800 => 5,
            ContextUsageListing.Delayed1200 => 11,
            ContextUsageListing.Lower600 => 12,
            _ => throw new InvalidOperationException(),
        });
        foreach (var row in result["sessions"]!.AsArray().OfType<JsonObject>())
        {
            if (row["key"]?.GetValue<string>() != SessionKey) continue;
            foreach (var field in new[] { "label", "displayName", "derivedTitle" })
                row[field] = Title(listing);
            row["contextTokens"] = 5_000;
            row["inputTokens"] = listing == ContextUsageListing.MissingTotal ? 12 : 0;
            row["outputTokens"] = listing == ContextUsageListing.MissingTotal ? 480 : 0;
            row.Remove("totalTokens");
            row["totalTokensFresh"] = false;
            if (listing is ContextUsageListing.Lower800 or ContextUsageListing.Lower600 or ContextUsageListing.Delayed1200)
            {
                row["totalTokens"] = listing switch
                {
                    ContextUsageListing.Lower800 => 800,
                    ContextUsageListing.Lower600 => 600,
                    _ => 1_200,
                };
                row["totalTokensFresh"] = true;
            }
        }
        return result;
    }

    internal object History(string sessionId, int limit) => new
    {
        sessionKey = SessionKey, sessionId, thinkingLevel = "off",
        messages = new object[]
        {
            new
            {
                role = "user", content = FirstPrompt, timestamp = MeasurementTimestamp - 21,
                __openclaw = new { id = "fixture-usage-first-user" }
            },
            new
            {
                role = "assistant", content = FirstReply, timestamp = MeasurementTimestamp - 20,
                usage = new { totalTokens = 1_000, contextPercent = 20 }
            },
            new
            {
                role = "user", content = SecondPrompt, timestamp = MeasurementTimestamp - 1,
                __openclaw = new { id = "fixture-usage-second-user" }
            },
            new
            {
                role = "assistant", content = FinalReply, timestamp = MeasurementTimestamp - 1,
                usage = new { totalTokens = 1_000, contextPercent = 20 }
            }
        }.TakeLast(limit).ToArray()
    };

    internal FixtureReply Send(JsonElement parameters)
    {
        GatewayScenario.ValidateProperties(parameters, "sessionKey", "message", "idempotencyKey");
        var key = GatewayScenario.RequiredString(parameters, "sessionKey");
        var prompt = GatewayScenario.RequiredString(parameters, "message");
        _ = GatewayScenario.RequiredString(parameters, "idempotencyKey");
        lock (_sync)
        {
            if (key != SessionKey || _sends >= 2 || prompt != (_sends == 0 ? FirstPrompt : SecondPrompt))
                throw new FixtureRequestException("INVALID_PARAMS", "Only the next exact synthetic usage prompt is accepted.");
            var first = _sends++ == 0;
            var timestamp = MeasurementTimestamp - (first ? 20 : 0);
            var reply = first ? FirstReply : FinalReply;
            var events = new List<object>
            {
                Chat("user", prompt, timestamp - 1),
                Chat("assistant", reply, timestamp),
            };
            // This replay deliberately omits both identity and timestamp.
            if (!first) events.Add(Chat("assistant", reply, null));
            return new(new { runId = $"fixture-usage-{_sends}", status = "started" }, events);
        }
    }

    private static object Chat(string role, string text, long? timestamp)
    {
        var payload = new JsonObject
        {
            ["sessionKey"] = SessionKey, ["state"] = "final",
            ["message"] = new JsonObject { ["role"] = role, ["content"] = text }
        };
        if (timestamp is not null) payload["ts"] = timestamp.Value;
        if (role == "user")
            payload["__openclaw"] = new JsonObject
            {
                ["id"] = text == FirstPrompt ? "fixture-usage-first-user" : "fixture-usage-second-user"
            };
        if (role == "assistant")
            payload["usage"] = new JsonObject
            {
                ["inputTokens"] = text == FirstReply ? 500 : 1_000,
                ["outputTokens"] = 500,
                ["totalTokens"] = text == FirstReply ? 1_000 : 1_500
            };
        return new { type = "event", @event = "chat", payload };
    }
}

internal sealed record FixtureReply(object Payload, IReadOnlyList<object> Events);
