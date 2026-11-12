using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// Pure projection of the Workspace account menu: profile header, Gateway submenu, Retry now,
/// Settings (with the Companion update badge), Pair a device, and Help &amp; about.
/// <c>WorkspaceWindow</c> applies it to native MenuFlyout items and owns every command.
/// </summary>
/// <example>
/// <code>
/// var menu = WorkspaceAccountMenu.Project(new(
///     registry.GetAll(), registry.GetActive()?.Id, snapshot?.OverallState, status,
///     gatewaySelf?.ServerVersion, appState.UpdateInfo,
///     WorkspaceAccountMenuInput.IsOperatorConnected(snapshot, status)));
/// if (menu.ShowRetry) AddRetryItem();
/// settingsItem.Click += (_, _) => OpenCompanion(menu.SettingsTarget);
/// </code>
/// </example>
internal static class WorkspaceAccountMenu
{
    // Upstream Control UI aliases, present in every released Gateway from v2026.8.35 through v2026.11.1
    // (ui/src/app-route-paths.ts): /nodes -> /settings/devices, /profile -> /settings/profile,
    // /config -> /settings/general. Recheck that file when raising or lowering the supported range.

    /// <summary>Dashboard alias for the Devices page, where device pairing starts.</summary>
    public const string PairDeviceDashboardPath = "nodes";
    /// <summary>Dashboard alias for the signed-in profile settings.</summary>
    public const string ProfileDashboardPath = "profile";
    /// <summary>Dashboard alias for Gateway general settings.</summary>
    public const string GatewaySettingsDashboardPath = "config";

    public static WorkspaceAccountMenuModel Project(WorkspaceAccountMenuInput input)
    {
        var active = input.Gateways.FirstOrDefault(gateway => gateway.Id == input.ActiveGatewayId);
        var (labelKey, accent) = ConnectionStatusPresenter.Pill(input.OverallState, input.Status);
        // The profile and version come from the operator channel, which can stay live while the
        // aggregate state is Degraded or PairingRequired (node issues), or be idle while it is Connected.
        var version = input.OperatorConnected && !string.IsNullOrWhiteSpace(input.ServerVersion)
            ? input.ServerVersion!.Trim().TrimStart('v', 'V')
            : null;
        var gateways = input.Gateways
            .OrderByDescending(gateway => gateway.Id == active?.Id)
            .ThenBy(GatewayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(gateway => new WorkspaceAccountMenuGateway(
                gateway.Id, GatewayName(gateway), GatewayHost(gateway), gateway.Id == active?.Id))
            .ToArray();
        var update = input.Update?.IsUpdateAvailable == true;
        return new WorkspaceAccountMenuModel(
            active is null ? null : GatewayName(active),
            labelKey,
            accent,
            version,
            input.OperatorConnected,
            ShowRetry: active is not null && NeedsRetry(input.OverallState, input.Status),
            gateways,
            update,
            update ? CompanionPageId.About : CompanionPageId.Settings);
    }

    /// <summary>
    /// Retry now applies only while the connection is neither live nor pending. Its click rechecks this,
    /// because the open menu is not rebuilt and a reconnect would drop a connection that came back.
    /// </summary>
    public static bool NeedsRetry(OverallConnectionState? overall, ConnectionStatus status) =>
        !ConnectionStatusPresenter.IsLiveOrPending(overall, status);

    /// <summary>The profile header's second line: the email, unless the label already is that email.</summary>
    public static string? ProfileDetail(string label, string? email) =>
        string.IsNullOrWhiteSpace(email) || string.Equals(label.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : email;

    /// <summary>The saved friendly name, else the endpoint host, else the raw URL.</summary>
    public static string GatewayName(GatewayRecord gateway) =>
        !string.IsNullOrWhiteSpace(gateway.FriendlyName) ? gateway.FriendlyName!.Trim() : GatewayHost(gateway);

    public static string GatewayHost(GatewayRecord gateway) =>
        Uri.TryCreate(gateway.Url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}"
            : gateway.Url;
}

internal sealed record WorkspaceAccountMenuInput(
    IReadOnlyList<GatewayRecord> Gateways,
    string? ActiveGatewayId,
    OverallConnectionState? OverallState,
    ConnectionStatus Status,
    string? ServerVersion,
    UpdateCommandCenterInfo? Update,
    bool OperatorConnected)
{
    /// <summary>The operator channel is live: from the snapshot when present, else the legacy status.</summary>
    public static bool IsOperatorConnected(GatewayConnectionSnapshot? snapshot, ConnectionStatus status) =>
        snapshot is null ? status == ConnectionStatus.Connected : ConnectionStatusPresenter.IsOperatorChannelLive(snapshot);
}

/// <param name="ActiveGatewayName">Display name of the active Gateway, or null when none is configured.</param>
/// <param name="StatusLabelKey">Resource key of the connection status label.</param>
/// <param name="ServerVersion">Gateway version shown while the operator channel is live, without a leading "v".</param>
/// <param name="OperatorConnected">The operator channel is live, so the profile and Dashboard are reachable.</param>
/// <param name="ShowRetry">An active Gateway is configured but neither connected nor connecting.</param>
/// <param name="SettingsTarget">About while an update is available (where it is installed), otherwise Settings.</param>
internal sealed record WorkspaceAccountMenuModel(
    string? ActiveGatewayName,
    string StatusLabelKey,
    ConnectionStatusAccent StatusAccent,
    string? ServerVersion,
    bool OperatorConnected,
    bool ShowRetry,
    IReadOnlyList<WorkspaceAccountMenuGateway> Gateways,
    bool UpdateAvailable,
    CompanionPageId SettingsTarget);

internal sealed record WorkspaceAccountMenuGateway(string Id, string Name, string Host, bool IsActive);
