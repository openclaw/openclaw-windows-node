using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Windows.UI;

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

    private void OnToggleEnabledClick(object sender, RoutedEventArgs e)
    {
        var jobId = (sender as Button)?.Tag as string;
        if (string.IsNullOrEmpty(jobId) || _hub?.GatewayClient == null) return;

        if (JobsList.ItemsSource is List<CronJobViewModel> jobs)
        {
            var vm = jobs.Find(j => j.Id == jobId);
            if (vm != null)
            {
                _ = _hub.GatewayClient.UpdateCronJobAsync(new { jobId, patch = new { enabled = !vm.IsEnabled } });
            }
        }
    }

    public void UpdateFromGateway(JsonElement data)
    {
        // The gateway client passes the payload directly (not wrapped)
        if (data.ValueKind == JsonValueKind.Array)
        {
            ParseCronList(data);
        }
        else if (data.ValueKind == JsonValueKind.Object)
        {
            // cron.list returns { jobs: [...], total, offset, limit, hasMore, ... }
            if (data.TryGetProperty("jobs", out var jobsEl) && jobsEl.ValueKind == JsonValueKind.Array)
            {
                ParseCronList(jobsEl);
            }
            // cron.status returns { enabled, storePath, jobs (count), nextWakeAtMs }
            else if (data.TryGetProperty("nextWakeAtMs", out _) || data.TryGetProperty("storePath", out _))
            {
                ParseCronStatus(data);
            }
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
            {
                if (schedEl.ValueKind == JsonValueKind.Object)
                {
                    var kind = schedEl.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? "" : "";
                    var tz = schedEl.TryGetProperty("tz", out var tzEl) ? tzEl.GetString() ?? "" : "";
                    vm.Schedule = kind switch
                    {
                        "cron" => FormatCronSchedule(schedEl, tz),
                        "every" => FormatEverySchedule(schedEl),
                        "at" => FormatAtSchedule(schedEl, tz),
                        _ => kind
                    };
                }
                else
                {
                    vm.Schedule = schedEl.GetString() ?? "";
                }
            }

            if (item.TryGetProperty("enabled", out var enabledEl))
                vm.IsEnabled = enabledEl.ValueKind == JsonValueKind.True;

            // Session target & wake mode chips
            if (item.TryGetProperty("sessionTarget", out var stEl))
            {
                vm.SessionTarget = stEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(vm.SessionTarget))
                    vm.SessionTargetVisibility = Visibility.Visible;
            }
            if (item.TryGetProperty("wakeMode", out var wmEl))
            {
                vm.WakeMode = wmEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(vm.WakeMode))
                    vm.WakeModeVisibility = Visibility.Visible;
            }

            // Delivery chip
            if (item.TryGetProperty("delivery", out var delEl) && delEl.ValueKind == JsonValueKind.Object)
            {
                var mode = delEl.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() ?? "" : "";
                var channel = delEl.TryGetProperty("channel", out var chEl) ? chEl.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(mode) && mode != "none")
                {
                    vm.DeliveryText = string.IsNullOrEmpty(channel) ? $"delivery: {mode}" : $"delivery: {mode} → {channel}";
                    vm.DeliveryVisibility = Visibility.Visible;
                }
            }

            // Description from payload message
            if (item.TryGetProperty("payload", out var payEl) && payEl.ValueKind == JsonValueKind.Object)
            {
                if (payEl.TryGetProperty("message", out var msgEl))
                {
                    vm.Description = msgEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(vm.Description))
                        vm.DescriptionVisibility = Visibility.Visible;
                }
            }
            // Also check top-level description
            if (string.IsNullOrEmpty(vm.Description) && item.TryGetProperty("description", out var descEl))
            {
                vm.Description = descEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(vm.Description))
                    vm.DescriptionVisibility = Visibility.Visible;
            }

            // --- State fields are nested under "state" ---
            var state = item.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.Object
                ? stateEl : item; // fallback to top-level for compat

            // Duration
            if (state.TryGetProperty("lastDurationMs", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
            {
                var durMs = durEl.GetInt64();
                if (durMs > 0)
                {
                    var durSpan = TimeSpan.FromMilliseconds(durMs);
                    vm.LastDuration = durSpan.TotalSeconds >= 60
                        ? $"{durSpan.TotalMinutes:0.#}m"
                        : $"{durSpan.TotalSeconds:0.#}s";
                    vm.DurationVisibility = Visibility.Visible;
                }
            }

            // Next run
            if (state.TryGetProperty("nextRunAtMs", out var nextEl) && nextEl.ValueKind == JsonValueKind.Number)
            {
                var ms = nextEl.GetInt64();
                if (ms > 0)
                {
                    vm.NextRunAtMs = ms;
                    var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                    vm.NextRunTime = dt.ToString("yyyy-MM-dd HH:mm");
                }
            }

            if (state.TryGetProperty("lastRunAtMs", out var lastRunEl) && lastRunEl.ValueKind == JsonValueKind.Number)
            {
                var ms = lastRunEl.GetInt64();
                vm.LastRunAtMs = ms;
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                vm.LastRunTime = dt.ToString("yyyy-MM-dd HH:mm");
            }
            else
            {
                vm.LastRunTime = "—";
            }

            if (state.TryGetProperty("lastRunStatus", out var statusEl))
            {
                var status = statusEl.GetString() ?? "";
                if (status == "ok" || status == "success")
                {
                    vm.LastResult = "success";
                    vm.ResultBadgeBackground = new SolidColorBrush(Color.FromArgb(40, 76, 175, 80));
                    vm.ResultBadgeForeground = new SolidColorBrush(Colors.LimeGreen);
                    vm.ResultBadgeVisibility = Visibility.Visible;
                }
                else if (!string.IsNullOrEmpty(status) && status != "none")
                {
                    vm.LastResult = status;
                    vm.ResultBadgeBackground = new SolidColorBrush(Color.FromArgb(40, 224, 85, 69));
                    vm.ResultBadgeForeground = new SolidColorBrush(Color.FromArgb(255, 224, 85, 69));
                    vm.ResultBadgeVisibility = Visibility.Visible;
                }
            }
            else if (state.TryGetProperty("lastRunOk", out var okEl))
            {
                if (okEl.ValueKind == JsonValueKind.True)
                {
                    vm.LastResult = "success";
                    vm.ResultBadgeBackground = new SolidColorBrush(Color.FromArgb(40, 76, 175, 80));
                    vm.ResultBadgeForeground = new SolidColorBrush(Colors.LimeGreen);
                    vm.ResultBadgeVisibility = Visibility.Visible;
                }
                else if (okEl.ValueKind == JsonValueKind.False)
                {
                    vm.LastResult = "fail";
                    vm.ResultBadgeBackground = new SolidColorBrush(Color.FromArgb(40, 224, 85, 69));
                    vm.ResultBadgeForeground = new SolidColorBrush(Color.FromArgb(255, 224, 85, 69));
                    vm.ResultBadgeVisibility = Visibility.Visible;
                }
            }

            // Build compact summary line with relative times
            BuildSummaryLine(vm);

            jobs.Add(vm);
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            JobCountText.Text = $"({jobs.Count})";
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
            StorePathText.Text = storePath;
            NextWakeText.Text = $"· Next wake: {nextWake}";
        });
    }

    private static string FormatCronSchedule(JsonElement sched, string tz)
    {
        var expr = sched.TryGetProperty("expr", out var exprEl) ? exprEl.GetString() ?? "" : "";
        return $"cron: {expr}" + (string.IsNullOrEmpty(tz) ? "" : $" ({tz})");
    }

    private static string FormatEverySchedule(JsonElement sched)
    {
        if (sched.TryGetProperty("everyMs", out var msEl) && msEl.ValueKind == JsonValueKind.Number)
        {
            var totalMs = msEl.GetInt64();
            var span = TimeSpan.FromMilliseconds(totalMs);
            if (span.TotalDays >= 2) return $"every {span.TotalDays:0.#} days";
            if (span.TotalDays >= 1 && span.TotalDays < 2) return "every day";
            if (span.TotalHours >= 2) return $"every {span.TotalHours:0.#} hours";
            if (span.TotalHours >= 1 && span.TotalHours < 2) return "every hour";
            if (span.TotalMinutes >= 2) return $"every {span.TotalMinutes:0.#} min";
            if (span.TotalMinutes >= 1 && span.TotalMinutes < 2) return "every minute";
            return $"every {span.TotalSeconds:0.#} sec";
        }
        // Fallback: try "every" as a string like "30m", "1h"
        if (sched.TryGetProperty("every", out var everyEl))
            return $"every {everyEl.GetString()}";
        return "every ?";
    }

    private static string FormatAtSchedule(JsonElement sched, string tz)
    {
        // "at" field is an ISO date string
        if (sched.TryGetProperty("at", out var atEl) && atEl.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(atEl.GetString(), out var dto))
            {
                var local = dto.LocalDateTime;
                return $"at {local:yyyy-MM-dd HH:mm}" + (string.IsNullOrEmpty(tz) ? "" : $" ({tz})");
            }
            return $"at {atEl.GetString()}";
        }
        // Fallback: "atMs" as unix timestamp
        if (sched.TryGetProperty("atMs", out var msEl) && msEl.ValueKind == JsonValueKind.Number)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(msEl.GetInt64()).LocalDateTime;
            return $"at {dt:yyyy-MM-dd HH:mm}";
        }
        return "at ?";
    }

    private static void ApplyExpandState(Grid grid, bool isExpanded)
    {
        if (grid.Children.Count < 3) return;

        // Row 2 is the detail panel (was Row 3 before layout change)
        if (grid.Children[2] is StackPanel detailPanel)
            detailPanel.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;

        // Chevron is inside the header grid (Row 0 child), last column
        if (grid.Children[0] is Grid headerGrid)
        {
            for (int i = headerGrid.Children.Count - 1; i >= 0; i--)
            {
                if (headerGrid.Children[i] is FontIcon chevron)
                {
                    chevron.Glyph = isExpanded ? "\uE70E" : "\uE70D";
                    break;
                }
            }
        }
    }

    private void JobsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CronJobViewModel vm) return;
        vm.IsExpanded = !vm.IsExpanded;

        var container = JobsList.ContainerFromItem(e.ClickedItem) as ListViewItem;
        if (container?.ContentTemplateRoot is Grid grid)
            ApplyExpandState(grid, vm.IsExpanded);
    }

    private void JobsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not CronJobViewModel vm) return;
        if (args.ItemContainer?.ContentTemplateRoot is Grid grid)
            ApplyExpandState(grid, vm.IsExpanded);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var found = FindChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    private static void BuildSummaryLine(CronJobViewModel vm)
    {
        var parts = new List<string>();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (vm.LastRunAtMs > 0)
        {
            var ago = FormatRelativeTime(nowMs - vm.LastRunAtMs);
            parts.Add($"ran {ago} ago");
        }

        if (vm.NextRunAtMs > 0)
        {
            var until = vm.NextRunAtMs - nowMs;
            if (until > 0)
                parts.Add($"next in {FormatRelativeTime(until)}");
            else
                parts.Add("overdue");
        }

        if (parts.Count > 0)
        {
            vm.SummaryLine = string.Join(" · ", parts);
            vm.SummaryVisibility = Visibility.Visible;
        }
    }

    private static string FormatRelativeTime(long ms)
    {
        if (ms < 0) ms = -ms;
        var span = TimeSpan.FromMilliseconds(ms);
        if (span.TotalMinutes < 1) return "<1m";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return span.Minutes > 0 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d {span.Hours}h";
    }

    private class CronJobViewModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Schedule { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
        public bool IsExpanded { get; set; } = false;
        public double CardOpacity => IsEnabled ? 1.0 : 0.5;
        public string EnabledText => IsEnabled ? "enabled" : "disabled";
        public SolidColorBrush EnabledBadgeBackground => IsEnabled
            ? new SolidColorBrush(Color.FromArgb(40, 76, 175, 80))
            : new SolidColorBrush(Color.FromArgb(40, 230, 168, 23));
        public SolidColorBrush EnabledBadgeForeground => IsEnabled
            ? new SolidColorBrush(Colors.LimeGreen)
            : new SolidColorBrush(Color.FromArgb(255, 230, 168, 23));
        public string LastRunTime { get; set; } = "—";
        public string LastResult { get; set; } = "";
        public SolidColorBrush ResultBadgeBackground { get; set; } = new(Colors.Gray);
        public SolidColorBrush ResultBadgeForeground { get; set; } = new(Colors.White);
        public Visibility ResultBadgeVisibility { get; set; } = Visibility.Collapsed;
        public string NextRunTime { get; set; } = "—";
        public string LastDuration { get; set; } = "";
        public Visibility DurationVisibility { get; set; } = Visibility.Collapsed;
        public long LastRunAtMs { get; set; } = 0;
        public long NextRunAtMs { get; set; } = 0;
        public string SummaryLine { get; set; } = "";
        public Visibility SummaryVisibility { get; set; } = Visibility.Collapsed;
        public string SessionTarget { get; set; } = "";
        public string SessionTargetLabel => string.IsNullOrEmpty(SessionTarget) ? "" : $"session: {SessionTarget}";
        public Visibility SessionTargetVisibility { get; set; } = Visibility.Collapsed;
        public string WakeMode { get; set; } = "";
        public string WakeModeLabel => string.IsNullOrEmpty(WakeMode) ? "" : $"wake: {WakeMode}";
        public Visibility WakeModeVisibility { get; set; } = Visibility.Collapsed;
        public string DeliveryText { get; set; } = "";
        public Visibility DeliveryVisibility { get; set; } = Visibility.Collapsed;
        public string ToggleEnabledLabel => IsEnabled ? "⏸ Disable" : "▶ Enable";
        public string ToggleEnabledGlyph => IsEnabled ? "\uE7E8" : "\uE7E8";
        public string ToggleEnabledText => IsEnabled ? "Disable" : "Enable";
        public string Description { get; set; } = "";
        public Visibility DescriptionVisibility { get; set; } = Visibility.Collapsed;
        public string DetailLine { get; set; } = "";
    }
}
