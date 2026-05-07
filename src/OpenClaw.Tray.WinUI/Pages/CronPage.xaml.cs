using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace OpenClawTray.Pages;

public sealed partial class CronPage : Page
{
    private HubWindow? _hub;

    public CronPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        if (hub.GatewayClient != null)
        {
            _ = hub.GatewayClient.RequestCronListAsync();
            _ = hub.GatewayClient.RequestCronStatusAsync();
        }
    }

    private void OnRunNowClick(object sender, RoutedEventArgs e)
    {
        var jobId = (sender as Button)?.Tag as string;
        if (string.IsNullOrEmpty(jobId) || _hub?.GatewayClient == null) return;
        _ = _hub.GatewayClient.RunCronJobAsync(jobId);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        var jobId = (sender as Button)?.Tag as string;
        if (string.IsNullOrEmpty(jobId) || _hub?.GatewayClient == null) return;
        _ = _hub.GatewayClient.RemoveCronJobAsync(jobId).ContinueWith(_ =>
        {
            DispatcherQueue?.TryEnqueue(() => _hub.GatewayClient.RequestCronListAsync());
        });
    }

    public void UpdateFromGateway(JsonElement data)
    {
        OpenClawTray.Services.Logger.Info("[CronPage] Received gateway cron data");

        // Determine whether this is a cron.list (payload is array) or cron.status (payload is object)
        if (!data.TryGetProperty("payload", out var payload))
            return;

        if (payload.ValueKind == JsonValueKind.Array)
        {
            ParseCronList(payload);
        }
        else if (payload.ValueKind == JsonValueKind.Object)
        {
            ParseCronStatus(payload);
        }
    }

    private void ParseCronList(JsonElement payload)
    {
        var jobs = new List<CronJobViewModel>();

        foreach (var item in payload.EnumerateArray())
        {
            var vm = new CronJobViewModel();

            if (item.TryGetProperty("id", out var idEl))
                vm.Id = idEl.GetString() ?? "";

            if (item.TryGetProperty("name", out var nameEl))
                vm.Name = nameEl.GetString() ?? "";

            if (item.TryGetProperty("schedule", out var schedEl))
                vm.Schedule = schedEl.GetString() ?? "";

            if (item.TryGetProperty("enabled", out var enabledEl))
                vm.IsEnabled = enabledEl.ValueKind == JsonValueKind.True;

            if (item.TryGetProperty("lastRunAt", out var lastRunEl) && lastRunEl.ValueKind == JsonValueKind.Number)
            {
                var ms = lastRunEl.GetInt64();
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                vm.LastRunTime = dt.ToString("yyyy-MM-dd HH:mm");
            }
            else
            {
                vm.LastRunTime = "—";
            }

            if (item.TryGetProperty("lastRunOk", out var okEl))
            {
                if (okEl.ValueKind == JsonValueKind.True)
                {
                    vm.LastResult = "success";
                    vm.ResultBadgeBackground = new SolidColorBrush(Colors.Green);
                }
                else if (okEl.ValueKind == JsonValueKind.False)
                {
                    vm.LastResult = "fail";
                    vm.ResultBadgeBackground = new SolidColorBrush(Colors.Red);
                }
                else
                {
                    vm.LastResult = "—";
                    vm.ResultBadgeBackground = new SolidColorBrush(Colors.Gray);
                }
            }

            jobs.Add(vm);
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            if (jobs.Count > 0)
            {
                JobsList.ItemsSource = jobs;
                JobsList.Visibility = Visibility.Visible;
                EmptyState.Visibility = Visibility.Collapsed;
            }
            else
            {
                JobsList.ItemsSource = null;
                JobsList.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Visible;
            }
        });
    }

    private void ParseCronStatus(JsonElement payload)
    {
        var enabled = true;
        if (payload.TryGetProperty("enabled", out var enabledEl))
            enabled = enabledEl.ValueKind == JsonValueKind.True;

        string storePath = "~/.openclaw/cron";
        if (payload.TryGetProperty("storePath", out var storeEl))
            storePath = storeEl.GetString() ?? storePath;

        string nextWake = "—";
        if (payload.TryGetProperty("nextWakeAtMs", out var wakeEl) && wakeEl.ValueKind == JsonValueKind.Number)
        {
            var ms = wakeEl.GetInt64();
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            nextWake = dt.ToString("yyyy-MM-dd HH:mm");
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            SchedulerToggle.IsOn = enabled;
            SchedulerStatusText.Text = enabled ? "Enabled" : "Disabled";
            SchedulerStatusIndicator.Fill = new SolidColorBrush(enabled ? Colors.LimeGreen : Colors.Gray);
            StorePathText.Text = $"Store: {storePath}";
            NextWakeText.Text = $"Next wake: {nextWake}";
        });
    }

    private class CronJobViewModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Schedule { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
        public string LastRunTime { get; set; } = "";
        public string LastResult { get; set; } = "";
        public SolidColorBrush ResultBadgeBackground { get; set; } = new(Colors.Gray);
        public string NextRunTime { get; set; } = "";
    }
}
