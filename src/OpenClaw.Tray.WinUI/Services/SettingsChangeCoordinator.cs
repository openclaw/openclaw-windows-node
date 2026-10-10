using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal sealed class SettingsChangeCoordinator
{
    private readonly SettingsChangeEffects _effects;
    private readonly object _gate = new();
    private ConnectionSettingsSnapshot? _previousSnapshot;

    public SettingsChangeCoordinator(SettingsChangeEffects effects, SettingsData? initialSettings = null)
    {
        _effects = effects;
        _previousSnapshot = initialSettings?.ToConnectionSnapshot();
    }

    public void Apply(SettingsData settings)
    {
        lock (_gate)
            ApplyCore(settings);
    }

    private void ApplyCore(SettingsData settings)
    {
        _effects.ApplyOllamaPermission(settings);
        _effects.ApplyChatToolCallVisibility(settings);

        var currentSnapshot = settings.ToConnectionSnapshot();
        var impact = SettingsChangeClassifier.Classify(_previousSnapshot, currentSnapshot);
        _previousSnapshot = currentSnapshot;

        _effects.SyncActiveGatewayBrowserProxyForward();
        Logger.Info($"[SETTINGS] Change impact: {impact}");
        _effects.PublishSandboxRiskNotification();

        switch (impact)
        {
            case SettingsChangeImpact.FullReconnectRequired:
            case SettingsChangeImpact.OperatorReconnectRequired:
                _effects.PrepareFullReconnect(settings);
                _effects.ReconnectWithSyncedBrowserProxyForward();
                break;

            case SettingsChangeImpact.NodeReconnectRequired:
            case SettingsChangeImpact.CapabilityReload:
                _effects.ReconnectWithSyncedBrowserProxyForward();
                break;
        }

        _effects.ApplyMcpRuntime(settings);
        _effects.ApplyGlobalHotkey(settings);
        _effects.ApplyAutoStartAndTelemetry(settings);
        _effects.ApplyOnUiThread(settings);
    }
}
