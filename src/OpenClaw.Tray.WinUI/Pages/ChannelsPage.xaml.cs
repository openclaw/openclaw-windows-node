using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;

namespace OpenClawTray.Pages;

public sealed partial class ChannelsPage : Page
{
    private HubWindow? _hub;

    public ChannelsPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        ConnectionWarning.Visibility = hub.GatewayClient != null ? Visibility.Collapsed : Visibility.Visible;
        if (hub.GatewayClient != null)
        {
            // Apply cached data immediately
            if (hub.LastChannels != null)
                UpdateChannels(hub.LastChannels);
            else
                ChannelsList.Children.Clear();
        }
    }

    public void UpdateChannels(ChannelHealth[] channels)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (channels.Length == 0)
            {
                ChannelsList.Children.Clear();
                EmptyState.Visibility = Visibility.Visible;
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;
            var vms = new List<ChannelViewModel>();
            foreach (var ch in channels)
            {
                var isHealthy = ChannelHealth.IsHealthyStatus(ch.Status);
                var isIntermediate = ChannelHealth.IsIntermediateStatus(ch.Status);
                vms.Add(new ChannelViewModel
                {
                    Name = ch.Name,
                    Status = ch.Error != null ? $"Error: {ch.Error}" : ch.Status,
                    StatusColor = isHealthy ? "Green" : (isIntermediate ? "Yellow" : "Red"),
                    IsRunning = isHealthy,
                    ProbeInfo = ch.AuthAge != null ? $"Auth age: {ch.AuthAge}" : null,
                });
            }
            RenderChannels(vms);
        });
    }

    private void RenderChannels(List<ChannelViewModel> channels)
    {
        ChannelsList.Children.Clear();
        if (channels.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;

        foreach (var vm in channels)
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

            // Header: name + status dot
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var dot = new Ellipse
            {
                Width = 10, Height = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = vm.StatusColor switch
                {
                    "Green" => new SolidColorBrush(Colors.LimeGreen),
                    "Yellow" => new SolidColorBrush(Colors.Orange),
                    _ => new SolidColorBrush(Colors.Red),
                },
            };
            header.Children.Add(dot);
            header.Children.Add(new TextBlock
            {
                Text = Capitalize(vm.Name),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(header);

            // Status text
            stack.Children.Add(new TextBlock
            {
                Text = vm.Status,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                FontSize = 12,
            });

            // Probe info
            if (!string.IsNullOrEmpty(vm.ProbeInfo))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = vm.ProbeInfo,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    FontSize = 12,
                });
            }

            // Start/Stop button
            var channelName = vm.Name;
            var actionBtn = new Button
            {
                Content = vm.IsRunning ? "Stop" : "Start",
                Padding = new Thickness(12, 4, 12, 4),
                Tag = channelName,
            };
            actionBtn.Click += vm.IsRunning ? OnStopChannel : OnStartChannel;
            stack.Children.Add(actionBtn);

            card.Child = stack;
            ChannelsList.Children.Add(card);
        }
    }

    private async void OnStartChannel(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
        {
            var client = _hub?.GatewayClient;
            if (client == null) { ConnectionWarning.Visibility = Visibility.Visible; return; }
            btn.IsEnabled = false;
            try
            {
                await client.StartChannelAsync(name);
                await client.CheckHealthAsync();
            }
            catch (Exception) { /* channel operation failed; button re-enabled below */ }
            finally { btn.IsEnabled = true; }
        }
    }

    private async void OnStopChannel(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
        {
            var client = _hub?.GatewayClient;
            if (client == null) { ConnectionWarning.Visibility = Visibility.Visible; return; }
            btn.IsEnabled = false;
            try
            {
                await client.StopChannelAsync(name);
                await client.CheckHealthAsync();
            }
            catch (Exception) { /* channel operation failed; button re-enabled below */ }
            finally { btn.IsEnabled = true; }
        }
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    public class ChannelViewModel
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string StatusColor { get; set; } = "Red";
        public bool IsRunning { get; set; }
        public string? ProbeInfo { get; set; }
    }
}
