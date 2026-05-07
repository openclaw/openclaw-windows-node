using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;

namespace OpenClawTray.Pages;

public sealed partial class InstancesPage : Page
{
    private HubWindow? _hub;

    public InstancesPage() { InitializeComponent(); }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;

        if (hub.CurrentStatus != ConnectionStatus.Connected)
        {
            ConnectionWarning.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        ConnectionWarning.Visibility = Visibility.Collapsed;

        // Use presence data if available, fall back to empty
        if (hub.LastPresence != null)
            RenderPresence(hub.LastPresence);
        else
            EmptyState.Visibility = Visibility.Visible;
    }

    public void UpdatePresenceData(PresenceEntry[] entries)
    {
        DispatcherQueue?.TryEnqueue(() => RenderPresence(entries));
    }

    private void RenderPresence(PresenceEntry[] entries)
    {
        InstancesList.Children.Clear();

        if (entries.Length == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        var currentHost = Environment.MachineName;

        foreach (var entry in entries)
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
            };

            var stack = new StackPanel { Spacing = 6 };

            // Row 1: Name + badges
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            headerPanel.Children.Add(new TextBlock
            {
                Text = entry.DisplayName,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                VerticalAlignment = VerticalAlignment.Center
            });

            // Platform badge
            if (!string.IsNullOrEmpty(entry.Platform))
            {
                headerPanel.Children.Add(CreateBadge(entry.PlatformLabel,
                    global::Windows.UI.Color.FromArgb(255, 0, 120, 212)));
            }

            // Mode badge
            if (!string.IsNullOrEmpty(entry.Mode))
            {
                headerPanel.Children.Add(CreateBadge(entry.ModeLabel,
                    global::Windows.UI.Color.FromArgb(255, 100, 100, 100)));
            }

            // Device family badge
            if (!string.IsNullOrEmpty(entry.DeviceFamily))
            {
                headerPanel.Children.Add(CreateBadge(entry.DeviceFamily,
                    global::Windows.UI.Color.FromArgb(255, 80, 80, 160)));
            }

            // "This instance" badge
            bool isCurrent = entry.Host?.Equals(currentHost, StringComparison.OrdinalIgnoreCase) == true
                          || entry.DeviceId?.Contains(currentHost, StringComparison.OrdinalIgnoreCase) == true;
            if (isCurrent)
            {
                headerPanel.Children.Add(CreateBadge("This instance",
                    global::Windows.UI.Color.FromArgb(255, 34, 139, 34)));
            }

            stack.Children.Add(headerPanel);

            // Row 2: Details
            var details = new List<string>();
            details.Add("🟢 Connected");

            if (!string.IsNullOrEmpty(entry.Ip)) details.Add(entry.Ip);
            if (!string.IsNullOrEmpty(entry.Version)) details.Add($"v{entry.Version}");
            if (!string.IsNullOrEmpty(entry.LastSeenText)) details.Add($"Last input {entry.LastSeenText}");

            // Roles
            if (entry.Roles is { Length: > 0 })
                details.Add($"Roles: {string.Join(", ", entry.Roles)}");

            stack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", details),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });

            // Row 3: Instance/Device ID
            var idText = entry.InstanceId ?? entry.DeviceId;
            if (!string.IsNullOrEmpty(idText))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"ID: {idText}",
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    FontFamily = new FontFamily("Consolas"),
                    IsTextSelectionEnabled = true
                });
            }

            // Row 4: Model identifier if present
            if (!string.IsNullOrEmpty(entry.ModelIdentifier))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"Device: {entry.ModelIdentifier}",
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                });
            }

            card.Child = stack;
            InstancesList.Children.Add(card);
        }
    }

    private static Border CreateBadge(string text, global::Windows.UI.Color color)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White)
            }
        };
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_hub?.LastPresence != null)
            RenderPresence(_hub.LastPresence);
    }
}
