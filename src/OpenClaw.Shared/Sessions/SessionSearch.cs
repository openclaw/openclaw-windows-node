using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>Searches active conversation transcripts, not just the locally loaded titles.</summary>
public sealed record SessionSearchRequest(string Query, bool IncludeBackground = false)
{
    public const int ResultLimit = 25;
    public const int MaximumQueryLength = 4096;

    internal object ToPayload()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Query);
        var query = Query.Trim();
        if (query.Length > MaximumQueryLength)
            throw new ArgumentOutOfRangeException(nameof(Query));
        var scope = new Dictionary<string, object>
        {
            ["archived"] = false,
            ["includeUnknown"] = true,
            ["configuredAgentsOnly"] = true
        };
        if (!IncludeBackground)
        {
            scope["excludeCron"] = true;
            scope["excludeSystem"] = true;
        }
        return new { query, limit = ResultLimit, scope };
    }
}

public sealed record SessionSearchHit(
    string SessionKey, string SessionId, string MessageId, string Role,
    long Timestamp, string Snippet, double Score, string? Title = null)
{
    public string DisplayTitle => Title ?? SessionKey;
}

public sealed record SessionSearchResult(
    IReadOnlyList<SessionSearchHit> Results,
    bool Indexing = false,
    bool Truncated = false,
    int ArchivedTranscriptsExcluded = 0,
    bool IsSupported = true);

public partial class OpenClawGatewayClient
{
    public async Task<SessionSearchResult> SearchSessionsAsync(SessionSearchRequest request, int timeoutMs = 15000)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = request.ToPayload();
        var epoch = SessionMutationConnectionEpoch
            ?? throw new InvalidOperationException("Gateway handshake has not completed.");
        JsonElement payload;
        try
        {
            payload = await SendResponseRequestAsync("sessions.search", parameters, timeoutMs, epoch).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsUnknownMethodError(ex.Message))
        {
            _logger.Warn("sessions.search unsupported on gateway");
            return new([], IsSupported: false);
        }
        return ParseSessionSearch(payload);
    }

    internal static SessionSearchResult ParseSessionSearch(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() > SessionSearchRequest.ResultLimit)
            throw new JsonException("Invalid sessions.search results.");
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload.TryGetProperty("sessions", out var sessions))
        {
            if (sessions.ValueKind != JsonValueKind.Array || sessions.GetArrayLength() > SessionSearchRequest.ResultLimit)
                throw new JsonException("Invalid sessions.search session rows.");
            foreach (var session in sessions.EnumerateArray())
            {
                var key = Required(session, "key");
                var title = OptionalTitle("label") ?? OptionalTitle("displayName") ?? key;
                if (!titles.TryAdd(key, title))
                    throw new JsonException("Duplicate sessions.search session row.");

                string? OptionalTitle(string name) => session.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                        ? value.GetString() : null;
            }
        }
        var hits = results.EnumerateArray().Select(row =>
        {
            var key = Required(row, "sessionKey");
            var sessionId = Required(row, "sessionId");
            var messageId = Required(row, "messageId");
            var role = Required(row, "role");
            if (role is not ("user" or "assistant") ||
                !row.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number ||
                !timestamp.TryGetInt64(out var ts) || ts < 0 ||
                !row.TryGetProperty("snippet", out var snippet) || snippet.ValueKind != JsonValueKind.String ||
                !row.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Number ||
                !score.TryGetDouble(out var rank) || !double.IsFinite(rank))
                throw new JsonException("Invalid sessions.search match.");
            return new SessionSearchHit(key, sessionId, messageId, role, ts, snippet.GetString()!, rank,
                titles.GetValueOrDefault(key));
        }).ToArray();
        var excluded = 0;
        if (payload.TryGetProperty("archivedTranscriptsExcluded", out var count) &&
            (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out excluded) || excluded < 0))
            throw new JsonException("Invalid sessions.search excluded count.");
        return new(hits, OptionalFlag("indexing"), OptionalFlag("truncated"), excluded);

        bool OptionalFlag(string name)
        {
            if (!payload.TryGetProperty(name, out var flag)) return false;
            if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException($"Invalid sessions.search {name}.");
            return flag.GetBoolean();
        }
        static string Required(JsonElement row, string name)
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new JsonException($"Invalid sessions.search {name}.");
            return value.GetString()!;
        }
    }
}
