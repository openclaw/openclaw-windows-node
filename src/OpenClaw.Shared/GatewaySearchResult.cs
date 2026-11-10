using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared;

public sealed record GatewayTranscriptHit(string SessionKey, string Snippet, double Score);

public sealed record GatewaySearchResult(
    IReadOnlyList<SessionInfo> Sessions,
    IReadOnlyList<GatewayTranscriptHit> Hits,
    bool Indexing = false,
    bool Truncated = false,
    int ArchivedExcluded = 0);

/// <summary>Preserves RPC error codes without requiring callers to display server error text.</summary>
public sealed class GatewayRequestException(string message, string? code)
    : InvalidOperationException(message)
{
    public string? Code { get; } = code;
}

public static class GatewaySearchProtocol
{
    public const int MaximumQueryLength = 4096;
    public const int MessageLimit = 25;
    internal const int CatalogPageSize = 200;
    internal const int CatalogEntryLimit = 5000;
    public static int MessageTimeoutMilliseconds => Timing("OPENCLAW_SEARCH_MESSAGE_TIMEOUT_MS", 8000);
    public static int CatalogTimeoutMilliseconds => Timing("OPENCLAW_SEARCH_CATALOG_TIMEOUT_MS", 10000);
    private static int Timing(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? Math.Clamp(value, 1000, 30000) : fallback;

    internal static async Task<JsonElement> ReadAutomationPagesAsync(
        Func<object?, CancellationToken, Task<JsonElement>> request, object? parameters, CancellationToken ct)
    {
        var args = JsonSerializer.SerializeToNode(parameters) as JsonObject ?? new JsonObject();
        args["limit"] = CatalogPageSize;
        var jobs = new JsonArray();
        var offset = 0;
        string? revision = null;
        int? total = null;
        JsonObject? result = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CatalogTimeoutMilliseconds);
        while (jobs.Count < CatalogEntryLimit)
        {
            args["offset"] = offset;
            JsonElement page;
            try { page = await request(args, budget.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
            {
                if (result is null) throw new TimeoutException("Automation search timed out.");
                result["hasMore"] = true;
                break;
            }
            if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("jobs", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Invalid automation search page.");
            var nextRevision = page.TryGetProperty("snapshotRevision", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            var nextTotal = page.TryGetProperty("total", out var t) && t.TryGetInt32(out var count) ? (int?)count : null;
            if (result is not null && (revision != nextRevision || total != nextTotal))
                throw new InvalidDataException("Automation inventory changed during search.");
            revision = nextRevision;
            total = nextTotal;
            result = JsonNode.Parse(page.GetRawText())!.AsObject();
            foreach (var row in rows.EnumerateArray())
            {
                if (jobs.Count >= CatalogEntryLimit) { result["hasMore"] = true; break; }
                jobs.Add(JsonNode.Parse(row.GetRawText()));
            }
            if (!page.TryGetProperty("hasMore", out var more) || more.ValueKind != JsonValueKind.True) break;
            // Legacy single-page replies need no revision. A multi-page read cannot establish
            // one consistent inventory without it, even if the total stays the same.
            if (string.IsNullOrEmpty(revision))
                throw new InvalidDataException("Automation pagination requires a snapshot revision.");
            if (!page.TryGetProperty("nextOffset", out var next) || !next.TryGetInt32(out var nextOffset) ||
                nextOffset <= offset || rows.GetArrayLength() == 0)
                throw new InvalidDataException("Automation search pagination did not advance.");
            offset = nextOffset;
        }
        ct.ThrowIfCancellationRequested();
        result!["jobs"] = jobs;
        return JsonSerializer.SerializeToElement(result);
    }

    public static object Scope() => new
    {
        includeGlobal = false,
        includeUnknown = false,
        configuredAgentsOnly = true,
        excludeSubagents = true,
        excludeCron = true,
        excludeSystem = true,
        excludeDock = true
    };

    internal static GatewaySearchResult ParseTranscript(JsonElement payload, IReadOnlyList<SessionInfo> sessions)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid transcript search response.");
        var hits = new List<GatewayTranscriptHit>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("sessionKey", out var key) || key.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(key.GetString()) ||
                !row.TryGetProperty("snippet", out var snippet) || snippet.ValueKind != JsonValueKind.String ||
                !row.TryGetProperty("score", out var score) || !score.TryGetDouble(out var number) || !double.IsFinite(number))
                throw new InvalidDataException("Invalid transcript search hit.");
            hits.Add(new(key.GetString()!, snippet.GetString()!, number));
        }
        return new(sessions, hits,
            payload.TryGetProperty("indexing", out var indexing) && indexing.ValueKind == JsonValueKind.True,
            payload.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True,
            payload.TryGetProperty("archivedTranscriptsExcluded", out var excluded) && excluded.TryGetInt32(out var count)
                ? Math.Max(0, count) : 0);
    }
}
