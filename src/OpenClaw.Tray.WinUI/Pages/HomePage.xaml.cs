using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Linq;

namespace OpenClawTray.Pages;

public sealed partial class HomePage : Page
{
    private HubWindow? _hub;
    private bool _toggleWired;
    private bool _buttonsWired;

    public HomePage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;

        // Connection status
        UpdateConnectionStatus(hub.CurrentStatus, hub.Settings?.GetEffectiveGatewayUrl());

        // Active toggle
        ActiveToggle.IsOn = hub.CurrentStatus == ConnectionStatus.Connected
                         || hub.CurrentStatus == ConnectionStatus.Connecting;
        if (!_toggleWired)
        {
            ActiveToggle.Toggled += OnActiveToggled;
            _toggleWired = true;
        }

        // Connection details from settings
        if (hub.Settings != null)
        {
            var url = hub.Settings.GetEffectiveGatewayUrl();
            GatewayUrlText.Text = url;
            GatewayUrlLabel.Text = url;
            SshTunnelText.Text = hub.Settings.UseSshTunnel ? "Enabled" : "Disabled";
            AuthModeText.Text = hub.Settings.Token.Length > 0 ? "Token" : "None";
            TailscaleText.Text = hub.Settings.UseSshTunnel ? "Via SSH tunnel" : "Not configured";
        }

        // Stats from cached data
        SessionsCountText.Text = (hub.LastSessions?.Length ?? 0).ToString();
        NodesCountText.Text = (hub.LastNodes?.Length ?? 0).ToString();

        // Usage cost
        if (hub.LastUsageCost != null)
            UsageCostText.Text = $"${hub.LastUsageCost.Totals.TotalCost:F2}";

        // Nodes mini list
        if (hub.LastNodes != null && hub.LastNodes.Length > 0)
            RenderNodesMini(hub.LastNodes);

