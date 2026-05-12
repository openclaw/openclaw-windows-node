using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.UI;

namespace OpenClawTray.Pages;

public sealed partial class CronPage : Page
{
    private HubWindow? _hub;
    private List<CronJobViewModel> _jobs = new();
    private Border? _editingCard = null; // card hidden during inline edit
    private string? _historyJobId = null; // job whose history is currently displayed
    private HashSet<string> _runningJobIds = new(); // jobs currently being triggered
    private HashSet<string> _expandedJobIds = new(); // persisted expanded state
    private CancellationTokenSource? _infoDismissCts = null; // auto-dismiss timer for InfoBar

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
        var vm = _jobs.Find(j => j.Id == jobId);
        if (vm != null && !vm.IsEnabled) return;
        _runningJobIds.Add(jobId);
        btn!.Content = "Running...";
        btn.IsEnabled = false;

        _hub.GatewayClient.RunCronJobAsync(jobId).ContinueWith(t =>
        {
            if (t.IsFaulted || (t.IsCompletedSuccessfully && !t.Result))
            {
                // Request failed — clear running state immediately
                DispatcherQueue?.TryEnqueue(() =>
                {
                    _runningJobIds.Remove(jobId);
                    _ = _hub?.GatewayClient?.RequestCronListAsync();
                });
            }
        });

