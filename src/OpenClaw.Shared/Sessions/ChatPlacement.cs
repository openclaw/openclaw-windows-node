using System.Text.Json;

namespace OpenClaw.Shared;

public enum ChatEnvironmentUnavailable
{
    None,
    Offline,
    HostingDisabled,
    CapacityUnknown,
    NoCapacity,
    CommandUnavailable,
    UpdateRequired,
    RuntimeUnsupported
}

public sealed record ChatEnvironment(
    string Id,
    string Label,
    bool IsLocal,
    ChatEnvironmentUnavailable Unavailable,
    string? Detail = null)
{
    public bool IsSelectable => Unavailable == ChatEnvironmentUnavailable.None;
}

public sealed record ChatWorkspace(
    string Id,
    string Label,
    string? Path = null,
    string? ProjectId = null,
    string? GitUrl = null);

public sealed record ChatPlacementCatalog(
    long ConnectionEpoch,
    string AgentId,
    ChatEnvironment[] Environments,
    ChatWorkspace DefaultWorkspace,
    ChatWorkspace[] Workspaces,
    string? AgentName = null,
    string? AgentEmoji = null,
    string? AgentAvatarUrl = null);

public sealed record ChatWorkspaceDirectory(
    string Path,
    string? Parent,
    string Home,
    ChatWorkspace[] Folders);

public sealed record ChatPlacementSelection(ChatEnvironment Environment, ChatWorkspace Workspace);

/// <summary>Gateway-scoped discovery and first-turn location preparation. No host filesystem access.</summary>
public interface IChatPlacementGateway
{
    long? ConnectionEpoch { get; }
    Task<ChatPlacementCatalog> LoadAsync(string agentId, CancellationToken cancellationToken);
    Task<ChatWorkspaceDirectory> BrowseAsync(string? path, long connectionEpoch, CancellationToken cancellationToken);
    Task PrepareAsync(string sessionKey, string agentId, ChatPlacementSelection selection,
        long connectionEpoch, CancellationToken cancellationToken);
}

public sealed class GatewayChatPlacementClient : IChatPlacementGateway
{
    private readonly Func<long?> _connectionEpoch;
    private readonly Func<string, object, int, long, Task<JsonElement>> _request;

    internal GatewayChatPlacementClient(Func<long?> connectionEpoch,
        Func<string, object, int, long, Task<JsonElement>> request)
    {
        _connectionEpoch = connectionEpoch;
        _request = request;
    }

    public long? ConnectionEpoch => _connectionEpoch();

    public async Task<ChatPlacementCatalog> LoadAsync(string agentId, CancellationToken cancellationToken)
    {
        var epoch = ConnectionEpoch ?? throw new InvalidOperationException("The Gateway is disconnected.");
        var projects = RequestAsync("projects.list", new { }, epoch, cancellationToken);
        var agents = RequestAsync("agents.list", new { }, epoch, cancellationToken);
        await Task.WhenAll(projects, agents).ConfigureAwait(false);
        var runtime = ReadRuntime(await agents, agentId);
        var environments = await RequestAsync("environments.list", EnvironmentParameters(runtime), epoch, cancellationToken)
            .ConfigureAwait(false);
        return ParseCatalog(epoch, agentId, environments, await projects, await agents);
    }

