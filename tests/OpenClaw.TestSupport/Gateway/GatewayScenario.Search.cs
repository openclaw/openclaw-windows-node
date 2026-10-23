using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.TestSupport.Gateway;

public enum TranscriptSearchFixtureMode { Ready, Unsupported, Forbidden, Indexing }

public sealed partial class GatewayScenario
{
    /// <summary>Hidden matches outnumber the result cap, exercising membership before limiting.</summary>
    public static GatewayScenario CreateTranscriptSearch(TranscriptSearchFixtureMode mode = TranscriptSearchFixtureMode.Ready)
    {
        var browse = CreateBrowse(allowSessionMutations: true);
        var extra = new[] { "cron", "system" }.Select((classification, index) =>
        {
            var session = CreateSession($"agent:main:{classification}:fixture-search",
                $"Fixture {classification}", "main", "browse", index + 8,
                Enumerable.Repeat("Synthetic lighthouse background match.", 30).ToArray());
            var row = JsonNode.Parse(session.Row.GetRawText())!.AsObject();
            row["classification"] = classification;
            row["isBackground"] = true;
            return session with { Row = JsonSerializer.SerializeToElement(row) };
        });
        return new([.. browse._sessions, .. extra], browse._reads, allowSessionMutations: true, transcriptSearchMode: mode);
    }

    private object SearchTranscripts(JsonElement p)
    {
        if (_transcriptSearchMode == TranscriptSearchFixtureMode.Unsupported)
            throw new FixtureRequestException("METHOD_NOT_FOUND", "Unknown method: sessions.search.");
        if (_transcriptSearchMode == TranscriptSearchFixtureMode.Forbidden)
            throw new FixtureRequestException("FORBIDDEN", "Missing scope: operator.read.");
        // Deliberately bounded fixture support for the native scope contract, not a catch-all search service.
        ValidateProperties(p, "query", "scope", "limit");
        var query = RequiredString(p, "query");
        if (query.Length > 4096)
            throw new FixtureRequestException("INVALID_PARAMS", "query exceeds the search limit.");
        var limit = PositiveInt(p, "limit", 25);
        if (limit > 25 || !p.TryGetProperty("scope", out var scope))
            throw new FixtureRequestException("INVALID_PARAMS", "Expected a bounded scope search.");
        ValidateProperties(scope, "archived", "includeUnknown", "configuredAgentsOnly", "excludeCron", "excludeSystem");
        var archived = OptionalBoolean(scope, "archived") ?? false;
        _ = OptionalBoolean(scope, "includeUnknown");
        _ = OptionalBoolean(scope, "configuredAgentsOnly");
        var excludeCron = OptionalBoolean(scope, "excludeCron") ?? false;
        var excludeSystem = OptionalBoolean(scope, "excludeSystem") ?? false;
        lock (_sessionChanges)
        {
            var matches = _sessions.Where(session =>
            {
                if (_sessionChanges.TryGetValue(session.Key, out var changed) && changed is null) return false;
                var row = changed is null ? session.Row : JsonSerializer.SerializeToElement(changed);
                var isArchived = row.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True;
                var classification = row.TryGetProperty("classification", out var c) ? c.GetString() : null;
                return isArchived == archived && !(excludeCron && classification == "cron") &&
                    !(excludeSystem && classification == "system");
            }).SelectMany(session => session.Messages.Select((message, index) => new { session, message, index }))
                .Where(match => match.message.GetProperty("role").GetString() is "user" or "assistant" &&
                    match.session.Texts[match.index].Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(match => match.session.Key, StringComparer.Ordinal).ThenBy(match => match.index)
                .ToArray();
            return new
            {
                results = matches.Take(limit).Select(match => new
                {
                    sessionKey = match.session.Key,
                    sessionId = match.session.Id,
                    messageId = match.message.GetProperty("__openclaw").GetProperty("id").GetString(),
                    role = match.message.GetProperty("role").GetString(),
                    timestamp = match.message.GetProperty("timestamp").GetInt64(),
                    snippet = match.session.Texts[match.index],
                    score = 1.0
                }).ToArray(),
                sessions = matches.Take(limit).Select(match => match.session).DistinctBy(session => session.Key)
                    .Select(session => _sessionChanges.TryGetValue(session.Key, out var changed) && changed is not null
                        ? JsonSerializer.SerializeToElement(changed) : session.Row).ToArray(),
                indexing = _transcriptSearchMode == TranscriptSearchFixtureMode.Indexing,
                truncated = matches.Length > limit,
                archivedTranscriptsExcluded = 0
            };
        }
    }
}
