using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.Cron;

/// <summary>
/// Response-aware Automations (cron) RPCs. Built on
/// <see cref="IOperatorGatewayClient.SendWizardRequestAsync"/> so every
/// gateway client (and test double) gets the same wire shapes.
/// Gateway rejections surface as <see cref="InvalidOperationException"/> with <see cref="GatewayRequestErrors"/> codes.
/// </summary>
public static class CronGatewayRequests
{
    public const int DefaultTimeoutMs = 15_000;

    private const string RevisionChangedMessage = "The automation changed since it was loaded.";

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
        return CronJsonReader.ParseJob(payload) ?? throw new InvalidOperationException(CronJsonReader.UnreadableJobMessage);
    }

    public static async Task<CronJob> AddAsync(IOperatorGatewayClient client, JsonObject definition, int timeoutMs = DefaultTimeoutMs)
    {
        var payload = await client.SendWizardRequestAsync("cron.add", definition, timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseJob(payload) ?? throw new InvalidOperationException(CronJsonReader.UnreadableJobMessage);
    }

    public static async Task<CronJob> UpdateAsync(
        IOperatorGatewayClient client, string id, string? expectedConfigRevision, JsonObject patch, int timeoutMs = DefaultTimeoutMs)
    {
        var parameters = new JsonObject { ["id"] = id };
        if (!string.IsNullOrEmpty(expectedConfigRevision))
            parameters["expectedConfigRevision"] = expectedConfigRevision;
        parameters["patch"] = patch;
        var payload = await client.SendWizardRequestAsync("cron.update", parameters, timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseJob(payload) ?? throw new InvalidOperationException(CronJsonReader.UnreadableJobMessage);
    }

    /// <summary>
    /// Removes a job. Released Gateways validate <c>cron.remove</c> against a closed schema that accepts only the id,
    /// so a loaded revision is confirmed with <c>cron.get</c> first and a changed definition raises the same
    /// revision conflict as an edit. That check is not atomic: a change between the read and the removal is not detected.
    /// </summary>
    public static async Task RemoveAsync(IOperatorGatewayClient client, string id, string? expectedConfigRevision, int timeoutMs = DefaultTimeoutMs)
    {
        if (!string.IsNullOrEmpty(expectedConfigRevision))
        {
            var current = await GetAsync(client, id, timeoutMs).ConfigureAwait(false);
            if (!string.Equals(current.ConfigRevision, expectedConfigRevision, StringComparison.Ordinal))
                throw GatewayRequestErrors.Create(RevisionChangedMessage, "INVALID_REQUEST", CronProtocol.JobChangedCode);
        }
        await client.SendWizardRequestAsync("cron.remove", new JsonObject { ["id"] = id }, timeoutMs).ConfigureAwait(false);
    }

    public static async Task<CronRunsPage> RunsAsync(IOperatorGatewayClient client, CronRunsQuery query, int timeoutMs = DefaultTimeoutMs)
    {
        var payload = await client.SendWizardRequestAsync("cron.runs", query.ToParameters(), timeoutMs).ConfigureAwait(false);
        return CronJsonReader.ParseRunsPage(payload, query.Offset);
    }

    public static bool IsRevisionConflict(Exception ex) =>
        GatewayRequestErrors.DetailCode(ex) == CronProtocol.JobChangedCode;

    /// <summary>Only this detail code proves the job is gone; other errors say nothing about it.</summary>
    public static bool IsJobNotFound(Exception ex) =>
        GatewayRequestErrors.DetailCode(ex) == CronProtocol.JobNotFoundCode;

    public static bool IsForbidden(Exception ex) =>
        GatewayRequestErrors.Code(ex) == "FORBIDDEN";
}
