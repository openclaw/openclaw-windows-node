using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray;

/// <summary>
/// App's <see cref="ISettingsConnectionEffects"/>/<see cref="ISettingsRuntimeEffects"/>/
/// <see cref="ISettingsSurfaceEffects"/> implementations, and the single explicit post-save
/// trigger (<see cref="OnSettingsSaved"/>) that hands a detached <see cref="SettingsData"/>
/// snapshot to <see cref="SettingsChangeCoordinator"/>. App never classifies impact or orders
/// effects itself; every step below is a thin adapter onto an existing App method or field.
/// </summary>
public partial class App : ISettingsConnectionEffects, ISettingsRuntimeEffects, ISettingsSurfaceEffects
{
    private SettingsChangeCoordinator? _settingsChangeCoordinator;

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        var settings = _settings;
        var coordinator = _settingsChangeCoordinator;
        if (settings == null || coordinator == null)
            return;

        ObserveBackgroundFault(
            coordinator.ApplyAsync(
                new SettingsChangeRequest(PersistedVersion: null, settings.ToSettingsData()),
                CancellationToken.None),
            "[App] Failed to apply saved settings");
    }

    void ISettingsConnectionEffects.SyncActiveGatewayBrowserProxyForward(SettingsData settings) =>
        SyncActiveGatewayBrowserProxyForward();

    void ISettingsConnectionEffects.PrepareFullReconnect(SettingsData settings)
    {
        _appState!.GatewaySelf = null;
        if (!settings.UseSshTunnel)
        {
            _sshTunnelService?.Stop();
        }
        // Status is updated by OnManagerStateChanged when reconnect starts.
        UpdateTrayIcon();

        // Reset the chat window because it has a stale URL or token.
        _windowManager?.ResetChatForCredentialChange();
    }

    void ISettingsConnectionEffects.ReconnectWithSyncedBrowserProxyForward() =>
        ReconnectWithSyncedBrowserProxyForward();

    void ISettingsRuntimeEffects.ApplyChatToolCallVisibility(SettingsData settings) =>
        OpenClawTray.Chat.OpenClawReactorChatRoot.SetToolCallsVisible(settings.ShowChatToolCalls);

    void ISettingsRuntimeEffects.PublishSandboxRiskNotification() =>
        PublishSandboxRiskNotificationIfNeeded();

    // Handle the MCP server lifecycle separately from gateway reconnects because MCP-only mode
    // doesn't involve a gateway at all. SetMcpEnabled checks actual runtime state
    // (_mcpServer != null), so it's safe to call unconditionally. Only create NodeService when
    // MCP is being enabled or the service already exists.
    void ISettingsRuntimeEffects.ApplyMcpRuntime(SettingsData settings)
    {
        if (_settings == null || (_nodeService == null && !settings.EnableMcpServer))
            return;

        var nodeService = EnsureNodeService(_settings);
        nodeService?.SetMcpEnabled(settings.EnableMcpServer);
        if (nodeService != null)
        {
            ApplyMcpStartupNotificationPlan(
                McpRuntimeStatePolicy.PlanStartupNotification(
                    settings.EnableMcpServer,
                    nodeService.IsMcpRunning,
                    nodeService.McpStartupError));
        }
        WireAppCapabilityHandlers();
    }

    void ISettingsRuntimeEffects.ApplyGlobalHotkey(SettingsData settings)
    {
        if (settings.GlobalHotkeyEnabled)
        {
            _globalHotkey ??= new GlobalHotkeyService();
            _globalHotkey.VoiceHotkeyPressed -= OnVoiceHotkeyPressed;
            _globalHotkey.VoiceHotkeyPressed += OnVoiceHotkeyPressed;
            _globalHotkey.SettingsHotkeyPressed -= OnSettingsHotkeyPressed;
            _globalHotkey.SettingsHotkeyPressed += OnSettingsHotkeyPressed;
            _globalHotkey.Register();
        }
        else
        {
            _globalHotkey?.Unregister();
        }
    }

    void ISettingsRuntimeEffects.ApplyAutoStartAndTelemetry(SettingsData settings)
    {
        ObserveBackgroundFault(
            AutoStartManager.SetAutoStartAsync(settings.AutoStart),
            "[App] Failed to apply auto-start setting");
        ApplyOpenTelemetryEndpointSettings();
    }

    // Apply UI-only settings and notify ad-hoc listeners. This public entry point can be
    // invoked from background work, while existing listeners update UI directly.
    void ISettingsSurfaceEffects.ApplyOnUiThread(SettingsData settings)
    {
        void ApplyUiSettingsAndNotify()
        {
            ApplyThemePreferenceToOpenWindows();
            _windowManager?.RefreshHubDiagnosticsNavigationVisibility();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            PermissionsRuntimeChanged?.Invoke(this, EventArgs.Empty);
        }

        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(ApplyUiSettingsAndNotify);
        }
        else
        {
            ApplyUiSettingsAndNotify();
        }
    }
}
