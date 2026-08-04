using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Reproduces the exact effect order of the former <c>App.OnSettingsSaved</c>: chat tool-call
/// visibility, impact classification against the last detached snapshot, browser proxy sync,
/// impact log, sandbox risk notification, the impact-driven reconnect step, MCP runtime, global
/// hotkey, auto-start/telemetry, then a UI-thread-marshaled surface refresh. A FIFO single-drainer
/// queue serializes concurrent and reentrant calls without holding its lock while effects run.
/// </summary>
internal sealed class SettingsChangeCoordinator : ISettingsChangeCoordinator
{
    private readonly ISettingsConnectionEffects _connectionEffects;
    private readonly ISettingsRuntimeEffects _runtimeEffects;
    private readonly ISettingsSurfaceEffects _surfaceEffects;
    private readonly object _gate = new();
    private readonly Queue<PendingRequest> _pendingRequests = new();

    private ConnectionSettingsSnapshot? _previousSnapshot;
    private long? _lastAppliedVersion;
    private bool _draining;
    private bool _disposed;

    private sealed record PendingRequest(
        SettingsChangeRequest Request,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion);

    public SettingsChangeCoordinator(
        ISettingsConnectionEffects connectionEffects,
        ISettingsRuntimeEffects runtimeEffects,
        ISettingsSurfaceEffects surfaceEffects,
        SettingsData? initialSnapshot = null)
    {
        _connectionEffects = connectionEffects ?? throw new ArgumentNullException(nameof(connectionEffects));
        _runtimeEffects = runtimeEffects ?? throw new ArgumentNullException(nameof(runtimeEffects));
        _surfaceEffects = surfaceEffects ?? throw new ArgumentNullException(nameof(surfaceEffects));
        _previousSnapshot = initialSnapshot?.ToConnectionSnapshot();
    }

    public Task ApplyAsync(SettingsChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        PendingRequest pending;
        var startDraining = false;
        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;

            pending = new PendingRequest(
                request,
                cancellationToken,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _pendingRequests.Enqueue(pending);
            if (!_draining)
            {
                _draining = true;
                startDraining = true;
            }
        }

        if (startDraining)
            DrainQueue();

        return pending.Completion.Task;
    }

    private void DrainQueue()
    {
        while (true)
        {
            PendingRequest pending;
            lock (_gate)
            {
                if (_pendingRequests.Count == 0)
                {
                    _draining = false;
                    return;
                }

                pending = _pendingRequests.Dequeue();
            }

            if (pending.CancellationToken.IsCancellationRequested)
            {
                pending.Completion.TrySetCanceled(pending.CancellationToken);
                continue;
            }

            try
            {
                ApplyCore(pending.Request);
                pending.Completion.TrySetResult();
            }
            catch (OperationCanceledException) when (pending.CancellationToken.IsCancellationRequested)
            {
                pending.Completion.TrySetCanceled(pending.CancellationToken);
            }
            catch (Exception ex)
            {
                pending.Completion.TrySetException(ex);
            }
        }
    }

    private void ApplyCore(SettingsChangeRequest request)
    {
        // Compare at dequeue time against the last fully successful request. A later save with a
        // new persisted version still runs even when its values are equal, and null versions are
        // never deduplicated.
        if (request.PersistedVersion is { } version && version == _lastAppliedVersion)
            return;

        _runtimeEffects.ApplyChatToolCallVisibility(request.Current);

        var currentSnapshot = request.Current.ToConnectionSnapshot();
        var impact = SettingsChangeClassifier.Classify(_previousSnapshot, currentSnapshot);

        _connectionEffects.SyncActiveGatewayBrowserProxyForward(request.Current);
        Logger.Info($"[SETTINGS] Change impact: {impact}");
        _runtimeEffects.PublishSandboxRiskNotification();

        switch (ToReconnectPlan(impact))
        {
            case SettingsReconnectPlan.Full:
                _connectionEffects.PrepareFullReconnect(request.Current);
                _connectionEffects.ReconnectWithSyncedBrowserProxyForward();
                break;

            case SettingsReconnectPlan.Node:
            case SettingsReconnectPlan.CapabilityReload:
                _connectionEffects.ReconnectWithSyncedBrowserProxyForward();
                break;

            case SettingsReconnectPlan.None:
                break;
        }

        _runtimeEffects.ApplyMcpRuntime(request.Current);
        _runtimeEffects.ApplyGlobalHotkey(request.Current);
        _runtimeEffects.ApplyAutoStartAndTelemetry(request.Current);
        _surfaceEffects.ApplyOnUiThread(request.Current);

        _previousSnapshot = currentSnapshot;
        _lastAppliedVersion = request.PersistedVersion;
    }

    private static SettingsReconnectPlan ToReconnectPlan(SettingsChangeImpact impact) => impact switch
    {
        SettingsChangeImpact.FullReconnectRequired or SettingsChangeImpact.OperatorReconnectRequired
            => SettingsReconnectPlan.Full,
        SettingsChangeImpact.NodeReconnectRequired => SettingsReconnectPlan.Node,
        SettingsChangeImpact.CapabilityReload => SettingsReconnectPlan.CapabilityReload,
        _ => SettingsReconnectPlan.None,
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }
}
