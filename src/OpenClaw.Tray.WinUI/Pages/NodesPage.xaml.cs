using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using WinDataTransfer = global::Windows.ApplicationModel.DataTransfer;
using WinColor = global::Windows.UI.Color;

namespace OpenClawTray.Pages;

public sealed partial class NodesPage : Page
{
    private HubWindow? _hub;

    public NodesPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        ConnectionWarning.Visibility = hub.GatewayClient != null ? Visibility.Collapsed : Visibility.Visible;
        if (hub.GatewayClient != null)
        {
            // Apply cached data immediately, then request fresh
            if (hub.LastNodes != null)
                UpdateNodes(hub.LastNodes);
            else
                NodesList.Children.Clear();
            _ = hub.GatewayClient.RequestNodesAsync();
        }
    }

    public void UpdateNodes(GatewayNodeInfo[] nodes)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (nodes.Length == 0)
            {
                NodesList.Children.Clear();
                EmptyState.Visibility = Visibility.Visible;
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;
            var vms = new List<NodeViewModel>();
            foreach (var n in nodes)
            {
                vms.Add(new NodeViewModel
                {
                    Name = string.IsNullOrWhiteSpace(n.DisplayName) ? n.ShortId : n.DisplayName,
                    DeviceId = n.NodeId,
                    Platform = n.Platform ?? "unknown",
                    IsOnline = n.IsOnline,
                    Capabilities = Array.Empty<string>(),
                    Commands = Array.Empty<string>(),
                });
            }
            RenderNodes(vms);
        });
    }

    private void RenderNodes(List<NodeViewModel> nodes)
    {
        NodesList.Children.Clear();
        if (nodes.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;

        foreach (var vm in nodes)
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
            };

            var stack = new StackPanel { Spacing = 8 };

            // Header: name + online dot
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new Ellipse
            {
                Width = 10, Height = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = vm.IsOnline ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Gray),
            });
            header.Children.Add(new TextBlock
            {
                Text = vm.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(header);

            // Platform badge
            var platformBadge = new Border
            {
                Background = new SolidColorBrush(GetPlatformColor(vm.Platform)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            platformBadge.Child = new TextBlock
            {
                Text = vm.Platform,
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.White),
            };
            stack.Children.Add(platformBadge);

            // Device ID with copy button
            var idRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var shortId = vm.DeviceId.Length > 16 ? vm.DeviceId[..16] + "…" : vm.DeviceId;
            idRow.Children.Add(new TextBlock
            {
                Text = shortId,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var copyBtn = new Button
            {
                Content = "📋",
                Padding = new Thickness(4, 2, 4, 2),
                Tag = vm.DeviceId,
                FontSize = 11,
            };
            copyBtn.Click += OnCopyDeviceId;
            idRow.Children.Add(copyBtn);
            stack.Children.Add(idRow);

            // Capabilities as tags
            if (vm.Capabilities.Length > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "Capabilities",
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Margin = new Thickness(0, 4, 0, 0),
                });
                var capWrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                foreach (var cap in vm.Capabilities)
                {
                    var badge = new Border
                    {
                        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                    };
                    badge.Child = new TextBlock { Text = cap, FontSize = 11 };
                    capWrap.Children.Add(badge);
                }
                stack.Children.Add(capWrap);
            }

            // Commands (collapsed by default in an Expander)
            if (vm.Commands.Length > 0)
            {
                var expander = new Expander
                {
                    Header = $"Commands ({vm.Commands.Length})",
                    IsExpanded = false,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                var cmdStack = new StackPanel { Spacing = 2 };
                foreach (var cmd in vm.Commands)
                {
                    cmdStack.Children.Add(new TextBlock
                    {
                        Text = $"  • {cmd}",
                        FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    });
                }
                expander.Content = cmdStack;
                stack.Children.Add(expander);
            }

            card.Child = stack;
            NodesList.Children.Add(card);
        }
    }

    private void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string deviceId)
        {
            var dataPackage = new WinDataTransfer.DataPackage();
            dataPackage.SetText(deviceId);
            WinDataTransfer.Clipboard.SetContent(dataPackage);
            btn.Content = "✓";
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (t, a) => { btn.Content = "📋"; timer.Stop(); };
            timer.Start();
        }
    }

    private static WinColor GetPlatformColor(string platform) => platform.ToLowerInvariant() switch
    {
        "windows" => WinColor.FromArgb(255, 0, 120, 215),
        "macos" => WinColor.FromArgb(255, 162, 132, 94),
        "linux" => WinColor.FromArgb(255, 221, 72, 20),
        "ios" => WinColor.FromArgb(255, 0, 122, 255),
        "android" => WinColor.FromArgb(255, 61, 220, 132),
        _ => WinColor.FromArgb(255, 128, 128, 128),
    };

    public class NodeViewModel
    {
        public string Name { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string Platform { get; set; } = "";
        public bool IsOnline { get; set; }
        public string[] Capabilities { get; set; } = Array.Empty<string>();
        public string[] Commands { get; set; } = Array.Empty<string>();
    }

    public void UpdatePairingRequests(PairingListInfo data)
    {
        PairingList.Children.Clear();
        if (data.Pending.Count == 0)
        {
            PairingSection.Visibility = Visibility.Collapsed;
            return;
        }
        PairingSection.Visibility = Visibility.Visible;

        foreach (var req in data.Pending)
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 4 };
            info.Children.Add(new TextBlock
            {
                Text = req.DisplayName ?? req.NodeId,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{req.Platform ?? "unknown"} · {req.RemoteIp ?? ""}",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            });
            if (req.IsRepair)
            {
                info.Children.Add(new TextBlock
                {
                    Text = "⚠️ Repair request",
                    Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
                });
            }
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var approveBtn = new Button { Content = "Approve", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var rejectBtn = new Button { Content = "Reject" };
            var capturedId = req.RequestId;
            approveBtn.Click += async (s, e) => { if (_hub?.GatewayClient != null) await _hub.GatewayClient.NodePairApproveAsync(capturedId); };
            rejectBtn.Click += async (s, e) => { if (_hub?.GatewayClient != null) await _hub.GatewayClient.NodePairRejectAsync(capturedId); };
            buttons.Children.Add(approveBtn);
            buttons.Children.Add(rejectBtn);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            card.Child = grid;
            PairingList.Children.Add(card);
        }
    }

    public void UpdateDevicePairingRequests(DevicePairingListInfo data)
    {
        DevicePairingList.Children.Clear();
        if (data.Pending.Count == 0)
        {
            DevicePairingSection.Visibility = Visibility.Collapsed;
            return;
        }
        DevicePairingSection.Visibility = Visibility.Visible;

        foreach (var req in data.Pending)
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 4 };
            info.Children.Add(new TextBlock
            {
                Text = req.DisplayName ?? req.DeviceId,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            var detail = $"{req.Platform ?? "unknown"}";
            if (!string.IsNullOrEmpty(req.Role)) detail += $" · {req.Role}";
            if (!string.IsNullOrEmpty(req.RemoteIp)) detail += $" · {req.RemoteIp}";
            info.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            });
            if (req.Scopes is { Length: > 0 })
            {
                info.Children.Add(new TextBlock
                {
                    Text = $"Scopes: {string.Join(", ", req.Scopes)}",
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
                });
            }
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var approveBtn = new Button { Content = "Approve", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var rejectBtn = new Button { Content = "Reject" };
            var capturedId = req.RequestId;
            approveBtn.Click += async (s, e) => { if (_hub?.GatewayClient != null) await _hub.GatewayClient.DevicePairApproveAsync(capturedId); };
            rejectBtn.Click += async (s, e) => { if (_hub?.GatewayClient != null) await _hub.GatewayClient.DevicePairRejectAsync(capturedId); };
            buttons.Children.Add(approveBtn);
            buttons.Children.Add(rejectBtn);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            card.Child = grid;
            DevicePairingList.Children.Add(card);
        }
    }

    public void UpdatePresence(PresenceEntry[] entries)
    {
        DispatcherQueue?.TryEnqueue(() => RenderPresence(entries));
    }

    private void RenderPresence(PresenceEntry[] entries)
    {
        PresenceList.Children.Clear();

        if (entries.Length == 0)
        {
            PresenceSection.Visibility = Visibility.Collapsed;
            return;
        }

        PresenceSection.Visibility = Visibility.Visible;

        foreach (var entry in entries)
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

            row.Children.Add(new TextBlock
            {
                Text = entry.DisplayName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (!string.IsNullOrEmpty(entry.Platform))
                row.Children.Add(new TextBlock
                {
                    Text = entry.PlatformLabel,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    VerticalAlignment = VerticalAlignment.Center
                });

            if (!string.IsNullOrEmpty(entry.Mode))
                row.Children.Add(new TextBlock
                {
                    Text = entry.ModeLabel,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    VerticalAlignment = VerticalAlignment.Center
                });

            if (!string.IsNullOrEmpty(entry.LastSeenText))
                row.Children.Add(new TextBlock
                {
                    Text = entry.LastSeenText,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    VerticalAlignment = VerticalAlignment.Center
                });

            card.Child = row;
            PresenceList.Children.Add(card);
        }
    }
}
