using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;

namespace OpenClawTray.Pages;

public sealed partial class ConnectionPage : Page
{
    private HubWindow? _hub;
    private GatewayDiscoveryService? _discoveryService;
    private List<DiscoveredGateway> _discoveredGateways = new();
    private bool _suppressToggle;
    private string? _pendingGatewayUrl; // URL waiting for token input
    private string? _pendingGatewayId;

    public ConnectionPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            _discoveryService?.Dispose();
            _discoveryService = null;
        };
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

        // Set connect toggle without triggering event
        _suppressToggle = true;
        ConnectToggle.IsOn = hub.CurrentStatus == ConnectionStatus.Connected ||
                             hub.CurrentStatus == ConnectionStatus.Connecting;
        _suppressToggle = false;

        UpdateStatus(hub.CurrentStatus);
        UpdateDeviceIdentity();
        LoadConnectionLog();

        // Auto-scan for gateways when disconnected or when no URL configured
        if (hub.CurrentStatus != ConnectionStatus.Connected ||
            string.IsNullOrWhiteSpace(settings.GatewayUrl))
        {
            _ = AutoScanAsync();
        }
    }

    private async System.Threading.Tasks.Task AutoScanAsync()
    {
        try
        {
            _discoveryService?.Dispose();
            _discoveryService = new GatewayDiscoveryService();
            ScanProgressRing.IsActive = true;
            ScanProgressRing.Visibility = Visibility.Visible;
            GatewayEmptyText.Text = "Scanning for gateways…";

            await _discoveryService.StartDiscoveryAsync();
            _discoveredGateways = _discoveryService.Gateways.ToList();
            PopulateGatewayList();
        }
        catch
        {
            // Silently fail on auto-scan — user can manually scan
        }
        finally
        {
            ScanProgressRing.IsActive = false;
            ScanProgressRing.Visibility = Visibility.Collapsed;
        }
    }

    public void UpdateStatus(ConnectionStatus status)
    {
        var (color, text) = status switch
        {
            ConnectionStatus.Connected => (Microsoft.UI.Colors.LimeGreen, "Connected"),
            ConnectionStatus.Connecting => (Microsoft.UI.Colors.Orange, "Connecting…"),
            ConnectionStatus.Error => (Microsoft.UI.Colors.Red, "Error"),
            _ => (Microsoft.UI.Colors.Gray, "Disconnected")
        };

        StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        StatusText.Text = text;

        // Reconnect button enabled when connected or error
        ReconnectButton.IsEnabled = status == ConnectionStatus.Connected ||
                                    status == ConnectionStatus.Error;

        // Update connect toggle without firing event
        _suppressToggle = true;
        ConnectToggle.IsOn = status == ConnectionStatus.Connected ||
                             status == ConnectionStatus.Connecting;
        _suppressToggle = false;

        // Gateway details
        var self = _hub?.LastGatewaySelf;
        var effectiveUrl = _hub?.Settings?.GetEffectiveGatewayUrl() ?? "";

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
            GatewayDetailText.Text = "";
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
                PairingStatusText.Text = "Pairing: ✓ Paired";
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
        // Pull from node + error categories which contain connection-related events
        var nodeItems = ActivityStreamService.GetItems(10, "node");
        var errorItems = ActivityStreamService.GetItems(5, "error");
        var items = nodeItems.Concat(errorItems)
            .OrderByDescending(i => i.Timestamp)
            .Take(10)
            .ToList();

        if (items.Count == 0)
        {
            ConnectionLogEmpty.Visibility = Visibility.Visible;
            return;
        }

        ConnectionLogEmpty.Visibility = Visibility.Collapsed;
        foreach (var item in items)
        {
            var tb = new TextBlock
            {
                Text = $"{item.Timestamp:HH:mm:ss}  {item.Title}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
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

    // ─── Event Handlers ───

    private void OnConnectToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || _hub == null) return;

        if (ConnectToggle.IsOn)
            _hub.ConnectAction?.Invoke();
        else
            _hub.DisconnectAction?.Invoke();
    }

    private void OnReconnect(object sender, RoutedEventArgs e)
    {
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

    private async void OnScanForGateways(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanProgressRing.IsActive = true;
        ScanProgressRing.Visibility = Visibility.Visible;
        GatewayEmptyText.Visibility = Visibility.Collapsed;

        try
        {
            _discoveryService?.Dispose();
            _discoveryService = new GatewayDiscoveryService();
            await _discoveryService.StartDiscoveryAsync();
            _discoveredGateways = _discoveryService.Gateways.ToList();
            PopulateGatewayList();
        }
        catch (Exception ex)
        {
            GatewayEmptyText.Text = $"Discovery failed: {ex.Message}";
            GatewayEmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            ScanButton.IsEnabled = true;
            ScanProgressRing.IsActive = false;
            ScanProgressRing.Visibility = Visibility.Collapsed;
        }
    }

    private void PopulateGatewayList()
    {
        var currentUrl = _hub?.Settings?.GetEffectiveGatewayUrl();
        // Build display list: discovered gateways + synthesized current gateway if not found
        var displayList = new List<DiscoveredGateway>(_discoveredGateways);

        // Compare by host:port to avoid ws:// vs http:// mismatches
        string? currentHostPort = null;
        Uri? currentUri = null;
        if (!string.IsNullOrEmpty(currentUrl) && Uri.TryCreate(currentUrl, UriKind.Absolute, out var parsedUri))
        {
            currentUri = parsedUri;
            currentHostPort = $"{parsedUri.Host}:{parsedUri.Port}";
        }

        if (_hub?.CurrentStatus == ConnectionStatus.Connected &&
            currentHostPort != null &&
            !displayList.Any(g => $"{g.Host}:{g.Port}".Equals(currentHostPort, StringComparison.OrdinalIgnoreCase)))
        {
            displayList.Insert(0, new DiscoveredGateway
            {
                Id = $"current-{currentHostPort}",
                DisplayName = _hub.LastGatewaySelf?.ServerVersion != null
                    ? $"Current Gateway (v{_hub.LastGatewaySelf.ServerVersion})"
                    : "Current Gateway",
                Host = currentUri!.Host,
                Port = currentUri!.Port,
                TlsEnabled = currentUri!.Scheme is "wss" or "https"
            });
        }

        if (displayList.Count > 0)
        {
            GatewayListPanel.Children.Clear();
            foreach (var gw in displayList)
            {
                var row = new Grid { Padding = new Thickness(4, 8, 4, 8), ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var nameTb = new TextBlock
                {
                    Text = gw.DisplayName,
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
                };
                var addrTb = new TextBlock
                {
                    Text = $"{gw.Host}:{gw.Port}",
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                };
                info.Children.Add(nameTb);
                info.Children.Add(addrTb);
                Grid.SetColumn(info, 0);
                row.Children.Add(info);

                if (gw.TlsEnabled)
                {
                    var tls = new TextBlock { Text = "🔒", VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(tls, 1);
                    row.Children.Add(tls);
                }

                // Match current gateway by host:port
                var gwHostPort = $"{gw.Host}:{gw.Port}";
                var isCurrentGw = currentHostPort != null &&
                    gwHostPort.Equals(currentHostPort, StringComparison.OrdinalIgnoreCase);
                var connectBtn = new Button
                {
                    Content = isCurrentGw ? "✓" : "→",
                    VerticalAlignment = VerticalAlignment.Center,
                    IsEnabled = !isCurrentGw,
                    Tag = gw.Id
                };
                connectBtn.Click += OnConnectToGateway;
                Grid.SetColumn(connectBtn, 2);
                row.Children.Add(connectBtn);

                GatewayListPanel.Children.Add(row);
            }
            GatewayListPanel.Visibility = Visibility.Visible;
            GatewayEmptyText.Visibility = Visibility.Collapsed;
        }
        else
        {
            GatewayListPanel.Visibility = Visibility.Collapsed;
            GatewayEmptyText.Text = "No gateways found. Click Scan to search.";
            GatewayEmptyText.Visibility = Visibility.Visible;
        }
    }

    private void OnConnectToGateway(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string gatewayId) return;
        var gw = _discoveredGateways.FirstOrDefault(g => g.Id == gatewayId);
        if (gw == null || _hub?.Settings == null) return;

        // When switching to a different gateway, always prompt for token
        // (different gateways may have different tokens)
        var currentUrl = _hub.Settings.GetEffectiveGatewayUrl() ?? "";
        var isSameGateway = !string.IsNullOrEmpty(currentUrl) &&
            Uri.TryCreate(currentUrl, UriKind.Absolute, out var curUri) &&
            $"{curUri.Host}:{curUri.Port}".Equals($"{gw.Host}:{gw.Port}", StringComparison.OrdinalIgnoreCase);

        if (isSameGateway)
            return; // already connected to this one

        _pendingGatewayUrl = gw.ConnectionUrl;
        _pendingGatewayId = gw.Id;
        TokenPromptText.Text = $"Connect to gateway at {gw.Host}:{gw.Port}";
        TokenPromptBox.Text = _hub.Settings.Token ?? "";
        TokenPromptPanel.Visibility = Visibility.Visible;
        TokenPromptBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void OnConnectWithToken(object sender, RoutedEventArgs e)
    {
        var token = TokenPromptBox.Text?.Trim();
        if (string.IsNullOrEmpty(token) || _hub?.Settings == null || string.IsNullOrEmpty(_pendingGatewayUrl))
            return;

        _hub.Settings.GatewayUrl = _pendingGatewayUrl;
        _hub.Settings.Token = token;
        if (!string.IsNullOrEmpty(_pendingGatewayId))
            _hub.Settings.PreferredGatewayId = _pendingGatewayId;
        _hub.Settings.Save();
        _hub?.RaiseSettingsSaved();

        // Clear auth error from previous attempt
        if (_hub != null) _hub.LastAuthError = null;
        AuthErrorBar.IsOpen = false;

        GatewayUrlTextBox.Text = _pendingGatewayUrl;
        TokenTextBox.Text = token;
        TokenPromptPanel.Visibility = Visibility.Collapsed;
        _pendingGatewayUrl = null;
        _pendingGatewayId = null;

        // Refresh discovery list to show ✓ on newly connected gateway
        PopulateGatewayList();
    }

    private void OnCancelTokenPrompt(object sender, RoutedEventArgs e)
    {
        TokenPromptPanel.Visibility = Visibility.Collapsed;
        _pendingGatewayUrl = null;
        _pendingGatewayId = null;
    }

    private void OnApplySetupCode(object sender, RoutedEventArgs e)
    {
        var code = SetupCodeTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            SetupCodeResultText.Text = "Please paste a setup code.";
            return;
        }

        var result = SetupCodeDecoder.Decode(code);
        if (!result.Success)
        {
            SetupCodeResultText.Text = $"✗ {result.Error}";
            return;
        }

        var settings = _hub?.Settings;
        if (settings == null) return;

        if (!string.IsNullOrEmpty(result.Url))
            settings.GatewayUrl = result.Url;
        if (!string.IsNullOrEmpty(result.Token))
        {
            // Bootstrap token goes to BootstrapToken only — it's single-use for pairing.
            // Don't save it as Settings.Token, which would cause reconnect storms on restart.
            settings.BootstrapToken = result.Token;
        }

        settings.Save();

        SetupCodeResultText.Text = $"✓ Applied — gateway: {SanitizeUrl(result.Url ?? settings.GatewayUrl ?? "")}";
        GatewayUrlTextBox.Text = settings.GatewayUrl ?? "";

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
