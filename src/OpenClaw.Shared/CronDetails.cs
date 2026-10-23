using System.Text.Json;

namespace OpenClaw.Shared;

public sealed record CronScratchSnapshot(string? Content, long CurrentRevision, int MaxBytes);
public sealed record CronScratchWriteResult(bool Ok, long CurrentRevision);
public sealed record CronTranscriptPage(JsonElement[] Messages, string? NextCursor);

public partial class OpenClawGatewayClient
{
    private const int CronRequestTimeoutMs = 15000;

    public async Task<JsonElement> GetCronJobAsync(string id, long connectionEpoch)
    {
        var job = await SendCronDetailsRequestAsync("cron.get", id, new { id }, connectionEpoch);
        if (job.ValueKind != JsonValueKind.Object || GetString(job, "id") != id)
            throw new JsonException("cron.get returned a different or invalid job.");
        return job;
    }

    public async Task<CronScratchSnapshot> GetCronScratchAsync(string id, long connectionEpoch)
    {
        var payload = await SendCronDetailsRequestAsync("cron.scratch.get", id, new { id }, connectionEpoch);
        return ParseCronScratch(payload);
    }

    internal static CronScratchSnapshot ParseCronScratch(JsonElement payload)
    {
        var revision = payload.GetProperty("currentRevision").GetInt64();
        var maxBytes = payload.GetProperty("maxBytes").GetInt32();
        var scratch = payload.GetProperty("scratch");
        if (revision < 0 || maxBytes <= 0 || scratch.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
            throw new JsonException("Invalid cron scratch response.");
        if (scratch.ValueKind == JsonValueKind.Null) return new(null, revision, maxBytes);
        var content = scratch.GetProperty("content");
        if (content.ValueKind != JsonValueKind.String || scratch.GetProperty("revision").GetInt64() != revision
            || scratch.GetProperty("updatedAtMs").GetInt64() < 0)
            throw new JsonException("Invalid cron scratch response.");
        return new(content.GetString(), revision, maxBytes);
    }

    public async Task<CronScratchWriteResult> SetCronScratchAsync(
        string id, string? content, long expectedRevision, long connectionEpoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        var payload = await SendCronDetailsRequestAsync("cron.scratch.set", id,
            new { id, content, expectedRevision }, connectionEpoch);
        return ParseCronScratchWrite(payload);
    }

    internal static CronScratchWriteResult ParseCronScratchWrite(JsonElement payload)
    {
        var ok = payload.GetProperty("ok").GetBoolean();
        var revision = payload.GetProperty("currentRevision").GetInt64();
        if (revision < 0 || (!ok && GetString(payload, "reason") != "revision-conflict"))
            throw new JsonException("Invalid cron scratch write response.");
        return new(ok, revision);
    }

    public async Task<CronTranscriptPage> GetCronHistoryAsync(
        string id, string? runId, long? runAtMs, string? cursor, long connectionEpoch)
    {
        if (string.IsNullOrWhiteSpace(runId) && runAtMs is null)
            throw new ArgumentException("An exact cron run is required.", nameof(runId));
        ArgumentOutOfRangeException.ThrowIfNegative(runAtMs ?? 0);
        var parameters = new Dictionary<string, object> { ["id"] = id, ["limit"] = 100 };
        if (!string.IsNullOrWhiteSpace(runId)) parameters["runId"] = runId;
        else parameters["runAtMs"] = runAtMs!.Value;
        if (!string.IsNullOrEmpty(cursor)) parameters["cursor"] = cursor;
        var payload = await SendCronDetailsRequestAsync("cron.history", id, parameters, connectionEpoch);
        return ParseCronTranscript(payload);
    }

    internal static CronTranscriptPage ParseCronTranscript(JsonElement payload)
    {
        var messages = payload.GetProperty("messages").EnumerateArray().Select(message => message.Clone()).ToArray();
        return new(messages, GetString(payload, "nextCursor"));
    }

    private async Task<JsonElement> SendCronDetailsRequestAsync(
        string method, string id, object parameters, long connectionEpoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var result = await SendResponseRequestAsync(method, parameters, CronRequestTimeoutMs, connectionEpoch);
        if (SessionMutationConnectionEpoch != connectionEpoch)
            throw new InvalidOperationException("The Gateway connection changed while reading cron data.");
        return result;
    }

    private async Task<bool> SendCronMutationAsync(string method, object parameters)
    {
        try
        {
            var epoch = GetReadyConnectionGeneration(method);
            var payload = await SendResponseRequestAsync(method, parameters, CronRequestTimeoutMs, epoch);
            if (SessionMutationConnectionEpoch != epoch)
                throw new InvalidOperationException("The Gateway connection changed while saving cron data.");
            if (payload.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException($"The Gateway rejected {method}.");
            await RequestCronListAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"{method} request failed: {SanitizeErrorForLog(ex.Message)}");
            return false;
        }
    }
}
