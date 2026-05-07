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
    private List<CronJobViewModel> _jobs = new();
    private Border? _editingCard = null; // card hidden during inline edit

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
        var btn = sender as Button;
        var jobId = btn?.Tag as string;
        if (string.IsNullOrEmpty(jobId) || _hub?.GatewayClient == null) return;
        // Brief visual feedback
        var origContent = btn!.Content;
        btn.Content = "Triggered ✓";
        btn.IsEnabled = false;
        _ = _hub.GatewayClient.RunCronJobAsync(jobId);
        DispatcherQueue?.TryEnqueue(async () =>
        {
            await Task.Delay(1500);
            btn.Content = origContent;
            btn.IsEnabled = true;
        });
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

        var vm = _jobs.Find(j => j.Id == jobId);
        if (vm != null)
        {
            _ = _hub.GatewayClient.UpdateCronJobAsync(new { jobId, patch = new { enabled = !vm.IsEnabled } });
        }
    }

    // --- Job creation/edit form ---
    private string? _editingJobId = null; // null = creating new, set = editing existing

    private void OnNewJobClick(object sender, RoutedEventArgs e)
    {
        _editingJobId = null;
        RestoreFormFromInline(); // ensure form is back in its home position
        ResetForm();
        FormTitle.Text = "New Job";
        FormSaveButton.Content = "Create Job";
        JobFormPanel.Visibility = Visibility.Visible;
    }

    private void OnEditJobClick(object sender, RoutedEventArgs e)
    {
        var jobId = (sender as Button)?.Tag as string;
        if (string.IsNullOrEmpty(jobId)) return;
        var vm = _jobs.Find(j => j.Id == jobId);
        if (vm == null) return;

        _editingJobId = jobId;
        FormTitle.Text = "Edit Job";
        FormSaveButton.Content = "Save Changes";

        // Populate form fields from VM
        FormName.Text = vm.Name;
        FormMessage.Text = vm.Description;

        // Schedule
        var kind = vm.ScheduleKind;
        FormScheduleKind.SelectedIndex = kind switch { "at" => 1, "cron" => 2, _ => 0 };
        UpdateScheduleFieldVisibility(kind);

        if (kind == "cron")
        {
            FormCronExpr.Text = vm.ScheduleExpr;
            SelectComboByTag(FormTimezone, vm.ScheduleTz);
            HighlightPreset(vm.ScheduleExpr);
        }
        else if (kind == "every")
        {
            // Decompose everyMs into value + unit
            var ms = vm.ScheduleEveryMs;
            if (ms >= 86400000 && ms % 86400000 == 0) { FormEveryValue.Text = (ms / 86400000).ToString(); FormEveryUnit.SelectedIndex = 2; }
            else if (ms >= 3600000 && ms % 3600000 == 0) { FormEveryValue.Text = (ms / 3600000).ToString(); FormEveryUnit.SelectedIndex = 1; }
            else { FormEveryValue.Text = (ms / 60000).ToString(); FormEveryUnit.SelectedIndex = 0; }
        }
        else if (kind == "at")
        {
            if (DateTimeOffset.TryParse(vm.ScheduleAt, out var dto))
            {
                var local = dto.LocalDateTime;
                FormAtDate.Date = new DateTimeOffset(local);
                FormAtTime.Text = local.ToString("h:mm tt");
            }
            FormDeleteAfterRun.IsChecked = vm.DeleteAfterRun;
        }

        // Delivery
        var deliveryMode = vm.RawDeliveryMode;
        FormDeliveryMode.SelectedIndex = deliveryMode == "announce" ? 1 : 0;
        FormDeliveryChannel.Text = vm.RawDeliveryChannel;
        DeliveryChannelPanel.Visibility = deliveryMode == "announce" ? Visibility.Visible : Visibility.Collapsed;

        // Advanced
        SelectComboByTag(FormSessionTarget, vm.SessionTarget);
        SelectComboByTag(FormWakeMode, vm.WakeMode);

        FormError.Visibility = Visibility.Collapsed;

        // Inline: find the card in the list panel, collapse it, insert form there
        PlaceFormInline(jobId);
    }

    private void OnFormCancelClick(object sender, RoutedEventArgs e)
    {
        RestoreFormFromInline();
        JobFormPanel.Visibility = Visibility.Collapsed;
        _editingJobId = null;
    }

    private void OnFormSaveClick(object sender, RoutedEventArgs e)
    {
        // Validate
        var name = FormName.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowFormError("Name is required.");
            return;
        }

        var message = FormMessage.Text?.Trim();
        if (string.IsNullOrEmpty(message))
        {
            ShowFormError("Prompt is required.");
            return;
        }

        if (_hub?.GatewayClient == null)
        {
            ShowFormError("Not connected to gateway.");
            return;
        }

        var kind = GetSelectedTag(FormScheduleKind) ?? "cron";

        // Build schedule object (use dictionaries for reliable serialization)
        object schedule;
        if (kind == "cron")
        {
            var expr = FormCronExpr.Text?.Trim();
            if (string.IsNullOrEmpty(expr))
            {
                ShowFormError("Cron expression is required.");
                return;
            }
            var tz = GetSelectedTag(FormTimezone);
            var sched = new Dictionary<string, object> { ["kind"] = "cron", ["expr"] = expr };
            if (!string.IsNullOrEmpty(tz)) sched["tz"] = tz;
            schedule = sched;
        }
        else if (kind == "every")
        {
            if (!int.TryParse(FormEveryValue.Text?.Trim(), out var everyVal) || everyVal <= 0)
            {
                ShowFormError("Enter a valid interval number.");
                return;
            }
            var unitStr = GetSelectedTag(FormEveryUnit) ?? "60000";
            var unitMs = long.Parse(unitStr);
            var everyMs = (long)everyVal * unitMs;
            schedule = new Dictionary<string, object> { ["kind"] = "every", ["everyMs"] = everyMs };
        }
        else // at
        {
            var date = FormAtDate.Date;
            if (date == null)
            {
                ShowFormError("Date is required for 'at' schedule.");
                return;
            }
            if (!TryParseTime(FormAtTime.Text, out var time))
            {
                ShowFormError("Invalid time. Use format like '3:30 PM' or '15:30'.");
                return;
            }
            var dt = date.Value.Date + time;
            var localDto = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
            if (localDto < DateTimeOffset.Now)
            {
                ShowFormError("Scheduled time must be in the future.");
                return;
            }
            var isoAt = localDto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            schedule = new Dictionary<string, object> { ["kind"] = "at", ["at"] = isoAt };
        }

        var deliveryMode = GetSelectedTag(FormDeliveryMode) ?? "none";
        var deliveryChannel = FormDeliveryChannel.Text?.Trim();

        var sessionTarget = GetSelectedTag(FormSessionTarget) ?? "isolated";
        var wakeMode = GetSelectedTag(FormWakeMode) ?? "now";

        if (_editingJobId != null)
        {
            // Update existing job — payload.kind depends on sessionTarget
            var payloadKind = sessionTarget == "main" ? "systemEvent" : "agentTurn";
            var payloadTextField = sessionTarget == "main" ? "text" : "message";
            var patch = new Dictionary<string, object>
            {
                ["name"] = name,
                ["schedule"] = schedule,
                ["sessionTarget"] = sessionTarget,
                ["wakeMode"] = wakeMode,
                ["payload"] = new Dictionary<string, object> { ["kind"] = payloadKind, [payloadTextField] = message },
                ["delivery"] = !string.IsNullOrEmpty(deliveryChannel) && deliveryMode == "announce"
                    ? new Dictionary<string, object> { ["mode"] = deliveryMode, ["channel"] = deliveryChannel }
                    : new Dictionary<string, object> { ["mode"] = deliveryMode }
            };
            if (kind == "at")
                patch["deleteAfterRun"] = FormDeleteAfterRun.IsChecked == true;

            var updatePayload = new Dictionary<string, object>
            {
                ["jobId"] = _editingJobId,
                ["patch"] = patch
            };
            _ = _hub.GatewayClient.UpdateCronJobAsync(updatePayload);
        }
        else
        {
            // Create new job — payload.kind depends on sessionTarget
            var payloadKind = sessionTarget == "main" ? "systemEvent" : "agentTurn";
            var payloadTextField = sessionTarget == "main" ? "text" : "message";
            var job = new Dictionary<string, object>
            {
                ["name"] = name,
                ["enabled"] = true,
                ["schedule"] = schedule,
                ["sessionTarget"] = sessionTarget,
                ["wakeMode"] = wakeMode,
                ["payload"] = new Dictionary<string, object> { ["kind"] = payloadKind, [payloadTextField] = message },
                ["delivery"] = !string.IsNullOrEmpty(deliveryChannel) && deliveryMode == "announce"
                    ? new Dictionary<string, object> { ["mode"] = deliveryMode, ["channel"] = deliveryChannel }
                    : new Dictionary<string, object> { ["mode"] = deliveryMode }
            };
            if (kind == "at")
                job["deleteAfterRun"] = FormDeleteAfterRun.IsChecked == true;

            _ = _hub.GatewayClient.AddCronJobAsync(job);
        }

        RestoreFormFromInline();
        JobFormPanel.Visibility = Visibility.Collapsed;
        _editingJobId = null;
    }

    private void OnScheduleKindChanged(object sender, SelectionChangedEventArgs e)
    {
        var kind = GetSelectedTag(FormScheduleKind) ?? "cron";
        UpdateScheduleFieldVisibility(kind);
    }

    private void UpdateScheduleFieldVisibility(string kind)
    {
        if (CronFields == null) return; // not yet loaded
        CronFields.Visibility = kind == "cron" ? Visibility.Visible : Visibility.Collapsed;
        EveryFields.Visibility = kind == "every" ? Visibility.Visible : Visibility.Collapsed;
        AtFields.Visibility = kind == "at" ? Visibility.Visible : Visibility.Collapsed;
        if (kind == "at")
        {
            var now = DateTimeOffset.Now;
            FormAtDate.Date = now;
            FormAtTime.Text = now.ToString("h:mm tt");
        }
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string expr)
        {
            FormCronExpr.Text = expr;
            HighlightPreset(expr);
        }
    }

    private void HighlightPreset(string? expr)
    {
        if (PresetGrid == null) return;
        foreach (var child in PresetGrid.Items)
        {
            if (child is Button b)
            {
                var isMatch = b.Tag is string tag && tag == expr;
                if (isMatch)
                {
                    if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style s)
                        b.Style = s;
                    b.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                }
                else
                {
                    b.ClearValue(Button.StyleProperty);
                    b.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                }
            }
        }
    }

    private void OnDeliveryModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeliveryChannelPanel == null) return;
        var mode = GetSelectedTag(FormDeliveryMode);
        DeliveryChannelPanel.Visibility = mode == "announce" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetForm()
    {
        FormName.Text = "";
        FormCronExpr.Text = "";
        FormTimezone.SelectedIndex = -1;
        FormEveryValue.Text = "30";
        FormEveryUnit.SelectedIndex = 0; // Minutes
        FormAtDate.Date = DateTimeOffset.Now;
        FormAtTime.Text = DateTimeOffset.Now.ToString("h:mm tt");
        FormDeleteAfterRun.IsChecked = true;
        FormMessage.Text = "";
        FormDeliveryMode.SelectedIndex = 0;
        FormDeliveryChannel.Text = "";
        DeliveryChannelPanel.Visibility = Visibility.Collapsed;
        FormSessionTarget.SelectedIndex = 0;
        FormWakeMode.SelectedIndex = 0;
        FormScheduleKind.SelectedIndex = 0; // "Every" is now index 0
        HighlightPreset(null);
        UpdateScheduleFieldVisibility("every");
        FormError.Visibility = Visibility.Collapsed;
    }

    private void ShowFormError(string message)
    {
        FormError.Text = message;
        FormError.Visibility = Visibility.Visible;
    }

    private static bool TryParseTime(string? input, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(input)) return false;
        // Try standard time formats: "3:30 PM", "15:30", "3:30PM", "3PM"
        if (DateTime.TryParse(input.Trim(), out var dt))
        {
            time = dt.TimeOfDay;
            return true;
        }
        return false;
    }

    private static string? GetSelectedTag(ComboBox combo)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    private static void SelectComboByTag(ComboBox combo, string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag as string == tag)
            {
                combo.SelectedIndex = i;
                return;
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

                    // Raw schedule fields for editing
                    vm.ScheduleKind = kind;
                    vm.ScheduleTz = tz;
                    if (kind == "cron" && schedEl.TryGetProperty("expr", out var exprEl))
                        vm.ScheduleExpr = exprEl.GetString() ?? "";
                    if (kind == "every" && schedEl.TryGetProperty("everyMs", out var evMsEl) && evMsEl.ValueKind == JsonValueKind.Number)
                        vm.ScheduleEveryMs = evMsEl.GetInt64();
                    if (kind == "at" && schedEl.TryGetProperty("at", out var atRawEl))
                        vm.ScheduleAt = atRawEl.GetString() ?? "";
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
                vm.RawDeliveryMode = mode;
                vm.RawDeliveryChannel = channel;
                if (!string.IsNullOrEmpty(mode) && mode != "none")
                {
                    vm.DeliveryText = string.IsNullOrEmpty(channel) ? $"delivery: {mode}" : $"delivery: {mode} → {channel}";
                    vm.DeliveryVisibility = Visibility.Visible;
                }
            }

            // deleteAfterRun flag
            if (item.TryGetProperty("deleteAfterRun", out var darEl))
                vm.DeleteAfterRun = darEl.ValueKind == JsonValueKind.True;

            // Description from payload message or text
            if (item.TryGetProperty("payload", out var payEl) && payEl.ValueKind == JsonValueKind.Object)
            {
                if (payEl.TryGetProperty("message", out var msgEl))
                {
                    vm.Description = msgEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(vm.Description))
                        vm.DescriptionVisibility = Visibility.Visible;
                }
                else if (payEl.TryGetProperty("text", out var txtEl))
                {
                    vm.Description = txtEl.GetString() ?? "";
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
            _jobs = jobs;
            JobCountText.Text = $"({jobs.Count})";
            if (jobs.Count > 0)
            {
                // Don't rebuild cards if we're currently editing inline (would lose the form)
                if (_editingJobId == null)
                    RebuildJobCards();
                JobsListPanel.Visibility = Visibility.Visible;
                EmptyState.Visibility = Visibility.Collapsed;
            }
            else
            {
                JobsListPanel.Children.Clear();
                JobsListPanel.Visibility = Visibility.Collapsed;
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
        var human = CronToHuman(expr);
        var tzSuffix = string.IsNullOrEmpty(tz) ? "" : $" ({tz})";
        return $"cron: {human}{tzSuffix}";
    }

    private static string CronToHuman(string expr)
    {
        var parts = expr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return expr;
        var (min, hour, dom, mon, dow) = (parts[0], parts[1], parts[2], parts[3], parts[4]);

        // Every minute
        if (min == "*" && hour == "*" && dom == "*" && mon == "*" && dow == "*")
            return "every minute";

        // Hourly (0 * * * *)
        if (hour == "*" && dom == "*" && mon == "*" && dow == "*" && min != "*")
            return "hourly";

        // Format time string
        var timeStr = "";
        if (int.TryParse(hour, out var h) && int.TryParse(min, out var m))
        {
            var ampm = h >= 12 ? "pm" : "am";
            var h12 = h == 0 ? 12 : h > 12 ? h - 12 : h;
            timeStr = m == 0 ? $"{h12}{ampm}" : $"{h12}:{m:00}{ampm}";
        }
        else
        {
            return expr; // complex hour/min, just show raw
        }

        // Daily (at specific time, all days)
        if (dom == "*" && mon == "*" && dow == "*")
            return $"daily at {timeStr}";

        // Day-of-week patterns
        if (dom == "*" && mon == "*" && dow != "*")
        {
            var dayLabel = dow switch
            {
                "1-5" => "weekdays",
                "0-4" => "weekdays",
                "1" => "Mondays",
                "0" => "Sundays",
                "6" => "Saturdays",
                "6,0" or "0,6" => "weekends",
                _ => $"days {dow}"
            };
            return $"{dayLabel} at {timeStr}";
        }

        // Monthly
        if (mon == "*" && dow == "*" && dom != "*")
            return $"monthly (day {dom}) at {timeStr}";

        return expr;
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

    private void OnCardClick(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Don't toggle expand when clicking buttons inside the detail panel
        if (e.OriginalSource is FrameworkElement fe)
        {
            var parent = fe;
            while (parent != null)
            {
                if (parent is Button) return;
                parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent) as FrameworkElement;
            }
        }

        if (sender is not Border card) return;
        var jobId = card.Tag as string;
        var vm = _jobs.Find(j => j.Id == jobId);
        if (vm == null) return;

        vm.IsExpanded = !vm.IsExpanded;
        if (card.Child is Grid grid)
            ApplyExpandState(grid, vm.IsExpanded);
    }

    // --- Inline form placement ---

    private void PlaceFormInline(string jobId)
    {
        // Remove form from its current parent
        if (JobFormPanel.Parent is Panel parentPanel)
            parentPanel.Children.Remove(JobFormPanel);

        // Find the card for this job in the list and collapse it
        for (int i = 0; i < JobsListPanel.Children.Count; i++)
        {
            if (JobsListPanel.Children[i] is Border card && card.Tag as string == jobId)
            {
                _editingCard = card;
                card.Visibility = Visibility.Collapsed;
                // Insert form right at this position
                JobsListPanel.Children.Insert(i, JobFormPanel);
                JobFormPanel.Visibility = Visibility.Visible;
                return;
            }
        }

        // Fallback: show at top if card not found
        _editingCard = null;
        var pageGrid = FindParentGrid();
        if (pageGrid != null && !pageGrid.Children.Contains(JobFormPanel))
        {
            pageGrid.Children.Add(JobFormPanel);
            Grid.SetRow(JobFormPanel, 2);
        }
        JobFormPanel.Visibility = Visibility.Visible;
    }

    private void RestoreFormFromInline()
    {
        // Remove form from wherever it is
        if (JobFormPanel.Parent is Panel parentPanel)
            parentPanel.Children.Remove(JobFormPanel);

        // Restore the hidden card
        if (_editingCard != null)
        {
            _editingCard.Visibility = Visibility.Visible;
            _editingCard = null;
        }

        // Put form back in the Grid at Row 2 (its home position)
        var pageGrid = FindParentGrid();
        if (pageGrid != null && !pageGrid.Children.Contains(JobFormPanel))
        {
            pageGrid.Children.Add(JobFormPanel);
            Grid.SetRow(JobFormPanel, 2);
        }
    }

    private Grid? FindParentGrid()
    {
        // The page's main Grid is inside the ScrollViewer
        if (this.Content is ScrollViewer sv && sv.Content is Grid g)
            return g;
        return null;
    }

    // --- Card building ---

    private void RebuildJobCards()
    {
        JobsListPanel.Children.Clear();
        foreach (var vm in _jobs)
            JobsListPanel.Children.Add(BuildJobCard(vm));
    }

    private Border BuildJobCard(CronJobViewModel vm)
    {
        var card = new Border
        {
            Tag = vm.Id,
            Padding = new Thickness(16, 10, 16, 12),
            Margin = new Thickness(0, 2, 0, 0),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Opacity = vm.CardOpacity
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: Name + badges + chevron
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock { Text = vm.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(nameText, 0);
        headerGrid.Children.Add(nameText);

        var scheduleBadge = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"]
        };
        scheduleBadge.Child = new TextBlock { Text = vm.Schedule, FontSize = 10, FontFamily = new FontFamily("Consolas"), Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        Grid.SetColumn(scheduleBadge, 1);
        headerGrid.Children.Add(scheduleBadge);

        var enabledBadge = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = vm.EnabledBadgeBackground
        };
        enabledBadge.Child = new TextBlock { Text = vm.EnabledText, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = vm.EnabledBadgeForeground };
        Grid.SetColumn(enabledBadge, 2);
        headerGrid.Children.Add(enabledBadge);

        if (vm.ResultBadgeVisibility == Visibility.Visible)
        {
            var resultBadge = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = vm.ResultBadgeBackground
            };
            resultBadge.Child = new TextBlock { Text = vm.LastResult, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = vm.ResultBadgeForeground };
            Grid.SetColumn(resultBadge, 3);
            headerGrid.Children.Add(resultBadge);
        }

        var chevron = new FontIcon { Glyph = "\uE70D", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] };
        Grid.SetColumn(chevron, 5);
        headerGrid.Children.Add(chevron);

        Grid.SetRow(headerGrid, 0);
        grid.Children.Add(headerGrid);

        // Row 1: Summary line
        if (vm.SummaryVisibility == Visibility.Visible)
        {
            var summary = new TextBlock { Text = vm.SummaryLine, FontSize = 11, Margin = new Thickness(0, 3, 0, 0), Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] };
            Grid.SetRow(summary, 1);
            grid.Children.Add(summary);
        }

        // Row 2: Expandable detail
        var detailPanel = BuildDetailPanel(vm);
        detailPanel.Visibility = vm.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(detailPanel, 2);
        grid.Children.Add(detailPanel);

        card.Child = grid;

        // Click to expand/collapse
        card.PointerReleased += OnCardClick;

        return card;
    }

    private StackPanel BuildDetailPanel(CronJobViewModel vm)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Spacing = 8 };

        // Description
        if (vm.DescriptionVisibility == Visibility.Visible)
        {
            panel.Children.Add(new TextBlock
            {
                Text = vm.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                IsTextSelectionEnabled = true
            });
        }

        // Timestamps
        var tsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        tsPanel.Children.Add(MakeTimestampPair("Last run:", vm.LastRunTime));
        tsPanel.Children.Add(MakeTimestampPair("Next:", vm.NextRunTime));
        if (vm.DurationVisibility == Visibility.Visible)
            tsPanel.Children.Add(MakeTimestampPair("Duration:", vm.LastDuration));
        panel.Children.Add(tsPanel);

        // Chips
        var chipsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (vm.SessionTargetVisibility == Visibility.Visible)
            chipsPanel.Children.Add(MakeChip(vm.SessionTargetLabel));
        if (vm.WakeModeVisibility == Visibility.Visible)
            chipsPanel.Children.Add(MakeChip(vm.WakeModeLabel));
        if (vm.DeliveryVisibility == Visibility.Visible)
            chipsPanel.Children.Add(MakeChip(vm.DeliveryText));
        if (chipsPanel.Children.Count > 0)
            panel.Children.Add(chipsPanel);

        // Action buttons
        var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttonsPanel.Children.Add(MakeActionButton("\uE768", "Run Now", vm.Id, OnRunNowClick));
        buttonsPanel.Children.Add(MakeActionButton(vm.ToggleEnabledGlyph, vm.ToggleEnabledText, vm.Id, OnToggleEnabledClick));
        buttonsPanel.Children.Add(MakeActionButton("\uE70F", "Edit", vm.Id, OnEditJobClick));
        buttonsPanel.Children.Add(MakeActionButton("\uE711", "Remove", vm.Id, OnRemoveClick));
        panel.Children.Add(buttonsPanel);

        return panel;
    }

    private static StackPanel MakeTimestampPair(string label, string value)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
        return sp;
    }

    private static Border MakeChip(string text)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"]
        };
        chip.Child = new TextBlock { Text = text, FontSize = 10, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        return chip;
    }

    private static Button MakeActionButton(string glyph, string text, string jobId, RoutedEventHandler handler)
    {
        var btn = new Button { Tag = jobId, FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        sp.Children.Add(new TextBlock { Text = text });
        btn.Content = sp;
        btn.Click += handler;
        return btn;
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

        // Raw fields for editing
        public string ScheduleKind { get; set; } = "cron";
        public string ScheduleExpr { get; set; } = "";
        public string ScheduleTz { get; set; } = "";
        public long ScheduleEveryMs { get; set; } = 0;
        public string ScheduleAt { get; set; } = "";
        public bool DeleteAfterRun { get; set; } = false;
        public string RawDeliveryMode { get; set; } = "none";
        public string RawDeliveryChannel { get; set; } = "";
    }
}
