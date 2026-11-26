// Authenticated task-suggestion RPC adapter. Mutations never fall back to chat.send or sessions.create.
using System.Text.Json;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    private static int TaskSuggestionReadTimeoutMs => TaskTimeout("OPENCLAW_TASK_SUGGESTION_READ_TIMEOUT_MS", 15_000);
    private static int TaskSuggestionAcceptTimeoutMs => TaskTimeout("OPENCLAW_TASK_SUGGESTION_ACCEPT_TIMEOUT_MS", 120_000);
    private static int TaskTimeout(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out var value) && value is >= 100 and <= 600_000
            ? value : fallback;
    public event EventHandler? TaskSuggestionsInvalidated;

    public bool SupportsTaskSuggestions =>
        AdvertisedServerMethods.Contains("taskSuggestions.list", StringComparer.Ordinal) && HasTaskScope("operator.read");
    public bool SupportsTaskSuggestionModes =>
        AdvertisedServerCapabilities.Contains("taskSuggestions.acceptModes", StringComparer.Ordinal);
    /// <summary>Whether the launch menu can offer the existing cloud-profile preflight path.</summary>
    public bool SupportsTaskSuggestionCloud => SupportsTaskSuggestionModes
        && AdvertisedServerMethods.Contains("environments.list", StringComparer.Ordinal);
    public bool CanAcceptTaskSuggestions =>
        AdvertisedServerMethods.Contains("taskSuggestions.accept", StringComparer.Ordinal) && !TaskSuggestionAdministratorRequired;
    public bool CanDismissTaskSuggestions =>
        AdvertisedServerMethods.Contains("taskSuggestions.dismiss", StringComparer.Ordinal) && HasTaskScope("operator.write");
    public bool TaskSuggestionAdministratorRequired => !GrantedOperatorScopes.Contains("operator.admin", StringComparer.Ordinal);

    private bool HasTaskScope(string scope) => !TaskSuggestionAdministratorRequired
        || GrantedOperatorScopes.Contains(scope, StringComparer.Ordinal)
        || (scope == "operator.read" && GrantedOperatorScopes.Contains("operator.write", StringComparer.Ordinal));

    /// <summary>Read-only task custody prerequisite, bounded across send queue and correlated reply.</summary>
    public Task<JsonElement> ReadTaskSuggestionOperatorProfileAsync(long epoch, int timeoutMs)
    {
        if (timeoutMs is < 1 or > 600_000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        if (!SupportsTaskSuggestions) throw new NotSupportedException("Task identity reads require supported task read access.");
        return TaskSuggestionRequestAsync("users.self", new { }, epoch, timeoutMs);
    }

    /// <summary>Authoritative pending snapshot. Unsupported older Gateways receive no speculative requests.</summary>
    public async Task<GatewayTaskSuggestionList> ListTaskSuggestionsAsync(long epoch)
    {
        if (!SupportsTaskSuggestions)
            return new(false, []);
        if (!HasTaskScope("operator.read"))
            throw new UnauthorizedAccessException("Reading suggested tasks requires operator read access.");
        var result = await TaskSuggestionRequestAsync("taskSuggestions.list", new { }, epoch,
            TaskSuggestionReadTimeoutMs).ConfigureAwait(false);
        return GatewayTaskSuggestions.ParseList(result);
    }

    /// <summary>Same task ID joins an in-flight acceptance or returns its retained accepted key.</summary>
    public async Task<string> AcceptTaskSuggestionAsync(GatewayTaskAcceptance request, long epoch)
    {
        GatewayTaskSuggestions.Validate(request);
        if (!AdvertisedServerMethods.Contains("taskSuggestions.accept", StringComparer.Ordinal) || !SupportsTaskSuggestionModes)
            throw new NotSupportedException("Task launch modes are not supported by this Gateway.");
        if (TaskSuggestionAdministratorRequired)
            throw new UnauthorizedAccessException("Starting suggested tasks requires administrator access to the Gateway.");
        if (request.Mode == "worktree" && request.Cwd is not null && !GatewayTaskSuggestions.IsAbsoluteGatewayPath(request.Cwd))
            throw new ArgumentException("The Gateway repository correction must be an absolute Windows or POSIX path.");
        if (request.Mode == "cloud")
        {
            // Read-only preflight failures are safe to correct. An acceptance rejection after dispatch
            // is not generally safe to reparameterize, even if its top-level code is INVALID_REQUEST.
            if (!AdvertisedServerMethods.Contains("environments.list", StringComparer.Ordinal))
                throw new GatewayTaskCloudProfileException();
            var catalog = await TaskSuggestionRequestAsync("environments.list", new { projection = "profiles" },
                epoch, TaskSuggestionReadTimeoutMs).ConfigureAwait(false);
            if (!catalog.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array
                || !profiles.EnumerateArray().Any(profile => profile.TryGetProperty("id", out var id)
                    && id.GetString() == request.CloudProfileId))
                throw new GatewayTaskCloudProfileException();
        }
        var parameters = new Dictionary<string, object> { ["taskId"] = request.TaskId, ["mode"] = request.Mode };
        if (request.CloudProfileId is not null) parameters["cloudProfileId"] = request.CloudProfileId;
        if (request.Cwd is not null) parameters["cwd"] = request.Cwd;
        var result = await TaskSuggestionRequestAsync("taskSuggestions.accept", parameters, epoch,
            TaskSuggestionAcceptTimeoutMs).ConfigureAwait(false);
        return GatewayTaskSuggestions.ParseAccepted(result, request.TaskId);
    }

    public async Task<bool> DismissTaskSuggestionAsync(string taskId, long epoch)
    {
        if (!AdvertisedServerMethods.Contains("taskSuggestions.dismiss", StringComparer.Ordinal))
            throw new NotSupportedException("Task dismissal is not supported by this Gateway.");
        if (!HasTaskScope("operator.write"))
            throw new UnauthorizedAccessException("Dismissing suggested tasks requires operator write access.");
        var result = await TaskSuggestionRequestAsync("taskSuggestions.dismiss", new { taskId }, epoch,
            TaskSuggestionReadTimeoutMs).ConfigureAwait(false);
        if (result.GetProperty("taskId").GetString() != taskId)
            throw new JsonException("Task dismissal identity changed.");
        return result.GetProperty("dismissed").GetBoolean();
    }

    private async Task<JsonElement> TaskSuggestionRequestAsync(string method, object parameters, long epoch, int timeoutMs)
    {
        // Unlike a reply-only timeout this also bounds transport queueing and sending.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        var result = await SendResponseRequestAsync(method, parameters, timeoutMs,
            expectedConnectionEpoch: epoch, requestCancellationToken: deadline.Token).ConfigureAwait(false);
        if (SessionMutationConnectionEpoch != epoch)
            throw new InvalidOperationException("The task suggestion connection changed.");
        return result;
    }
}
