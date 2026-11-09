using System.Reflection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.Shared.Usage;
using OpenClawTray.Presentation.Usage;

namespace OpenClaw.Tray.Tests.Usage;

public sealed class UsageExportWorkflowTests
{
    private static readonly DateOnly Today = new(2026, 10, 30);

    private static SessionsUsageReport Report(int? count = 1) => new()
    {
        Totals = new() { TotalTokens = 100, TotalCost = 1, Report = UsageReportState.Available },
        Sessions = [new() { Key = "one", Usage = new() { Totals = new() { TotalTokens = 100, TotalCost = 1 } } }],
        Aggregates = new()
        {
            SessionCount = count,
            CostDaily = [new("2026-10-30", new() { TotalTokens = 100, TotalCost = 1 })],
        },
    };

    private static (UsagePageController Controller, UsagePageControllerTests.UsageGateway Gateway) Controller(
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, UsagePageControllerTests.UsageGateway>();
        return (new(() => client, () => Today, action => action(), delay), (UsagePageControllerTests.UsageGateway)client);
    }

    [Theory]
    [InlineData("unchanged", true)]
    [InlineData("gateway", false)]
    [InlineData("page", false)]
    [InlineData("cancel", false)]
    public async Task PendingPicker_RechecksOriginBeforeWritingTheSelectedFile(string change, bool writes)
    {
        var first = DispatchProxy.Create<IOperatorGatewayClient, UsagePageControllerTests.UsageGateway>();
        var second = DispatchProxy.Create<IOperatorGatewayClient, UsagePageControllerTests.UsageGateway>();
        ((UsagePageControllerTests.UsageGateway)first).Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report()));
        IOperatorGatewayClient activeClient = first;
        using var controller = new UsagePageController(() => activeClient, () => Today, action => action());
        controller.Activate();
        using var temp = new TempDirectory();
        var target = temp.Combine("selected.csv");
        await File.WriteAllTextAsync(target, "existing file must survive a stale picker");
        var selected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pickerOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pageCurrent = true;
        var warnings = new List<string>();
        var workflow = new UsageExportWorkflow(controller, (name, content, current) =>
            UsageExportWorkflow.SaveAfterSelectionAsync<string>(() =>
            {
                pickerOpened.SetResult();
                return selected.Task;
            }, async path =>
            {
                await File.WriteAllTextAsync(path, content);
                return new UsageSavedExport(name, temp.Path);
            }, current), () => pageCurrent, warnings.Add);

        var export = workflow.ExportAsync(UsageExportFormat.DailyCsv);
        Assert.True(pickerOpened.Task.IsCompleted, "The synchronous fixture read must reach the pending picker.");
        Assert.False(export.IsCompleted);
        if (change == "gateway")
        {
            activeClient = second;
            controller.Activate(); // Replacement retains this controller, as the production page does.
        }
        if (change == "page") pageCurrent = false;
        selected.SetResult(change == "cancel" ? null : target);
        await export;

        var bytes = await File.ReadAllTextAsync(target);
        if (writes)
        {
            Assert.StartsWith("date,inputTokens", bytes);
            Assert.Contains("2026-10-30", bytes);
            Assert.NotNull(controller.State.ExportNotice?.FileName);
        }
        else
        {
            Assert.Equal("existing file must survive a stale picker", bytes);
            Assert.Null(controller.State.ExportNotice);
        }
        Assert.False(controller.State.ExportInteraction);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task BlockedSessions_OffersDailyTotalsUsingTheOriginalRangeAndReportsTheSavedFile()
    {
        var (controller, gateway) = Controller();
        using var lifetime = controller;
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report(1500)));
        var saved = new List<string>();
        var warnings = new List<string>();
        var workflow = new UsageExportWorkflow(controller, (name, content, _) =>
        {
            Assert.StartsWith("date,inputTokens", content);
            saved.Add(name);
            return Task.FromResult<UsageSavedExport?>(new(name, @"C:\proof"));
        }, () => true, warnings.Add);
        var run = workflow.ExportAsync(UsageExportFormat.SessionsCsv);
        Assert.False(run.IsCompleted);
        Assert.Equal(new UsageExportCoverage(1, 1500), controller.State.ExportBlock!.Coverage);
        Assert.True(controller.State.ExportInteraction);
        controller.SetPreset(UsagePreset.Today);
        controller.CompleteExportAlternative(true);
        await run;
        Assert.Equal(["usage-daily-2026-10-24_to_2026-10-30.csv"], saved);
        Assert.Equal("2026-10-24", gateway.Queries.Last().StartDate);
        Assert.Equal(@"C:\proof", controller.State.ExportNotice!.Folder);
        Assert.Null(controller.State.ExportNotice.Error);
        Assert.False(controller.State.ExportInteraction);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task UnknownPopulationAtTheCap_DoesNotInventATotalAndCloseDoesNotSave()
    {
        var (controller, gateway) = Controller();
        using var lifetime = controller;
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report(null) with
        {
            Sessions = Enumerable.Range(0, 1000).Select(index => new SessionUsageRow { Key = index.ToString() }).ToList(),
        }));
        var saves = 0;
        var warnings = new List<string>();
        var workflow = new UsageExportWorkflow(controller, (_, _, _) =>
        {
            saves++;
            return Task.FromResult<UsageSavedExport?>(null);
        }, () => true, warnings.Add);
        var run = workflow.ExportAsync(UsageExportFormat.SessionsCsv);
        Assert.Equal(new UsageExportCoverage(1000, null), controller.State.ExportBlock!.Coverage);
        controller.CompleteExportAlternative(false);
        await run;
        Assert.Equal(0, saves);
        Assert.Null(controller.State.ExportNotice);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveCancellationOrFailure_NeverProducesASuccessNotice(bool cancelled)
    {
        var (controller, gateway) = Controller();
        using var lifetime = controller;
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report()));
        var warnings = new List<string>();
        var workflow = new UsageExportWorkflow(controller, (_, _, _) => cancelled
            ? Task.FromResult<UsageSavedExport?>(null)
            : throw new UnauthorizedAccessException("synthetic private path"), () => true, warnings.Add);
        await workflow.ExportAsync(UsageExportFormat.DailyCsv);
        if (cancelled)
        {
            Assert.Null(controller.State.ExportNotice);
            Assert.Empty(warnings);
        }
        else
        {
            Assert.Equal(UsageExportError.Failed, controller.State.ExportNotice!.Error);
            Assert.Null(controller.State.ExportNotice.FileName);
            Assert.DoesNotContain("private path", Assert.Single(warnings));
        }
    }

    [Fact]
    public async Task IncompleteDailyAlternative_StaysBlockedRatherThanWritingAPartialFile()
    {
        var (controller, gateway) = Controller();
        using var lifetime = controller;
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report() with
        {
            CacheStatus = new("partial", 1, 0, null),
        }));
        var saved = false;
        var warnings = new List<string>();
        var workflow = new UsageExportWorkflow(controller, (_, _, _) =>
        {
            saved = true;
            return Task.FromResult<UsageSavedExport?>(null);
        }, () => true, warnings.Add);
        var run = workflow.ExportAsync(UsageExportFormat.SessionsCsv);
        controller.CompleteExportAlternative(true);
        await run;
        Assert.False(saved);
        Assert.Equal(UsageExportError.Incomplete, controller.State.ExportNotice!.Error);
    }

    [Fact]
    public async Task StaleDialogOrDisposedPage_NeverStartsASave()
    {
        var (controller, gateway) = Controller();
        using var lifetime = controller;
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report(1500)));
        var current = true;
        var saved = false;
        var warnings = new List<string>();
        var workflow = new UsageExportWorkflow(controller, (_, _, _) =>
        {
            saved = true;
            return Task.FromResult<UsageSavedExport?>(null);
        }, () => current, warnings.Add);
        var run = workflow.ExportAsync(UsageExportFormat.SessionsCsv);
        current = false;
        controller.CompleteExportAlternative(true);
        await run;
        Assert.False(saved);
        current = true;
        run = workflow.ExportAsync(UsageExportFormat.SessionsCsv);
        controller.Dispose();
        await run;
        Assert.False(saved);
    }

    [Fact]
    public async Task OneSaveOwnsTheInteractionAndAStaleReceiptCannotNotifyANewPage()
    {
        var (controller, gateway) = Controller();
        using var lifetime = controller;
        gateway.Usage = _ => Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(Report()));
        var pending = new TaskCompletionSource<UsageSavedExport?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = 0;
        var current = true;
        var warnings = new List<string>();
        var workflow = new UsageExportWorkflow(controller, (_, _, _) =>
        {
            saves++;
            return pending.Task;
        }, () => current, warnings.Add);
        var first = workflow.ExportAsync(UsageExportFormat.DailyCsv);
        Assert.True(controller.State.ExportInteraction);
        await workflow.ExportAsync(UsageExportFormat.DailyCsv);
        Assert.Equal(1, saves);
        current = false;
        pending.SetResult(new("saved.csv", @"C:\proof"));
        await first;
        Assert.Null(controller.State.ExportNotice);
        Assert.False(controller.State.ExportInteraction);
    }

    [Fact]
    public async Task NoticeTimer_IsSevenSecondsAndAnOldTimerCannotDismissANewerNotice()
    {
        var timers = new List<TaskCompletionSource>();
        var (controller, _) = Controller((duration, _) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(7), duration);
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            timers.Add(pending);
            return pending.Task;
        });
        using var lifetime = controller;
        controller.SetExportNotice(new("one.csv", null, @"C:\proof"));
        var second = new UsageExportNotice("two.csv", null, @"C:\proof");
        controller.SetExportNotice(second);
        timers[0].SetResult();
        await Task.Yield();
        Assert.Same(second, controller.State.ExportNotice);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += () => { if (controller.State.ExportNotice is null) expired.TrySetResult(); };
        timers[1].SetResult();
        await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(controller.State.ExportNotice);
    }

    [Fact]
    public void MissingPrices_KeepKnownTokensButNeverBecomeZeroCostInCsv()
    {
        var totals = new UsageTotals { TotalTokens = 100, MissingCostEntries = 1 };
        var sessions = UsageExport.SessionsCsv([new()
        {
            Key = "one", ModelProvider = "base", Model = "base-model", ProviderOverride = "actual", ModelOverride = "actual-model",
            Usage = new() { Totals = totals },
        }]).Split('\n')[1].Split(',');
        Assert.Equal("actual", sessions[4]);
        Assert.Equal("actual-model", sessions[5]);
        Assert.Equal("100", sessions[^2]);
        Assert.Equal("", sessions[^1]);
        var daily = UsageExport.DailyCsv([new("2026-10-30", totals)]).Split('\n')[1].Split(',');
        Assert.Equal("100", daily[5]);
        Assert.All(daily.Skip(6), value => Assert.Equal("", value));
    }
}