    public async Task<ChatWorkspaceDirectory> BrowseAsync(
        string? path, long connectionEpoch, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(path))
            parameters["path"] = path;
        var response = await RequestAsync("fs.listDir", parameters, connectionEpoch, cancellationToken)
            .ConfigureAwait(false);
        return new(
            RequiredString(response, "path"),
            OptionalString(response, "parent"),
            RequiredString(response, "home"),
            RequiredArray(response, "entries").EnumerateArray()
                .Select(entry => new ChatWorkspace(
                    "folder:" + RequiredString(entry, "path"),
                    RequiredString(entry, "name"),
                    RequiredString(entry, "path")))
                .ToArray());
    }

    public async Task PrepareAsync(string sessionKey, string agentId, ChatPlacementSelection selection,
        long connectionEpoch, CancellationToken cancellationToken)
    {
        if (!selection.Environment.IsSelectable)
            throw new InvalidOperationException("The selected environment is unavailable.");

        // Refresh eligibility before allocating or changing a workspace. Selection never falls back.
        var agents = await RequestAsync("agents.list", new { }, connectionEpoch, cancellationToken).ConfigureAwait(false);
        var runtime = ReadRuntime(agents, agentId);
        var inventory = await RequestAsync("environments.list", EnvironmentParameters(runtime), connectionEpoch, cancellationToken)
            .ConfigureAwait(false);
        var environment = ParseEnvironments(inventory, runtime)
            .SingleOrDefault(item => item.Id == selection.Environment.Id);
        if (environment?.IsSelectable != true)
            throw new InvalidOperationException("The selected environment is no longer available. Choose an environment again.");

        var described = await RequestAsync("sessions.describe", new { key = sessionKey, agentId },
            connectionEpoch, cancellationToken).ConfigureAwait(false);
        if (!described.TryGetProperty("session", out var session) ||
            session.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new InvalidOperationException("The Gateway did not describe the selected session. No location was changed.");
        if (session.ValueKind == JsonValueKind.Object &&
            session.TryGetProperty("placement", out var placement) &&
            placement.ValueKind == JsonValueKind.Object &&
            OptionalString(placement, "state") != "local")
        {
            throw new InvalidOperationException(
                "This session already has an environment placement. Review it in the Gateway before changing location or retrying.");
        }

        var workspace = selection.Workspace;
        if (workspace.GitUrl is { } gitUrl)
        {
            var project = await RequestAsync("projects.add", new { gitUrl }, connectionEpoch,
                cancellationToken, timeoutMs: 120_000).ConfigureAwait(false);
            workspace = new("project:" + RequiredString(project, "id"),
                RequiredString(project, "displayName"), OptionalString(project, "repoRoot"),
                RequiredString(project, "id"));
        }

        var parameters = new Dictionary<string, object>
        {
            ["key"] = sessionKey,
            ["agentId"] = agentId,
            ["emitCommandHooks"] = false
        };
        if (workspace.ProjectId is { } projectId)
            parameters["projectId"] = projectId;
        else if (!string.IsNullOrWhiteSpace(workspace.Path))
            parameters["cwd"] = workspace.Path;
        else if (workspace.Id != "default")
            throw new InvalidOperationException("The Gateway did not provide a workspace path. Choose a project or browse for a folder.");

        if (!environment.IsLocal)
        {
            parameters["worktree"] = true;
        }

        // sessions.create can adopt an existing empty session's cwd without resetting its transcript.
        var created = await RequestAsync("sessions.create", parameters, connectionEpoch,
            cancellationToken, timeoutMs: 120_000).ConfigureAwait(false);
        RequireSessionResult(created, sessionKey);
        if (environment.IsLocal)
            return;

        var dispatched = await RequestAsync("sessions.dispatch",
            new { key = sessionKey, agentId, deviceId = environment.Id["node:".Length..] },
            connectionEpoch, cancellationToken, timeoutMs: 120_000).ConfigureAwait(false);
        RequireSessionResult(dispatched, sessionKey);
        if (!dispatched.TryGetProperty("placement", out var active) ||
            OptionalString(active, "state") != "active")
        {
            throw new InvalidOperationException(
                "The Gateway has not confirmed the selected environment is ready. No message was sent.");
        }
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, long epoch,
        CancellationToken cancellationToken, int timeoutMs = 15_000)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectionEpoch != epoch)
            throw new InvalidOperationException("The Gateway connection changed. Refresh the location choices.");
        // Drain an admitted mutation even when the view closes; do not imply that cancellation rolled it back.
        var result = await _request(method, parameters, timeoutMs, epoch).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectionEpoch != epoch)
            throw new InvalidOperationException("The Gateway connection changed. Refresh the location choices.");
        return result;
    }

    internal static ChatPlacementCatalog ParseCatalog(long epoch, string agentId,
        JsonElement environments, JsonElement projects, JsonElement agents)
    {
        var agent = RequiredArray(agents, "agents").EnumerateArray()
            .FirstOrDefault(item => OptionalString(item, "id") == agentId);
        var path = agent.ValueKind == JsonValueKind.Object ? OptionalString(agent, "workspace") : null;
        var workspace = new ChatWorkspace("default", path is null ? "" : FolderName(path), path);
        var workspaces = RequiredArray(projects, "projects").EnumerateArray()
            .Where(project => OptionalString(project, "source") != "workspace" ||
                OptionalString(project, "agentId") != agentId)
            .Select(project => new ChatWorkspace(
                "project:" + RequiredString(project, "id"),
                RequiredString(project, "displayName"),
                OptionalString(project, "repoRoot"),
                RequiredString(project, "id"))).ToList();
        if (projects.TryGetProperty("recents", out var recents) && recents.ValueKind == JsonValueKind.Array)
        {
            foreach (var recent in recents.EnumerateArray())
            {
                if (OptionalString(recent, "kind") == "folder" && OptionalString(recent, "execNode") is null)
                {
                    var folder = RequiredString(recent, "folder");
                    if (folder != path && !workspaces.Any(item => item.Path == folder))
                        workspaces.Add(new("folder:" + folder, RequiredString(recent, "displayName"), folder));
                }
            }
        }
        var identity = agent.ValueKind == JsonValueKind.Object && agent.TryGetProperty("identity", out var value)
            && value.ValueKind == JsonValueKind.Object ? value : default;
        var name = OptionalString(identity, "name") ?? OptionalString(agent, "name") ?? agentId;
        return new(epoch, agentId, ParseEnvironments(environments, ReadRuntime(agents, agentId)), workspace, workspaces.ToArray(),
            name, OptionalString(identity, "emoji"), OptionalString(identity, "avatarUrl") ?? OptionalString(identity, "avatar"));
    }

    internal sealed record RuntimeRequirement(
        string? Id = null, bool Supported = true, bool ConsumesWorkerSlot = true, bool RequiresCommand = false);

    private static RuntimeRequirement ReadRuntime(JsonElement agents, string agentId)
    {
        var agent = RequiredArray(agents, "agents").EnumerateArray()
            .FirstOrDefault(item => OptionalString(item, "id") == agentId);
        if (agent.ValueKind != JsonValueKind.Object || !agent.TryGetProperty("agentRuntime", out var runtime))
            return new();
        var supported = !runtime.TryGetProperty("devicePlacementSupported", out var allowed) || allowed.ValueKind != JsonValueKind.False;
        var consumesSlot = true;
        var requiresCommand = false;
        if (runtime.TryGetProperty("devicePlacement", out var placement))
        {
            consumesSlot = !placement.TryGetProperty("consumesWorkerSlot", out var consumes) || consumes.ValueKind != JsonValueKind.False;
            requiresCommand = placement.TryGetProperty("requiredNodeCommands", out var commands) &&
                commands.ValueKind == JsonValueKind.Array && commands.GetArrayLength() > 0;
        }
        return new(OptionalString(runtime, "id"), supported, consumesSlot, requiresCommand);
    }

    private static object EnvironmentParameters(RuntimeRequirement runtime) =>
        runtime.Id is { } id ? new Dictionary<string, object> { ["runtimeId"] = id } : new Dictionary<string, object>();

    internal static ChatEnvironment[] ParseEnvironments(JsonElement response, RuntimeRequirement? runtime = null) =>
        RequiredArray(response, "environments").EnumerateArray()
            .Where(item => OptionalString(item, "type") == "local" ||
                OptionalString(item, "type") == "node" &&
                RequiredString(item, "id") is { Length: > 5 } id && id.StartsWith("node:", StringComparison.Ordinal))
            .Select(item =>
            {
                var id = RequiredString(item, "id");
                var local = RequiredString(item, "type") == "local";
                var unavailable = OptionalString(item, "status") != "available"
                    ? ChatEnvironmentUnavailable.Offline
                    : ChatEnvironmentUnavailable.None;
                string? detail = null;
                if (!local && unavailable == ChatEnvironmentUnavailable.None)
                {
                    if (!item.TryGetProperty("sessionHost", out var hosting) || hosting.ValueKind != JsonValueKind.True)
                        unavailable = ChatEnvironmentUnavailable.HostingDisabled;
                    else if ((runtime?.ConsumesWorkerSlot ?? true) &&
                        (!item.TryGetProperty("workerSlots", out var slots) ||
                         !slots.TryGetProperty("available", out var available) || !available.TryGetInt32(out _)))
                        unavailable = ChatEnvironmentUnavailable.CapacityUnknown;
                    else if ((runtime?.ConsumesWorkerSlot ?? true) &&
                        item.GetProperty("workerSlots").GetProperty("available").GetInt32() <= 0)
                        unavailable = ChatEnvironmentUnavailable.NoCapacity;
                    if (runtime?.RequiresCommand == true &&
                        (!item.TryGetProperty("requiredNodeCommand", out var command) ||
                         OptionalString(command, "state") != "invocable"))
                    {
                        unavailable = ChatEnvironmentUnavailable.CommandUnavailable;
                        detail = OptionalString(command, "message");
                    }
                    if (runtime?.Supported == false)
                        unavailable = ChatEnvironmentUnavailable.RuntimeUnsupported;
                }
                if (item.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
                {
                    foreach (var issue in issues.EnumerateArray())
                    {
                        if (OptionalString(issue, "code") is "update-required" or "worker-host-unavailable")
                        {
                            unavailable = ChatEnvironmentUnavailable.UpdateRequired;
                            detail = OptionalString(issue, "message");
                            break;
                        }
                    }
                }
                return new ChatEnvironment(id, OptionalString(item, "label") ?? id, local, unavailable, detail);
            }).ToArray();

    private static void RequireSessionResult(JsonElement response, string key)
    {
        if (OptionalString(response, "key") != key ||
            response.TryGetProperty("ok", out var ok) && ok.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("The Gateway did not confirm the requested session location. No message was sent.");
    }

    private static JsonElement RequiredArray(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var array) &&
        array.ValueKind == JsonValueKind.Array
            ? array : throw new InvalidOperationException($"The Gateway returned an invalid {name} catalog.");

    private static string RequiredString(JsonElement value, string name) =>
        OptionalString(value, name) ?? throw new InvalidOperationException($"The Gateway response is missing {name}.");

    private static string? OptionalString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString() : null;

    public static string FolderName(string path) =>
        path.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault() is { Length: > 0 } name ? name : path;
}
