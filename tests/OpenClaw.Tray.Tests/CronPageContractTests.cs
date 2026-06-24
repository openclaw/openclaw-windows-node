namespace OpenClaw.Tray.Tests;

public sealed class CronPageContractTests
{
    [Fact]
    public void CompletedOneTimeJobs_KeepHistoryAndRemoveActionable()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "CronPage.xaml.cs"));

        Assert.DoesNotContain("CardOpacity", source);
        Assert.DoesNotContain(".Opacity = 0.4", source);
        Assert.Contains("IsCompletedOneShot", source);
        Assert.Contains("var canRunOrEditJob = _cronLoading.CanEdit && !isRunning;", source);
        Assert.Contains("editBtn.IsEnabled = canRunOrEditJob;", source);
        Assert.Contains("histBtn.IsEnabled = _cronLoading.CanEdit && vm.HasRunHistory;", source);
        Assert.Contains("removeBtn.IsEnabled = _cronLoading.CanEdit && !isRunning;", source);
        Assert.Contains("IsEnabled = _cronLoading.CanEdit && !vm.IsCompletedOneShot && !_runningJobIds.Contains(vm.Id)", source);
        Assert.Contains("if (_runningJobIds.Contains(jobId)) return;", ExtractMethod(source, "OnRemoveClick"));
        Assert.Contains("if (_runningJobIds.Contains(jobId)) return;", ExtractMethod(source, "OnEnabledToggleChanged"));
        Assert.Contains("CancelAllRunRefreshLoops();", source);
        Assert.Contains("CancelRunRefreshLoop(jobId);", source);
        Assert.Contains("ClearRunningJob(jobId);", source);
        Assert.Contains("Task.Delay(TimeSpan.FromSeconds(3), token)", source);
        Assert.Contains("CronPage_DisableJobTooltip", source);
        Assert.Contains("CronPage_EnableJobTooltip", source);
        Assert.Contains("BuildFullResponseExpander", source);
        Assert.Contains("CronRunHistoryDisplay.ExtractText(entry, isError)", source);
        Assert.Contains("MakeRunChatButton(sessionKey)", source);
        var runNowHandler = ExtractMethod(source, "OnRunNowClick");
        Assert.DoesNotContain("if (vm != null && !vm.IsEnabled) return;", runNowHandler);
        Assert.Contains("if (_runningJobIds.Contains(jobId)) return;", runNowHandler);
        Assert.Contains("RunCronJobDetailedAsync(jobId)", runNowHandler);
        Assert.Contains("result?.Enqueued != true", runNowHandler);
        Assert.Contains("ShowRunNotStartedNotification", runNowHandler);
        Assert.Contains("RefreshJobActionButtons(jobId);", runNowHandler);
        Assert.Contains("StartRunRefreshLoop(jobId);", runNowHandler);
        var refreshLoop = ExtractMethod(source, "StartRunRefreshLoop");
        Assert.Contains("RequestCronRunsAsync(jobId, limit: 20, offset: 0)", refreshLoop);
        Assert.DoesNotContain("RequestCronListAsync()", refreshLoop);
        var parseCronList = ExtractMethod(source, "ParseCronList");
        Assert.Contains("oldVm != null && vm.LastRunAtMs > 0 && vm.LastRunAtMs != oldVm.LastRunAtMs", parseCronList);
        Assert.DoesNotContain("vm.RunningAtMs == 0 || oldVm == null || vm.LastRunAtMs != oldVm.LastRunAtMs", parseCronList);
        Assert.Contains("shouldPreserveVisualTreeForRunningRefresh", parseCronList);
        Assert.Contains("RefreshJobActionButtons(runningJobId);", parseCronList);
        var editHandler = ExtractMethod(source, "OnEditJobClick");
        Assert.DoesNotContain("if (vm == null || !vm.IsEnabled) return;", editHandler);
        Assert.Contains("if (vm == null || _runningJobIds.Contains(jobId)) return;", editHandler);
        var historyHandler = ExtractMethod(source, "OnHistoryClick");
        Assert.DoesNotContain("if (vm != null && !vm.IsEnabled) return;", historyHandler);
        Assert.Contains("if (vm != null && !vm.HasRunHistory) return;", historyHandler);
        var cardTappedHandler = ExtractMethod(source, "OnCardTapped");
        Assert.Contains("parent is Button or ToggleSwitch or Expander", cardTappedHandler);
        Assert.DoesNotContain("or ScrollViewer", cardTappedHandler);
        Assert.Contains("RebuildJobCards(preserveHistory: _historyJobId != null);", source);
        Assert.Contains("ShowHistoryLoading(_historyJobId);", source);
        Assert.Contains("Name = actionName", source);
        Assert.Contains("LocalizationHelper.GetString(\"CronPage_OpenChat\")", source);
        Assert.DoesNotContain("SessionsPage_OpenChatButton.Content", source);
        Assert.Contains("Tag = $\"running_{vm.Id}\"", source);
        Assert.Contains("badge.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;", source);
        Assert.Contains("_expandedRunFullResponseIds.Contains(runEntryKey)", source);
        Assert.Contains("BuildHistoryRenderSignature(entries, total, hasMore)", source);
        Assert.Contains("string.Equals(_lastHistoryRenderSignature, signature", source);
        var fullResponseBuilder = ExtractMethod(source, "BuildFullResponseExpander");
        Assert.DoesNotContain("VerticalScrollBarVisibility", fullResponseBuilder);
        Assert.DoesNotContain("MaxHeight = 360", fullResponseBuilder);
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var markers = new[]
        {
            $"private void {methodName}",
            $"private UIElement {methodName}",
            $"private static string {methodName}"
        };
        var marker = markers.FirstOrDefault(m => source.Contains(m, StringComparison.Ordinal));
        Assert.NotNull(marker);

        var start = source.IndexOf(marker!, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {methodName}.");

        var brace = source.IndexOf('{', start);
        Assert.True(brace >= 0, $"Could not find {methodName} body.");

        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(i + 1)];
            }
        }

        Assert.Fail($"Could not parse {methodName} body.");
        return string.Empty;
    }
}
