using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Post-save request built by App's <see cref="IAppCommands.NotifySettingsSaved"/> implementation:
/// the authoritative detached current settings and, when the save went through the A0 settings
/// store, the store's persisted version (used only for a reentrancy dedup, not for gating a later
/// equal-value save).
/// </summary>
internal sealed record SettingsChangeRequest(long? PersistedVersion, SettingsData Current);

/// <summary>Which connection-effect step to run for a classified settings change.</summary>
internal enum SettingsReconnectPlan
{
    None,
    CapabilityReload,
    Node,
    Full,
}

/// <summary>
/// Owns detached authoritative snapshot comparison via <see cref="SettingsChangeClassifier"/> and
/// the exact existing effect order for a settings save (browser proxy sync, impact log, sandbox
/// risk notification, connection reconnect, MCP runtime, hotkey, auto-start, telemetry, and
/// surface notifications). Requests run through a FIFO single drainer, and comparison/version
/// state advances only after the complete effect chain succeeds. App applies every effect through
/// the three narrow ports below; the coordinator never persists settings, owns a settings page, or
/// reads/writes credentials.
/// </summary>
internal interface ISettingsChangeCoordinator : IDisposable
{
    Task ApplyAsync(SettingsChangeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Connection-facing effects. <c>SyncActiveGatewayBrowserProxyForward</c> runs unconditionally on
/// every save; <c>PrepareFullReconnect</c> plus <c>ReconnectWithSyncedBrowserProxyForward</c> run
/// only for <see cref="SettingsReconnectPlan.Full"/>, and <c>ReconnectWithSyncedBrowserProxyForward</c>
/// alone runs for <see cref="SettingsReconnectPlan.Node"/> and <see cref="SettingsReconnectPlan.CapabilityReload"/>.
/// </summary>
internal interface ISettingsConnectionEffects
{
    void SyncActiveGatewayBrowserProxyForward(SettingsData settings);
    void PrepareFullReconnect(SettingsData settings);
    void ReconnectWithSyncedBrowserProxyForward();
}

/// <summary>Runtime effects that run unconditionally on every non-deduplicated save, in this order.</summary>
internal interface ISettingsRuntimeEffects
{
    void ApplyChatToolCallVisibility(SettingsData settings);
    void PublishSandboxRiskNotification();
    void ApplyMcpRuntime(SettingsData settings);
    void ApplyGlobalHotkey(SettingsData settings);
    void ApplyAutoStartAndTelemetry(SettingsData settings);
}

/// <summary>
/// UI-only settings application and ad-hoc surface notification. App marshals this onto the UI
/// thread when the caller is not already on it, exactly as the prior inline implementation did.
/// </summary>
internal interface ISettingsSurfaceEffects
{
    void ApplyOnUiThread(SettingsData settings);
}
