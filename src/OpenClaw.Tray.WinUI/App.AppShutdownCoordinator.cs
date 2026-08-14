using Microsoft.Toolkit.Uwp.Notifications;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenClawTray;

/// <summary>
/// Builds the immutable <see cref="AppShutdownPlan"/> from the App-owned resources still present
/// at exit time. App owns the resource fields and their null-timing; <see cref="AppShutdownCoordinator"/>
/// owns only the first-wins scheduling and per-step log/catch/continue mechanics. All field capture
/// and conditional inclusion below runs synchronously so it completes atomically on the UI thread
/// before the coordinator's first internal await, matching the exactly-once guarantee the former
/// <c>_isExiting</c> guard provided.
/// </summary>
public partial class App
{
    private readonly AppShutdownCoordinator _shutdownCoordinator = new();

    private void ExitApplication()
    {
        _ = ExitApplicationAsync();
    }

    private Task ExitApplicationAsync()
    {
        var plan = BuildShutdownPlan();
        return _shutdownCoordinator.ShutdownAsync(plan);
    }

    private AppShutdownPlan BuildShutdownPlan()
    {
        var steps = new List<AppShutdownStep>();

        // Detach the toast boundary, cancel every admitted activation, and drain the forwarded
        // listener before anything else, matching the former early deep-link-cancel touchpoint.
        var activationRouter = _activationRouter;
        steps.Add(new AppShutdownStep("activation router", async () =>
        {
            ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
            if (ReferenceEquals(_activationRouter, activationRouter))
                _activationRouter = null;
            if (activationRouter is not null)
                await activationRouter.DisposeAsync();
        }));

        steps.Add(new AppShutdownStep("global hotkey", () =>
        {
            _globalHotkey?.Dispose();
            _globalHotkey = null;
            return ValueTask.CompletedTask;
        }));

        // Stop chat first so provider event handlers cannot drain client-only queued prompts
        // while the gateway connection is shutting down.
        steps.Add(new AppShutdownStep("chat coordinator", () =>
        {
            _chatCoordinator?.Dispose();
            _chatCoordinator = null;
            return ValueTask.CompletedTask;
        }));

        // Dispose runtime services. Stop the auto-repair monitor BEFORE the connection manager so
        // an in-flight repair cannot drive a reconnect into a disposing manager.
        var autoRepairMonitor = _managedLocalAutoRepairMonitor;
        if (autoRepairMonitor is not null)
        {
            steps.Add(new AppShutdownStep("managed-local auto-repair monitor", async () =>
            {
                try
                {
                    await autoRepairMonitor.DisposeAsync();
                }
                finally
                {
                    if (ReferenceEquals(_managedLocalAutoRepairMonitor, autoRepairMonitor))
                        _managedLocalAutoRepairMonitor = null;
                }
            }));
        }

        var connectionManager = _connectionManager;
        if (connectionManager is not null)
        {
            steps.Add(new AppShutdownStep("gateway client", async () =>
            {
                try
                {
                    await connectionManager.DisposeAsync();
                }
                finally
                {
                    if (ReferenceEquals(_connectionManager, connectionManager))
                        _connectionManager = null;
                }
            }));
        }

        steps.Add(new AppShutdownStep("OpenTelemetry endpoint", () =>
        {
            _openTelemetryConnection?.Dispose();
            _openTelemetryConnection = null;
            return ValueTask.CompletedTask;
        }));

        var nodeService = _nodeService;
        if (nodeService is not null)
        {
            steps.Add(new AppShutdownStep("node service", async () =>
            {
                try
                {
                    await nodeService.DisposeAsync();
                }
                finally
                {
                    if (ReferenceEquals(_nodeService, nodeService))
                        _nodeService = null;
                }
            }));
        }

        var standaloneVoiceService = _standaloneVoiceService;
        if (standaloneVoiceService is not null)
        {
            steps.Add(new AppShutdownStep("standalone voice service", async () =>
            {
                try
                {
                    await standaloneVoiceService.DisposeAsync();
                }
                finally
                {
                    if (ReferenceEquals(_standaloneVoiceService, standaloneVoiceService))
                        _standaloneVoiceService = null;
                }
            }));
        }

        steps.Add(new AppShutdownStep("ssh tunnel service", () =>
        {
            _sshTunnelService?.Dispose();
            _sshTunnelService = null;
            return ValueTask.CompletedTask;
        }));

        steps.Add(new AppShutdownStep("pairing approval", () =>
        {
            _pairingApprovalPollTimer?.Stop();
            _pairingApprovalPollTimer = null;
            _pairingApprovalDialog?.Close();
            _pairingApprovalDialog = null;
            return ValueTask.CompletedTask;
        }));

        steps.Add(new AppShutdownStep("app state observers", () =>
        {
            if (_appState != null)
                _appState.PropertyChanged -= OnAppStateChanged;
            PermissionsRuntimeChanged = null;
            return ValueTask.CompletedTask;
        }));

        // Close windows explicitly for deterministic shutdown tracing. Null the field BEFORE
        // awaiting close so a queued Frame.Navigated callback during shutdown cannot resolve
        // against a window manager that is mid-disposal. The field stays populated through the
        // earlier steps (and BeginShutdown) exactly as it did before this extraction.
        var windowManager = _windowManager;
        if (windowManager is not null)
        {
            steps.Add(new AppShutdownStep("window manager", async () =>
            {
                _windowManager = null;
                await windowManager.CloseForShutdownAsync();
            }));
        }

        steps.Add(new AppShutdownStep("tray menu window", () =>
        {
            _trayController?.CloseMenuForShutdown();
            return ValueTask.CompletedTask;
        }));

        // Dispose the DI composition root. The container only owns the presentation
        // infrastructure it created (navigation scope manager + any open page-view-model scope).
        // App-owned services were registered as pre-built instances, so this does not re-dispose
        // them (no double-dispose). Null the field BEFORE awaiting disposal so a queued
        // Frame.Navigated callback during shutdown cannot resolve the page activator against a
        // disposing/disposed provider.
        var services = _services;
        if (services is not null)
        {
            steps.Add(new AppShutdownStep("service provider", async () =>
            {
                _services = null;
                await services.DisposeAsync();
            }));
        }

        // Dispose tray and mutex
        steps.Add(new AppShutdownStep("tray icon", () =>
        {
            _trayController?.Dispose();
            _trayController = null;
            return ValueTask.CompletedTask;
        }));

        steps.Add(new AppShutdownStep("single-instance mutex", () =>
        {
            _mutex?.Dispose();
            _mutex = null;
            return ValueTask.CompletedTask;
        }));

        return new AppShutdownPlan(
            BeginShutdown: () =>
            {
                _settingsChangeCoordinator = null;
                _windowManager?.BeginShutdown();
                _trayController?.BeginShutdown();
                Logger.Info("Application exiting");
            },
            Steps: steps,
            ExitApplication: Exit);
    }
}
