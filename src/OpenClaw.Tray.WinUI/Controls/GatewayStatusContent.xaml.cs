using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;

namespace OpenClawTray.Controls;

public sealed partial class GatewayStatusContent : UserControl
{
    private static App CurrentApp => (App)Application.Current;
    private Action? _close;
    private Action? _openConnection;
    private Action? _reconnect;

    public GatewayStatusContent() => InitializeComponent();

    internal void Initialize(Action close, Action openConnection, Action reconnect)
    {
        _close = close;
        _openConnection = openConnection;
        _reconnect = reconnect;
        Render();
    }

    private void Render()
    {
        var snapshot = CurrentApp.ConnectionManager?.CurrentSnapshot;
        var settings = CurrentApp.Settings;
        var nodeEnabled = settings?.EnableNodeMode == true;
        var enabledCapabilities = CountEnabledCapabilities(settings);
        var op = snapshot?.OperatorState ?? RoleConnectionState.Idle;

        GatewayRowDot.Fill = AccentBrush(ConnectionStatusPresenter.RoleAccent(op));
        GatewayRowDetail.Text = BuildGatewayDetail(snapshot);
        GatewayRowAction.Visibility =
            op is RoleConnectionState.Connected or RoleConnectionState.Connecting
                ? Visibility.Collapsed
                : Visibility.Visible;

        OperatorRowDot.Fill = AccentBrush(ConnectionStatusPresenter.RoleAccent(op));
        OperatorRowDetail.Text = LocalizationHelper.GetString(
            ConnectionStatusPresenter.RoleStateLabelKey(op));

        if (snapshot is not null)
        {
            var (nodeKey, nodeAccent) = ConnectionStatusPresenter.NodeRow(snapshot, nodeEnabled, enabledCapabilities);
            NodeRowDot.Fill = AccentBrush(nodeAccent);
            NodeRowDetail.Text = LocalizationHelper.GetString(nodeKey);
            NodeRowAction.Visibility = ConnectionStatusPresenter.NodeNeedsApproval(snapshot, nodeEnabled)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        else
        {
            NodeRowDot.Fill = AccentBrush(ConnectionStatusAccent.Neutral);
            NodeRowDetail.Text = LocalizationHelper.GetString("HubWindow_Role_Disabled");
            NodeRowAction.Visibility = Visibility.Collapsed;
        }
    }

    private string BuildGatewayDetail(GatewayConnectionSnapshot? snapshot)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot?.GatewayName))
            parts.Add(snapshot!.GatewayName!);
        if (!string.IsNullOrWhiteSpace(snapshot?.GatewayUrl))
            parts.Add(snapshot!.GatewayUrl!);
        if (CurrentApp.AppState?.GatewaySelf is { ServerVersion: { Length: > 0 } ver })
            parts.Add($"v{ver}");
        return parts.Count > 0
            ? string.Join(" · ", parts)
            : LocalizationHelper.GetString("StatusDisplay_Disconnected");
    }

    private static int CountEnabledCapabilities(SettingsManager? settings)
    {
        if (settings is null) return 0;

        var count = 0;
        if (settings.NodeBrowserProxyEnabled) count++;
        if (settings.NodeCameraEnabled) count++;
        if (settings.NodeCanvasEnabled) count++;
        if (settings.NodeScreenEnabled) count++;
        if (settings.NodeLocationEnabled) count++;
        if (settings.NodeTtsEnabled) count++;
        if (settings.NodeSttEnabled) count++;
        if (settings.NodeOllamaInferenceEnabled) count++;
        return count;
    }

    private void OnStatusFlyoutOpenConnectionClick(object sender, RoutedEventArgs e)
    {
        _close?.Invoke();
        _openConnection?.Invoke();
    }

    private void OnStatusFlyoutReconnectClick(object sender, RoutedEventArgs e)
    {
        _close?.Invoke();
        _reconnect?.Invoke();
    }

    private void OnStatusFlyoutNodeActionClick(object sender, RoutedEventArgs e)
    {
        _close?.Invoke();
        _openConnection?.Invoke();
    }


    private static string AccentBrushKey(ConnectionStatusAccent accent) => accent switch
    {
        ConnectionStatusAccent.Success => "SystemFillColorSuccessBrush",
        ConnectionStatusAccent.Caution => "SystemFillColorCautionBrush",
        ConnectionStatusAccent.Critical => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorNeutralBrush",
    };

    internal static Brush AccentBrush(ConnectionStatusAccent accent)
    {
        var resources = Application.Current.Resources;
        if (resources.TryGetValue(AccentBrushKey(accent), out var brush) && brush is Brush typed)
            return typed;

        if (resources.TryGetValue("SystemFillColorNeutralBrush", out var neutral) && neutral is Brush fallback)
            return fallback;

        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

}
