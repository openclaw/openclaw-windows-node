using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

internal enum WorkspaceGatewaySource
{
    AgentWorkspace,
    SessionFiles,
    LegacyAgentFiles,
    Unsupported
}

internal static class WorkspaceScopeDisclosure
{
    public static string? ResourceKeyForList(WorkspaceGatewaySource source) =>
        source switch
        {
            WorkspaceGatewaySource.SessionFiles => "WorkspacePage_LimitedScopeMessage",
            WorkspaceGatewaySource.LegacyAgentFiles => "WorkspacePage_LegacyAgentFilesScopeMessage",
            _ => null
        };
}

internal sealed class WorkspaceScopeDisclosureRequest
{
    private readonly object _sync = new();
    private bool _isPending = true;
    private WorkspaceGatewaySource? _queuedSource;

    public bool TryQueue(WorkspaceGatewaySource source)
    {
        if (WorkspaceScopeDisclosure.ResourceKeyForList(source) is null)
            return false;

        lock (_sync)
        {
            if (!_isPending)
                return false;

            _queuedSource = source;
            return true;
        }
    }

    public bool CanApply(WorkspaceGatewaySource source)
    {
        lock (_sync)
        {
            return _isPending && _queuedSource == source;
        }
    }

    public void Complete()
    {
        lock (_sync)
        {
            _isPending = false;
            _queuedSource = null;
        }
    }
}

internal sealed record WorkspaceListGatewayResult(
    WorkspaceGatewaySource Source,
    AgentWorkspaceListResult? AgentWorkspace = null,
    SessionFileList? SessionFiles = null,
    JsonElement? LegacyPayload = null);

internal sealed record WorkspaceFileGatewayResult(
    WorkspaceGatewaySource Source,
    AgentWorkspaceGetResult? AgentWorkspace = null,
    SessionFileContent? SessionFile = null,
    JsonElement? LegacyPayload = null);

internal static class WorkspaceSessionResolver
{
    public static string? Resolve(
        string? selectedAgentId,
        IEnumerable<SessionInfo>? sessions,
        string? canonicalMainSessionKey)
    {
        var agentId = string.IsNullOrWhiteSpace(selectedAgentId)
            ? "main"
            : selectedAgentId.Trim();
        var snapshot = sessions ?? Array.Empty<SessionInfo>();

        if (string.Equals(agentId, "main", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(canonicalMainSessionKey))
        {
            return canonicalMainSessionKey;
        }

        return snapshot
            .Where(session =>
                !string.IsNullOrWhiteSpace(session.Key) &&
                string.Equals(
                    session.AgentId,
                    agentId,
                    StringComparison.Ordinal) &&
                (session.IsMain ||
                 SessionActionPlanner.IsMainSessionKeyShape(session.Key)))
            .OrderBy(session => session.Key, StringComparer.Ordinal)
            .Select(session => session.Key)
            .FirstOrDefault();
    }
}

internal sealed class WorkspaceSessionReloadGate
{
    private string? _resolvedSessionKey;

    public bool DependsOnSessionKey { get; private set; }

    public void RecordCompletedLoad(
        WorkspaceGatewaySource source,
        bool fallbackKeyWasResolved,
        string? resolvedSessionKey)
    {
        _resolvedSessionKey = Normalize(resolvedSessionKey);
        DependsOnSessionKey =
            source == WorkspaceGatewaySource.SessionFiles ||
            (fallbackKeyWasResolved &&
             _resolvedSessionKey is null &&
             source is WorkspaceGatewaySource.LegacyAgentFiles or WorkspaceGatewaySource.Unsupported);
    }

    public bool ShouldReload(string? resolvedSessionKey)
    {
        if (!DependsOnSessionKey)
            return false;

        var normalized = Normalize(resolvedSessionKey);
        if (string.Equals(_resolvedSessionKey, normalized, StringComparison.Ordinal))
            return false;

        _resolvedSessionKey = normalized;
        return true;
    }

    public void Reset()
    {
        DependsOnSessionKey = false;
        _resolvedSessionKey = null;
    }

    private static string? Normalize(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : key;
}

internal interface IWorkspaceGatewayApi
{
    Task<AgentWorkspaceListResult> ListAgentWorkspaceAsync(
        AgentWorkspaceListRequest request,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default);

    Task<AgentWorkspaceGetResult> GetAgentWorkspaceFileAsync(
        AgentWorkspaceGetRequest request,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default);

    Task<SessionFileList> ListSessionFilesAsync(
        string key,
        string? path = null,
        string? search = null,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default);

    Task<SessionFileContent> GetSessionFileAsync(
        string key,
        string path,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default);

    Task<LegacyAgentFilesResponse> ListLegacyAgentFilesAsync(
        string agentId,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default);

    Task<LegacyAgentFilesResponse> GetLegacyAgentFileAsync(
        string agentId,
        string name,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default);
}

internal sealed class WorkspaceGatewayApi : IWorkspaceGatewayApi
{
    private readonly IOperatorGatewayClient _client;

