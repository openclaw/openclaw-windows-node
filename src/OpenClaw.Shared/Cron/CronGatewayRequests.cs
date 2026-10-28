using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.Cron;

/// <summary>
/// Response-aware cron, MCP Events and conversation RPCs. Built on
/// <see cref="IOperatorGatewayClient.SendWizardRequestAsync"/> so every
/// gateway client (and test double) gets the same wire shapes.
/// Gateway rejections surface as <see cref="InvalidOperationException"/> with <see cref="GatewayRequestErrors"/> codes.
/// </summary>
public static class CronGatewayRequests
{
    public const int DefaultTimeoutMs = 15_000;
    /// <summary>Upper bound for paging the MCP event catalog, matching the web client.</summary>
    public const int MaxMcpEventPages = 100;

    public static async Task<CronListPage> ListAsync(IOperatorGatewayClient client, CronListQuery query, int timeoutMs = DefaultTimeoutMs)
    {
        var payload = await client.SendWizardRequestAsync("cron.list", query.ToParameters(), timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseListPage(payload, query.Offset);
    }

    public static async Task<CronSchedulerStatus> StatusAsync(IOperatorGatewayClient client, int timeoutMs = DefaultTimeoutMs)
    {
        var payload = await client.SendWizardRequestAsync("cron.status", new JsonObject(), timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseStatus(payload);
    }

    public static async Task<CronJob> GetAsync(IOperatorGatewayClient client, string id, int timeoutMs = DefaultTimeoutMs)
    {
        var payload = await client.SendWizardRequestAsync("cron.get", new JsonObject { ["id"] = id }, timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseJob(payload) ?? throw new InvalidOperationException("The Gateway returned an unreadable automation.");
    }

    public static async Task<CronJob> AddAsync(IOperatorGatewayClient client, JsonObject definition, int timeoutMs = DefaultTimeoutMs)
    {
        var payload = await client.SendWizardRequestAsync("cron.add", definition, timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseJob(payload) ?? throw new InvalidOperationException("The Gateway returned an unreadable automation.");
    }

    public static async Task<CronJob> UpdateAsync(
        IOperatorGatewayClient client, string id, string? expectedConfigRevision, JsonObject patch, int timeoutMs = DefaultTimeoutMs)
    {
        var parameters = new JsonObject { ["id"] = id };
        if (!string.IsNullOrEmpty(expectedConfigRevision))
            parameters["expectedConfigRevision"] = expectedConfigRevision;
        parameters["patch"] = patch;
        var payload = await client.SendWizardRequestAsync("cron.update", parameters, timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseJob(payload) ?? throw new InvalidOperationException("The Gateway returned an unreadable automation.");
    }

    public static async Task RemoveAsync(IOperatorGatewayClient client, string id, string? expectedConfigRevision, int timeoutMs = DefaultTimeoutMs)
    {
        var parameters = new JsonObject { ["id"] = id };
        if (!string.IsNullOrEmpty(expectedConfigRevision))
            parameters["expectedConfigRevision"] = expectedConfigRevision;
        await client.SendWizardRequestAsync("cron.remove", parameters, timeoutMs).ConfigureAwait(false);
    }

    public static async Task<CronRunsPage> RunsAsync(IOperatorGatewayClient client, CronRunsQuery query, int timeoutMs = DefaultTimeoutMs)
    {
        var payload = await client.SendWizardRequestAsync("cron.runs", query.ToParameters(), timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseRunsPage(payload, query.Offset);
    }

    /// <summary>Transcript bound to a recorded run (<c>cron.history</c>); prefers the run ID over its timestamp.</summary>
    public static async Task<CronHistoryPage> HistoryAsync(
        IOperatorGatewayClient client, CronRunEntry run, string? cursor = null, int timeoutMs = DefaultTimeoutMs)
    {
        if (!run.CanOpenTranscript)
            throw new InvalidOperationException("This run is missing the identity needed to open its transcript.");
        var parameters = new JsonObject { ["id"] = run.JobId };
        if (!string.IsNullOrWhiteSpace(run.RunId)) parameters["runId"] = run.RunId;
        else parameters["runAtMs"] = run.RunAtMs;
        parameters["limit"] = CronProtocol.TranscriptPageSize;
        if (!string.IsNullOrEmpty(cursor)) parameters["cursor"] = cursor;
        var payload = await client.SendWizardRequestAsync("cron.history", parameters, timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseHistoryPage(payload);
    }

    public static async Task<CronScratchSnapshot> GetScratchAsync(IOperatorGatewayClient client, string id, int timeoutMs = DefaultTimeoutMs)
    {
        var payload = await client.SendWizardRequestAsync("cron.scratch.get", new JsonObject { ["id"] = id }, timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseScratch(payload);
    }

    /// <summary>Saves (or with null content removes) scratch using compare-and-swap on <paramref name="expectedRevision"/>.</summary>
    public static async Task<CronScratchSetResult> SetScratchAsync(
        IOperatorGatewayClient client, string id, string? content, long expectedRevision, int timeoutMs = DefaultTimeoutMs)
    {
        var parameters = new JsonObject
        {
            ["id"] = id,
            ["content"] = content is null ? null : JsonValue.Create(content),
            ["expectedRevision"] = expectedRevision,
        };
        var payload = await client.SendWizardRequestAsync("cron.scratch.set", parameters, timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseScratchSet(payload);
    }

    /// <summary>Full webhook-deliverable event catalog for one MCP server, following cursors.</summary>
    public static async Task<IReadOnlyList<McpEventDefinition>> ListMcpEventsAsync(
        IOperatorGatewayClient client, string? agentId, string serverName, int timeoutMs = DefaultTimeoutMs)
    {
        var events = new List<McpEventDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var parameters = new JsonObject { ["serverName"] = serverName };
            if (!string.IsNullOrWhiteSpace(agentId)) parameters["agentId"] = agentId;
            if (cursor is not null) parameters["cursor"] = cursor;
            var payload = await client.SendWizardRequestAsync("mcp.events.list", parameters, timeoutMs).ConfigureAwait(false);
            var page = CronJsonReader.ParseMcpEvents(payload);
            if (!string.Equals(page.ServerName, serverName, StringComparison.Ordinal))
                throw new InvalidOperationException("The event catalog returned a different server.");
            events.AddRange(page.Events);
            cursor = page.NextCursor;
            if (cursor is not null && (!seen.Add(cursor) || seen.Count > MaxMcpEventPages))
                throw new InvalidOperationException("The event catalog repeated a page.");
        }
        while (cursor is not null);
        return events;
    }

    public static async Task<IReadOnlyList<ConversationTarget>> ListConversationsAsync(
        IOperatorGatewayClient client, string agentId, string? channel, int timeoutMs = DefaultTimeoutMs)
    {
        var parameters = new JsonObject { ["agentId"] = agentId, ["limit"] = CronProtocol.ConversationSuggestionLimit };
        if (!string.IsNullOrWhiteSpace(channel) && channel != CronProtocol.ChannelLast)
            parameters["channel"] = channel.Trim();
        var payload = await client.SendWizardRequestAsync("conversations.list", parameters, timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseConversations(payload);
    }

    /// <summary>Editor hints from the Gateway config: enabled MCP servers and configured models.</summary>
    public static async Task<CronEditorHints> EditorHintsAsync(IOperatorGatewayClient client, int timeoutMs = DefaultTimeoutMs)
    {
        var payload = await client.SendWizardRequestAsync("config.get", new JsonObject(), timeoutMs).ConfigureAwait(false);
        return new CronEditorHints(CronJsonReader.ParseEnabledMcpServers(payload), CronJsonReader.ParseConfiguredModels(payload));
    }

    public static bool IsRevisionConflict(Exception ex) =>
        GatewayRequestErrors.DetailCode(ex) == CronProtocol.JobChangedCode;

    /// <summary>Only this detail code proves the job is gone; other errors say nothing about it.</summary>
    public static bool IsJobNotFound(Exception ex) =>
        GatewayRequestErrors.DetailCode(ex) == CronProtocol.JobNotFoundCode;

    public static bool IsForbidden(Exception ex) =>
        GatewayRequestErrors.Code(ex) == "FORBIDDEN";
}

public sealed record CronEditorHints(IReadOnlyList<string> McpServers, IReadOnlyList<string> Models)
{
    public static CronEditorHints Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());
}
