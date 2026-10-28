using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Native Automations page against the synthetic automations Gateway. With
/// OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS=1 every main state is captured from the owned window.
/// </summary>
public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_EditorDraftSurvivesReconnect()
    {
        await WithAppAsync(async run =>
        {
            await OpenAutomationEditorAsync(run);
            const string draft = "Keep this unsaved draft";
            SetValue(FindById(run, "AutomationEditorName")!, draft);
            await run.Gateway.GoOfflineAsync();
            await WaitUiAsync(run, () => FindById(run, "AutomationsOpenConnection") is not null, "disconnected editor");
            run.Gateway.GoOnline();
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorSave") is { Current.IsEnabled: true }, "reconnected editor");
            Assert.Equal(draft, EditorValue(run, "AutomationEditorName"));
            Capture(run, "automations-reconnected-draft.png", "AutomationEditorName");
        }, scenario: GatewayScenario.CreateAutomations());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_OldSaveCannotCloseNewEditor()
    {
        await WithAppAsync(async run =>
        {
            await OpenAutomationEditorAsync(run);
            SetValue(FindById(run, "AutomationEditorName")!, "Saved editor A");
            run.Gateway.HoldResponses("cron.update");
            Invoke(FindById(run, "AutomationEditorSave")!);
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(r =>
                r.Method == "cron.update" && r.Outcome == "pending")), "held editor A save");
            Invoke(FindById(run, "AutomationsNew")!);
            await WaitUiAsync(run, () => EditorValue(run, "AutomationEditorName") == "", "new editor B");
            const string draft = "Unsaved editor B";
            SetValue(FindById(run, "AutomationEditorName")!, draft);
            run.Gateway.ReleaseResponses("cron.update");
            await WaitUiAsync(run, () => FindById(run, "AutomationsNotice") is not null, "completed editor A save");
            Assert.Equal(draft, EditorValue(run, "AutomationEditorName"));
            Capture(run, "automations-newer-editor.png", "AutomationEditorName", "AutomationsNotice");
        }, scenario: GatewayScenario.CreateAutomations());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_DirectorySuggestionsBelongToCurrentRoute()
    {
        await WithAppAsync(async run =>
        {
            await OpenAutomationEditorAsync(run);
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(r =>
                r.Method == "conversations.list" && r.Outcome == "ok")), "Slack directory");
            var recipient = FindById(run, "AutomationEditorTo")!;
            ScrollAncestorToBottom(recipient);
            SetEditorValue(run, "AutomationEditorTo", "On-call");
            await WaitUiAsync(run, () => FindText(run, "On-call (#oncall)") is not null, "Slack suggestion");
            run.Gateway.HoldResponses("conversations.list");
            SetValue(FindById(run, "AutomationEditorChannel")!, "telegram");
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(r =>
                r.Method == "conversations.list" && r.Outcome == "pending")), "held Telegram directory");
            SetEditorValue(run, "AutomationEditorTo", "On-call");
            await WaitUiAsync(run, () => EditorValue(run, "AutomationEditorTo") == "On-call", "current recipient input");
            await WaitUiAsync(run, () => FindText(run, "On-call (#oncall)") is null, "removed previous-route suggestion");
            Assert.Null(FindText(run, "On-call (#oncall)"));
            Capture(run, "automations-route-scoped-suggestions.png", "AutomationEditorChannel", "AutomationEditorTo");
            SetEditorValue(run, "AutomationEditorTo", "Owner");
            await WaitUiAsync(run, () => FindText(run, "@owner") is not null, "rendered saved-recipient query");
            SetValue(FindById(run, "AutomationEditorAccount")!, "default");
            run.Gateway.ReleaseResponses("conversations.list");
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.All(r =>
                r.Method != "conversations.list" || r.Outcome != "pending")), "completed Telegram directory");
            EditorValueElement(run, "AutomationEditorTo")!.SetFocus();
            SetEditorValue(run, "AutomationEditorTo", "Owne");
            await WaitUiAsync(run, () => FindText(run, "Owner (@owner)") is not null, "current Telegram suggestion");
            Assert.Null(FindText(run, "On-call (#oncall)"));
            Capture(run, "automations-current-route-suggestions.png", "AutomationEditorAccount", "AutomationEditorTo");
        }, scenario: GatewayScenario.CreateAutomations());
    }

    private static async Task OpenAutomationEditorAsync(GatewayFixtureRun run)
    {
        await run.InvokeAsync("app.navigate", new { page = "agent:main:cron" });
        await WaitUiAsync(run, () => FindById(run, "AutomationRow:morning-brief") is not null, "main automations");
        ((WindowPattern)AutomationsWindow(run).GetCurrentPattern(WindowPattern.Pattern))
            .SetWindowVisualState(WindowVisualState.Maximized);
        Invoke(FindById(run, "AutomationRow:morning-brief")!);
        await WaitUiAsync(run, () => FindById(run, "CronEdit:morning-brief") is { Current.IsEnabled: true }, "edit action");
        Invoke(FindById(run, "CronEdit:morning-brief")!);
        await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is not null, "automation editor");
    }

    private static AutomationElement? EditorValueElement(GatewayFixtureRun run, string id)
    {
        var element = FindById(run, id);
        if (element is null || element.TryGetCurrentPattern(ValuePattern.Pattern, out _))
            return element;
        return element.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
    }

    private static string? EditorValue(GatewayFixtureRun run, string id) =>
        EditorValueElement(run, id) is { } element
            ? ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value
            : null;

    private static void SetEditorValue(GatewayFixtureRun run, string id, string value) =>
        SetValue(EditorValueElement(run, id) ?? throw new InvalidOperationException($"Missing editor input {id}."), value);

    [GatewayFixtureUiTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_ScopesToAgentAndCoversMainStates(string theme)
    {
        var highContrast = new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        output.WriteLine($"Requested app theme: {theme}; Windows high contrast: {highContrast}");
        var scenario = GatewayScenario.CreateAutomations();
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageAppTheme") is not null, "theme setting");
            var selector = FindById(run, "SettingsPageAppTheme")!;
            ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            await WaitUiAsync(run, () => FindText(run, theme) is not null, "theme choice");
            var option = AppWindows(run).SelectMany(window => window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, theme)).Cast<AutomationElement>())
                .First(element => element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _));
            ((SelectionItemPattern)option.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            await run.InvokeAsync("app.navigate", new { page = "agent:main:cron" });
            await WaitUiAsync(run, () => FindById(run, "AutomationRow:morning-brief") is not null, "main automations list");
            ((WindowPattern)AutomationsWindow(run).GetCurrentPattern(WindowPattern.Pattern))
                .SetWindowVisualState(WindowVisualState.Maximized);
            Assert.Null(FindById(run, $"AutomationRow:{GatewayScenario.AutomationsOtherAgentJobId}"));
            Assert.All(run.Gateway.Requests.Where(r => r.Method == "cron.list"), r => Assert.Equal("main", r.AgentId));
            await WaitUiAsync(run, () => FindById(run, "AutomationsListCount") is not null, "list count");
            await ScanAutomationsAsync(run, theme, "list");
            Capture(run, "automations-list.png", "AutomationsListCount", "AutomationsRefresh", "AutomationRow:morning-brief");

            Invoke(FindById(run, "AutomationRow:morning-brief")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationDetailTitle") is not null
                && FindById(run, "AutomationScheduleSummary") is not null, "morning brief details");
            await WaitUiAsync(run, () => FindText(run, "92% succeeded") is not null, "loaded recent run summary");
            await ScanAutomationsAsync(run, theme, "details");
            Capture(run, "automations-details.png", "AutomationDetailTitle", "AutomationScheduleSummary", "CronRunNow:morning-brief");

            SelectItem(run, "Run history", detailPane: true);
            await WaitUiAsync(run, () => FindByIdPrefix(run, "AutomationRunTranscript:") is not null, "job run history");
            Invoke(FindByIdPrefix(run, "AutomationRunTranscript:")!);
            await WaitUiAsync(run, () => FindText(run, "Three things today") is not null && FindText(run, "Run transcript") is not null, "run transcript");
            await ScanAutomationsAsync(run, theme, "run transcript");
            Capture(run, "automations-run-history.png", "AutomationRunsSearch", "AutomationRunsStatusFilter");

            SelectItem(run, "Overview", detailPane: true);
            await WaitUiAsync(run, () => FindById(run, "CronEdit:morning-brief") is { Current.IsEnabled: true }, "edit action");
            Invoke(FindById(run, "CronEdit:morning-brief")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is not null, "editor");
            var updates = run.Gateway.Requests.Count(r => r.Method == "cron.update" && r.Outcome == "ok");
            SetValue(FindById(run, "AutomationEditorName")!, "Morning brief (edited)");
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorTitle") is not null, "editor title");
            await ScanAutomationsAsync(run, theme, "editor");
            Capture(run, "automations-editor.png", "AutomationEditorName", "AutomationEditorTitle");
            Invoke(FindById(run, "AutomationEditorSave")!);
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Count(r => r.Method == "cron.update" && r.Outcome == "ok") > updates), "accepted cron.update");
            var saved = scenario.GetCronJobs().Single(j => j.GetProperty("id").GetString() == "morning-brief");
            Assert.Equal("Morning brief (edited)", saved.GetProperty("name").GetString());
            Assert.Equal("America/Los_Angeles", saved.GetProperty("schedule").GetProperty("tz").GetString());
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null, "closed editor");

            Invoke(FindById(run, "AutomationRow:inbox-sweep")!);
            await WaitUiAsync(run, () => FindById(run, "CronPause:inbox-sweep") is { Current.IsEnabled: true }, "pause action");
            Invoke(FindById(run, "CronPause:inbox-sweep")!);
            await WaitUiAsync(run, () => FindButton(run, "Pause") is not null && FindText(run, "Pause \"Inbox sweep\"?") is not null, "pause confirmation");
            Capture(run, "automations-confirm-pause.png");
            var pauseButtons = FindAllButtons(run, "Pause");
            Invoke(pauseButtons.Single(button => button.Current.AutomationId != "CronPause:inbox-sweep"));
            await run.WaitForAsync(() => Task.FromResult(scenario.GetCronJobs()
                .Single(j => j.GetProperty("id").GetString() == "inbox-sweep").GetProperty("enabled").GetBoolean() == false), "paused job");

            SelectItem(run, "Run history", detailPane: false);
            await WaitUiAsync(run, () => FindText(run, "Every run of every automation") is not null && FindByIdPrefix(run, "AutomationRunTranscript:") is not null, "all runs");
            Assert.Null(FindText(run, "Ops backup (other agent)"));
            Capture(run, "automations-all-runs.png", "AutomationRunsSearch");

            await run.InvokeAsync("app.navigate", new { page = "agent:research:cron" });
            await WaitUiAsync(run, () => FindById(run, "AutomationSuggestion:repoPulse") is not null, "empty research agent");
            Assert.Null(FindByIdPrefix(run, "AutomationRow:"));
            await ScanAutomationsAsync(run, theme, "empty");
            Capture(run, "automations-empty.png", "AutomationsEmptyNew", "AutomationSuggestion:repoPulse");

            // Keep the page's own recovery state mounted while the connection manager retries.
            await run.Gateway.GoOfflineAsync();
            await WaitUiAsync(run, () => FindById(run, "AutomationsOpenConnection") is not null, "disconnected state");
            // AccessibilityScanTests covers disconnected CronPage without reconnect events
            // invalidating Axe's cached elements while it walks the native tree.
            Capture(run, "automations-disconnected.png", "AutomationsOpenConnection");
            run.Gateway.GoOnline();
        }, scenario: scenario);
    }

    private static async Task ScanAutomationsAsync(GatewayFixtureRun run, string theme, string state)
    {
        await WaitUiAsync(run, () => run.Gateway.Requests.All(request => request.Outcome != "pending"),
            $"completed Gateway reads before {state} scan");
        AxeHelper.AssertNoAccessibilityErrors(run.AppProcessId,
            new IntPtr(AutomationsWindow(run).Current.NativeWindowHandle),
            context: $"Automations {theme}: {state}");
    }

    private static AutomationElement AutomationsWindow(GatewayFixtureRun run) =>
        AppWindows(run).First(window => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "CronPageMarker")) is not null);

    private static AutomationElement? FindByIdPrefix(GatewayFixtureRun run, string prefix)
    {
        foreach (var window in AppWindows(run))
            foreach (AutomationElement element in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                if (element.Current.AutomationId.StartsWith(prefix, StringComparison.Ordinal))
                    return element;
        return null;
    }

    private static AutomationElement[] FindAllButtons(GatewayFixtureRun run, string name) =>
        AppWindows(run).SelectMany(window => window.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, name))).Cast<AutomationElement>()).ToArray();

    /// <summary>SelectorBar items expose no container id; the detail tabs sit right of the list pane's view selector.</summary>
    private static void SelectItem(GatewayFixtureRun run, string name, bool detailPane)
    {
        var candidates = AppWindows(run).SelectMany(window => window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, name)).Cast<AutomationElement>())
            .Where(element => element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
            .OrderBy(element => element.Current.BoundingRectangle.Left)
            .ToList();
        Assert.NotEmpty(candidates);
        Invoke(detailPane ? candidates.Last() : candidates.First());
    }
    private static void SetValue(AutomationElement element, string value) =>
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);

    private static void Capture(GatewayFixtureRun run, string name, params string[] assertedIds)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") != "1")
            return;
        Thread.Sleep(400);
        var window = AutomationsWindow(run);
        OwnedWindowCapture.Save(run, window, name,
            assertedIds.Select(id => FindById(run, id) ?? throw new InvalidOperationException($"Missing {id} for {name}.")).ToArray());
    }
}