    public WorkspaceGatewayApi(IOperatorGatewayClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<AgentWorkspaceListResult> ListAgentWorkspaceAsync(
        AgentWorkspaceListRequest request,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default) =>
        _client.ListAgentWorkspaceAsync(request, timeoutMs, cancellationToken);

    public Task<AgentWorkspaceGetResult> GetAgentWorkspaceFileAsync(
        AgentWorkspaceGetRequest request,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default) =>
        _client.GetAgentWorkspaceFileAsync(request, timeoutMs, cancellationToken);

    public Task<SessionFileList> ListSessionFilesAsync(
        string key,
        string? path = null,
        string? search = null,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default) =>
        _client.ListSessionFilesAsync(key, path, search, timeoutMs, cancellationToken);

    public Task<SessionFileContent> GetSessionFileAsync(
        string key,
        string path,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default) =>
        _client.GetSessionFileAsync(key, path, timeoutMs, cancellationToken);

    public Task<LegacyAgentFilesResponse> ListLegacyAgentFilesAsync(
        string agentId,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default) =>
        _client.ListLegacyAgentFilesAsync(agentId, timeoutMs, cancellationToken);

    public Task<LegacyAgentFilesResponse> GetLegacyAgentFileAsync(
        string agentId,
        string name,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default) =>
        _client.GetLegacyAgentFileAsync(agentId, name, timeoutMs, cancellationToken);
}

internal sealed class WorkspaceGatewayCoordinator
{
    internal const int PageLimit = 250;

    private readonly IWorkspaceGatewayApi _gateway;

    public WorkspaceGatewayCoordinator(IWorkspaceGatewayApi gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public async Task<WorkspaceListGatewayResult> ListAsync(
        string agentId,
        string path,
        string? search,
        Func<string?> resolveSessionKey,
        Action? onLegacyFallback = null,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(agentId))
            throw new ArgumentException("Agent id is required", nameof(agentId));
        ArgumentNullException.ThrowIfNull(resolveSessionKey);

        var primary = await ListAllAgentWorkspacePagesAsync(
            agentId,
            path ?? string.Empty,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
        if (primary.IsSupported)
            return new WorkspaceListGatewayResult(
                WorkspaceGatewaySource.AgentWorkspace,
                AgentWorkspace: primary);

        var sessionKey = resolveSessionKey();
        if (!string.IsNullOrWhiteSpace(sessionKey))
        {
            var session = await _gateway.ListSessionFilesAsync(
                sessionKey,
                path,
                string.IsNullOrWhiteSpace(search) ? null : search,
                timeoutMs,
                cancellationToken).ConfigureAwait(false);
            if (session.IsSupported)
            {
                return new WorkspaceListGatewayResult(
                    WorkspaceGatewaySource.SessionFiles,
                    SessionFiles: session);
            }
        }

        onLegacyFallback?.Invoke();
        var legacy = await _gateway.ListLegacyAgentFilesAsync(
            agentId,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
        return legacy.IsSupported && legacy.Payload.HasValue
            ? new WorkspaceListGatewayResult(
                WorkspaceGatewaySource.LegacyAgentFiles,
                LegacyPayload: legacy.Payload)
            : new WorkspaceListGatewayResult(WorkspaceGatewaySource.Unsupported);
    }

    public async Task<WorkspaceFileGatewayResult> GetAsync(
        string agentId,
        string path,
        Func<string?> resolveSessionKey,
        Action? onLegacyFallback = null,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(agentId))
            throw new ArgumentException("Agent id is required", nameof(agentId));
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("File path is required", nameof(path));
        ArgumentNullException.ThrowIfNull(resolveSessionKey);

        var primary = await _gateway.GetAgentWorkspaceFileAsync(
            new AgentWorkspaceGetRequest { AgentId = agentId, Path = path },
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
        if (primary.IsSupported)
        {
            return new WorkspaceFileGatewayResult(
                WorkspaceGatewaySource.AgentWorkspace,
                AgentWorkspace: primary);
        }

        var sessionKey = resolveSessionKey();
        if (!string.IsNullOrWhiteSpace(sessionKey))
        {
            var session = await _gateway.GetSessionFileAsync(
                sessionKey,
                path,
                timeoutMs,
                cancellationToken).ConfigureAwait(false);
            if (session.IsSupported)
            {
                return new WorkspaceFileGatewayResult(
                    WorkspaceGatewaySource.SessionFiles,
                    SessionFile: session);
            }
        }

        onLegacyFallback?.Invoke();
        var legacy = await _gateway.GetLegacyAgentFileAsync(
            agentId,
            path,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
        return legacy.IsSupported && legacy.Payload.HasValue
            ? new WorkspaceFileGatewayResult(
                WorkspaceGatewaySource.LegacyAgentFiles,
                LegacyPayload: legacy.Payload)
            : new WorkspaceFileGatewayResult(WorkspaceGatewaySource.Unsupported);
    }

    private async Task<AgentWorkspaceListResult> ListAllAgentWorkspacePagesAsync(
        string agentId,
        string path,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var entries = new List<AgentWorkspaceEntry>();
        long requestedOffset = 0;
        AgentWorkspaceListResult? latest = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _gateway.ListAgentWorkspaceAsync(
                new AgentWorkspaceListRequest
                {
                    AgentId = agentId,
                    Path = path,
                    Offset = requestedOffset,
                    Limit = PageLimit
                },
                timeoutMs,
                cancellationToken).ConfigureAwait(false);

            if (!page.IsSupported)
                return page;
            if (page.Offset != requestedOffset)
                throw new InvalidOperationException("Agent workspace pagination returned an unexpected offset.");

            latest = page;
            var pageEntries = page.Entries ?? Array.Empty<AgentWorkspaceEntry>();
            entries.AddRange(pageEntries);

            var nextOffset = page.Offset + pageEntries.Count;
            if (nextOffset >= page.TotalEntries)
                break;
            if (pageEntries.Count == 0 || nextOffset <= requestedOffset)
                throw new InvalidOperationException("Agent workspace pagination did not advance.");

            requestedOffset = nextOffset;
        }

        return new AgentWorkspaceListResult
        {
            AgentId = latest!.AgentId,
            Path = latest.Path,
            ParentPath = latest.ParentPath,
            Entries = entries,
            TotalEntries = latest.TotalEntries,
            Offset = 0,
            IsSupported = true
        };
    }
}
