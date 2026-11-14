// <summary>
// Gives the connected Gateway's file-transfer plugin an allow-all policy entry for this node, so file requests
// reach the node and the Node Sandbox (MXC) decides what is accessible. The Gateway policy is deny-by-default,
// so without this entry every file request fails before reaching the node.
// Usage:
//   var sync = new GatewayFileAccessPolicySync(getClient, getSnapshot, nodeEnforcesSandbox, logger);
//   sync.RequestSync(); // whenever the connection state changes.
// </summary>
using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.FileTransfer;
using OpenClawTray.Presentation;

namespace OpenClawTray.Services;

internal enum GatewayFileAccessSyncStatus
{
    NotApplicable,
    Synced,
    NeedsConfigAccess,
    MigrationRequired,
    PendingApplication,
    Failed,
}

/// <summary>
/// Single-flight, latest-wins writer of <c>plugins.entries.file-transfer.config.nodes.&lt;nodeId&gt;</c>.
/// Each pass reads fresh config, writes only on the operator connection epoch it started on, and is
/// skipped once confirmed for that Gateway, node and connection. Failures are reported through
/// <see cref="Status"/> and retried on the next connection, never in a loop.
/// </summary>
internal sealed class GatewayFileAccessPolicySync
{
    private const string RequestTimeoutVariable = "OPENCLAW_GATEWAY_FILE_POLICY_TIMEOUT_MS";
    private const int DefaultRequestTimeoutMs = 15_000;
    // config.patch waits for hot-reload application; a Store Gateway reloading its plugins took over 15 s.
    private const string WriteTimeoutVariable = "OPENCLAW_GATEWAY_FILE_POLICY_WRITE_TIMEOUT_MS";
    private const int DefaultWriteTimeoutMs = 60_000;
    // Plugin reloads can defer behind active work, so application is confirmed by bounded polling.
    private const string ApplyWaitVariable = "OPENCLAW_GATEWAY_FILE_POLICY_APPLY_WAIT_MS";
    private const int DefaultApplyWaitMs = 60_000;
    private static readonly TimeSpan ApplyPollInterval = TimeSpan.FromSeconds(2);
    // A failed Sandbox probe is retried by NodeService after a few seconds, and no connection event follows.
    private static readonly TimeSpan UnknownSandboxRetryInterval = TimeSpan.FromSeconds(5);
    private const int MaxUnknownSandboxRetries = 12;

    private readonly Func<TimeSpan, Task> _delay;
    private readonly Func<IOperatorGatewayClient?> _getClient;
    private readonly Func<GatewayConnectionSnapshot> _getSnapshot;
    private readonly Func<bool?> _nodeEnforcesSandbox;
    private readonly IOpenClawLogger _logger;
    private readonly object _gate = new();
    private bool _pending;
    private bool _running;
    private Task _drain = Task.CompletedTask;
    private string? _attemptKey;
    private IOperatorGatewayClient? _attemptClient;
    private GatewayFileAccessSyncStatus? _attemptStatus;
    private GatewayFileAccessSyncStatus _status;
    private string? _unknownRetryKey;
    private int _unknownRetries;
    private bool _unknownRetryScheduled;

