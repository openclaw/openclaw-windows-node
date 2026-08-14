using OpenClaw.Shared;
using OpenClawTray.Services;
using System;

namespace OpenClawTray;

public partial class App
{
    private SettingsChangeCoordinator? _settingsChangeCoordinator;

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        if (_settings == null)
            return;

        _settingsChangeCoordinator?.Apply(_settings.ToSettingsData());
    }

    private SettingsChangeCoordinator CreateSettingsChangeCoordinator(SettingsData initialSettings) =>
        new(
            new SettingsChangeEffects(
                settings => OpenClawTray.Chat.OpenClawReactorChatRoot.SetToolCallsVisible(settings.ShowChatToolCalls),
                SyncActiveGatewayBrowserProxyForward,
                PublishSandboxRiskNotificationIfNeeded,
                PrepareFullReconnect,
                ReconnectWithSyncedBrowserProxyForward,
                ApplyMcpRuntime,
                ApplyGlobalHotkey,
                ApplyAutoStartAndTelemetry,
                ApplySettingsSurface),
            initialSettings);

    private void PrepareFullReconnect(SettingsData settings)
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

    // Handle the MCP server lifecycle separately from gateway reconnects because MCP-only mode
    // doesn't involve a gateway at all. SetMcpEnabled checks actual runtime state
    // (_mcpServer != null), so it's safe to call unconditionally. Only create NodeService when
    // MCP is being enabled or the service already exists.
    private void ApplyMcpRuntime(SettingsData settings)
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

    private void ApplyGlobalHotkey(SettingsData settings)
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

    private void ApplyAutoStartAndTelemetry(SettingsData settings)
    {
        ObserveBackgroundFault(
            AutoStartManager.SetAutoStartAsync(settings.AutoStart),
            "[App] Failed to apply auto-start setting");
        ApplyOpenTelemetryEndpointSettings();
    }

    // Apply UI-only settings and notify ad-hoc listeners. This public entry point can be
    // invoked from background work, while existing listeners update UI directly.
    private void ApplySettingsSurface(SettingsData settings)
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