        // Quick action buttons (one-time wire)
        if (!_buttonsWired)
        {
            QuickSendButton.Click += (s, e) => _hub?.NavigateTo("chat");
            DashboardButton.Click += (s, e) => _hub?.OpenDashboardAction?.Invoke(null);
            HealthCheckButton.Click += async (s, e) =>
            {
                if (_hub?.GatewayClient != null) await _hub.GatewayClient.CheckHealthAsync();
            };
            HealthCheckButton2.Click += async (s, e) =>
            {
                if (_hub?.GatewayClient != null) await _hub.GatewayClient.CheckHealthAsync();
            };
            ViewNodesButton.Click += (s, e) => _hub?.NavigateTo("nodes");
            _buttonsWired = true;
        }
    }

    public void UpdateConnectionStatus(ConnectionStatus status, string? gatewayUrl)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            // Active card
            ConnectionModeText.Text = status switch
            {
                ConnectionStatus.Connected => gatewayUrl != null && !gatewayUrl.Contains("localhost")
                    ? "Connected to remote"
                    : "Running locally",
                ConnectionStatus.Connecting => "Connecting...",
                _ => "Disconnected"
            };
            if (!string.IsNullOrEmpty(gatewayUrl))
                GatewayUrlLabel.Text = gatewayUrl;

            // Health dot
            HealthStatusDot.Fill = status switch
            {
                ConnectionStatus.Connected => new SolidColorBrush(Colors.LimeGreen),
                ConnectionStatus.Connecting => new SolidColorBrush(Colors.Orange),
                _ => new SolidColorBrush(Colors.Red)
            };
            HealthStatusText.Text = status switch
            {
                ConnectionStatus.Connected => "Healthy",
                ConnectionStatus.Connecting => "Checking...",
                _ => "Error"
            };
            HealthLastCheckText.Text = $"Last check: {DateTime.Now:HH:mm:ss}";

            // Sync toggle without re-triggering handler
            ActiveToggle.Toggled -= OnActiveToggled;
            ActiveToggle.IsOn = status == ConnectionStatus.Connected
                             || status == ConnectionStatus.Connecting;
            ActiveToggle.Toggled += OnActiveToggled;
        });
    }

    public void UpdateSessionCount(int count)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            SessionsCountText.Text = count.ToString();
        });
    }

    public void UpdateNodes(GatewayNodeInfo[] nodes)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            NodesCountText.Text = nodes.Length.ToString();
            RenderNodesMini(nodes);
        });
    }

    private void RenderNodesMini(GatewayNodeInfo[] nodes)
    {
        NodesMiniList.Children.Clear();
        if (nodes.Length == 0)
        {
            NoNodesText.Visibility = Visibility.Visible;
            return;
        }

        NoNodesText.Visibility = Visibility.Collapsed;

        foreach (var node in nodes.Take(5))
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(node.IsOnline ? Colors.LimeGreen : Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName,
                VerticalAlignment = VerticalAlignment.Center
            };

            var platform = new TextBlock
            {
                Text = node.Platform ?? "",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            row.Children.Add(dot);
            row.Children.Add(name);
            row.Children.Add(platform);
            NodesMiniList.Children.Add(row);
        }
    }

    private void OnActiveToggled(object sender, RoutedEventArgs e)
    {
        if (_hub == null) return;
        if (ActiveToggle.IsOn)
            _hub.ConnectAction?.Invoke();
        else
            _hub.DisconnectAction?.Invoke();
    }

    private Services.GatewayDiscoveryService? _discoveryService;

    private async void OnScanNetwork(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanButton.Content = "Scanning...";
        DiscoveryStatusText.Text = "Searching for gateways on your network...";
        DiscoveryList.Children.Clear();

        _discoveryService ??= new Services.GatewayDiscoveryService();

        try
        {
            await _discoveryService.StartDiscoveryAsync();
            var gateways = _discoveryService.Gateways;

            if (gateways.Count == 0)
            {
                DiscoveryStatusText.Text = "No gateways found on your network";
            }
            else
            {
                DiscoveryStatusText.Text = $"Found {gateways.Count} gateway{(gateways.Count != 1 ? "s" : "")}";
                foreach (var gw in gateways)
                {
                    var currentUrl = _hub?.Settings?.GetEffectiveGatewayUrl() ?? "";
                    bool isSelected = false;
                    try
                    {
                        if (!string.IsNullOrEmpty(currentUrl))
                        {
                            var currentUri = new Uri(currentUrl);
                            isSelected = string.Equals(currentUri.Host, gw.Host, StringComparison.OrdinalIgnoreCase)
                                && currentUri.Port == gw.Port;
                        }
                    }
                    catch { }

                    var card = new Border
                    {
                        Background = isSelected
                            ? (Brush)Application.Current.Resources["SystemFillColorSuccessBackgroundBrush"]
                            : (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 8, 12, 8),
                        Margin = new Thickness(0, 2, 0, 2)
                    };

                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var info = new StackPanel { Spacing = 2 };
                    info.Children.Add(new TextBlock
                    {
                        Text = gw.DisplayName,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    });
                    info.Children.Add(new TextBlock
                    {
                        Text = gw.HttpUrl,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        FontFamily = new FontFamily("Consolas")
                    });
                    Grid.SetColumn(info, 0);
                    grid.Children.Add(info);

                    if (!isSelected)
                    {
                        var selectBtn = new Button { Content = "Connect", VerticalAlignment = VerticalAlignment.Center };
                        var capturedGw = gw;
                        selectBtn.Click += (s, args) =>
                        {
                            if (_hub?.Settings != null)
                            {
                                _hub.Settings.GatewayUrl = capturedGw.HttpUrl;
                                _hub.Settings.Save();
                                _hub.ConnectAction?.Invoke();
                                GatewayUrlLabel.Text = capturedGw.HttpUrl;
                            }
                        };
                        Grid.SetColumn(selectBtn, 1);
                        grid.Children.Add(selectBtn);
                    }
                    else
                    {
                        var check = new TextBlock { Text = "✓ Connected", VerticalAlignment = VerticalAlignment.Center,
                            Foreground = new SolidColorBrush(Colors.Green) };
                        Grid.SetColumn(check, 1);
                        grid.Children.Add(check);
                    }

                    card.Child = grid;
                    DiscoveryList.Children.Add(card);
                }
            }
        }
        catch (Exception ex)
        {
            DiscoveryStatusText.Text = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            ScanButton.IsEnabled = true;
            ScanButton.Content = "Scan Network";
        }
    }
}
