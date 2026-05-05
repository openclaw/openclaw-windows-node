using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Linq;
using System.Text.Json;

namespace OpenClawTray.Pages;

public sealed partial class HomePage : Page
{
    private HubWindow? _hub;
    private bool _buttonsWired;
    private ConnectionStatus _lastStatus = ConnectionStatus.Disconnected;
    private SessionInfo[]? _lastSessions;

    public HomePage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;

        UpdateConnectionStatus(hub.CurrentStatus, hub.Settings?.GetEffectiveGatewayUrl());

        if (hub.Settings != null)
        {
            GatewayUrlText.Text = hub.Settings.GetEffectiveGatewayUrl();
        }

        if (hub.LastSessions != null)
            UpdateSessions(hub.LastSessions);

        if (!_buttonsWired)
        {
            DashboardButton.Click += (s, e) => _hub?.OpenDashboardAction?.Invoke(null);
            ChatButton.Click += (s, e) => _hub?.NavigateTo("chat");
            HealthCheckButton.Click += async (s, e) =>
            {
                if (_hub?.GatewayClient != null) await _hub.GatewayClient.CheckHealthAsync();
            };
            ScanButton.Click += OnScanForGateways;
            _buttonsWired = true;
        }
    }

    // ── Zone A: Companion Stage status updates ──

    public void UpdateConnectionStatus(ConnectionStatus status, string? gatewayUrl)
    {
        _lastStatus = status;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!string.IsNullOrEmpty(gatewayUrl))
                GatewayUrlText.Text = gatewayUrl;

            UpdateMoltyRing(status);
            UpdateStatusText(status);
        });
    }

    private void UpdateMoltyRing(ConnectionStatus status)
    {
        bool hasActiveSessions = _lastSessions?.Any(s =>
            string.Equals(s.Status, "active", StringComparison.OrdinalIgnoreCase)) ?? false;

        if (status == ConnectionStatus.Connected && hasActiveSessions)
        {
            // Agent working — animated blue ring
            MoltyRing.Visibility = Visibility.Collapsed;
            MoltyProgressRing.IsActive = true;
            MoltyProgressRing.Visibility = Visibility.Visible;
        }
        else
        {
            MoltyProgressRing.IsActive = false;
            MoltyProgressRing.Visibility = Visibility.Collapsed;
            MoltyRing.Visibility = Visibility.Visible;

            MoltyRing.Stroke = status switch
            {
                ConnectionStatus.Connected => new SolidColorBrush(Colors.LimeGreen),
                ConnectionStatus.Error => new SolidColorBrush(Colors.Red),
                ConnectionStatus.Connecting => new SolidColorBrush(Colors.DodgerBlue),
                _ => new SolidColorBrush(Colors.Gray)
            };
        }
    }

    private void UpdateStatusText(ConnectionStatus status)
    {
        int sessionCount = _lastSessions?.Length ?? 0;
        int activeCount = _lastSessions?.Count(s =>
            string.Equals(s.Status, "active", StringComparison.OrdinalIgnoreCase)) ?? 0;
        var channels = _lastSessions?
            .Where(s => !string.IsNullOrEmpty(s.Channel))
            .Select(s => s.Channel!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        StatusHeadline.Text = status switch
        {
            ConnectionStatus.Connected when activeCount > 0 =>
                $"Agent active — {activeCount} session{(activeCount != 1 ? "s" : "")}",
            ConnectionStatus.Connected when sessionCount > 0 =>
                $"All agents idle — watching {channels.Length} channel{(channels.Length != 1 ? "s" : "")}",
            ConnectionStatus.Connected =>
                "Connected — no sessions",
            ConnectionStatus.Connecting => "Connecting to gateway…",
            ConnectionStatus.Error => "Connection error",
            _ => "Not connected to gateway"
        };

        StatusSubtext.Text = status switch
        {
            ConnectionStatus.Connected when activeCount > 0 && channels.Length > 0 =>
                $"Active on {string.Join(", ", channels)}",
            ConnectionStatus.Connected when channels.Length > 0 =>
                $"Channels: {string.Join(", ", channels)}",
            _ => ""
        };
    }

    // ── Zone B: Agent Roster (removed — agents now in nav sidebar) ──

    public void UpdateSessions(SessionInfo[] sessions)
    {
        _lastSessions = sessions;
        DispatcherQueue?.TryEnqueue(() =>
        {
            UpdateMoltyRing(_lastStatus);
            UpdateStatusText(_lastStatus);
        });
    }

    public void UpdateNodes(GatewayNodeInfo[] nodes)
    {
        // Nodes are no longer displayed on HomePage; this method is kept
        // so HubWindow callers don't break.
    }

    // ── Gateway Discovery (as dialog) ──

    private Services.GatewayDiscoveryService? _discoveryService;

    private async void OnScanForGateways(object sender, RoutedEventArgs e)
    {
        _discoveryService ??= new Services.GatewayDiscoveryService();

        var dialog = new ContentDialog
        {
            Title = "Gateway Discovery",
            CloseButtonText = "Close",
            XamlRoot = this.XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };

        var stack = new StackPanel { Spacing = 8, MinWidth = 360 };
        var statusText = new TextBlock
        {
            Text = "Scanning for gateways on your network…",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var progress = new ProgressBar { IsIndeterminate = true };
        var resultList = new StackPanel { Spacing = 4 };

        stack.Children.Add(statusText);
        stack.Children.Add(progress);
        stack.Children.Add(resultList);
        dialog.Content = stack;

        // Start scan in background, then update dialog
        _ = ScanAndPopulateAsync(statusText, progress, resultList, dialog);

        await dialog.ShowAsync();
    }

    private async System.Threading.Tasks.Task ScanAndPopulateAsync(
        TextBlock statusText, ProgressBar progress,
        StackPanel resultList, ContentDialog dialog)
    {
        try
        {
            await _discoveryService!.StartDiscoveryAsync();
            var gateways = _discoveryService.Gateways;

            DispatcherQueue?.TryEnqueue(() =>
            {
                progress.IsIndeterminate = false;
                progress.Visibility = Visibility.Collapsed;

                if (gateways.Count == 0)
                {
                    statusText.Text = "No gateways found on your network";
                    return;
                }

                statusText.Text = $"Found {gateways.Count} gateway{(gateways.Count != 1 ? "s" : "")}";

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
                                GatewayUrlText.Text = capturedGw.HttpUrl;
                                dialog.Hide();
                            }
                        };
                        Grid.SetColumn(selectBtn, 1);
                        grid.Children.Add(selectBtn);
                    }
                    else
                    {
                        var check = new TextBlock
                        {
                            Text = "✓ Connected",
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = new SolidColorBrush(Colors.Green)
                        };
                        Grid.SetColumn(check, 1);
                        grid.Children.Add(check);
                    }

                    card.Child = grid;
                    resultList.Children.Add(card);
                }
            });
        }
        catch (Exception ex)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                progress.IsIndeterminate = false;
                progress.Visibility = Visibility.Collapsed;
                statusText.Text = $"Discovery failed: {ex.Message}";
            });
        }
    }

    // ── Agent List from agents.list ──

    public void UpdateAgentsList(JsonElement data)
    {
        // Agent roster removed from home page — agents are now in the nav sidebar.
        // Method kept for HubWindow compatibility.
    }
}
