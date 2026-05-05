using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Pages;

public sealed partial class SessionsPage : Page
{
    private HubWindow? _hub;

    public SessionsPage()
    {
        InitializeComponent();

        // Sample data for design-time preview
        var samples = new List<SessionViewModel>
        {
            new() { Key = "agent:main", Preview = "Help me refactor the authentication module to use JWT tokens...", TimeAgo = "2m ago", ThinkingLevel = "medium", VerboseLevel = null, IsActive = true },
            new() { Key = "agent:cron:daily-summary", Preview = "Generated daily summary for 3 channels with 47 messages.", TimeAgo = "1h ago", ThinkingLevel = null, VerboseLevel = "detailed", IsActive = false },
            new() { Key = "telegram:user:12345", Preview = "Remind me to check the deployment status at 5pm today.", TimeAgo = "15m ago", ThinkingLevel = null, VerboseLevel = null, IsActive = true },
        };
        SessionListView.ItemsSource = samples;
        EmptyState.Visibility = Visibility.Collapsed;
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        if (hub.GatewayClient != null)
        {
            ConnectionWarning.Visibility = Visibility.Collapsed;
            if (hub.LastSessions != null)
                UpdateSessions(hub.LastSessions);
            else
                SessionListView.ItemsSource = null;
            _ = hub.GatewayClient.RequestSessionsAsync();
            _ = hub.GatewayClient.RequestModelsListAsync();
        }
        else
        {
            ConnectionWarning.Visibility = Visibility.Visible;
        }
    }

    public void UpdateSessions(SessionInfo[] sessions)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (sessions.Length == 0)
            {
                SessionListView.ItemsSource = null;
                EmptyState.Visibility = Visibility.Visible;
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;
            SessionListView.ItemsSource = sessions.Select(s => new SessionViewModel
            {
                Key = s.Key,
                Preview = s.CurrentActivity ?? s.RichDisplayText,
                TimeAgo = s.AgeText,
                ThinkingLevel = s.ThinkingLevel,
                VerboseLevel = s.VerboseLevel,
                IsActive = s.Status == "active" || s.Status == "running",
            }).ToList();
        });
    }

    private async void OnResetSession(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            var client = _hub?.GatewayClient;
            if (client == null) { ShowNotConnected(); return; }
            try { await client.ResetSessionAsync(key); }
            catch (Exception) { /* reset failed silently */ }
        }
    }

    private async void OnDeleteSession(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            var client = _hub?.GatewayClient;
            if (client == null) { ShowNotConnected(); return; }
            try { await client.DeleteSessionAsync(key); }
            catch (Exception) { /* delete failed silently */ }
        }
    }

    private async void OnCompactSession(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            var client = _hub?.GatewayClient;
            if (client == null) { ShowNotConnected(); return; }
            try { await client.CompactSessionAsync(key); }
            catch (Exception) { /* compact failed silently */ }
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        var client = _hub?.GatewayClient;
        if (client != null)
        {
            _ = client.RequestSessionsAsync();
        }
        RefreshButton.Content = "Refreshing...";
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (t, a) => { RefreshButton.Content = "Refresh"; timer.Stop(); };
        timer.Start();
    }

    private void ShowNotConnected()
    {
        ConnectionWarning.Visibility = Visibility.Visible;
    }

    public class SessionViewModel
    {
        public string Key { get; set; } = "";
        public string Preview { get; set; } = "";
        public string TimeAgo { get; set; } = "";
        public string? ThinkingLevel { get; set; }
        public string? VerboseLevel { get; set; }
        public bool IsActive { get; set; }

        public string ThinkingBadge => !string.IsNullOrEmpty(ThinkingLevel) ? $"🧠 {ThinkingLevel}" : "";
        public Visibility ThinkingVisible => !string.IsNullOrEmpty(ThinkingLevel) ? Visibility.Visible : Visibility.Collapsed;
        public string VerboseBadge => !string.IsNullOrEmpty(VerboseLevel) ? $"📝 {VerboseLevel}" : "";
        public Visibility VerboseVisible => !string.IsNullOrEmpty(VerboseLevel) ? Visibility.Visible : Visibility.Collapsed;
    }

    public void UpdateModelsList(ModelsListInfo data)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            ModelsList.Children.Clear();
            if (data.Models.Count == 0)
            {
                ModelsSection.Visibility = Visibility.Collapsed;
                return;
            }
            ModelsSection.Visibility = Visibility.Visible;

            foreach (var model in data.Models)
            {
                var card = new Border
                {
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 8, 12, 8),
                };

                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                sp.Children.Add(new TextBlock { Text = model.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                if (!string.IsNullOrEmpty(model.Provider))
                    sp.Children.Add(new TextBlock
                    {
                        Text = model.Provider,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        VerticalAlignment = VerticalAlignment.Center
                    });
                if (model.ContextWindow is > 0)
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"{model.ContextWindow / 1000}K ctx",
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        VerticalAlignment = VerticalAlignment.Center
                    });

                card.Child = sp;
                ModelsList.Children.Add(card);
            }
        });
    }
}
