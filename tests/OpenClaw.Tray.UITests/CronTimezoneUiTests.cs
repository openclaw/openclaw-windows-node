using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task CronEditor_PreservesAbsentAndUnlistedZonesUntilExplicitSelection()
    {
        var scenario = GatewayScenario.CreateCronEditing();
        await WithAppAsync(async run =>
        {
            await OpenCronPageAsync(run);
            await EditCronAsync(run, "listed");
            await SaveCronAsync(run, scenario, "listed", "America/Chicago");
            await EditCronAsync(run, "absent");
            await SaveCronAsync(run, scenario, "absent", null);
            await EditCronAsync(run, "unlisted");
            await SaveCronAsync(run, scenario, "unlisted", "Pacific/Auckland");
            await EditCronAsync(run, "unlisted");
            Invoke(CronControl(run, "FormCancelButton"));
            await EditCronAsync(run, "unlisted");
            await SelectCronOptionAsync(run, "FormTimezone", "UTC");
            await SaveCronAsync(run, scenario, "unlisted", "UTC");

            await EditCronAsync(run, "listed");
            Invoke(CronControl(run, "FormCancelButton"));
            await EditCronAsync(run, "every");
            await SelectCronOptionAsync(run, "FormScheduleKind", "Cron");
            SetCronText(run, "FormCronExpr", "0 4 * * *");
            await SaveCronAsync(run, scenario, "every", null);
        }, scenario: scenario);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task CronEditor_NewJobOtherJobAndScheduleKindDoNotInheritPreservedZone()
    {
        foreach (var kind in new[] { "Every", "At" })
        {
            var scenario = GatewayScenario.CreateCronEditing();
            await WithAppAsync(async run =>
            {
                await OpenCronPageAsync(run);
                await EditCronAsync(run, "unlisted");
                Invoke(CronControl(run, "NewJobButton"));
                await WaitUiAsync(run, () => FindCronControl(run, "FormName") is { } name
                    && ((ValuePattern)name.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == "", "reset new-job form");
                SetCronText(run, "FormName", "Fixture new cron");
                SetCronText(run, "FormMessage", "Synthetic proof only.");
                await SelectCronOptionAsync(run, "FormScheduleKind", "Cron");
                SetCronText(run, "FormCronExpr", "0 5 * * *");
                await SaveCronAsync(run, scenario, "created-4", null, create: true);

                await EditCronAsync(run, "unlisted");
                await SelectCronOptionAsync(run, "FormScheduleKind", kind);
                await SelectCronOptionAsync(run, "FormScheduleKind", "Cron");
                await SaveCronAsync(run, scenario, "unlisted", null);
            }, scenario: scenario);
        }

        var switchScenario = GatewayScenario.CreateCronEditing();
        await WithAppAsync(async run =>
        {
            await OpenCronPageAsync(run);
            await EditCronAsync(run, "unlisted");
            await EditCronAsync(run, "absent");
            await SaveCronAsync(run, switchScenario, "absent", null);
        }, scenario: switchScenario);
    }

    private static async Task OpenCronPageAsync(GatewayFixtureRun run)
    {
        await run.InvokeAsync("app.navigate", new { page = "cron" });
        await WaitUiAsync(run, () => FindById(run, "CronJobTitle:listed") is not null, "fixture cron list");
        ((WindowPattern)CronWindow(run).GetCurrentPattern(WindowPattern.Pattern))
            .SetWindowVisualState(WindowVisualState.Maximized);
    }

    private static AutomationElement? FindCronWindow(GatewayFixtureRun run) =>
        AppWindows(run).SingleOrDefault(window => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsConnectionStatus")) is not null);

    private static AutomationElement CronWindow(GatewayFixtureRun run) =>
        FindCronWindow(run) ?? throw new InvalidOperationException("Owned Settings window is unavailable.");

    private static AutomationElement? FindCronControl(GatewayFixtureRun run, string id) =>
        FindCronWindow(run)?.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static AutomationElement CronControl(GatewayFixtureRun run, string id) =>
        FindCronControl(run, id) ?? throw new InvalidOperationException($"Missing cron control {id}.");

    private static string CronText(GatewayFixtureRun run, string id) =>
        ((ValuePattern)CronControl(run, id).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    private static void SetCronText(GatewayFixtureRun run, string id, string value) =>
        ((ValuePattern)CronControl(run, id).GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);

    private static async Task EditCronAsync(GatewayFixtureRun run, string id)
    {
        await WaitUiAsync(run, () => FindCronControl(run, $"CronJobTitle:{id}") is not null, $"available cron card {id}");
        var editId = $"CronEdit:{id}";
        if (FindCronControl(run, editId) is null)
        {
            var title = CronControl(run, $"CronJobTitle:{id}");
            ScrollCronControlIntoView(run, title);
            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                OwnedWindowCapture.Save(run, CronWindow(run), $"cron-target-{id}.png", title);
            ClickOwnedCronTitle(run, CronWindow(run), title);
            await WaitUiAsync(run, () => FindCronControl(run, editId) is not null, $"expanded cron card {id}");
        }
        Invoke(CronControl(run, editId));
        await WaitUiAsync(run, () => FindCronControl(run, "FormName") is { } name
            && ((ValuePattern)name.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == $"Fixture cron {id}", $"editing exact job {id}");
    }

    private static async Task SelectCronOptionAsync(GatewayFixtureRun run, string id, string name)
    {
        var combo = CronControl(run, id);
        ScrollCronControlIntoView(run, combo);
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.NameProperty, name));
        AutomationElement? Option() => combo.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>()
            .SingleOrDefault(item => item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection)
                && ((SelectionItemPattern)selection).Current.SelectionContainer is { } container
                && Automation.Compare(container, combo));
        await WaitUiAsync(run, () => Option() is not null, $"{id} option {name}");
        Invoke(Option()!);
        expand.Collapse();
        await WaitUiAsync(run, () => FindCronControl(run, id) is { } updated
            && ((SelectionPattern)updated.GetCurrentPattern(SelectionPattern.Pattern))
                .Current.GetSelection().SingleOrDefault()?.Current.Name == name, $"selected cron option {name}");
    }

    private async Task SaveCronAsync(GatewayFixtureRun run, GatewayScenario scenario, string id, string? timezone,
        bool create = false)
    {
        var method = create ? "cron.add" : "cron.update";
        var before = run.Gateway.Requests.Count(request => request.Method == method && request.Outcome == "ok");
        var previousJobs = scenario.GetCronJobs();
        // A unique edit prevents an unchanged target from passing when another job receives the save.
        SetCronText(run, "FormCronExpr", $"{before + 11} 7 * * *");
        var expression = CronText(run, "FormCronExpr");
        var label = $"{before}-{id}-{timezone ?? "absent"}".Replace('/', '-');
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
        {
            ScrollCronControlIntoView(run, CronControl(run, "FormName"));
            OwnedWindowCapture.Save(run, CronWindow(run), $"cron-edit-{label}.png",
                CronControl(run, "FormName"), CronControl(run, "FormTimezone"));
        }
        Invoke(CronControl(run, "FormSaveButton"));
        await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Count(request =>
            request.Method == method && request.Outcome == "ok") > before), $"accepted {method}");
        await WaitUiAsync(run, () => FindCronWindow(run) is { } window
            && window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "FormName")) is null, "closed saved cron editor");
        var refreshedJobs = scenario.GetCronJobs();
        Assert.Equal(previousJobs.Length + (create ? 1 : 0), refreshedJobs.Length);
        foreach (var other in previousJobs.Where(job => job.GetProperty("id").GetString() != id))
        {
            var refreshed = refreshedJobs.Single(job => job.GetProperty("id").GetString() == other.GetProperty("id").GetString());
            Assert.Equal(other.GetRawText(), refreshed.GetRawText());
        }
        var stored = refreshedJobs.Single(job => job.GetProperty("id").GetString() == id);
        var schedule = stored.GetProperty("schedule");
        Assert.Equal("cron", schedule.GetProperty("kind").GetString());
        Assert.Equal(expression, schedule.GetProperty("expr").GetString());
        if (timezone is null) Assert.False(schedule.TryGetProperty("tz", out _));
        else Assert.Equal(timezone, schedule.GetProperty("tz").GetString());
        // Persist only the synthetic fixture's schedule, never credentials or user content.
        await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, $"cron-saved-{label}.json"),
            JsonSerializer.Serialize(new { method, id, schedule }));
        output.WriteLine($"{method} id={id} saved schedule={schedule.GetRawText()}");
        Invoke(CronControl(run, "RefreshButton"));
        await WaitUiAsync(run, () => FindCronControl(run, $"CronJobTitle:{id}") is not null, $"refreshed saved job {id}");
    }

    private static void ScrollCronControlIntoView(GatewayFixtureRun run, AutomationElement element)
    {
        if (!element.Current.IsOffscreen) return;
        var scroll = CronWindow(run).FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))
            .Cast<AutomationElement>()
            .Select(candidate => (ScrollPattern)candidate.GetCurrentPattern(ScrollPattern.Pattern))
            .First(pattern => pattern.Current.VerticallyScrollable);
        for (var step = 0; step <= 10 && element.Current.IsOffscreen; step++)
        {
            scroll.SetScrollPercent(ScrollPattern.NoScroll, step * 10);
            Thread.Sleep(50);
        }
        Assert.False(element.Current.IsOffscreen, "Target must be visible before native cron input.");
    }

    private static void ClickOwnedCronTitle(GatewayFixtureRun run, AutomationElement window, AutomationElement title)
    {
        run.EnsureRunning();
        Assert.Equal(run.AppProcessId, window.Current.ProcessId);
        Assert.Equal(run.AppProcessId, title.Current.ProcessId);
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        Assert.True(SetForegroundWindow(handle), "Cannot foreground owned cron window.");
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Assert.NotEqual(IntPtr.Zero, previous);
        try
        {
            Assert.Equal(handle, GetForegroundWindow());
            var bounds = title.Current.BoundingRectangle;
            Assert.True(bounds.Width > 0 && bounds.Height > 0 && !title.Current.IsOffscreen);
            var point = new CronPoint { X = (int)(bounds.Left + bounds.Width / 2), Y = (int)(bounds.Top + bounds.Height / 2) };
            Assert.Equal(handle, GetAncestor(WindowFromPoint(point), 2));
            var hit = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            // WinUI can return its content bridge instead of the text peer for point queries.
            var bridgeContainsTitle = hit.Current.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge"
                && hit.Current.ProcessId == run.AppProcessId
                && hit.FindFirst(TreeScope.Descendants, new PropertyCondition(
                    AutomationElement.AutomationIdProperty, title.Current.AutomationId)) is not null;
            Assert.True(title.Current.AutomationId == hit.Current.AutomationId || bridgeContainsTitle,
                $"Title {title.Current.AutomationId} at {bounds}, point {point.X},{point.Y}; hit " +
                $"{hit.Current.AutomationId}/{hit.Current.Name}/{hit.Current.ClassName}/{hit.Current.ControlType.ProgrammaticName} at {hit.Current.BoundingRectangle}");
            Assert.True(SetCursorPos(point.X, point.Y));
            Assert.True(GetCursorPos(out var actual));
            Assert.Equal(point, actual);
            Assert.Equal(handle, GetForegroundWindow());
            Assert.Equal(handle, GetAncestor(WindowFromPoint(actual), 2));
            var inputs = new[]
            {
                new CronInput { Mouse = new CronMouse { Flags = 2 } },
                new CronInput { Mouse = new CronMouse { Flags = 4 } }
            };
            var sent = SendInput(2, inputs, Marshal.SizeOf<CronInput>());
            if (sent == 1) SendInput(1, [inputs[1]], Marshal.SizeOf<CronInput>());
            Assert.Equal(2u, sent);
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CronPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CronInput { public uint Type; public CronMouse Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CronMouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(CronPoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CronPoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, CronInput[] inputs, int size);
}
