using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Chat.Markdown;
using OpenClawTray.FunctionalUI.Hosting;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.UI;

namespace OpenClawTray.Pages;

public sealed partial class CronPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private AppState? _appState;
    private List<CronJobViewModel> _jobs = new();
    private Border? _editingCard = null; // card hidden during inline edit
    private string? _historyJobId = null; // job whose history is currently displayed
    private HashSet<string> _runningJobIds = new(); // jobs currently being triggered
    private HashSet<string> _removedJobIds = new(); // jobs user explicitly removed (suppress auto-delete notification)
    private HashSet<string> _expandedJobIds = new(); // persisted expanded state
    private readonly Dictionary<string, CancellationTokenSource> _runRefreshCts = new();
    private readonly HashSet<string> _expandedRunFullResponseIds = new(); // persisted run response expansion state
    private string? _lastHistoryRenderSignature = null;
    private CancellationTokenSource? _infoDismissCts = null; // auto-dismiss timer for InfoBar
    private readonly AsyncListLoadingState _cronLoading = new();

    public CronPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
            CancelAllRunRefreshLoops();
        };
    }

    public void Initialize()
    {
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        _appState = CurrentApp.AppState!;
        _appState.PropertyChanged += OnAppStateChanged;
        var client = CurrentApp.GatewayClient;
        if (client != null)
        {
            ConnectionInfoBar.IsOpen = false;

            // If we already have loaded data (returning to this page), keep showing it
            // while refreshing in the background
            if (_cronLoading.HasLoaded)
            {
                _cronLoading.BeginRefresh();
                if (_editingJobId == null)
                    RebuildJobCards();
            }
            else if (_appState.CronList.HasValue)
            {
                // First visit but AppState has cached data from gateway — process it
                _cronLoading.BeginRefresh();
                UpdateFromGateway(_appState.CronList.Value);
                if (_appState.CronStatus.HasValue)
                    ParseCronStatus(_appState.CronStatus.Value);
            }
            else
            {
                _cronLoading.BeginInitialRefresh();
            }
            UpdateCronLoadingVisuals();
            _ = client.RequestCronListAsync();
            _ = client.RequestCronStatusAsync();
        }
        else
        {
            _cronLoading.Fail();
            ShowDisconnected();
            UpdateCronLoadingVisuals();
        }
    }

    private void OnOpenConnectionClick(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("connection");

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            _cronLoading.Fail();
            ShowDisconnected();
            UpdateCronLoadingVisuals();
            return;
        }

        ConnectionInfoBar.IsOpen = false;
        _cronLoading.BeginRefresh();
        if (_editingJobId == null)
            RebuildJobCards();
        UpdateCronLoadingVisuals();
        _ = client.RequestCronListAsync();
        _ = client.RequestCronStatusAsync();
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.CronList):
                if (_appState!.CronList.HasValue) UpdateFromGateway(_appState.CronList.Value);
                break;
            case nameof(AppState.CronStatus):
                if (_appState!.CronStatus.HasValue) UpdateFromGateway(_appState.CronStatus.Value);
                break;
            case nameof(AppState.CronRuns):
                if (_appState!.CronRuns.HasValue) UpdateCronRuns(_appState.CronRuns.Value);
                break;
        }
    }

    private void OnRunNowClick(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        var jobId = btn?.Tag as string;
        if (string.IsNullOrEmpty(jobId)) return;
        if (!_cronLoading.CanEdit) return;
        if (CurrentApp.GatewayClient == null) { ShowDisconnected(); return; }
        var vm = _jobs.Find(j => j.Id == jobId);
        if (_runningJobIds.Contains(jobId)) return;
        _runningJobIds.Add(jobId);
        RefreshJobActionButtons(jobId);

        CurrentApp.GatewayClient.RunCronJobDetailedAsync(jobId).ContinueWith(t =>
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                var client = CurrentApp.GatewayClient;
                var result = t.IsCompletedSuccessfully ? t.Result : null;
                if (t.IsFaulted || result?.Enqueued != true)
                {
                    var detail = t.IsFaulted
                        ? t.Exception?.GetBaseException().Message ?? LocalizationHelper.GetString("AppNotification_CronRunFailed_DefaultDetail")
                        : result?.Reason ?? result?.Error ?? LocalizationHelper.GetString("AppNotification_CronRunRejectedDetail");
                    ClearRunningJob(jobId);
                    ShowRunNotStartedNotification(vm?.Name ?? jobId, detail);
                    ShowCronAppNotification(
                        LocalizationHelper.GetString("AppNotification_CronRunFailed_Title"),
                        LocalizationHelper.Format("AppNotification_CronRunFailed_MessageFormat", vm?.Name ?? jobId, detail),
                        AppNotificationSeverity.Error,
                        $"cron:{jobId}:run-failed");
                    if (client != null) _ = client.RequestCronListAsync();
                    return;
                }

                if (client != null)
                {
                    StartRunRefreshLoop(jobId);
                }
            });
        });

        // Safety timeout: clear running state after 90s if gateway never reports completion
        // Safety timeout uses CurrentApp directly
        Task.Delay(TimeSpan.FromSeconds(90)).ContinueWith(_ =>
        {
            if (_runningJobIds.Contains(jobId))
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    ClearRunningJob(jobId);
                    var client = CurrentApp.GatewayClient;
                    if (client != null)
                    {
                        _ = client.RequestCronListAsync();
                        if (_historyJobId == jobId)
                            _ = client.RequestCronRunsAsync(jobId, limit: 20, offset: 0);
                    }
                });
            }
        });
    }

    private void StartRunRefreshLoop(string jobId)
    {
        CancelRunRefreshLoop(jobId);
        var cts = new CancellationTokenSource();
        _runRefreshCts[jobId] = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (token.IsCancellationRequested)
                            return;
                        if (!_runningJobIds.Contains(jobId))
                        {
                            CancelRunRefreshLoop(jobId);
                            return;
                        }

                        var client = CurrentApp.GatewayClient;
                        if (client == null) return;

                        _ = client.RequestCronRunsAsync(jobId, limit: 20, offset: 0);
                    });
                }
            }
            catch (TaskCanceledException)
            {
                // Expected when the run completes, times out, or the page unloads.
            }
            finally
            {
                var dispatcher = DispatcherQueue;
                if (dispatcher != null)
                {
                    dispatcher.TryEnqueue(() =>
                    {
                        if (_runRefreshCts.TryGetValue(jobId, out var current) && ReferenceEquals(current, cts))
                            _runRefreshCts.Remove(jobId);
                        cts.Dispose();
                    });
                }
                else
                {
                    cts.Dispose();
                }
            }
        });
    }

    private void ClearRunningJob(string jobId)
    {
        _runningJobIds.Remove(jobId);
        CancelRunRefreshLoop(jobId);
        RefreshJobActionButtons(jobId);
    }

    private void CancelRunRefreshLoop(string jobId)
    {
        if (_runRefreshCts.Remove(jobId, out var cts))
            cts.Cancel();
    }

    private void CancelAllRunRefreshLoops()
    {
        foreach (var cts in _runRefreshCts.Values)
            cts.Cancel();
        _runRefreshCts.Clear();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        var jobId = (sender as Button)?.Tag as string;
        if (string.IsNullOrEmpty(jobId)) return;
        if (!_cronLoading.CanEdit) return;
        if (_runningJobIds.Contains(jobId)) return;
        if (CurrentApp.GatewayClient == null) { ShowDisconnected(); return; }
        _removedJobIds.Add(jobId);
        // Gateway client's HandleKnownResponse refreshes the list automatically on cron.remove
        _ = CurrentApp.GatewayClient.RemoveCronJobAsync(jobId);
    }

    private void OnEnabledToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts) return;
        var jobId = ts.Tag as string;
        if (string.IsNullOrEmpty(jobId)) return;
        if (!_cronLoading.CanEdit) return;
        if (CurrentApp.GatewayClient == null) { ShowDisconnected(); return; }

        var vm = _jobs.Find(j => j.Id == jobId);
        if (vm == null) return;
        if (_runningJobIds.Contains(jobId)) return;
        if (ts.IsOn == vm.IsEnabled) return; // no-op (e.g., programmatic init)
        _ = CurrentApp.GatewayClient.UpdateCronJobAsync(jobId, new { enabled = ts.IsOn });
    }

    // --- Job creation/edit form ---
    private string? _editingJobId = null; // null = creating new, set = editing existing

    private void OnNewJobClick(object sender, RoutedEventArgs e)
    {
        if (!_cronLoading.CanEdit) return;
        if (CurrentApp.GatewayClient == null) { ShowDisconnected(); return; }
        _editingJobId = null;
        RestoreFormFromInline(); // ensure form is back in its home position
        ResetForm();
        FormTitle.Text = LocalizationHelper.GetString("CronPage_NewJobTitle");
        FormSaveButton.Content = LocalizationHelper.GetString("CronPage_CreateJobLabel");
        JobFormPanel.Visibility = Visibility.Visible;
    }

    private void OnEditJobClick(object sender, RoutedEventArgs e)
    {
        var jobId = (sender as Button)?.Tag as string;
        if (string.IsNullOrEmpty(jobId)) return;
        if (!_cronLoading.CanEdit) return;
        if (CurrentApp.GatewayClient == null) { ShowDisconnected(); return; }
        var vm = _jobs.Find(j => j.Id == jobId);
        if (vm == null || _runningJobIds.Contains(jobId)) return;

        _editingJobId = jobId;
        FormTitle.Text = LocalizationHelper.GetString("CronPage_EditJob");
        FormSaveButton.Content = LocalizationHelper.GetString("CronPage_SaveChanges");

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
        if (!_cronLoading.CanEdit)
        {
            ShowFormError("Wait for the latest cron jobs to finish loading before saving changes.");
            return;
        }

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

        if (CurrentApp.GatewayClient == null)
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

            _ = CurrentApp.GatewayClient.UpdateCronJobAsync(_editingJobId, patch);
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

            _ = CurrentApp.GatewayClient.AddCronJobAsync(job);
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

        JobCompletedInfoBar.Title = LocalizationHelper.GetString("CronPage_JobCompleted");
        JobCompletedInfoBar.Message = LocalizationHelper.Format("CronPage_JobCompletedRanSuccessfully", jobName);
        JobCompletedInfoBar.Severity = InfoBarSeverity.Success;
        JobCompletedInfoBar.IsOpen = true;
        DispatcherQueue?.TryEnqueue(async () =>
        {
            try { await Task.Delay(10000, cts.Token); } catch (TaskCanceledException) { return; }
            JobCompletedInfoBar.IsOpen = false;
        });
    }

    private void ShowRunNotStartedNotification(string jobName, string? reason)
    {
        _infoDismissCts?.Cancel();
        _infoDismissCts?.Dispose();
        _infoDismissCts = new CancellationTokenSource();
        var cts = _infoDismissCts;

        JobCompletedInfoBar.Title = LocalizationHelper.GetString("CronPage_RunNotStarted");
        JobCompletedInfoBar.Message = string.IsNullOrWhiteSpace(reason)
            ? LocalizationHelper.Format("CronPage_RunNotStartedGeneric", jobName)
            : LocalizationHelper.Format("CronPage_RunNotStartedWithReason", jobName, reason);
        JobCompletedInfoBar.Severity = InfoBarSeverity.Warning;
        JobCompletedInfoBar.IsOpen = true;
        DispatcherQueue?.TryEnqueue(async () =>
        {
            try { await Task.Delay(10000, cts.Token); } catch (TaskCanceledException) { return; }
            JobCompletedInfoBar.IsOpen = false;
        });
    }

    private static void ShowCronAppNotification(
        string title,
        string message,
        AppNotificationSeverity severity,
        string dedupeKey)
    {
        AppNotificationPublisher.Show(
            CurrentApp.AppNotifications,
            title,
            message,
            "cron",
            "jobs",
            severity,
            dedupeKey,
            "cron",
            LocalizationHelper.GetString("AppNotification_ActionOpenCron"));
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
                vm.LastRunTime = "-";
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
                    vm.ResultBadgeForeground = (SolidColorBrush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                    vm.ResultBadgeVisibility = Visibility.Visible;
                }
                else if (!string.IsNullOrEmpty(status) && status != "none")
                {
                    vm.LastResult = status;
                    vm.ResultBadgeForeground = (SolidColorBrush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                    vm.ResultBadgeVisibility = Visibility.Visible;
                }
            }
            else if (state.TryGetProperty("lastRunOk", out var okEl))
            {
                if (okEl.ValueKind == JsonValueKind.True)
                {
                    vm.LastResult = "success";
                    vm.ResultBadgeForeground = (SolidColorBrush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                    vm.ResultBadgeVisibility = Visibility.Visible;
                }
                else if (okEl.ValueKind == JsonValueKind.False)
                {
                    vm.LastResult = "fail";
                    vm.ResultBadgeForeground = (SolidColorBrush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                    vm.ResultBadgeVisibility = Visibility.Visible;
                }
            }

            // Build compact summary line with relative times
            BuildSummaryLine(vm);

            jobs.Add(vm);
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            var oldJobs = _jobs;
            var hadRunningJobs = _runningJobIds.Count > 0;
            var newIds = new HashSet<string>(jobs.Select(j => j.Id));
            foreach (var runningJobId in _runningJobIds.ToArray())
            {
                if (!newIds.Contains(runningJobId))
                    ClearRunningJob(runningJobId);
            }

            // Clear optimistic running state only after the gateway reports a completed run.
            foreach (var vm in jobs)
            {
                if (_runningJobIds.Contains(vm.Id))
                {
                    var oldVm = oldJobs.Find(j => j.Id == vm.Id);
                    if (oldVm != null && vm.LastRunAtMs > 0 && vm.LastRunAtMs != oldVm.LastRunAtMs)
                    {
                        ClearRunningJob(vm.Id);
                        if (_historyJobId == vm.Id && CurrentApp.GatewayClient != null)
                            _ = CurrentApp.GatewayClient.RequestCronRunsAsync(vm.Id, limit: 20, offset: 0);
                    }
                }
            }

            // Detect one-shot jobs that disappeared (ran and deleted themselves)
            foreach (var oldVm in _jobs)
            {
                if (!newIds.Contains(oldVm.Id) && oldVm.DeleteAfterRun && !_removedJobIds.Contains(oldVm.Id))
                {
                    ShowJobCompletedNotification(oldVm.Name);
                }
                if (!newIds.Contains(oldVm.Id))
                    _removedJobIds.Remove(oldVm.Id);
            }

            var oldIds = new HashSet<string>(oldJobs.Select(j => j.Id));
            var sameJobSet = oldIds.SetEquals(newIds);
            var shouldPreserveVisualTreeForRunningRefresh =
                hadRunningJobs &&
                _runningJobIds.Count > 0 &&
                sameJobSet &&
                _editingJobId == null;

            _jobs = jobs;
            _cronLoading.Complete(jobs.Count);

            // Restore expanded state from persisted set
            foreach (var vm in _jobs)
            {
                if (_expandedJobIds.Contains(vm.Id)) vm.IsExpanded = true;
            }

            JobCountText.Text = $"({jobs.Count})";
            if (jobs.Count > 0)
            {
                // Don't rebuild cards if we're currently editing inline (would lose the form)
                if (_editingJobId == null && !shouldPreserveVisualTreeForRunningRefresh)
                {
                    RebuildJobCards(preserveHistory: _historyJobId != null);
                    if (_historyJobId != null && CurrentApp.GatewayClient != null)
                    {
                        ShowHistoryLoading(_historyJobId);
                        _ = CurrentApp.GatewayClient.RequestCronRunsAsync(_historyJobId, limit: 20, offset: 0);
                    }
                }
                else if (shouldPreserveVisualTreeForRunningRefresh)
                {
                    foreach (var runningJobId in _runningJobIds)
                        RefreshJobActionButtons(runningJobId);
                }
            }
            else
            {
                JobsListPanel.Children.Clear();
            }

            UpdateCronLoadingVisuals();
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

        string nextWake = "-";
        if (payload.TryGetProperty("nextWakeAtMs", out var wakeEl) && wakeEl.ValueKind == JsonValueKind.Number)
        {
            var ms = wakeEl.GetInt64();
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            nextWake = dt.ToString("yyyy-MM-dd HH:mm");
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            SchedulerStatusText.Text = enabled ? "Enabled" : "Disabled";
            SchedulerStatusIndicator.Fill = enabled
                ? (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]
                : (Brush)Application.Current.Resources["SystemFillColorNeutralBrush"];
            StorePathText.Text = storePath;
            NextWakeText.Text = LocalizationHelper.Format("CronPage_NextWakeFormat", nextWake);
        });
    }

    private void UpdateCronLoadingVisuals()
    {
        LoadingState.Visibility = _cronLoading.ShouldShowLoading ? Visibility.Visible : Visibility.Collapsed;
        JobsListPanel.Visibility = _cronLoading.ShouldShowContent ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _cronLoading.ShouldShowEmpty ? Visibility.Visible : Visibility.Collapsed;
        var canUseGateway = CurrentApp.GatewayClient != null && _cronLoading.CanEdit;
        NewJobButton.IsEnabled = canUseGateway;
        RefreshButton.IsEnabled = canUseGateway;
        FormSaveButton.IsEnabled = _cronLoading.CanEdit;
    }

    private void ShowDisconnected()
    {
        ConnectionInfoBar.Title = LocalizationHelper.GetString("CronPage_GatewayDisconnected.Title");
        ConnectionInfoBar.Message = LocalizationHelper.GetString("CronPage_GatewayDisconnected.Message");
        ConnectionInfoBar.Severity = InfoBarSeverity.Warning;
        ConnectionInfoBar.IsOpen = true;
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
                if (parent is Button or ToggleSwitch or Expander) return;
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
        // The page's main Grid (with row definitions) is named PageRootGrid;
        // it sits inside an outer wrapper Grid inside the ScrollViewer. Returning
        // the wrapper instead would lose the row definitions and cause the form
        // to overlap other rows of the inner grid.
        return PageRootGrid;
    }

    // --- Card building ---

    private void RebuildJobCards(bool preserveHistory = false)
    {
        var historyToRestore = preserveHistory ? _historyJobId : null;
        if (historyToRestore != null)
        {
            var historyVm = _jobs.Find(j => j.Id == historyToRestore);
            if (historyVm != null)
            {
                historyVm.IsExpanded = true;
                _expandedJobIds.Add(historyToRestore);
            }
            else
            {
                historyToRestore = null;
            }
        }

        _historyJobId = historyToRestore;
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
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: Name + badges + spacer + toggle + chevron
        var headerGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // 0: name
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // 1: schedule
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // 2: result
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // 3: running
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 4: spacer
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // 5: toggle
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // 6: chevron

        var nameText = new TextBlock { Text = vm.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(nameText, 0);
        headerGrid.Children.Add(nameText);

        var scheduleBadge = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
        };
        scheduleBadge.Child = new TextBlock { Text = vm.Schedule, FontSize = 10, FontFamily = new FontFamily("Consolas"), Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        Grid.SetColumn(scheduleBadge, 1);
        headerGrid.Children.Add(scheduleBadge);

        if (vm.ResultBadgeVisibility == Visibility.Visible)
        {
            var resultBadge = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
            };
            resultBadge.Child = new TextBlock { Text = vm.LastResult, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = vm.ResultBadgeForeground };
            Grid.SetColumn(resultBadge, 2);
            headerGrid.Children.Add(resultBadge);
        }

        var runningBadge = new Border
        {
            Tag = $"running_{vm.Id}",
            Visibility = _runningJobIds.Contains(vm.Id) ? Visibility.Visible : Visibility.Collapsed,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new TextBlock
            {
                Text = LocalizationHelper.GetString("CronPage_RunningBadge"),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorAttentionBrush"]
            }
        };
        Grid.SetColumn(runningBadge, 3);
        headerGrid.Children.Add(runningBadge);

        // Inline enabled/disabled toggle (right-aligned, before chevron).
        // Stop tapped from bubbling so the card doesn't toggle expand state.
        var enabledToggle = new ToggleSwitch
        {
            Tag = vm.Id,
            IsOn = vm.IsEnabled,
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            IsEnabled = _cronLoading.CanEdit && !vm.IsCompletedOneShot && !_runningJobIds.Contains(vm.Id)
        };
        ToolTipService.SetToolTip(enabledToggle, vm.IsCompletedOneShot
            ? LocalizationHelper.GetString("CronPage_CompletedOneTimeJobTooltip")
            : vm.IsEnabled ? LocalizationHelper.GetString("CronPage_DisableJobTooltip") : LocalizationHelper.GetString("CronPage_EnableJobTooltip"));
        enabledToggle.Toggled += OnEnabledToggleChanged;
        enabledToggle.Tapped += (s, ev) => { ev.Handled = true; };
        Grid.SetColumn(enabledToggle, 5);
        headerGrid.Children.Add(enabledToggle);

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
        var isRunning = _runningJobIds.Contains(vm.Id);
        var canRunOrEditJob = _cronLoading.CanEdit && !isRunning;

        // "Run Now" button — show running state if job is in the running set
        var runNowBtn = MakeActionButton("\uE768", LocalizationHelper.GetString("CronPage_RunNow"), vm.Id, OnRunNowClick, "RunNow");
        if (isRunning)
        {
            runNowBtn.Content = LocalizationHelper.GetString("CronPage_Running");
            runNowBtn.IsEnabled = false;
        }
        else if (!canRunOrEditJob)
        {
            runNowBtn.IsEnabled = false;
        }
        buttonsPanel.Children.Add(runNowBtn);

        var editBtn = MakeActionButton("\uE70F", LocalizationHelper.GetString("CronPage_EditAction"), vm.Id, OnEditJobClick, "Edit");
        editBtn.IsEnabled = canRunOrEditJob;
        buttonsPanel.Children.Add(editBtn);

        var histBtn = MakeActionButton("\uE81C", LocalizationHelper.GetString("CronPage_HistoryAction"), vm.Id, OnHistoryClick, "History");
        histBtn.IsEnabled = _cronLoading.CanEdit && vm.HasRunHistory;
        buttonsPanel.Children.Add(histBtn);

        var removeBtn = MakeActionButton("\uE711", LocalizationHelper.GetString("CronPage_RemoveAction"), vm.Id, OnRemoveClick, "Remove");
        removeBtn.IsEnabled = _cronLoading.CanEdit && !isRunning;
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

    private Button MakeActionButton(string glyph, string text, string jobId, RoutedEventHandler handler, string actionName)
    {
        var btn = new Button
        {
            Name = actionName,
            Tag = jobId,
            FontSize = 12,
            Padding = new Thickness(8, 4, 8, 4),
            IsEnabled = _cronLoading.CanEdit
        };
        btn.Content = MakeActionButtonContent(glyph, text);
        btn.Click += handler;
        return btn;
    }

    private static StackPanel MakeActionButtonContent(string glyph, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        sp.Children.Add(new TextBlock { Text = text });
        return sp;
    }

    private void RefreshJobActionButtons(string jobId)
    {
        var vm = _jobs.Find(j => j.Id == jobId);
        var isRunning = _runningJobIds.Contains(jobId);

        foreach (var button in FindVisualChildren<Button>(JobsListPanel))
        {
            if (button.Tag as string != jobId)
                continue;

            switch (button.Name)
            {
                case "RunNow":
                    button.Content = isRunning
                        ? LocalizationHelper.GetString("CronPage_Running")
                        : MakeActionButtonContent("\uE768", LocalizationHelper.GetString("CronPage_RunNow"));
                    button.IsEnabled = _cronLoading.CanEdit && !isRunning;
                    break;
                case "Edit":
                    button.IsEnabled = _cronLoading.CanEdit && !isRunning;
                    break;
                case "History":
                    button.IsEnabled = _cronLoading.CanEdit && vm?.HasRunHistory == true;
                    break;
                case "Remove":
                    button.IsEnabled = _cronLoading.CanEdit && !isRunning;
                    break;
            }
        }

        foreach (var toggle in FindVisualChildren<ToggleSwitch>(JobsListPanel))
        {
            if (toggle.Tag as string == jobId)
                toggle.IsEnabled = _cronLoading.CanEdit && vm?.IsCompletedOneShot != true && !isRunning;
        }

        foreach (var badge in FindVisualChildren<Border>(JobsListPanel))
        {
            if (badge.Tag as string == $"running_{jobId}")
                badge.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                yield return typed;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private UIElement BuildFullResponseExpander(string runEntryKey, string fullText, bool isError)
    {
        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
            IsExpanded = _expandedRunFullResponseIds.Contains(runEntryKey),
            Header = new TextBlock
            {
                Text = LocalizationHelper.GetString("CronPage_FullResponse"),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            }
        };
        expander.Expanding += (_, _) => _expandedRunFullResponseIds.Add(runEntryKey);
        expander.Collapsed += (_, _) => _expandedRunFullResponseIds.Remove(runEntryKey);

        UIElement content;

        if (isError)
        {
            content = new TextBlock
            {
                Text = fullText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 224, 85, 69))
            };
        }
        else
        {
            var markdown = ChatMarkdownSanitizer.Sanitize(fullText);
            var host = new FunctionalHostControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent)
            };
            host.Mount(_ => ChatMarkdownRenderer.Render(markdown)
                ?? OpenClawTray.FunctionalUI.Factories.TextBlock(markdown));
            content = host;
        }

        expander.Content = new Border
        {
            Padding = new Thickness(10, 8, 10, 10),
            Margin = new Thickness(0, 4, 0, 0),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = content
        };

        return expander;
    }

    private static Button MakeRunChatButton(string sessionKey)
    {
        var btn = new Button
        {
            Tag = sessionKey,
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new FontIcon { Glyph = "\uE8F2", FontSize = 11 });
        sp.Children.Add(new TextBlock { Text = LocalizationHelper.GetString("CronPage_OpenChat") });
        btn.Content = sp;
        btn.Click += OnOpenRunChatClick;
        return btn;
    }

    private static void OnOpenRunChatClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string sessionKey } || string.IsNullOrWhiteSpace(sessionKey))
            return;

        CurrentApp.PendingChatSessionKey = sessionKey;
        if (CurrentApp.ActiveHubWindow is HubWindow hub)
            hub.PendingChatSessionKey = sessionKey;

        ((IAppCommands)CurrentApp).Navigate("chat");
    }

    // --- Run History ---

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        var jobId = (sender as Button)?.Tag as string;
        if (string.IsNullOrEmpty(jobId)) return;
        if (!_cronLoading.CanEdit) return;
        if (CurrentApp.GatewayClient == null) { ShowDisconnected(); return; }
        var vm = _jobs.Find(j => j.Id == jobId);
        if (vm != null && !vm.HasRunHistory) return;
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

        ShowHistoryLoading(jobId);

        _ = CurrentApp.GatewayClient.RequestCronRunsAsync(jobId, limit: 20, offset: 0);
    }

    private void ShowHistoryLoading(string jobId)
    {
        var histPanel = FindHistoryPanel(jobId);
        if (histPanel == null) return;

        _lastHistoryRenderSignature = null;
        histPanel.Children.Clear();
        histPanel.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("CronPage_LoadingRunHistory"),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        histPanel.Visibility = Visibility.Visible;
    }

    private void HideHistoryPanel(string jobId)
    {
        var panel = FindHistoryPanel(jobId);
        if (panel != null)
        {
            _lastHistoryRenderSignature = null;
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
        if (data.TryGetProperty("entries", out var completionEntries) &&
            completionEntries.ValueKind == JsonValueKind.Array)
        {
            DetectCompletedRunsFromHistory(completionEntries);
        }

        if (_historyJobId == null) return;

        if (!data.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            var emptyHistoryPanel = FindHistoryPanel(_historyJobId);
            if (emptyHistoryPanel == null) return;
            _lastHistoryRenderSignature = null;
            emptyHistoryPanel.Children.Clear();
            emptyHistoryPanel.Children.Add(new TextBlock
            {
                Text = LocalizationHelper.GetString("CronPage_NoRunHistoryAvailable"),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            return;
        }

        var total = data.TryGetProperty("total", out var totalEl) && totalEl.ValueKind == JsonValueKind.Number ? totalEl.GetInt32() : 0;
        var hasMore = data.TryGetProperty("hasMore", out var hmEl) && hmEl.ValueKind == JsonValueKind.True;
        var signature = BuildHistoryRenderSignature(entries, total, hasMore);
        if (_runningJobIds.Count > 0 && string.Equals(_lastHistoryRenderSignature, signature, StringComparison.Ordinal))
            return;

        _lastHistoryRenderSignature = signature;
        var histPanel = FindHistoryPanel(_historyJobId);
        if (histPanel == null) return;
        histPanel.Children.Clear();

        var entryCount = 0;

        // Header
        var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerText = new TextBlock
        {
            Text = LocalizationHelper.GetString("CronPage_RunHistory"),
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
        headerText.Text = total > 0
            ? LocalizationHelper.Format("CronPage_RunHistoryShowing", entryCount, total)
            : LocalizationHelper.Format("CronPage_RunHistoryCount", entryCount);

        // "Load more" if there are more
        if (hasMore)
        {
            var nextOffset = data.TryGetProperty("nextOffset", out var noEl) && noEl.ValueKind == JsonValueKind.Number ? noEl.GetInt32() : entryCount;
            var loadMoreBtn = new Button
            {
                Content = LocalizationHelper.Format("CronPage_LoadOlderRuns", total - entryCount - (nextOffset - entryCount)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(0, 6, 0, 6),
                FontSize = 12,
                Tag = _historyJobId
            };

            // Simple "load more" content
            var remaining = total - nextOffset;
            if (remaining > 0)
                loadMoreBtn.Content = LocalizationHelper.Format("CronPage_LoadOlderRuns", remaining);
            else
                loadMoreBtn.Content = LocalizationHelper.GetString("CronPage_LoadMoreRuns");

            loadMoreBtn.Click += (s, args) =>
            {
                var jid = (s as Button)?.Tag as string;
                if (!string.IsNullOrEmpty(jid) && CurrentApp.GatewayClient != null)
                {
                    loadMoreBtn.IsEnabled = false;
                    loadMoreBtn.Content = LocalizationHelper.GetString("CronPage_Loading");
                    // For simplicity, reload with higher limit
                    _ = CurrentApp.GatewayClient.RequestCronRunsAsync(jid, limit: nextOffset + 20, offset: 0);
                }
            };
            histPanel.Children.Add(loadMoreBtn);
        }

        histPanel.Visibility = Visibility.Visible;
    }

    private static string BuildHistoryRenderSignature(JsonElement entries, int total, bool hasMore)
    {
        var parts = new List<string> { total.ToString(), hasMore ? "more" : "done" };
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            parts.Add(GetRunEntryKey(entry));
            if (entry.TryGetProperty("summary", out var summaryEl) && summaryEl.ValueKind == JsonValueKind.String)
                parts.Add((summaryEl.GetString()?.Length ?? 0).ToString());
            if (entry.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
                parts.Add(statusEl.GetString() ?? "");
        }
        return string.Join("|", parts);
    }

    private void DetectCompletedRunsFromHistory(JsonElement entries)
    {
        var completedJobIds = new HashSet<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            if (!entry.TryGetProperty("jobId", out var jobIdEl) || jobIdEl.ValueKind != JsonValueKind.String)
                continue;

            var jobId = jobIdEl.GetString();
            if (string.IsNullOrWhiteSpace(jobId) || !_runningJobIds.Contains(jobId))
                continue;

            var oldVm = _jobs.Find(j => j.Id == jobId);
            if (oldVm == null)
                continue;

            var runAtMs = entry.TryGetProperty("runAtMs", out var runAtEl) && runAtEl.ValueKind == JsonValueKind.Number
                ? runAtEl.GetInt64()
                : (entry.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number ? tsEl.GetInt64() : 0);
            if (runAtMs > oldVm.LastRunAtMs)
                completedJobIds.Add(jobId);
        }

        if (completedJobIds.Count == 0)
            return;

        foreach (var jobId in completedJobIds)
        {
            ClearRunningJob(jobId);
        }

        var client = CurrentApp.GatewayClient;
        if (client != null)
        {
            _ = client.RequestCronListAsync();
            _ = client.RequestCronStatusAsync();
        }
    }

    private Border BuildRunEntry(JsonElement entry)
    {
        var runEntryKey = GetRunEntryKey(entry);
        var status = entry.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "" : "";
        var durationMs = entry.TryGetProperty("durationMs", out var dEl) && dEl.ValueKind == JsonValueKind.Number ? dEl.GetInt64() : 0;
        var model = entry.TryGetProperty("model", out var modEl) ? modEl.GetString() ?? "" : "";
        var provider = entry.TryGetProperty("provider", out var providerEl) ? providerEl.GetString() ?? "" : "";
        var sessionKey = entry.TryGetProperty("sessionKey", out var sessionEl) ? sessionEl.GetString() ?? "" : "";
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
        var text = CronRunHistoryDisplay.ExtractText(entry, isError);
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
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2),
            Background = statusBg, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left
        };
        statusBadge.Child = new TextBlock { Text = status, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = statusFg };
        Grid.SetColumn(statusBadge, 0);
        grid.Children.Add(statusBadge);

        // Summary/error + metadata
        var contentPanel = new StackPanel { Margin = new Thickness(4, 0, 8, 0) };
        var displayText = text.PreviewText;
        if (!string.IsNullOrEmpty(displayText))
        {
            var truncated = displayText.Length > CronRunHistoryDisplay.PreviewMaxChars
                ? displayText[..CronRunHistoryDisplay.PreviewMaxChars] + "…"
                : displayText;
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
        if (!string.IsNullOrEmpty(provider)) metaParts.Add(provider);
        if (totalTokens > 0) metaParts.Add($"{totalTokens:N0} tokens");
        if (delivered) metaParts.Add("delivered");
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
        if (text.HasExpandableFullText)
            contentPanel.Children.Add(BuildFullResponseExpander(runEntryKey, text.FullText!, isError));
        if (!string.IsNullOrWhiteSpace(sessionKey))
            contentPanel.Children.Add(MakeRunChatButton(sessionKey));
        Grid.SetColumn(contentPanel, 1);
        grid.Children.Add(contentPanel);

        // Duration
        var durText = durationMs > 0 ? $"{durationMs / 1000.0:F1}s" : "-";
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
        var timeText = "-";
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

    private static string GetRunEntryKey(JsonElement entry)
    {
        var runId = entry.TryGetProperty("runId", out var runIdEl) && runIdEl.ValueKind == JsonValueKind.String
            ? runIdEl.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(runId))
            return runId!;

        var jobId = entry.TryGetProperty("jobId", out var jobIdEl) && jobIdEl.ValueKind == JsonValueKind.String
            ? jobIdEl.GetString()
            : "";
        var runAtMs = entry.TryGetProperty("runAtMs", out var runAtEl) && runAtEl.ValueKind == JsonValueKind.Number
            ? runAtEl.GetInt64()
            : (entry.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number ? tsEl.GetInt64() : 0);

        return $"{jobId}:{runAtMs}";
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
        public bool IsCompletedOneShot =>
            string.Equals(ScheduleKind, "at", StringComparison.OrdinalIgnoreCase) &&
            LastRunAtMs > 0 &&
            NextRunAtMs <= 0;
        public bool HasRunHistory => LastRunAtMs > 0;
        public string LastRunTime { get; set; } = "-";
        public string LastResult { get; set; } = "";
        public SolidColorBrush ResultBadgeForeground { get; set; } = new(Colors.White);
        public Visibility ResultBadgeVisibility { get; set; } = Visibility.Collapsed;
        public string NextRunTime { get; set; } = "-";
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
