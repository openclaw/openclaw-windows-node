using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;

namespace OpenClawTray.Pages;

public sealed partial class ConnectionPage : Page
{
    private HubWindow? _hub;
    private int _connectionAttempts;

    public ConnectionPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        var settings = hub.Settings;
        if (settings == null) return;

        // Populate manual connection fields
        GatewayUrlTextBox.Text = settings.GatewayUrl ?? "";
        TokenTextBox.Text = settings.Token ?? "";
        SshToggle.IsOn = settings.UseSshTunnel;
        SshDetailsPanel.Visibility = settings.UseSshTunnel ? Visibility.Visible : Visibility.Collapsed;
        SshUserBox.Text = settings.SshTunnelUser ?? "";
        SshHostBox.Text = settings.SshTunnelHost ?? "";
        SshRemotePortBox.Text = settings.SshTunnelRemotePort.ToString();
        SshLocalPortBox.Text = settings.SshTunnelLocalPort.ToString();

        UpdateStatus(hub.CurrentStatus);
        UpdateDeviceIdentity();
        LoadConnectionLog();
        LoadGatewayList();
    }

    public void UpdateStatus(ConnectionStatus status)
    {
        var registry = _hub?.GatewayRegistry;
        var activeGw = registry?.GetActive();
        var hasOperatorToken = !string.IsNullOrWhiteSpace(activeGw?.OperatorDeviceToken);
        var hasNodeToken = !string.IsNullOrWhiteSpace(activeGw?.NodeDeviceToken);
        var hasBothTokens = hasOperatorToken && hasNodeToken;

        // Only show "Connected" when operator has a working token
        var effectiveStatus = status;
        if (status == ConnectionStatus.Connected && !hasOperatorToken)
            effectiveStatus = ConnectionStatus.Connecting; // still bootstrapping

        var (color, text) = effectiveStatus switch
        {
            ConnectionStatus.Connected => (Microsoft.UI.Colors.LimeGreen, "Connected"),
            ConnectionStatus.Connecting => (Microsoft.UI.Colors.Orange, "Connecting…"),
            ConnectionStatus.Error => (Microsoft.UI.Colors.Red, "Error"),
            _ => (Microsoft.UI.Colors.Gray, "Disconnected")
        };

        StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        StatusText.Text = text;

        var isConnected = effectiveStatus == ConnectionStatus.Connected;
        ReconnectButton.IsEnabled = effectiveStatus != ConnectionStatus.Connecting;
        ReconnectButton.Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;

        if (effectiveStatus == ConnectionStatus.Connecting)
        {
            _connectionAttempts++;
            ConnectionAttemptsText.Text = $"Connection attempt {_connectionAttempts}…";
            ConnectionAttemptsText.Visibility = Visibility.Visible;
        }
        else
        {
            if (effectiveStatus == ConnectionStatus.Connected)
                _connectionAttempts = 0;
            ConnectionAttemptsText.Visibility = Visibility.Collapsed;
        }

        // Gateway details
        var self = _hub?.LastGatewaySelf;
        var effectiveUrl = activeGw?.Url ?? "";

        if (self != null && status == ConnectionStatus.Connected)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(self.ServerVersion))
                parts.Add($"v{self.ServerVersion}");
            parts.Add($"Up {self.UptimeText}");
            if (self.PresenceCount is > 0)
                parts.Add($"{self.PresenceCount} clients");
            GatewayDetailText.Text = string.Join(" · ", parts);

            var authLabel = string.IsNullOrWhiteSpace(self.AuthMode) ? "" : $" · {self.AuthMode} auth";
            GatewayUrlDetail.Text = $"{SanitizeUrl(effectiveUrl)}{authLabel}";
        }
        else
        {
            // Show registry state even when not connected
            var detailParts = new List<string>();
            if (activeGw != null)
            {
                if (!string.IsNullOrWhiteSpace(activeGw.OperatorDeviceToken))
                    detailParts.Add("Operator: paired");
                else if (!string.IsNullOrWhiteSpace(activeGw.BootstrapToken))
                    detailParts.Add("Operator: pairing…");
                if (!string.IsNullOrWhiteSpace(activeGw.NodeDeviceToken))
                    detailParts.Add("Node: paired");
            }
            GatewayDetailText.Text = detailParts.Count > 0 ? string.Join(" · ", detailParts) : "";
            GatewayUrlDetail.Text = !string.IsNullOrEmpty(effectiveUrl)
                ? SanitizeUrl(effectiveUrl) : "";
        }

        // Operator status
        OperatorStatusText.Text = status switch
        {
            ConnectionStatus.Connected => "Operator: ✓ Connected",
            ConnectionStatus.Connecting => "Operator: ⏳ Connecting",
            ConnectionStatus.Error => "Operator: ✗ Error",
            _ => "Operator: — Disconnected"
        };

        // Node status
        if (_hub != null && _hub.Settings?.EnableNodeMode == true)
        {
            if (_hub.NodeIsPaired)
                NodeStatusText.Text = "Node: ✓ Paired";
            else if (_hub.NodeIsPendingApproval)
                NodeStatusText.Text = "Node: ⏳ Pending approval";
            else if (_hub.NodeIsConnected)
                NodeStatusText.Text = "Node: ✓ Connected";
            else
                NodeStatusText.Text = "Node: — Disconnected";
        }
        else
        {
            NodeStatusText.Text = "Node: — Disabled";
        }

        UpdateDeviceIdentity();
        LoadConnectionLog();
        LoadGatewayList();

        // Show auth error if present
        var authError = _hub?.LastAuthError;
        if (!string.IsNullOrEmpty(authError))
        {
            AuthErrorBar.Message = GetAuthErrorGuidance(authError!);
            AuthErrorBar.IsOpen = true;
        }
        else
        {
            AuthErrorBar.IsOpen = false;
        }
    }

    private static string GetAuthErrorGuidance(string error)
    {
        if (error.Contains("token", StringComparison.OrdinalIgnoreCase))
            return $"{error}\n\nCheck your token in the settings below, or paste a new setup code.";
        if (error.Contains("pairing", StringComparison.OrdinalIgnoreCase))
            return $"{error}\n\nYour device needs approval on the gateway host.";
        if (error.Contains("password", StringComparison.OrdinalIgnoreCase))
            return $"{error}\n\nThis gateway requires password authentication.";
        if (error.Contains("signature", StringComparison.OrdinalIgnoreCase))
            return $"{error}\n\nThe gateway may require a different auth protocol version.";
        return $"{error}\n\nCheck your connection settings and try again.";
    }

    private void UpdateDeviceIdentity()
    {
        if (_hub == null) return;

        var shortId = _hub.NodeShortDeviceId;
        var fullId = _hub.NodeFullDeviceId;

        if (!string.IsNullOrEmpty(shortId) || !string.IsNullOrEmpty(fullId))
        {
            DeviceIdentityCard.Visibility = Visibility.Visible;
            DeviceIdText.Text = shortId ?? fullId ?? "";

            if (_hub.NodeIsPaired)
            {
                var gwUrl = _hub.Settings?.GetEffectiveGatewayUrl();
                var gwDisplay = !string.IsNullOrEmpty(gwUrl) ? $" to {GatewayUrlHelper.SanitizeForDisplay(gwUrl)}" : "";
                PairingStatusText.Text = $"Pairing: ✓ Paired{gwDisplay}";
                ApprovalHelpPanel.Visibility = Visibility.Collapsed;
            }
            else if (_hub.NodeIsPendingApproval)
            {
                PairingStatusText.Text = "Pairing: ⏳ Pending approval";
                ApprovalHelpPanel.Visibility = Visibility.Visible;
                var deviceRef = fullId ?? shortId ?? "";
                ApprovalCommandText.Text = $"openclaw devices approve {deviceRef}";
            }
            else
            {
                PairingStatusText.Text = "Pairing: — Not paired";
                ApprovalHelpPanel.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            DeviceIdentityCard.Visibility = Visibility.Collapsed;
        }
    }

    private void LoadConnectionLog()
    {
        ConnectionLogPanel.Children.Clear();
        var nodeItems = ActivityStreamService.GetItems(15, "node");
        var errorItems = ActivityStreamService.GetItems(10, "error");
        var items = nodeItems.Concat(errorItems)
            .OrderByDescending(i => i.Timestamp)
            .Take(15)
            .ToList();

        if (items.Count == 0)
        {
            ConnectionLogEmpty.Visibility = Visibility.Visible;
            return;
        }

        ConnectionLogEmpty.Visibility = Visibility.Collapsed;
        foreach (var item in items)
        {
            var text = $"{item.Timestamp:HH:mm:ss}  {item.Title}";
            if (!string.IsNullOrEmpty(item.Details))
                text += $" — {item.Details}";
            var tb = new TextBlock
            {
                Text = text,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            };
            ConnectionLogPanel.Children.Add(tb);
        }
    }

    private static string SanitizeUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.Port > 0 ? $"{uri.Scheme}://{uri.Host}:{uri.Port}" : $"{uri.Scheme}://{uri.Host}";
        }
        catch { }
        return url;
    }

    // ─── Gateway List ───

    private void LoadGatewayList()
    {
        GatewayListPanel.Children.Clear();
        var registry = _hub?.GatewayRegistry;
        if (registry == null)
        {
            RecentGatewaysCard.Visibility = Visibility.Collapsed;
            return;
        }

        var gateways = registry.GetAll();
        if (gateways.Count == 0)
        {
            RecentGatewaysCard.Visibility = Visibility.Collapsed;
            return;
        }

        RecentGatewaysCard.Visibility = Visibility.Visible;
        var active = registry.GetActive();

        foreach (var gw in gateways)
        {
            var isActive = gw.Id == active?.Id;
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            // Active indicator
            var indicator = new TextBlock
            {
                Text = isActive ? "✓" : "",
                VerticalAlignment = VerticalAlignment.Center,
                Width = 16,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            };
            Grid.SetColumn(indicator, 0);
            row.Children.Add(indicator);

            // URL + token status
            var hasOp = !string.IsNullOrWhiteSpace(gw.OperatorDeviceToken);
            var hasNode = !string.IsNullOrWhiteSpace(gw.NodeDeviceToken);
            var hasBoot = !string.IsNullOrWhiteSpace(gw.BootstrapToken);
            var statusParts = new List<string>();
            if (hasOp && hasNode) statusParts.Add("paired");
            else if (hasBoot) statusParts.Add("pairing…");
            else if (hasNode) statusParts.Add("node only");
            var statusSuffix = statusParts.Count > 0 ? $"  ({string.Join(", ", statusParts)})" : "";

            var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            infoPanel.Children.Add(new TextBlock
            {
                Text = GatewayUrlHelper.SanitizeForDisplay(gw.Url),
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            infoPanel.Children.Add(new TextBlock
            {
                Text = $"{gw.Id}{statusSuffix}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            Grid.SetColumn(infoPanel, 1);
            row.Children.Add(infoPanel);

            // Connect button
            var connectBtn = new Button
            {
                Content = isActive ? "Active" : "Connect",
                IsEnabled = !isActive,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = gw.Id,
            };
            connectBtn.Click += OnConnectGateway;
            Grid.SetColumn(connectBtn, 2);
            row.Children.Add(connectBtn);

            // Remove button
            var removeBtn = new Button
            {
                Content = "✕",
                VerticalAlignment = VerticalAlignment.Center,
                Tag = gw.Id,
                Padding = new Thickness(6, 4, 6, 4),
            };
            removeBtn.Click += OnRemoveGateway;
            Grid.SetColumn(removeBtn, 3);
            row.Children.Add(removeBtn);

            GatewayListPanel.Children.Add(row);
        }
    }

    private void OnConnectGateway(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string gwId) return;
        var registry = _hub?.GatewayRegistry;
        if (registry == null) return;

        registry.SetActive(gwId);

        // Update settings URL from the new active gateway (for the Advanced UI)
        var active = registry.GetActive();
        if (active != null && _hub?.Settings != null)
        {
            _hub.Settings.GatewayUrl = active.Url;
            _hub.Settings.Token = active.OperatorDeviceToken ?? "";
            _hub.Settings.Save();
        }

        LoadGatewayList();
        _hub?.ReconnectAction?.Invoke();
    }

    private void OnRemoveGateway(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string gwId) return;
        var registry = _hub?.GatewayRegistry;
        if (registry == null) return;

        registry.Remove(gwId);
        LoadGatewayList();
    }

    // ─── Event Handlers ───

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        _hub?.DisconnectAction?.Invoke();
    }

    private void OnReconnect(object sender, RoutedEventArgs e)
    {
        _connectionAttempts = 0;
        _hub?.ReconnectAction?.Invoke();
    }

    private void OnSshToggled(object sender, RoutedEventArgs e)
    {
        SshDetailsPanel.Visibility = SshToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        TestResultText.Text = "Testing…";
        TestButton.IsEnabled = false;
        try
        {
            if (_hub?.GatewayClient != null)
            {
                await _hub.GatewayClient.CheckHealthAsync();
                TestResultText.Text = "✓ Connection successful";
            }
            else
            {
                TestResultText.Text = "Not connected — save settings and reconnect";
            }
        }
        catch (Exception ex)
        {
            TestResultText.Text = $"✗ {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var settings = _hub?.Settings;
        if (settings == null) return;

        settings.GatewayUrl = GatewayUrlTextBox.Text.Trim();
        settings.Token = TokenTextBox.Text.Trim();
        settings.UseSshTunnel = SshToggle.IsOn;
        settings.SshTunnelUser = SshUserBox.Text.Trim();
        settings.SshTunnelHost = SshHostBox.Text.Trim();
        if (int.TryParse(SshRemotePortBox.Text, out var rp)) settings.SshTunnelRemotePort = rp;
        if (int.TryParse(SshLocalPortBox.Text, out var lp)) settings.SshTunnelLocalPort = lp;

        settings.Save();
        _hub?.RaiseSettingsSaved();
    }

    private void OnSetupCodeTextChanged(object sender, TextChangedEventArgs e)
    {
        var code = SetupCodeTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length < 10)
        {
            SetupCodePreviewPanel.Visibility = Visibility.Collapsed;
            SetupCodeResultText.Text = "";
            return;
        }

        var decoded = SetupCodeDecoder.Decode(code);
        if (decoded.Success)
        {
            SetupCodePreviewUrl.Text = $"Gateway: {decoded.Url ?? "(not specified)"}";
            SetupCodePreviewToken.Text = $"Bootstrap token: {decoded.Token?[..Math.Min(8, decoded.Token?.Length ?? 0)]}…";
            SetupCodePreviewPanel.Visibility = Visibility.Visible;
            SetupCodeResultText.Text = "";
        }
        else
        {
            SetupCodePreviewPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnApplySetupCode(object sender, RoutedEventArgs e)
    {
        var settings = _hub?.Settings;
        if (settings == null) return;

        // Compute the shared identity data path (same as App.DataPath)
        var dataPath = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } v
            ? v
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClawTray");

        var result = SetupCodeApplicator.Apply(SetupCodeTextBox.Text, settings, dataPath, _hub?.GatewayRegistry);
        if (!result.Success)
        {
            SetupCodeResultText.Text = $"✗ {result.Error}";
            return;
        }

        SetupCodeResultText.Text = $"✓ Applied — gateway: {result.DisplayUrl}";
        GatewayUrlTextBox.Text = settings.GatewayUrl ?? "";
        TokenTextBox.Text = ""; // Token was cleared by the applicator

        _hub?.RaiseSettingsSaved();
    }

    private void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        var id = _hub?.NodeFullDeviceId ?? _hub?.NodeShortDeviceId;
        if (string.IsNullOrEmpty(id)) return;
        CopyToClipboard(id);
    }

    private void OnCopyApprovalCommand(object sender, RoutedEventArgs e)
    {
        var cmd = ApprovalCommandText.Text;
        if (!string.IsNullOrEmpty(cmd))
            CopyToClipboard(cmd);
    }

    private static void CopyToClipboard(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }
}