    /// <param name="nodeEnforcesSandbox">True when file commands run inside the Node Sandbox, false when they
    /// run uncontained, null when that is not known yet (node service not ready or a failed probe).</param>
    public GatewayFileAccessPolicySync(
        Func<IOperatorGatewayClient?> getClient,
        Func<GatewayConnectionSnapshot> getSnapshot,
        Func<bool?> nodeEnforcesSandbox,
        IOpenClawLogger logger,
        Func<TimeSpan, Task>? delay = null)
    {
        _getClient = getClient ?? throw new ArgumentNullException(nameof(getClient));
        _getSnapshot = getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));
        _nodeEnforcesSandbox = nodeEnforcesSandbox ?? throw new ArgumentNullException(nameof(nodeEnforcesSandbox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _delay = delay ?? (interval => Task.Delay(interval));
    }

    /// <summary>Latest outcome, for diagnostics and tests; non-success outcomes are logged, not shown in the UI.</summary>
    public GatewayFileAccessSyncStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>Queues a pass; returns a task that completes once no pass is pending.</summary>
    public Task RequestSync()
    {
        lock (_gate)
        {
            _pending = true;
            if (!_running)
            {
                _running = true;
                _drain = Task.Run(DrainAsync);
            }
            return _drain;
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_pending)
                {
                    _running = false;
                    return;
                }
                _pending = false;
            }
            GatewayFileAccessSyncStatus? status;
            try { status = await SyncOnceAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.Warn($"[file-policy] Gateway file access sync failed ({ex.GetType().Name})");
                status = GatewayFileAccessSyncStatus.Failed;
            }
            if (status is { } value)
                SetStatus(value);
        }
    }

    /// <summary>Returns null when the connection changed mid-pass; the new connection queues its own pass.</summary>
    private async Task<GatewayFileAccessSyncStatus?> SyncOnceAsync()
    {
        var snapshot = _getSnapshot();
        if (snapshot is not
            {
                OperatorState: RoleConnectionState.Connected,
                NodeState: RoleConnectionState.Connected,
                NodePairingStatus: PairingStatus.Paired,
                NodeDeviceId: { Length: > 0 } nodeId,
                GatewayId: { Length: > 0 } gatewayId,
            })
            return GatewayFileAccessSyncStatus.NotApplicable;
        if (_getClient() is not { IsConnectedToGateway: true } client ||
            client.SessionMutationConnectionEpoch is not long epoch)
            return GatewayFileAccessSyncStatus.NotApplicable;
        // config.patch is an admin control-plane write; operator.write alone would only fail remotely.
        if (!OperatorScopeHelper.HasAdminScope(client.GrantedOperatorScopes))
            return GatewayFileAccessSyncStatus.NeedsConfigAccess;

        var key = string.Join('\n', gatewayId, nodeId, epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        lock (_gate)
        {
            // One outcome per connection, so repeated state changes never replay a write. Epochs are
            // client-local, so the outcome is also bound to the client instance.
            if (key == _attemptKey && ReferenceEquals(client, _attemptClient) && _attemptStatus is { } settled)
                return settled;
        }
        // Allow-all is safe only because the Sandbox enforces access. Uncontained compatibility execution
        // keeps the Gateway's deny-by-default policy.
        switch (_nodeEnforcesSandbox())
        {
            case null:
                ScheduleUnknownSandboxRetry(key);
                return GatewayFileAccessSyncStatus.NotApplicable;
            case false:
                _logger.Info("[file-policy] File commands run uncontained on this Windows version; the Gateway keeps its own file policy");
                return Settle(GatewayFileAccessSyncStatus.NotApplicable);
        }
        try
        {
            return await ApplyAsync(client, epoch, gatewayId, nodeId).ConfigureAwait(false) is { } status ? Settle(status) : null;
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(client, _getClient()) || !client.IsConnectedToGateway || client.SessionMutationConnectionEpoch != epoch)
                return null;
            // An uncertain write stays settled too: the next connection reads it back instead of replaying it.
            _logger.Warn($"[file-policy] Gateway file access sync failed ({ex.GetType().Name})");
            return Settle(GatewayFileAccessSyncStatus.Failed);
        }

        GatewayFileAccessSyncStatus Settle(GatewayFileAccessSyncStatus status)
        {
            lock (_gate)
            {
                _attemptKey = key;
                _attemptClient = client;
                _attemptStatus = status;
            }
            return status;
        }
    }

    /// <summary>
    /// A failed Sandbox probe gets no connection event when a later probe succeeds, so the pass is requeued a
    /// bounded number of times per connection. Each requeue rechecks the connection and the settled outcome.
    /// </summary>
    private void ScheduleUnknownSandboxRetry(string key)
    {
        lock (_gate)
        {
            if (_unknownRetryKey != key)
            {
                _unknownRetryKey = key;
                _unknownRetries = 0;
            }
            if (_unknownRetryScheduled || _unknownRetries >= MaxUnknownSandboxRetries)
                return;
            _unknownRetries++;
            _unknownRetryScheduled = true;
        }
        _ = Task.Run(async () =>
        {
            try { await _delay(UnknownSandboxRetryInterval).ConfigureAwait(false); }
            finally { lock (_gate) _unknownRetryScheduled = false; }
            _ = RequestSync();
        });
    }

    private async Task<GatewayFileAccessSyncStatus?> ApplyAsync(IOperatorGatewayClient client, long epoch, string gatewayId, string nodeId)
    {
        bool IsCurrent() =>
            ReferenceEquals(client, _getClient()) && client.IsConnectedToGateway &&
            client.SessionMutationConnectionEpoch == epoch &&
            _getSnapshot() is var current && current.GatewayId == gatewayId && current.NodeDeviceId == nodeId;

        var timeoutMs = RequestTimeoutMs();
        var readBackOnly = false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            JsonElement response;
            string? displayName = null;
            try
            {
                response = await client.SendWizardRequestOnConnectionAsync("config.get", new { }, epoch, timeoutMs)
                    .ConfigureAwait(false);
                if (GatewayConfigEnvelope.TryRead(response, out var read, out _) &&
                    GatewayFileAccessPolicy.NeedsDisplayName(read, nodeId))
                {
                    // The Gateway matches its own (possibly renamed) display name, not the one we advertise.
                    var nodes = await client.SendWizardRequestOnConnectionAsync("node.list", new { }, epoch, timeoutMs)
                        .ConfigureAwait(false);
                    displayName = GatewayDisplayName(nodes, nodeId);
                }
            }
            catch (Exception) when (!IsCurrent()) { return null; }
            if (!IsCurrent()) return null;
            if (!GatewayConfigEnvelope.TryRead(response, out var config, out var baseHash))
                return GatewayFileAccessSyncStatus.Failed;

            var plan = GatewayFileAccessPolicy.Plan(config, nodeId, displayName);
            switch (plan.Kind)
            {
                case GatewayFileAccessPlanKind.NoChange:
                    // Saved is not applied: the plugin reads the running config, not the file.
                    return await ConfirmAppliedAsync(response).ConfigureAwait(false);
                case GatewayFileAccessPlanKind.MigrationRequired:
                    return GatewayFileAccessSyncStatus.MigrationRequired;
                case GatewayFileAccessPlanKind.UnsupportedPolicyVersion:
                    _logger.Warn("[file-policy] The Gateway's file-transfer policyVersion is not one Companion supports; leaving it unchanged");
                    return GatewayFileAccessSyncStatus.Failed;
                case GatewayFileAccessPlanKind.InvalidNode:
                    return GatewayFileAccessSyncStatus.Failed;
            }
            // A rejected write is not replayed; the read-back only recognizes one that was persisted.
            if (readBackOnly)
                return GatewayFileAccessSyncStatus.Failed;

            ConfigPatchResult result;
            try
            {
                result = await client.PatchConfigOnConnectionAsync(
                        JsonSerializer.SerializeToElement(plan.Patch), baseHash, epoch, WriteTimeoutMs())
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!IsCurrent()) { return null; }
            if (result.Ok)
            {
                // An acknowledgment can precede application (for example with hot reload off).
                var confirmed = await ConfirmAppliedAsync(null).ConfigureAwait(false);
                if (confirmed is not null && IsCurrent())
                {
                    try { await client.RequestConfigAsync().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.Warn($"[file-policy] Config refresh after the write failed ({ex.GetType().Name})"); }
                }
                return confirmed;
            }
            if (!IsCurrent()) return null;
            if (attempt == 1)
                return GatewayFileAccessSyncStatus.Failed;
            // A timeout or an UNAVAILABLE "persisted but not applied" reply may have committed the write, so the
            // second pass reads it back (NoChange, then application check). Only a stale hash is re-planned.
            readBackOnly = !result.LooksLikeStaleBaseHash;
        }
        return GatewayFileAccessSyncStatus.Failed;

        // Bounded polling of the authoritative config.get until the running Gateway reports the saved
        // revision as applied, and that same snapshot still holds this node's entry. Null means the
        // connection changed; the new connection queues its own pass.
        async Task<GatewayFileAccessSyncStatus?> ConfirmAppliedAsync(JsonElement? latest)
        {
            var started = Stopwatch.GetTimestamp();
            var applyWait = TimeSpan.FromMilliseconds(ReadPositive(ApplyWaitVariable, DefaultApplyWaitMs));
            for (int poll = 0; ; poll++)
            {
                if (latest is { } envelope && IsApplied(envelope) is var applied && applied != false)
                {
                    if (applied is null)
                        _logger.Info("[file-policy] Gateway does not report its applied config revision; treating saved as applied");
                    // A concurrent writer may have replaced the entry; report it rather than overwrite it.
                    if (!GatewayConfigEnvelope.TryRead(envelope, out var appliedConfig, out _) ||
                        GatewayFileAccessPolicy.Plan(appliedConfig, nodeId).Kind != GatewayFileAccessPlanKind.NoChange)
                    {
                        _logger.Warn("[file-policy] The applied Gateway config no longer allows this node's file requests");
                        return GatewayFileAccessSyncStatus.Failed;
                    }
                    _logger.Info("[file-policy] Gateway forwards this node's file requests; the Node Sandbox decides access");
                    return GatewayFileAccessSyncStatus.Synced;
                }
                if (poll == 0 && latest is { } first)
                    _logger.Info($"[file-policy] Gateway config saved but not yet applied (saved={Revision(first, "configRevisionHash")} applied={Revision(first, "appliedConfigHash")})");
                var remaining = applyWait - Stopwatch.GetElapsedTime(started);
                if (poll >= MaxApplyPolls() || remaining <= TimeSpan.Zero)
                    return NotYetApplied(poll);
                if (latest is not null)
                {
                    await _delay(remaining < ApplyPollInterval ? remaining : ApplyPollInterval).ConfigureAwait(false);
                    remaining = applyWait - Stopwatch.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero)
                        return NotYetApplied(poll);
                }
                if (!IsCurrent()) return null;
                var pollTimeoutMs = (int)Math.Clamp(remaining.TotalMilliseconds, 1, timeoutMs);
                try
                {
                    latest = await client.SendWizardRequestOnConnectionAsync("config.get", new { }, epoch, pollTimeoutMs)
                        .ConfigureAwait(false);
                }
                catch (Exception) when (!IsCurrent()) { return null; }
                // The wait budget ran out mid-request; the write is still saved, only its application is unknown.
                catch (TimeoutException) { return NotYetApplied(poll + 1); }
                if (!IsCurrent()) return null;
            }

            GatewayFileAccessSyncStatus NotYetApplied(int checks)
            {
                _logger.Warn($"[file-policy] Gateway saved the file access policy but has not applied it after {checks} checks");
                return GatewayFileAccessSyncStatus.PendingApplication;
            }
        }
    }

    /// <summary>The Gateway policy matches only the node's <c>displayName</c> field, never name or label.</summary>
    private static string? GatewayDisplayName(JsonElement payload, string nodeId)
    {
        var nodes = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("nodes", out var nested) ? nested : payload;
        if (nodes.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The Gateway returned an invalid node list.");
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind == JsonValueKind.Object &&
                node.TryGetProperty("nodeId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == nodeId)
            {
                return node.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(name.GetString()) ? name.GetString() : null;
            }
        }
        return null;
    }

    private static string Revision(JsonElement envelope, string name)
    {
        if (!envelope.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return "none";
        var text = value.GetString()!;
        return text.Length > 12 ? text[^12..] : text;
    }

    /// <summary>
    /// True when the saved revision is the one the running Gateway applied, false when it is not yet
    /// applied, and null when this Gateway does not report its applied revision.
    /// </summary>
    internal static bool? IsApplied(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("configRevisionHash", out var saved) || saved.ValueKind != JsonValueKind.String ||
            !envelope.TryGetProperty("appliedConfigHash", out var applied))
            return null;
        return applied.ValueKind == JsonValueKind.String &&
            string.Equals(applied.GetString(), saved.GetString(), StringComparison.Ordinal);
    }

    private void SetStatus(GatewayFileAccessSyncStatus status)
    {
        lock (_gate)
        {
            if (_status == status) return;
            _status = status;
        }
        if (status is not GatewayFileAccessSyncStatus.Synced and not GatewayFileAccessSyncStatus.NotApplicable)
            _logger.Warn($"[file-policy] Gateway file access sync status={status}");
    }

    private int RequestTimeoutMs() => ReadPositive(RequestTimeoutVariable, DefaultRequestTimeoutMs);
    private int WriteTimeoutMs() => ReadPositive(WriteTimeoutVariable, DefaultWriteTimeoutMs);
    private int MaxApplyPolls() =>
        Math.Max(1, ReadPositive(ApplyWaitVariable, DefaultApplyWaitMs) / (int)ApplyPollInterval.TotalMilliseconds);

    private int ReadPositive(string variable, int defaultValue)
    {
        string? configured = Environment.GetEnvironmentVariable(variable);
        if (configured is null)
            return defaultValue;
        if (int.TryParse(configured, out int milliseconds) && milliseconds > 0)
            return milliseconds;
        _logger.Warn($"{variable} must be a positive integer; using the default.");
        return defaultValue;
    }
}