        // Safety timeout: clear running state after 90s if gateway never reports completion
        var capturedHub = _hub;
        Task.Delay(TimeSpan.FromSeconds(90)).ContinueWith(_ =>
        {
            if (_runningJobIds.Contains(jobId))
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    _runningJobIds.Remove(jobId);
                    _ = capturedHub?.GatewayClient?.RequestCronListAsync();
                });
            }
        });
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        var jobId = (sender as Button)?.Tag as string;
        if (string.IsNullOrEmpty(jobId) || _hub?.GatewayClient == null) return;
        var vm = _jobs.Find(j => j.Id == jobId);
        if (vm != null && !vm.IsEnabled) return;
        // Gateway client's HandleKnownResponse refreshes the list automatically on cron.remove
        _ = _hub.GatewayClient.RemoveCronJobAsync(jobId);
    }

    private void OnToggleEnabledClick(object sender, RoutedEventArgs e)
    {
        var jobId = (sender as Button)?.Tag as string;
        if (string.IsNullOrEmpty(jobId) || _hub?.GatewayClient == null) return;

        var vm = _jobs.Find(j => j.Id == jobId);
        if (vm != null)
        {
            _ = _hub.GatewayClient.UpdateCronJobAsync(jobId, new { enabled = !vm.IsEnabled });
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
        if (vm == null || !vm.IsEnabled) return;

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

            _ = _hub.GatewayClient.UpdateCronJobAsync(_editingJobId, patch);
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
            var defaultTime = DateTimeOffset.Now.AddMinutes(5);
            FormAtDate.Date = defaultTime;
            FormAtTime.Text = defaultTime.ToString("h:mm tt");
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

    private void ShowJobCompletedNotification(string jobName)
    {
        // Cancel and dispose any pending auto-dismiss timer
        _infoDismissCts?.Cancel();
        _infoDismissCts?.Dispose();
        _infoDismissCts = new CancellationTokenSource();
        var cts = _infoDismissCts;

        JobCompletedInfoBar.Title = "Job completed";
        JobCompletedInfoBar.Message = $"\"{jobName}\" ran successfully and was removed.";
        JobCompletedInfoBar.IsOpen = true;
        DispatcherQueue?.TryEnqueue(async () =>
        {
            try { await Task.Delay(10000, cts.Token); } catch (TaskCanceledException) { return; }
            JobCompletedInfoBar.IsOpen = false;
        });
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

            // Running state (job currently executing)
            if (state.TryGetProperty("runningAtMs", out var runningEl) && runningEl.ValueKind == JsonValueKind.Number)
            {
                vm.RunningAtMs = runningEl.GetInt64();
                if (vm.RunningAtMs > 0)
                    _runningJobIds.Add(vm.Id);
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

            // Infer running state: if scheduled time has passed but lastRunAtMs hasn't caught up
            if (vm.RunningAtMs == 0 && !_runningJobIds.Contains(vm.Id))
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Check if old nextRunAtMs has passed (compare with previous VM data)
                var oldVm = _jobs.Find(j => j.Id == vm.Id);
                if (oldVm != null && oldVm.NextRunAtMs > 0 && nowMs >= oldVm.NextRunAtMs && vm.LastRunAtMs == oldVm.LastRunAtMs)
                {
                    // The scheduled time has passed but the job hasn't completed yet — it's running
                    _runningJobIds.Add(vm.Id);
                }
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
            // Clear running state for jobs whose lastRunAtMs changed or runningAtMs is 0
            foreach (var vm in jobs)
            {
                if (_runningJobIds.Contains(vm.Id))
                {
                    var oldVm = _jobs.Find(j => j.Id == vm.Id);
                    if (vm.RunningAtMs == 0 || oldVm == null || vm.LastRunAtMs != oldVm.LastRunAtMs)
                        _runningJobIds.Remove(vm.Id);
                }
            }

            // Detect one-shot jobs that disappeared (ran and deleted themselves)
            var newIds = new HashSet<string>(jobs.Select(j => j.Id));
            foreach (var oldVm in _jobs)
            {
                if (!newIds.Contains(oldVm.Id) && oldVm.DeleteAfterRun)
                {
                    ShowJobCompletedNotification(oldVm.Name);
                }
            }

            _jobs = jobs;

            // Restore expanded state from persisted set
            foreach (var vm in _jobs)
            {
                if (_expandedJobIds.Contains(vm.Id)) vm.IsExpanded = true;
            }

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
        // Find detail panel (assigned to Row 2) regardless of children count
        StackPanel? detailPanel = null;
        for (int i = 0; i < grid.Children.Count; i++)
        {
            if (grid.Children[i] is StackPanel sp && Grid.GetRow(sp) == 2)
            {
                detailPanel = sp;
                break;
            }
        }

        if (detailPanel != null)
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

    private void OnCardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
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
        if (vm == null || string.IsNullOrEmpty(jobId)) return;

        vm.IsExpanded = !vm.IsExpanded;

        // Persist expanded state
        if (vm.IsExpanded)
            _expandedJobIds.Add(jobId);
        else
            _expandedJobIds.Remove(jobId);

        // If collapsing and history is open for this job, close it
        if (!vm.IsExpanded && _historyJobId == jobId)
        {
            HideHistoryPanel(jobId);
            _historyJobId = null;
        }

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
        _historyJobId = null; // history panel is destroyed on rebuild
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
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0: name
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1: schedule
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 2: enabled
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 3: result
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 4: running
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 5: spacer
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 6: chevron

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

        // Show "Running" badge when job is in-progress
        if (_runningJobIds.Contains(vm.Id))
        {
            var runningBadge = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(40, 33, 150, 243))
            };
            runningBadge.Child = new TextBlock { Text = "⏳ Running", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(255, 100, 181, 246)) };
            Grid.SetColumn(runningBadge, 4);
            headerGrid.Children.Add(runningBadge);
        }

        var chevron = new FontIcon { Glyph = "\uE70D", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] };
        Grid.SetColumn(chevron, 6);
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
        card.Tapped += OnCardTapped;

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
        var jobDisabled = !vm.IsEnabled;

        // "Run Now" button — show running state if job is in the running set
        var runNowBtn = MakeActionButton("\uE768", "Run Now", vm.Id, OnRunNowClick);
        if (_runningJobIds.Contains(vm.Id))
        {
            runNowBtn.Content = "Running...";
            runNowBtn.IsEnabled = false;
        }
        else if (jobDisabled)
        {
            runNowBtn.Opacity = 0.4;
        }
        buttonsPanel.Children.Add(runNowBtn);

        buttonsPanel.Children.Add(MakeActionButton(vm.ToggleEnabledGlyph, vm.ToggleEnabledText, vm.Id, OnToggleEnabledClick));

        var editBtn = MakeActionButton("\uE70F", "Edit", vm.Id, OnEditJobClick);
        if (jobDisabled) editBtn.Opacity = 0.4;
        buttonsPanel.Children.Add(editBtn);

        var histBtn = MakeActionButton("\uE81C", "History", vm.Id, OnHistoryClick);
        if (jobDisabled) histBtn.Opacity = 0.4;
        buttonsPanel.Children.Add(histBtn);

        var removeBtn = MakeActionButton("\uE711", "Remove", vm.Id, OnRemoveClick);
        if (jobDisabled) removeBtn.Opacity = 0.4;
        buttonsPanel.Children.Add(removeBtn);

        panel.Children.Add(buttonsPanel);

        // History panel (populated when History button is clicked)
        var historyPanel = new StackPanel
        {
            Tag = $"history_{vm.Id}",
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0)
        };
        panel.Children.Add(historyPanel);

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

    // --- Run History ---

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        var jobId = (sender as Button)?.Tag as string;
        if (string.IsNullOrEmpty(jobId) || _hub?.GatewayClient == null) return;
        var vm = _jobs.Find(j => j.Id == jobId);
        if (vm != null && !vm.IsEnabled) return;
        if (_historyJobId == jobId)
        {
            HideHistoryPanel(jobId);
            _historyJobId = null;
            return;
        }

        // Hide previous history if any
        if (_historyJobId != null)
            HideHistoryPanel(_historyJobId);

        _historyJobId = jobId;

        // Show loading state
        var histPanel = FindHistoryPanel(jobId);
        if (histPanel != null)
        {
            histPanel.Children.Clear();
            histPanel.Children.Add(new TextBlock
            {
                Text = "Loading run history...",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            histPanel.Visibility = Visibility.Visible;
        }

        _ = _hub.GatewayClient.RequestCronRunsAsync(jobId, limit: 20, offset: 0);
    }

    private void HideHistoryPanel(string jobId)
    {
        var panel = FindHistoryPanel(jobId);
        if (panel != null)
        {
            panel.Children.Clear();
            panel.Visibility = Visibility.Collapsed;
        }
    }

    private StackPanel? FindHistoryPanel(string jobId)
    {
        var tag = $"history_{jobId}";
        foreach (var child in JobsListPanel.Children)
        {
            if (child is Border card && card.Tag as string == jobId)
            {
                return FindTaggedPanel(card, tag);
            }
        }
        return null;
    }

    private static StackPanel? FindTaggedPanel(DependencyObject parent, string tag)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is StackPanel sp && sp.Tag as string == tag) return sp;
            var found = FindTaggedPanel(child, tag);
            if (found != null) return found;
        }
        return null;
    }

    public void UpdateCronRuns(JsonElement data)
    {
        // data is the full response: { entries: [...], total, offset, limit, hasMore, ... }
        if (_historyJobId == null) return;

        var histPanel = FindHistoryPanel(_historyJobId);
        if (histPanel == null) return;
        histPanel.Children.Clear();

        if (!data.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            histPanel.Children.Add(new TextBlock
            {
                Text = "No run history available.",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            return;
        }

        var total = data.TryGetProperty("total", out var totalEl) && totalEl.ValueKind == JsonValueKind.Number ? totalEl.GetInt32() : 0;
        var entryCount = 0;

        // Header
        var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerText = new TextBlock
        {
            Text = "Run History",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        Grid.SetColumn(headerText, 0);
        headerRow.Children.Add(headerText);

        var sep = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 4),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"]
        };
        histPanel.Children.Add(sep);
        histPanel.Children.Add(headerRow);

        foreach (var entry in entries.EnumerateArray())
        {
            entryCount++;
            histPanel.Children.Add(BuildRunEntry(entry));
        }

        // Update header with count
        headerText.Text = total > 0 ? $"Run History — showing {entryCount} of {total}" : $"Run History — {entryCount} runs";

        // "Load more" if there are more
        var hasMore = data.TryGetProperty("hasMore", out var hmEl) && hmEl.ValueKind == JsonValueKind.True;
        if (hasMore)
        {
            var nextOffset = data.TryGetProperty("nextOffset", out var noEl) && noEl.ValueKind == JsonValueKind.Number ? noEl.GetInt32() : entryCount;
            var loadMoreBtn = new Button
            {
                Content = $"Load older runs ({total - entryCount - (nextOffset - entryCount)} more)...",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(0, 6, 0, 6),
                FontSize = 12,
                Tag = _historyJobId
            };

            // Simple "load more" content
            var remaining = total - nextOffset;
            if (remaining > 0)
                loadMoreBtn.Content = $"Load older runs ({remaining} more)...";
            else
                loadMoreBtn.Content = "Load more runs...";

            loadMoreBtn.Click += (s, args) =>
            {
                var jid = (s as Button)?.Tag as string;
                if (!string.IsNullOrEmpty(jid) && _hub?.GatewayClient != null)
                {
                    loadMoreBtn.IsEnabled = false;
                    loadMoreBtn.Content = "Loading...";
                    // For simplicity, reload with higher limit
                    _ = _hub.GatewayClient.RequestCronRunsAsync(jid, limit: nextOffset + 20, offset: 0);
                }
            };
            histPanel.Children.Add(loadMoreBtn);
        }

        histPanel.Visibility = Visibility.Visible;
    }

    // Redact tokens, secrets, and file paths from text before UI display
    private static readonly Regex AbsolutePathPattern = new(
        @"(?:[A-Za-z]:\\(?:Users|home|usr|var|tmp)\\[^\s""']+)|(?:/(?:home|Users|usr|var|tmp)/[^\s""']+)",
        RegexOptions.Compiled);

    private static string SanitizeForDisplay(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sanitized = TokenSanitizer.Sanitize(text);
        sanitized = AbsolutePathPattern.Replace(sanitized, m =>
        {
            var sep = m.Value.Contains('\\') ? '\\' : '/';
            var lastSep = m.Value.LastIndexOf(sep);
            return lastSep >= 0 ? "…" + sep + m.Value[(lastSep + 1)..] : m.Value;
        });
        return sanitized;
    }

    private Border BuildRunEntry(JsonElement entry)
    {
        var status = entry.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "" : "";
        var durationMs = entry.TryGetProperty("durationMs", out var dEl) && dEl.ValueKind == JsonValueKind.Number ? dEl.GetInt64() : 0;
        var summary = SanitizeForDisplay(entry.TryGetProperty("summary", out var sumEl) ? sumEl.GetString() ?? "" : "");
        var error = SanitizeForDisplay(entry.TryGetProperty("error", out var errEl) ? errEl.GetString() ?? "" : "");
        var model = entry.TryGetProperty("model", out var modEl) ? modEl.GetString() ?? "" : "";
        var tsMs = entry.TryGetProperty("runAtMs", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number
            ? tsEl.GetInt64()
            : (entry.TryGetProperty("ts", out var ts2El) && ts2El.ValueKind == JsonValueKind.Number ? ts2El.GetInt64() : 0);

        var totalTokens = 0L;
        if (entry.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            if (usageEl.TryGetProperty("total_tokens", out var ttEl) && ttEl.ValueKind == JsonValueKind.Number)
                totalTokens = ttEl.GetInt64();
        }

        var delivered = entry.TryGetProperty("delivered", out var delEl) && delEl.ValueKind == JsonValueKind.True;
        var deliveryStatus = entry.TryGetProperty("deliveryStatus", out var dsEl) ? dsEl.GetString() ?? "" : "";

        // Colors
        bool isError = status == "error" || status == "failed";
        var statusBg = isError
            ? new SolidColorBrush(Color.FromArgb(40, 224, 85, 69))
            : new SolidColorBrush(Color.FromArgb(40, 76, 175, 80));
        var statusFg = isError
            ? new SolidColorBrush(Color.FromArgb(255, 224, 85, 69))
            : new SolidColorBrush(Colors.LimeGreen);

        // Container
        var row = new Border
        {
            Padding = new Thickness(0, 6, 0, 6),
            BorderBrush = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) }); // status
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // summary
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // duration
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); // time

        // Status badge
        var statusBadge = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1),
            Background = statusBg, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left
        };
        statusBadge.Child = new TextBlock { Text = status, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = statusFg };
        Grid.SetColumn(statusBadge, 0);
        grid.Children.Add(statusBadge);

        // Summary/error + metadata
        var contentPanel = new StackPanel { Margin = new Thickness(4, 0, 8, 0) };
        var displayText = isError && !string.IsNullOrEmpty(error) ? error : summary;
        if (!string.IsNullOrEmpty(displayText))
        {
            var truncated = displayText.Length > 120 ? displayText[..120] + "…" : displayText;
            contentPanel.Children.Add(new TextBlock
            {
                Text = truncated,
                FontSize = 11,
                TextWrapping = TextWrapping.WrapWholeWords,
                Foreground = isError
                    ? new SolidColorBrush(Color.FromArgb(255, 224, 85, 69))
                    : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                MaxLines = 2
            });
        }

        // Meta line: model · tokens · delivery
        var metaParts = new List<string>();
        if (!string.IsNullOrEmpty(model)) metaParts.Add(model);
        if (totalTokens > 0) metaParts.Add($"{totalTokens:N0} tokens");
        if (delivered) metaParts.Add("delivered ✓");
        else if (!string.IsNullOrEmpty(deliveryStatus)) metaParts.Add(deliveryStatus);

        if (metaParts.Count > 0)
        {
            contentPanel.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", metaParts),
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
            });
        }
        Grid.SetColumn(contentPanel, 1);
        grid.Children.Add(contentPanel);

        // Duration
        var durText = durationMs > 0 ? $"{durationMs / 1000.0:F1}s" : "—";
        var durBlock = new TextBlock
        {
            Text = durText,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        Grid.SetColumn(durBlock, 2);
        grid.Children.Add(durBlock);

        // Timestamp
        var timeText = "—";
        if (tsMs > 0)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(tsMs).LocalDateTime;
            var now = DateTime.Now;
            if (dt.Date == now.Date)
                timeText = $"today {dt:h:mm tt}";
            else if (dt.Date == now.Date.AddDays(-1))
                timeText = $"yesterday {dt:h:mm tt}";
            else
                timeText = dt.ToString("MMM d h:mm tt");
        }
        var timeBlock = new TextBlock
        {
            Text = timeText,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        Grid.SetColumn(timeBlock, 3);
        grid.Children.Add(timeBlock);

        row.Child = grid;
        return row;
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

    private void BuildSummaryLine(CronJobViewModel vm)
    {
        var parts = new List<string>();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var isRunning = _runningJobIds.Contains(vm.Id);

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
            else if (!isRunning)
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
        public long RunningAtMs { get; set; } = 0;
        public bool IsRunning => RunningAtMs > 0;
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
