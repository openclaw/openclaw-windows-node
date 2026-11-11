using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.UITests;

/// <summary>Mounted native Reactor UI, synthetic Gateway only. Never invokes browser or clipboard handoffs.</summary>
public sealed partial class GatewayFixtureUiTests
{
    private static readonly TimeSpan ErrorToastPersistenceProbe = TimeSpan.FromSeconds(5);

    internal static void AssertAutomationsPageReadScope(IEnumerable<GatewayFixtureRequest> requests, string agentId)
    {
        var reads = requests.Where(request => request.Method == "cron.list").ToArray();
        // App-owned Inbox hydration legitimately reads the full catalog before navigation.
        // Only its strict paged-catalog shape is separate; an unscoped typed page read still fails.
        Assert.Contains(reads, request => request.IsUnscopedCronCatalogRead && request.Outcome == "ok");
        Assert.All(reads.Where(request => request.IsUnscopedCronCatalogRead), request => Assert.Null(request.AgentId));
        var pageReads = reads.Where(request => !request.IsUnscopedCronCatalogRead).ToArray();
        Assert.NotEmpty(pageReads);
        Assert.All(pageReads, request => Assert.Equal(agentId, request.AgentId));
        Assert.Contains(pageReads, request => request.Outcome == "ok");
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_DesignerAgendaBudgetAndResizeKeepExpansionAndDialogDraft()
    {
        var scenario = GatewayScenario.CreateAutomations(additionalJobs: 12);
        var tomorrow = new DateTimeOffset(DateTime.Now.Date.AddDays(1).AddHours(12)).ToUnixTimeMilliseconds();
        // Overdue Gateway-reported instants still belong to today's first group.
        var today = new DateTimeOffset(DateTime.Now.Date.AddMinutes(5)).ToUnixTimeMilliseconds();
        scenario.SetCronJobRunning("inbox-sweep", true);
        scenario.SetCronJobNextRun("morning-brief", tomorrow);
        scenario.SetCronJobNextRun("deploy-watch", tomorrow);
        scenario.SetCronJobNextRun("dentist", tomorrow);
        for (var index = 0; index < 12; index++)
            scenario.SetCronJobNextRun($"scheduled-{index:D3}", index < 9 ? today : tomorrow + index * 60_000);
        await WithAppAsync(async run =>
        {
            await SelectAppThemeAsync(run, "Light");
            await OpenMainAutomationsAsync(run);
            async Task ResizeAsync(int outerWindowDip)
            {
                var window = AutomationsWindow(run);
                var scale = OwnedWindowCapture.OwnedDpiScale(run, window);
                var bounds = OwnedWindowCapture.ReadOwnedBounds(run, window);
                ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
                await WaitUiAsync(run, () => ((WindowPattern)AutomationsWindow(run).GetCurrentPattern(WindowPattern.Pattern))
                    .Current.WindowVisualState == WindowVisualState.Normal,
                    "owned window restored for responsive resize");
                window = AutomationsWindow(run);
                OwnedWindowCapture.ResizeOwnedPixels(run, window, (int)Math.Ceiling(outerWindowDip * scale), (int)bounds.Height);
                await WaitUiAsync(run, () => Math.Abs(OwnedWindowCapture.ReadOwnedBounds(run, AutomationsWindow(run)).Width / scale
                    - outerWindowDip) <= 2, $"owned window at {outerWindowDip} DIP");
            }
            double AgendaWidth() => OwnedWindowCapture.WithPhysicalCoordinates(() =>
            {
                // Reactor layout Borders have no UIA peer. Measure the exposed agenda columns.
                if (FindById(run, "AutomationsAgendaList") is not { } list) return 0;
                var bounds = list.Current.BoundingRectangle;
                foreach (var earlier in AutomationsWindow(run).FindAll(TreeScope.Descendants, Condition.TrueCondition)
                    .Cast<AutomationElement>().Where(element => element.Current.AutomationId.StartsWith("AutomationEarlier:", StringComparison.Ordinal)))
                    bounds.Union(earlier.Current.BoundingRectangle);
                return bounds.Width / OwnedWindowCapture.OwnedDpiScale(run, AutomationsWindow(run));
            });
            async Task CaptureAgendaAsync(string name)
            {
                await WaitUiAsync(run,
                    () => FindById(run, "AutomationAgenda:inbox-sweep") is { Current.IsOffscreen: false },
                    "scheduled agenda row visible for capture");
                if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                    OwnedWindowCapture.Save(run, AutomationsWindow(run), name, FindById(run, "AutomationAgenda:inbox-sweep")!);
            }
            int AgendaRows() => AutomationsWindow(run).FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>().Count(element => element.Current.AutomationId.StartsWith("AutomationAgenda:", StringComparison.Ordinal));
            await ResizeAsync(1400);
            await WaitUiAsync(run, () => AgendaWidth() >= 1000 && AgendaRows() == 6, "wide agenda has six scheduled rows");
            Assert.NotNull(FindById(run, "AutomationAgenda:inbox-sweep"));
            Assert.Null(FindById(run, "AutomationAgendaMore:Running"));
            await CaptureAgendaAsync("automations-designer-Light-1400.png");
            await ResizeAsync(1000);
            await WaitUiAsync(run, () => AgendaWidth() < 820 && AgendaRows() == 4
                && FindById(run, "AutomationAgendaMore:Tomorrow")?.Current.Name == "Show all 5", "own-width compact budget and fully hidden group");
            Assert.Null(FindById(run, "AutomationAgendaMore:Running"));
            await CaptureAgendaAsync("automations-designer-Light-shell-minimum.png");
            Invoke(FindById(run, "AutomationAgendaMore:Hour")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationAgendaMore:Hour")?.Current.Name == "Show fewer",
                "hour group expanded");
            await ScrollAutomationsToTopAsync(run);
            await InvokeWhenEnabledAsync(run, () => FindById(run, "AutomationsNew"), "new editor after agenda expansion");
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is not null, "editor before resizing");
            Invoke(FindById(run, "AutomationEditorSave")!);
            await WaitUiAsync(run, () => FindText(run, "Enter a name.") is not null
                && FindText(run, "Describe the task this agent should run.") is not null, "editor required errors");
            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                OwnedWindowCapture.Save(run, AutomationsWindow(run), "automations-designer-editor-errors.png",
                    FindById(run, "AutomationEditorName")!, FindById(run, "AutomationEditorSave")!);
            SetEditorValue(run, "AutomationEditorName", "Resize-kept name");
            SetEditorValue(run, "AutomationEditorPrompt", "Resize-kept prompt");
            await ResizeAsync(1400);
            Assert.Equal("Resize-kept name", EditorValue(run, "AutomationEditorName"));
            Assert.Equal("Resize-kept prompt", EditorValue(run, "AutomationEditorPrompt"));
            Invoke(FindById(run, "AutomationEditorCancel")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null
                && FindById(run, "AutomationAgendaMore:Hour")?.Current.Name == "Show fewer", "resize retained expansion and draft");
            Invoke(FindById(run, "AutomationAgendaMore:Hour")!);
            await WaitUiAsync(run, () => AgendaRows() == 6, "group collapses again");
            await SelectAppThemeAsync(run, "Dark");
            await OpenMainAutomationsAsync(run, maximize: false);
            await WaitUiAsync(run, () => AgendaWidth() >= 1000 && AgendaRows() == 6, "Dark wide agenda");
            await CaptureAgendaAsync("automations-designer-Dark-1400.png");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method is "cron.add" or "cron.update" or "cron.run");
        }, scenario: scenario);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_DesignerValidationFocusAndDraftsStayIsolated()
    {
        await WithAppAsync(async run =>
        {
            await SelectAppThemeAsync(run, "Light");
            await OpenMainAutomationsAsync(run);
            var scheduledExpander = FindById(run, "AutomationExpander:morning-brief")!;
            var scheduledDisclosure = (ExpandCollapsePattern)scheduledExpander.GetCurrentPattern(
                ExpandCollapsePattern.Pattern);
            Assert.Equal(ControlType.Button, scheduledExpander.Current.ControlType);
            Assert.Equal(ExpandCollapseState.Collapsed, scheduledDisclosure.Current.ExpandCollapseState);
            // The Expander is the row's only button peer and owns its single native disclosure chevron.
            Assert.Empty(scheduledExpander.FindAll(TreeScope.Descendants, new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>());
            Assert.Null(FindById(run, "AutomationRow:morning-brief"));
            Assert.NotNull(FindById(run, "AutomationRowChat:morning-brief"));
            await WaitUiAsync(run,
                () => FindById(run, "AutomationExpander:morning-brief") is { Current.IsOffscreen: false },
                "scheduled row visible for expand/collapse proof");
            await WaitUiAsync(run, () => OwnedWindowCapture.WithPhysicalCoordinates(() =>
            {
                var header = scheduledExpander.Current.BoundingRectangle;
                var chat = FindById(run, "AutomationRowChat:morning-brief")!.Current.BoundingRectangle;
                return Math.Abs(chat.Top + chat.Height / 2 - header.Top - header.Height / 2)
                    <= OwnedWindowCapture.OwnedDpiScale(run, AutomationsWindow(run));
            }), "measured chat placement centered on the collapsed header");
            Capture(run, "automations-stock-expander-collapsed-Light.png", "AutomationExpander:morning-brief");
            scheduledExpander.SetFocus();
            await WaitUiAsync(run, () => FindById(run, "AutomationExpander:morning-brief") is { Current.HasKeyboardFocus: true },
                "native Expander keyboard focus");
            Capture(run, "automations-stock-expander-focused-Light.png", "AutomationExpander:morning-brief");
            var (collapsedBounds, collapsedTitleBounds, collapsedChatBounds) =
                await WaitForAutomationRowLayoutAsync(run, "morning-brief", expanded: false);
            var noChatBounds = OwnedWindowCapture.WithPhysicalCoordinates(() =>
                FindById(run, "AutomationExpander:deploy-watch")!.Current.BoundingRectangle);
            Assert.Null(FindById(run, "AutomationRowChat:deploy-watch"));
            Assert.InRange(Math.Abs(collapsedBounds.Right - noChatBounds.Right), 0, 1);
            Assert.InRange(Math.Abs(collapsedChatBounds.Top + collapsedChatBounds.Height / 2
                - collapsedBounds.Top - collapsedBounds.Height / 2), 0,
                OwnedWindowCapture.OwnedDpiScale(run, AutomationsWindow(run)));
            Assert.InRange(collapsedChatBounds.Top - collapsedBounds.Top, -1,
                collapsedBounds.Height - collapsedChatBounds.Height + 1);
            scheduledDisclosure.Expand();
            await WaitUiAsync(run,
                () => scheduledDisclosure.Current.ExpandCollapseState == ExpandCollapseState.Expanded,
                "native scheduled item expansion");
            await WaitUiAsync(run, () => FindById(run, "AutomationScheduleSummary") is not null,
                "scheduled item details");
            var (expandedBounds, expandedTitleBounds, expandedChatBounds) =
                await WaitForAutomationRowLayoutAsync(run, "morning-brief", expanded: true);
            // Expander's UIA rectangle includes expanded template/content ink. The mounted
            // responsive proof checks allocated row/header widths; this checks visible anchors.
            Assert.InRange(Math.Abs(expandedTitleBounds.Width - collapsedTitleBounds.Width), 0, 1);
            Assert.InRange(Math.Abs(expandedTitleBounds.Left - collapsedTitleBounds.Left), 0, 1);
            Assert.InRange(Math.Abs(expandedTitleBounds.Top - collapsedTitleBounds.Top), 0, 1);
            Assert.InRange(Math.Abs(expandedTitleBounds.Height - collapsedTitleBounds.Height), 0, 1);
            Assert.True(expandedBounds.Height > collapsedBounds.Height + expandedChatBounds.Height,
                "The expanded detail must be tall enough to expose centering against the whole card.");
            Assert.InRange(Math.Abs((expandedChatBounds.Top - expandedTitleBounds.Top)
                - (collapsedChatBounds.Top - collapsedTitleBounds.Top)), 0, 1);
            Assert.InRange(Math.Abs(expandedChatBounds.Left - collapsedChatBounds.Left), 0, 1);
            Assert.InRange(Math.Abs(expandedChatBounds.Top - collapsedChatBounds.Top), 0, 1);
            Assert.InRange(Math.Abs(expandedChatBounds.Width - collapsedChatBounds.Width), 0, 1);
            Assert.InRange(Math.Abs(expandedChatBounds.Height - collapsedChatBounds.Height), 0, 1);
            Assert.InRange(expandedChatBounds.Top - collapsedBounds.Top + expandedChatBounds.Height / 2,
                -1, collapsedBounds.Height + 1);
            await ScrollAutomationIntoViewAsync(run, "AutomationExpander:morning-brief");
            Capture(run, "automations-stock-expander-expanded-Light.png", "AutomationExpander:morning-brief",
                "AutomationScheduleSummary");
            scheduledDisclosure.Collapse();
            await WaitUiAsync(run,
                () => scheduledDisclosure.Current.ExpandCollapseState == ExpandCollapseState.Collapsed,
                "native scheduled item collapse");
            await WaitForAutomationRowLayoutAsync(run, "morning-brief", expanded: false);
            var rowChat = FindById(run, "AutomationRowChat:morning-brief")!;
            rowChat.SetFocus();
            var hoverBounds = OwnedWindowCapture.WithPhysicalCoordinates(() =>
                FindById(run, "AutomationExpander:morning-brief")!.Current.BoundingRectangle);
            var hoverPoint = new FixturePoint
            {
                X = (int)(hoverBounds.Left + hoverBounds.Width / 2),
                Y = (int)(hoverBounds.Top + hoverBounds.Height / 2),
            };
            Assert.True(SetCursorPos(hoverPoint.X, hoverPoint.Y));
            await WaitUiAsync(run, () => GetCursorPos(out var current) && current.X == hoverPoint.X && current.Y == hoverPoint.Y,
                "pointer positioned over collapsed Expander for hover proof");
            Assert.True(rowChat.Current.HasKeyboardFocus,
                "Hovering the Expander must not take keyboard focus.");
            Capture(run, "automations-stock-expander-hovered-Light.png", "AutomationExpander:morning-brief");

            await SelectAppThemeAsync(run, "Dark");
            await OpenMainAutomationsAsync(run);
            var darkExpander = FindById(run, "AutomationExpander:morning-brief")!;
            var darkDisclosure = (ExpandCollapsePattern)darkExpander.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            darkDisclosure.Expand();
            await WaitUiAsync(run, () => darkDisclosure.Current.ExpandCollapseState == ExpandCollapseState.Expanded
                && FindById(run, "AutomationScheduleSummary") is not null, "dark expanded scheduled row");
            await ScrollAutomationIntoViewAsync(run, "AutomationExpander:morning-brief");
            Capture(run, "automations-stock-expander-expanded-Dark.png", "AutomationExpander:morning-brief",
                "AutomationScheduleSummary");
            await SelectAppThemeAsync(run, "Light");
            await OpenMainAutomationsAsync(run);
            Invoke(FindById(run, "AutomationsNew")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is not null, "new editor");
            foreach (var save in new[] { "AutomationEditorSave", "AutomationEditorCreateAndRun" })
            {
                Invoke(FindById(run, save)!);
                await WaitUiAsync(run, () => FindText(run, "Enter a name.") is not null
                    && FindText(run, "Describe the task this agent should run.") is not null
                    && EditorValueElement(run, "AutomationEditorName") is { Current.HasKeyboardFocus: true },
                    "required errors and first invalid focus");
                Assert.NotNull(FindText(run, "Complete the required fields before saving."));
            }
            Capture(run, "automations-designer-editor-errors-Light.png", "AutomationEditorName", "AutomationEditorSave");
            SetEditorValue(run, "AutomationEditorName", "New draft only");
            SetEditorValue(run, "AutomationEditorPrompt", "Keep this new prompt");
            Invoke(FindById(run, "AutomationEditorCancel")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null, "cancel keeps new draft");

            ExpandAutomation(run, "morning-brief");
            await InvokeWhenEnabledAsync(run, () => FindCommand(run, "Edit"), "edit source job");
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is not null, "edit draft");
            SetEditorValue(run, "AutomationEditorName", "Edited draft only");
            Invoke(FindById(run, "AutomationEditorCancel")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null, "cancel edit");
            await OpenAutomationMoreAsync(run, "morning-brief");
            await InvokeWhenEnabledAsync(run, () => FindInvokable(run, "Copy as new"), "copy source job");
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is not null, "copy draft");
            Assert.NotEqual("Edited draft only", EditorValue(run, "AutomationEditorName"));
            SetEditorValue(run, "AutomationEditorName", "Copy draft only");
            Invoke(FindById(run, "AutomationEditorCancel")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null, "cancel copy");

            await InvokeWhenEnabledAsync(run, () => FindCommand(run, "Edit"), "reopen source draft");
            await WaitUiAsync(run, () => EditorValue(run, "AutomationEditorName") == "Edited draft only", "separate edit draft restored");
            Assert.NotNull(FindById(run, "AutomationEditorDiscardDraft"));
            SetEditorValue(run, "AutomationEditorName", "");
            Invoke(FindById(run, "AutomationEditorSave")!);
            await WaitUiAsync(run, () => EditorValueElement(run, "AutomationEditorName") is { Current.HasKeyboardFocus: true }
                && FindText(run, "Enter a name.") is not null, "edit uses identical required validation");
            Invoke(FindById(run, "AutomationEditorCancel")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null, "closed edited draft");

            Invoke(FindById(run, "AutomationsNew")!);
            await WaitUiAsync(run, () => EditorValue(run, "AutomationEditorName") == "New draft only", "new draft was not overwritten");
            Assert.Equal("Keep this new prompt", EditorValue(run, "AutomationEditorPrompt"));
            await ScrollEditorFieldIntoViewAsync(run, "AutomationEditorDiscardDraft");
            Invoke(FindById(run, "AutomationEditorDiscardDraft")!);
            await WaitUiAsync(run, () => EditorValue(run, "AutomationEditorName") == ""
                && FindById(run, "AutomationEditorDiscardDraft") is null, "discard resets only new draft");
            Invoke(FindById(run, "AutomationEditorCancel")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null, "closed reset new editor");
            await OpenAutomationMoreAsync(run, "morning-brief");
            await InvokeWhenEnabledAsync(run, () => FindInvokable(run, "Copy as new"), "reopen copy draft");
            await WaitUiAsync(run, () => EditorValue(run, "AutomationEditorName") == "Copy draft only", "discard did not touch copy");
            Assert.DoesNotContain(run.Gateway.Requests, r => r.Method is "cron.add" or "cron.update" or "cron.run");
            Invoke(FindById(run, "AutomationEditorCancel")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null, "closed copy");
            var inboxTop = (await WaitForAutomationRowLayoutAsync(run, "inbox-sweep", expanded: false)).Title.Top;
            ExpandAutomation(run, "inbox-sweep");
            bool HasTextInCard(string id, string value) => FindById(run, $"AutomationExpander:{id}") is { } card
                && card.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                    .Cast<AutomationElement>().Any(element => element.Current.Name.Contains(value, StringComparison.Ordinal));
            await WaitUiAsync(run, () => HasTextInCard("inbox-sweep", "Archived 4 newsletters, flagged 0 message.")
                && HasTextInCard("morning-brief", "Three things today: design review at 10"),
                "both expanded rows retain their own runs");
            Assert.True(HasTextInCard("morning-brief", "Three things today: design review at 10"));
            var inboxTitle = (await WaitForAutomationRowLayoutAsync(run, "inbox-sweep", expanded: true)).Title;
            Assert.InRange(Math.Abs(inboxTitle.Top - inboxTop), 0, 1);
            foreach (var id in new[] { "morning-brief", "inbox-sweep" })
                Assert.Equal(ExpandCollapseState.Expanded, ((ExpandCollapsePattern)FindById(run, $"AutomationExpander:{id}")!
                    .GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState);
        }, scenario: GatewayScenario.CreateAutomations());
    }

    [GatewayFixtureUiTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_DesignerCreateRevealsExpandsAndFocusesRealInventory(bool emptyAgent)
    {
        var scenario = GatewayScenario.CreateAutomations(additionalJobs: emptyAgent ? 0 : 205);
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = emptyAgent ? "agent:research:cron" : "agent:main:cron" });
            await WaitUiAsync(run, () => emptyAgent
                ? FindById(run, "AutomationsEmptyNew") is { Current.IsOffscreen: false }
                    && FindText(run, "No automations yet") is { Current.IsOffscreen: false }
                : FindById(run, "AutomationExpander:scheduled-000") is not null,
                "initial scoped inventory");
            if (emptyAgent)
            {
                await WaitUiAsync(run, () => FindById(run, "AutomationsTileFailing") is not null,
                    "empty agent has the zero-count failing tile");
                var failing = FindById(run, "AutomationsTileFailing")!;
                Assert.Equal(ControlType.Button, failing.Current.ControlType);
                Assert.Equal("Failing: 0", failing.Current.Name);
                Assert.False(failing.Current.IsEnabled);
            }
            if (!emptyAgent) Assert.Null(FindById(run, "AutomationExpander:scheduled-059"));
            Invoke(FindById(run, emptyAgent ? "AutomationsEmptyNew" : "AutomationsNew")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is not null, "create editor");
            // Exercise invalid-to-valid creation. Cleared owner-derived errors acknowledge
            // TextChanged; ValuePattern alone only observes the native control's text.
            Invoke(FindById(run, "AutomationEditorSave")!);
            await WaitUiAsync(run, () => FindText(run, "Enter a name.") is not null
                && FindText(run, "Describe the task this agent should run.") is not null,
                "empty editor validation before input");
            SetEditorValue(run, "AutomationEditorName", "Created from native designer proof");
            SetEditorValue(run, "AutomationEditorPrompt", "Synthetic prompt, do not execute anything");
            await WaitUiAsync(run, () => EditorValue(run, "AutomationEditorName") == "Created from native designer proof"
                && EditorValue(run, "AutomationEditorPrompt") == "Synthetic prompt, do not execute anything"
                && FindById(run, "AutomationEditorName") is { Current.ItemStatus: "" }
                && FindById(run, "AutomationEditorPrompt") is { Current.ItemStatus: "" },
                "root-owned draft accepted both required fields");
            Invoke(FindById(run, "AutomationEditorSave")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null
                && FindById(run, "AutomationExpander:created-1") is { Current.HasKeyboardFocus: true, Current.IsOffscreen: false },
                "saved row is revealed and focused");
            var expander = FindById(run, "AutomationExpander:created-1")!;
            Assert.Equal(ExpandCollapseState.Expanded,
                ((ExpandCollapsePattern)expander.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState);
            Assert.Null(FindById(run, "AutomationsEmptyNew"));
            Assert.Null(FindText(run, "No automations yet"));
            if (!emptyAgent) Assert.NotNull(FindById(run, "AutomationExpander:scheduled-059"));
            Assert.Single(run.Gateway.Requests, request => request.Method == "cron.add" && request.Outcome == "ok");
            Capture(run, emptyAgent ? "automations-designer-created-from-empty.png" : "automations-designer-created-later-page.png",
                "AutomationExpander:created-1");
        }, scenario: scenario);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_FailingSummaryTileRevealsAndFocusesItsInventoryRow()
    {
        await WithAppAsync(async run =>
        {
            await OpenMainAutomationsAsync(run);
            await WaitUiAsync(run, () => FindById(run, "AutomationsTileFailing") is { Current.IsEnabled: true }
                && FindById(run, "AutomationExpander:flaky-report") is not null,
                "failing tile and target automation");
            Invoke(FindById(run, "AutomationsTileFailing")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationExpander:flaky-report") is
                { Current.HasKeyboardFocus: true, Current.IsOffscreen: false },
                "failing tile reveals, scrolls to and focuses the real inventory row");
            var row = FindById(run, "AutomationExpander:flaky-report")!;
            Assert.Equal("Vendor report", FindById(run, "CronJobTitle:flaky-report")!.Current.Name);
            Assert.NotNull(FindById(run, "AutomationAttentionBadge:flaky-report"));
            Capture(run, "automations-failing-tile-reveal.png", "CronJobTitle:flaky-report", "AutomationAttentionBadge:flaky-report");
            Assert.Equal("AutomationExpander:flaky-report", row.Current.AutomationId);
        }, scenario: GatewayScenario.CreateAutomations());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_EditorDraftSurvivesReconnect()
    {
        await WithAppAsync(async run =>
        {
            await OpenAutomationEditorAsync(run);
            SetEditorValue(run, "AutomationEditorName", "Keep this unsaved draft");
            await run.Gateway.GoOfflineAsync();
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorSave") is { Current.IsEnabled: false }, "disconnected editor");
            Assert.Equal("Keep this unsaved draft", EditorValue(run, "AutomationEditorName"));
            run.Gateway.GoOnline();
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorSave") is { Current.IsEnabled: true }, "reconnected editor");
            Assert.Equal("Keep this unsaved draft", EditorValue(run, "AutomationEditorName"));
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
            SetEditorValue(run, "AutomationEditorName", "Saved editor A");
            run.Gateway.HoldResponses("cron.update");
            Invoke(FindById(run, "AutomationEditorSave")!);
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(r =>
                r.Method == "cron.update" && r.Outcome == "pending")), "held editor A save");
            // Cancel closes this draft, not the already submitted Gateway operation.
            Invoke(FindById(run, "AutomationEditorCancel")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null, "closed editor A");
            Invoke(FindById(run, "AutomationsNew")!);
            await WaitUiAsync(run, () => EditorValue(run, "AutomationEditorName") == "", "new editor B");
            SetEditorValue(run, "AutomationEditorName", "Unsaved editor B");
            run.Gateway.ReleaseResponses("cron.update");
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(r =>
                r.Method == "cron.update" && r.Outcome == "ok")), "completed editor A save");
            await WaitUiAsync(run, () => FindById(run, "AutomationEditorSave") is { Current.IsEnabled: true }, "editor B remains open");
            Assert.Equal("Unsaved editor B", EditorValue(run, "AutomationEditorName"));
            Capture(run, "automations-newer-editor.png", "AutomationEditorName");
        }, scenario: GatewayScenario.CreateAutomations());
    }

    [GatewayFixtureUiTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_HeroWashScrollsWhileTheBackplateStaysAnchored(string theme)
    {
        await WithAppAsync(async run =>
        {
            await SelectAppThemeAsync(run, theme);
            await OpenMainAutomationsAsync(run);
            await WaitUiAsync(run, () => FindById(run, "AutomationsAgendaList") is not null, "complete agenda");
            await ScrollAutomationsToTopAsync(run);
            await AssertAutomationsHeroWashAsync(run, theme);
        }, scenario: GatewayScenario.CreateAutomations(additionalJobs: 20));
    }

    [GatewayFixtureUiTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_ScopesToAgentsAndCoversMainStates(string theme)
    {
        var scenario = GatewayScenario.CreateAutomations();
        await WithAppAsync(async run =>
        {
            var accessibilityFailures = new List<string>();
            async Task ScanStateAsync(string state)
            {
                try
                {
                    await ScanAutomationsAsync(run, theme, state);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    accessibilityFailures.Add(failure.ToString());
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "automations-accessibility-failures.txt"),
                        string.Join(Environment.NewLine + Environment.NewLine, accessibilityFailures));
                }
            }
            var proofStates = new List<object>();
            var proofFailures = new List<string>();
            async Task SelectJobAsync(string id)
            {
                AutomationElement? row = null;
                await WaitUiAsync(run, () => (row = FindById(run, $"AutomationExpander:{id}")) is { Current.IsEnabled: true },
                    $"available automation {id}");
                if (FindById(run, $"CronCommands:{id}") is null)
                    ((ExpandCollapsePattern)row!.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                await WaitUiAsync(run, () => FindById(run, $"CronCommands:{id}") is not null, $"selected {id}");
            }
            async Task ProofStateAsync(string name, Func<Task> proof)
            {
                var previousImages = Directory.GetFiles(run.ArtifactsDirectory, "*.png").ToHashSet(StringComparer.OrdinalIgnoreCase);
                var accessibilityCount = accessibilityFailures.Count;
                string? failure = null;
                string? failureCapture = null;
                try
                {
                    scenario.QueueCronRunsOnly = false;
                    scenario.CronReadOnly = false;
                    scenario.SetCronJobRunning("inbox-sweep", false);
                    if (FindById(run, "AutomationEditorCancel") is { } editorCancel) Invoke(editorCancel);
                    if (FindById(run, "AutomationsConfirmDialog") is { } confirmation)
                        Invoke(confirmation.FindFirst(TreeScope.Descendants, new AndCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                            new PropertyCondition(AutomationElement.NameProperty, "Cancel"))));
                    await run.InvokeAsync("app.navigate", new { page = "agent:main:cron" });
                    await WaitUiAsync(run, () => FindById(run, "AutomationExpander:morning-brief") is not null, "proof baseline");
                    await ScrollAutomationsToTopAsync(run);
                    await proof();
                    if (accessibilityFailures.Count > accessibilityCount)
                        failure = string.Join(Environment.NewLine, accessibilityFailures.Skip(accessibilityCount));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failure = error.ToString();
                    try { OwnedWindowCapture.Save(run, AutomationsWindow(run), $"automations-failed-{name}.png"); }
                    catch (Exception captureError) { failureCapture = captureError.ToString(); }
                }
                if (failure is not null) proofFailures.Add($"{name}: {failure}");
                proofStates.Add(new
                {
                    state = name,
                    status = failure is null ? "captured" : "failed",
                    error = failure,
                    failureCaptureError = failureCapture,
                    images = Directory.GetFiles(run.ArtifactsDirectory, "*.png").Where(path => !previousImages.Contains(path)).Select(Path.GetFileName).ToArray(),
                });
                await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "automations-proof-states.json"),
                    System.Text.Json.JsonSerializer.Serialize(proofStates, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "automations-theme.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    requestedTheme = theme,
                    windowsHighContrast = new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast,
                }));
            await SelectAppThemeAsync(run, theme);
            await OpenMainAutomationsAsync(run);
            await WaitUiAsync(run, () => FindById(run, "AutomationsAgendaList") is not null, "complete agenda");
            await ProofStateAsync("list", async () =>
            {
                Assert.Null(FindById(run, $"AutomationExpander:{GatewayScenario.AutomationsOtherAgentJobId}"));
                Assert.Null(FindById(run, "AutomationsBack"));
                Assert.Null(FindById(run, "AutomationsListPane"));
                Assert.StartsWith("Open chat:", FindById(run, "AutomationRowChat:morning-brief")!.Current.Name);
                AssertAutomationsPageReadScope(run.Gateway.Requests, "main");
                await ScanStateAsync("populated");
                var failing = Assert.IsType<AutomationElement>(FindById(run, "AutomationsTileFailing"));
                Assert.Equal("Failing: 1", failing.Current.Name);
                Assert.True(failing.Current.IsEnabled);
                // The headline and summary share the initial viewport; the row-specific
                // failure badge is captured separately after its reveal action.
                Capture(run, "automations-list.png", "AutomationsHeadline", "AutomationsTileFailing");
            });
            await ProofStateAsync("new-editor", async () =>
            {
                Invoke(FindById(run, "AutomationsNew")!);
                await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is not null, "plain new editor");
                Assert.Null(FindById(run, "AutomationEditorPreserved"));
                Assert.Equal("", EditorValue(run, "AutomationEditorName"));
                Assert.Equal("", EditorValue(run, "AutomationEditorPrompt"));
                await ScanStateAsync("plain new editor");
                Capture(run, "automations-new-editor.png", "AutomationEditorTitle", "AutomationEditorName", "AutomationEditorCreateAndRun");
                Invoke(FindById(run, "AutomationEditorCancel")!);
                await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null, "closed plain new editor");
            });

            await ProofStateAsync("details", async () =>
            {
                await SelectJobAsync("morning-brief");
                await WaitUiAsync(run, () => FindById(run, "AutomationScheduleSummary") is not null
                    && FindText(run, "12/13 recent runs succeeded") is not null, "inline details and recent runs");
                Assert.NotNull(FindById(run, "AutomationExpander:weekly-digest"));
                Assert.NotNull(FindByIdPrefix(run, "AutomationRunChat:"));
                await WaitUiAsync(run, () => FindText(run, "Three things today") is not null, "visible run response");
                Assert.StartsWith("Open chat:", FindByIdPrefix(run, "AutomationRunChat:")!.Current.Name);
                await ScanStateAsync("inline details");
                Assert.NotNull(FindCommand(run, "Edit"));
                await ScrollAutomationIntoViewAsync(run, "AutomationExpander:morning-brief");
                await WaitUiAsync(run, () => FindById(run, "CronJobTitle:morning-brief") is { Current.IsOffscreen: false }
                    && FindById(run, "AutomationScheduleSummary") is { Current.IsOffscreen: false }, "visible expanded row and definition");
                Capture(run, "automations-details.png", "CronJobTitle:morning-brief", "AutomationScheduleSummary");
                var responseElement = FindByIdPrefix(run, "AutomationRunExpand:")!;
                var responseId = responseElement.Current.AutomationId;
                Invoke(responseElement);
                await WaitUiAsync(run, () => FindById(run, responseId)?.Current.Name == "Show less",
                    "full response disclosure");
                Capture(run, "automations-response.png", responseId);
            });

            await ProofStateAsync("weekly-editor", async () =>
            {
                await SelectJobAsync("morning-brief");
                await InvokeWhenEnabledAsync(run, () => FindCommand(run, "Edit"), "invoked Edit");
                await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is not null, "native editor dialog");
                Assert.NotNull(FindById(run, "AutomationEditorDialog"));
                Assert.NotNull(FindById(run, "AutomationEditorPreserved"));
                Assert.Null(FindById(run, "AutomationEditorKind:Event"));
                Assert.Null(FindById(run, "AutomationEditorCronExpr"));
                Assert.Null(FindById(run, "AutomationEditorStagger"));
                Assert.NotNull(FindById(run, "AutomationEditorCalendarTime"));
                SetEditorValue(run, "AutomationEditorName", "Morning brief (edited)");
                Assert.False(FindById(run, "AutomationEditorSave")!.Current.IsOffscreen);
                Assert.False(FindById(run, "AutomationEditorCancel")!.Current.IsOffscreen);
                await ScanStateAsync("editor");
                Capture(run, "automations-editor.png", "AutomationEditorName", "AutomationEditorTitle");
                await ScrollEditorFieldIntoViewAsync(run, "AutomationEditorScheduleKind");
                await ChooseAutomationComboAsync(run, "AutomationEditorScheduleKind", "Weekly");
                await ScrollEditorFieldIntoViewAsync(run, "AutomationEditorWeekday");
                await ChooseAutomationComboAsync(run, "AutomationEditorWeekday", "Tuesday");
                await ScrollEditorFieldIntoViewAsync(run, "AutomationEditorWeekday");
                Capture(run, "automations-editor-weekly.png", "AutomationEditorWeekday", "AutomationEditorSave");
                Invoke(FindById(run, "AutomationEditorSave")!);
                await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null, "saved editor");
                var saved = scenario.GetCronJobs().Single(j => j.GetProperty("id").GetString() == "morning-brief");
                Assert.Equal("Morning brief (edited)", saved.GetProperty("name").GetString());
                Assert.Equal("America/Los_Angeles", saved.GetProperty("schedule").GetProperty("tz").GetString());
                Assert.Equal("0 7 * * 2", saved.GetProperty("schedule").GetProperty("expr").GetString());
                Assert.Equal(300_000, saved.GetProperty("schedule").GetProperty("staggerMs").GetInt64());
                Assert.Equal("fixture/browse", saved.GetProperty("payload").GetProperty("model").GetString());
                Assert.Equal(120, saved.GetProperty("payload").GetProperty("timeoutSeconds").GetDouble());
                Assert.Equal("work", saved.GetProperty("delivery").GetProperty("accountId").GetString());
                Assert.True(saved.GetProperty("delivery").GetProperty("bestEffort").GetBoolean());
            });
            await ProofStateAsync("copy-as-new", async () =>
            {
                await SelectJobAsync("morning-brief");
                await OpenAutomationMoreAsync(run, "morning-brief");
                await WaitUiAsync(run, () => FindInvokable(run, "Copy as new") is not null, "copy as new action");
                Capture(run, "automations-more-actions.png");
                await InvokeWhenEnabledAsync(run, () => FindInvokable(run, "Copy as new"), "invoked Copy as new");
                await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is not null
                    && FindById(run, "AutomationEditorPreserved") is not null, "copy as new editor");
                var copiedName = scenario.GetCronJobs().Single(job => job.GetProperty("id").GetString() == "morning-brief").GetProperty("name").GetString();
                Assert.NotNull(FindText(run, $"Copy “{copiedName}” as new"));
                Capture(run, "automations-copy-as-new.png", "AutomationEditorTitle", "AutomationEditorName");
                Invoke(FindById(run, "AutomationEditorCancel")!);
                await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is null, "closed copy editor");
            });

            await ProofStateAsync("run-queued", async () =>
            {
                await SelectJobAsync("morning-brief");
                scenario.QueueCronRunsOnly = true;
                var runsBefore = CronRunRequests(run);
                await InvokeWhenEnabledAsync(run, () => FindCommand(run, "Run now"), "invoked Run now after save");
                await WaitUiAsync(run, () => scenario.GetQueuedCronRunIds("morning-brief").Length == 1, "accepted fixture invocation");
                var queuedId = Assert.Single(scenario.GetQueuedCronRunIds("morning-brief"));
                await WaitUiAsync(run, () => FindText(run, $"Run queued · {queuedId}") is not null, "queued result toast");
                Assert.Equal(runsBefore + 1, CronRunRequests(run));
                Capture(run, "automations-run-queued.png", "AutomationsNotice");
                await WaitUiAsync(run, () => FindById(run, "AutomationsNotice") is null, "auto-dismissed queued result toast");
                scenario.QueueCronRunsOnly = false;
            });

            await ProofStateAsync("not-started", async () =>
            {
                await SelectJobAsync("inbox-sweep");
                await WaitUiAsync(run, () => FindCommand(run, "Run now") is { Current.IsEnabled: true }
                    && run.Gateway.Requests.All(r => r.Outcome != "pending"), "idle inbox action");
                scenario.SetCronJobRunning("inbox-sweep", true);
                var runsBefore = CronRunRequests(run);
                await InvokeWhenEnabledAsync(run, () => FindCommand(run, "Run now"), "invoked Run now for running job");
                await WaitUiAsync(run, () => FindText(run, "Not started: “Inbox sweep” is already running.") is not null, "already-running result toast");
                Assert.Equal(runsBefore + 1, CronRunRequests(run));
                Capture(run, "automations-already-running.png", "AutomationsNotice");
                scenario.SetCronJobRunning("inbox-sweep", false);
            });
            await ProofStateAsync("pause", async () =>
            {
                await SelectJobAsync("inbox-sweep");
                await ScrollAutomationsToTopAsync(run);
                Invoke(FindById(run, "AutomationsRefresh")!);
                await InvokeWhenEnabledAsync(run, () => FindCommand(run, "Pause"), "invoked Pause");
                await WaitUiAsync(run, () => FindText(run, "Pause “Inbox sweep”?") is not null, "pause confirmation");
                Capture(run, "automations-confirm-pause.png");
                var confirm = FindById(run, "AutomationsConfirmDialog")!;
                var pause = confirm.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Pause")));
                Invoke(pause);
                await run.WaitForAsync(() => Task.FromResult(!scenario.GetCronJobs().Single(j =>
                    j.GetProperty("id").GetString() == "inbox-sweep").GetProperty("enabled").GetBoolean()), "paused job");
            });

            await ProofStateAsync("remove", async () =>
            {
                await SelectJobAsync("weekly-digest");
                await WaitUiAsync(run, () => FindById(run, "CronMore:weekly-digest") is not null, "weekly digest overflow");
                await OpenAutomationMoreAsync(run, "weekly-digest");
                await WaitUiAsync(run, () => FindInvokable(run, "Remove") is not null, "remove action");
                RecordTransientPeers(run, "before-remove-menu-action");
                await InvokeWhenEnabledAsync(run, () => FindInvokable(run, "Remove"), "invoked Remove");
                await WaitUiAsync(run, () => FindById(run, "AutomationsConfirmDialog") is not null
                    && FindText(run, "Remove “Weekly digest”?") is not null, "named destructive confirmation");
                RecordTransientPeers(run, "remove-dialog-open");
                Assert.Empty(VisibleMenuPeers(run));
                var removeBody = await WaitForElementByIdAsync(run, "AutomationConfirmationBody", "removal destination text");
                var removeText = (TextPattern)removeBody.GetCurrentPattern(TextPattern.Pattern);
                Assert.Equal($"It's removed from Fixture Gateway ({run.Profile.RunId[..8]}) along with its schedule and run history. This can't be undone.",
                    removeText.DocumentRange.GetText(-1).TrimEnd('\r', '\n'));
                var removeDialog = FindById(run, "AutomationsConfirmDialog")!;
                var cancel = removeDialog.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Cancel")));
                await WaitUiAsync(run, () => cancel.Current.HasKeyboardFocus, "Cancel receives destructive initial focus");
                Assert.Equal(ControlType.Window, removeDialog.Current.ControlType);
                Assert.True(((WindowPattern)removeDialog.GetCurrentPattern(WindowPattern.Pattern)).Current.IsModal);
                await ScanStateAsync("remove confirmation");
                Capture(run, "automations-confirm-remove.png", "AutomationConfirmationTitle", "AutomationConfirmationBody");
                var remove = removeDialog.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Remove")));
                Invoke(remove);
                await WaitUiAsync(run, () => FindText(run, "Removed “Weekly digest”") is not null, "removed result toast");
                Assert.DoesNotContain(scenario.GetCronJobs(), j => j.GetProperty("id").GetString() == "weekly-digest");
                Capture(run, "automations-removed.png", "AutomationsNotice");
            });

            await ProofStateAsync("important-all-error", async () =>
            {
                await SelectJobAsync("dentist");
                await WaitUiAsync(run, () => FindCommand(run, "Run now") is { Current.IsEnabled: true }
                    && run.Gateway.Requests.All(r => r.Outcome != "pending"), "idle one-time action");
                scenario.CronReadOnly = true;
                var announced = new TaskCompletionSource<NotificationEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                var notifications = new System.Collections.Concurrent.ConcurrentQueue<object>();
                AutomationEventHandler notification = (_, args) =>
                {
                    if (args is NotificationEventArgs notice)
                    {
                        notifications.Enqueue(new
                        {
                            receivedUtc = DateTimeOffset.UtcNow,
                            notice.DisplayString,
                            processing = notice.NotificationProcessing.ToString(),
                            kind = notice.NotificationKind.ToString()
                        });
                        if (notice.DisplayString.Contains("operator.admin", StringComparison.Ordinal))
                            announced.TrySetResult(notice);
                    }
                };
                var announcementRoot = AutomationsWindow(run);
                Automation.AddAutomationEventHandler(AutomationElementIdentifiers.NotificationEvent, announcementRoot, TreeScope.Subtree, notification);
                try
                {
                    await InvokeWhenEnabledAsync(run, () => FindCommand(run, "Run now"), "invoked read-only Run now");
                    await WaitUiAsync(run, () => FindById(run, "AutomationsNotice") is { } toast
                        && toast.Current.Name.Contains("operator.admin", StringComparison.Ordinal), "permission error toast");
                    Capture(run, "automations-error-toast.png", "AutomationsNotice");
                    await WaitUiAsync(run, () => announced.Task.IsCompleted, "native error notification");
                    Assert.Equal(AutomationNotificationProcessing.ImportantAll, (await announced.Task).NotificationProcessing);
                }
                finally
                {
                    Automation.RemoveAutomationEventHandler(AutomationElementIdentifiers.NotificationEvent, announcementRoot, notification);
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "automations-native-notifications.json"),
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            processId = run.AppProcessId,
                            hwnd = announcementRoot.Current.NativeWindowHandle,
                            scope = "owned hub subtree",
                            events = notifications.ToArray(),
                        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                await Task.Delay(ErrorToastPersistenceProbe);
                Assert.NotNull(FindById(run, "AutomationsNotice"));
                Assert.Null(FindById(run, "AutomationsOperationError"));
                var notice = FindById(run, "AutomationsNotice")!;
                var close = Assert.Single(notice.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                    .Cast<AutomationElement>(), button => !button.Current.IsOffscreen && button.Current.IsEnabled);
                Invoke(close);
                await WaitUiAsync(run, () => FindById(run, "AutomationsNotice") is null, "error toast dismissed explicitly");
                scenario.CronReadOnly = false;
            });

            await ProofStateAsync("scope-empty", async () =>
            {
                await ScrollAutomationsToTopAsync(run);
                await ChooseAutomationAgentAsync(run, "All agents");
                await WaitUiAsync(run, () => FindById(run, $"AutomationExpander:{GatewayScenario.AutomationsOtherAgentJobId}") is not null, "all agents");
                Assert.Contains(run.Gateway.Requests, r => r.Method == "cron.list" && !r.IsUnscopedCronCatalogRead
                    && r.AgentId is null && r.Outcome == "ok");
                await ChooseAutomationAgentAsync(run, "ops");
                await WaitUiAsync(run, () => FindById(run, $"AutomationExpander:{GatewayScenario.AutomationsOtherAgentJobId}") is not null
                    && FindById(run, "AutomationExpander:morning-brief") is null, "single-job agent");
                Assert.NotNull(FindById(run, "AutomationsAgentFilter"));
                await ChooseAutomationAgentAsync(run, "All agents");
                await WaitUiAsync(run, () => FindById(run, "AutomationExpander:morning-brief") is not null, "return from single-job agent");
                await run.InvokeAsync("app.navigate", new { page = "agent:research:cron" });
                await WaitUiAsync(run, () => FindText(run, "No automations yet") is not null, "empty agent");
                Assert.Null(FindByIdPrefix(run, "AutomationExpander:"));
                RecordHeadline(run, "empty-body-loaded");
                await WaitUiAsync(run, () => FindById(run, "AutomationsHeroAnswer") is { } headline
                    && headline.Current.Name == "Nothing scheduled yet", "overview commits empty headline");
                RecordHeadline(run, "empty-overview-committed");
                Assert.NotNull(FindById(run, "AutomationsEmptyNew"));
                Assert.Null(FindById(run, "AutomationsOperationError"));
                Capture(run, "automations-empty.png", "AutomationsEmptyChat");
                await ChooseAutomationAgentAsync(run, "All agents");
                await WaitUiAsync(run, () => FindById(run, "AutomationExpander:morning-brief") is not null
                    && FindById(run, $"AutomationExpander:{GatewayScenario.AutomationsOtherAgentJobId}") is not null,
                    "empty agent retains gateway-wide discovery");
            });
            Assert.True(proofFailures.Count == 0, string.Join(Environment.NewLine + Environment.NewLine, proofFailures));
            Assert.True(accessibilityFailures.Count == 0, string.Join(Environment.NewLine + Environment.NewLine, accessibilityFailures));
        }, scenario: scenario);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_ResponsiveShellWidthsHaveDistinctMeasuredFrames()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageAppTheme") is not null, "theme setting");
            ((ExpandCollapsePattern)FindById(run, "SettingsPageAppTheme")!.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            await WaitUiAsync(run, () => FindText(run, "Light") is not null, "Light theme");
            var light = AppWindows(run).SelectMany(window => window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "Light")).Cast<AutomationElement>())
                .First(element => element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _));
            ((SelectionItemPattern)light.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            await OpenMainAutomationsAsync(run, maximize: false);
            try
            {
                var baselinePeer = OwnedWindowCapture.WithPhysicalCoordinates(() => PageScrollPeer(run, dumpAncestors: true));
                var window = AutomationsWindow(run);
                var scale = OwnedWindowCapture.OwnedDpiScale(run, window);
                var windowBounds = OwnedWindowCapture.ReadOwnedBounds(run, window);
                var peerBounds = OwnedWindowCapture.WithPhysicalCoordinates(() => baselinePeer.Current.BoundingRectangle);
                await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "automations-native-width-baseline.json"),
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        peerClass = baselinePeer.Current.ClassName,
                        peerId = baselinePeer.Current.AutomationId,
                        peerType = baselinePeer.Current.ControlType.ProgrammaticName,
                        dpiScale = scale,
                        pageHostWidthDip = peerBounds.Width / scale,
                        peerRectPx = new { peerBounds.Left, peerBounds.Top, peerBounds.Width, peerBounds.Height },
                        windowRectPx = new { windowBounds.Left, windowBounds.Top, windowBounds.Width, windowBounds.Height },
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                OwnedWindowCapture.Save(run, window, "automations-native-width-baseline.png");
                ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
            }
            catch
            {
                try { OwnedWindowCapture.Save(run, AutomationsWindow(run), "automations-native-width-no-owner.png"); }
                catch (Exception captureFailure)
                {
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "automations-native-width-baseline-capture-error.txt"),
                        captureFailure.ToString());
                }
                throw;
            }
            var states = new List<object>();
            var failures = new List<string>();
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            var frames = new HashSet<string>(StringComparer.Ordinal);
            var markerMisses = 0;

            // A UIA re-find can transiently miss the marker after a satisfied wait. Reads keep
            // polling within the bounded wait, measure the peer they found, and record each miss;
            // a marker absent for the whole wait or without a scroll ancestor still fails.
            (global::System.Windows.Rect Host, AutomationElement Peer)? TryHostRead() =>
                OwnedWindowCapture.WithPhysicalCoordinates<(global::System.Windows.Rect Host, AutomationElement Peer)?>(() =>
                {
                    if (TryPageScrollPeer(run) is not { } peer)
                    {
                        markerMisses++;
                        return null;
                    }
                    return (peer.Current.BoundingRectangle, peer);
                });
            object DipRect(global::System.Windows.Rect rect, double scale) =>
                new { left = rect.Left / scale, top = rect.Top / scale, width = rect.Width / scale, height = rect.Height / scale };

            async Task StateAsync(string name, double requestedWindowDip, double? desiredHostDip, PageWidthTier expectedTier, bool collapsePane = false)
            {
                var paneChanged = false;
                double? expandedHostBeforeToggle = null;
                string? failure = null;
                object? measurements = null;
                string? imageHash = null;
                var targets = new List<object>();
                int? initialTargetPixels = null;
                int? finalTargetPixels = null;
                var missesAtStart = markerMisses;
                try
                {
                    var window = AutomationsWindow(run);
                    var scale = OwnedWindowCapture.OwnedDpiScale(run, window);
                    var before = OwnedWindowCapture.ReadOwnedBounds(run, window);
                    async Task<(global::System.Windows.Rect Host, AutomationElement Peer)> WaitForStableHostAsync()
                    {
                        (global::System.Windows.Rect Host, AutomationElement Peer)? previousRead = null;
                        double? previousWindowWidth = null;
                        await WaitUiAsync(run, () =>
                        {
                            var currentRead = TryHostRead();
                            var currentWidth = OwnedWindowCapture.ReadOwnedBounds(run, AutomationsWindow(run)).Width;
                            var stable = currentRead is { } current && previousRead is { } previous
                                && Math.Abs(current.Host.Width - previous.Host.Width) < 0.1
                                && Math.Abs(current.Host.Left - previous.Host.Left) < 0.1
                                && previousWindowWidth is { } previousWidth && Math.Abs(currentWidth - previousWidth) < 0.1;
                            previousRead = currentRead;
                            previousWindowWidth = currentWidth;
                            return stable;
                        }, "two stable window and measured page-host reads");
                        return previousRead!.Value;
                    }
                    var beforeHost = (await WaitForStableHostAsync()).Host.Width / scale;
                    if (collapsePane)
                    {
                        expandedHostBeforeToggle = beforeHost;
                        Invoke(FindById(run, "SettingsTogglePane") ?? throw new InvalidOperationException("Missing owned navigation toggle."));
                        paneChanged = true;
                        await WaitUiAsync(run, () => TryHostRead() is { } read && read.Host.Width / scale > beforeHost + 100,
                            "collapsed pane increases measured page host");
                    }
                    else
                    {
                        initialTargetPixels = (int)Math.Ceiling(requestedWindowDip * scale);
                        finalTargetPixels = initialTargetPixels;
                        for (var correction = 0; correction <= 2; correction++)
                        {
                            OwnedWindowCapture.ResizeOwnedPixels(run, AutomationsWindow(run), finalTargetPixels.Value, (int)Math.Ceiling(950 * scale));
                            var measuredHost = (await WaitForStableHostAsync()).Host.Width / scale;
                            var actual = OwnedWindowCapture.ReadOwnedBounds(run, AutomationsWindow(run));
                            targets.Add(new
                            {
                                correction,
                                requestedWindowPx = finalTargetPixels.Value,
                                actualWindowPx = actual.Width,
                                measuredHostDip = measuredHost,
                                measuredTier = PageResponsiveLayout.TierFor(measuredHost).ToString()
                            });
                            var needsCorrection = desiredHostDip is { } desired
                                && (Math.Abs(desired - measuredHost) > 2 || PageResponsiveLayout.TierFor(measuredHost) != expectedTier);
                            if (!needsCorrection || correction == 2) break;
                            finalTargetPixels = OwnedWindowCapture.CorrectedWindowWidthPixels(
                                finalTargetPixels.Value, desiredHostDip!.Value, measuredHost, scale);
                        }
                    }
                    await ScrollAutomationsToTopAsync(run);
                    var (actualHost, pagePeer) = await WaitForStableHostAsync();
                    var actualWindow = OwnedWindowCapture.ReadOwnedBounds(run, AutomationsWindow(run));
                    var hostDip = actualHost.Width / scale;
                    if (pagePeer.Current.IsKeyboardFocusable) pagePeer.SetFocus();
                    await WaitUiAsync(run, () => OwnedWindowCapture.WithPhysicalCoordinates(() =>
                    {
                        var headline = FindById(run, "AutomationsHeadline")!.Current.BoundingRectangle;
                        var owned = OwnedWindowCapture.ReadOwnedBounds(run, AutomationsWindow(run));
                        return headline.Left >= owned.Left && headline.Top >= owned.Top && headline.Right <= owned.Right && headline.Bottom <= owned.Bottom;
                    }), "stable headline anchor wholly inside the owned window");
                    var path = Path.Combine(run.ArtifactsDirectory, $"automations-native-width-{name}-Light.png");
                    OwnedWindowCapture.Save(run, AutomationsWindow(run), Path.GetFileName(path), FindById(run, "AutomationsHeadline")!);
                    imageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, $"automations-native-width-{name}-loaded.json"),
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            initialTargetPixels,
                            finalTargetPixels,
                            desiredHostDip,
                            targets,
                            pageHostWidthDip = hostDip,
                            contentWidthDip = PageResponsiveLayout.ContentWidth(hostDip),
                            tier = PageResponsiveLayout.TierFor(hostDip).ToString(),
                            windowRectPx = new { actualWindow.Left, actualWindow.Top, actualWindow.Width, actualWindow.Height },
                            windowRectDip = DipRect(actualWindow, scale),
                            dpiScale = scale,
                            markerMissReads = markerMisses - missesAtStart,
                            sha256 = imageHash,
                        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    if (finalTargetPixels is { } targetPixels)
                    {
                        Assert.InRange(Math.Abs(actualWindow.Width - targetPixels), 0, 1);
                        Assert.True(Math.Abs(actualWindow.Width - before.Width) > 1, "The window did not change size.");
                        Assert.Equal(Math.Sign(targetPixels - before.Width), Math.Sign(actualWindow.Width - before.Width));
                    }
                    Assert.Equal(expectedTier, PageResponsiveLayout.TierFor(hostDip));
                    if (desiredHostDip is { } desiredWidth) Assert.InRange(Math.Abs(hostDip - desiredWidth), 0, 2);
                    Assert.True(hashes.Add(imageHash), "Native width frame is byte-identical to an earlier width.");
                    Assert.True(frames.Add($"{actualWindow.Width:R},{actualWindow.Height:R},{actualHost.Width:R}"),
                        "Native width frame repeated earlier window/host bounds.");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failure = error.ToString();
                    failures.Add($"{name}: {failure}");
                    try { OwnedWindowCapture.Save(run, AutomationsWindow(run), $"automations-native-width-{name}-failed.png"); }
                    catch (Exception captureFailure) { failure += Environment.NewLine + captureFailure; }
                }
                finally
                {
                    try
                    {
                        var ownedWindow = AutomationsWindow(run);
                        var scale = OwnedWindowCapture.OwnedDpiScale(run, ownedWindow);
                        var actualWindow = OwnedWindowCapture.ReadOwnedBounds(run, ownedWindow);
                        var hostPeer = await WaitForPageScrollPeerAsync(run);
                        var actualHost = OwnedWindowCapture.WithPhysicalCoordinates(() => hostPeer.Current.BoundingRectangle);
                        var widthDip = actualHost.Width / scale;
                        measurements = new
                        {
                            requestedWindowWidthDip = requestedWindowDip,
                            desiredPageHostWidthDip = desiredHostDip,
                            initialTargetPixels,
                            finalTargetPixels,
                            targets,
                            pageHostWidthDip = widthDip,
                            contentWidthDip = PageResponsiveLayout.ContentWidth(widthDip),
                            tier = PageResponsiveLayout.TierFor(widthDip).ToString(),
                            windowRectPx = new { actualWindow.Left, actualWindow.Top, actualWindow.Width, actualWindow.Height },
                            windowRectDip = DipRect(actualWindow, scale),
                            pageHostRectDip = DipRect(actualHost, scale),
                            dpiScale = scale,
                            hostMeasurementSource = "Owned PART_ScrollPresenter beneath exposed CronPageMarker",
                            hostPeerClass = hostPeer.Current.ClassName,
                            hostPeerAutomationId = hostPeer.Current.AutomationId,
                            markerMissReads = markerMisses - missesAtStart,
                            paneTransition = collapsePane ? "collapsed then restored" : "expanded baseline",
                        };
                    }
                    catch (Exception measurementFailure)
                    {
                        failure = (failure ?? "") + Environment.NewLine + measurementFailure;
                        failures.Add($"{name}: unable to measure owned window.");
                    }
                    if (paneChanged)
                    {
                        try
                        {
                            Invoke(FindById(run, "SettingsTogglePane")!);
                            await WaitUiAsync(run, () => TryHostRead() is { } read
                                && Math.Abs(read.Host.Width / OwnedWindowCapture.OwnedDpiScale(run, AutomationsWindow(run))
                                - expandedHostBeforeToggle!.Value) <= 2, "restored navigation pane and original host width");
                        }
                        catch (Exception restoreFailure)
                        {
                            failure = (failure ?? "") + Environment.NewLine + restoreFailure;
                            failures.Add($"{name}: navigation restoration failed.");
                        }
                    }
                    var state = new { state = name, status = failure is null ? "captured" : "failed", error = failure, measurements, sha256 = imageHash };
                    states.Add(state);
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, $"automations-native-width-{name}-Light.metrics.json"),
                        System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "automations-native-width-manifest.json"),
                        System.Text.Json.JsonSerializer.Serialize(states, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
            }
            await StateAsync("wide-host-1386", 1661, 1386, PageWidthTier.Wide);
            await StateAsync("wide-host-1008", 1261, 1008, PageWidthTier.Wide);
            await StateAsync("medium-host-754", 1021, 754, PageWidthTier.Medium);
            await StateAsync("shell-minimum-expanded", 1000, null, PageWidthTier.Medium);
            await StateAsync("shell-minimum-collapsed", 1000, null, PageWidthTier.Medium, collapsePane: true);
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }, scenario: GatewayScenario.CreateAutomations());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_ReadOnlyOperatorBrowsesWithoutManagementActions()
    {
        var scenario = GatewayScenario.CreateAutomations();
        scenario.CronReadOnly = true;
        await WithAppAsync(async run =>
        {
            await OpenMainAutomationsAsync(run);
            await WaitUiAsync(run, () => FindById(run, "AutomationsReadOnlyBanner") is not null, "read-only explanation");
            Assert.Null(FindById(run, "AutomationsNew"));
            Assert.NotNull(FindById(run, "AutomationExpander:weekly-digest"));
            FindById(run, "AutomationExpander:morning-brief")!.SetFocus();
            ExpandAutomation(run, "morning-brief");
            await WaitUiAsync(run, () => FindByIdPrefix(run, "AutomationRunChat:") is not null, "readable inline runs");
            foreach (var label in new[] { "Run now", "Pause", "Resume", "Edit", "Edit in web" })
                Assert.Null(FindCommand(run, label));
            Assert.NotNull(FindCommand(run, "Copy automation ID"));
            await ScanAutomationsAsync(run, "Default", "read-only");
            await ScrollAutomationIntoViewAsync(run, "AutomationExpander:morning-brief");
            await WaitUiAsync(run, () => FindById(run, "CronJobTitle:morning-brief") is { Current.IsOffscreen: false }
                && FindById(run, "AutomationScheduleSummary") is { Current.IsOffscreen: false }, "visible read-only definition");
            Capture(run, "automations-read-only.png", "CronJobTitle:morning-brief", "AutomationScheduleSummary");
            Assert.DoesNotContain(run.Gateway.Requests, r => r.Method is "cron.add" or "cron.update" or "cron.remove" or "cron.run");
        }, scenario: scenario);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_ForbiddenReadShowsAccessErrorNotEmptyInventory()
    {
        var scenario = GatewayScenario.CreateAutomations();
        scenario.CronReadDenied = true;
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "agent:main:cron" });
            await WaitUiAsync(run, () => FindText(run, "Automations are not visible to this operator") is not null, "access panel");
            Assert.Null(FindText(run, "No automations yet"));
            Assert.Null(FindByIdPrefix(run, "AutomationExpander:"));
            Assert.Null(FindText(run, "All automations"));
            Capture(run, "automations-read-denied.png");
        }, scenario: scenario);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_LoadingErrorsRefreshAndOfflineKeepHonestStates()
    {
        var scenario = GatewayScenario.CreateAutomations(sparse: true);
        scenario.CronSchedulerEnabled = false;
        scenario.CronReadError = "Synthetic initial read failure.";
        await WithAppAsync(async run =>
        {
            run.Gateway.HoldResponses("cron.list");
            await run.InvokeAsync("app.navigate", new { page = "agent:main:cron" });
            await WaitUiAsync(run, () => FindById(run, "AutomationsListLoading") is not null, "loading skeleton");
            Capture(run, "automations-loading.png", "AutomationsListLoading");
            run.Gateway.ReleaseResponses("cron.list");
            await WaitUiAsync(run, () => FindById(run, "AutomationsRetry") is not null, "initial error retry");
            Capture(run, "automations-initial-error.png", "AutomationsRetry");
            scenario.CronReadError = null;
            Invoke(FindById(run, "AutomationsRetry")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationExpander:morning-brief") is not null
                && FindById(run, "AutomationAgenda:morning-brief") is not null, "sparse inventory");
            Assert.NotNull(FindById(run, "AutomationsSchedulerOffBanner"));
            Assert.Null(FindById(run, "AutomationsStatusFilter"));
            Capture(run, "automations-sparse-scheduler-off.png", "AutomationsHeadline", "AutomationsSchedulerOffBanner");
            scenario.CronReadError = "Synthetic refresh failure.";
            Invoke(FindById(run, "AutomationsRefresh")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationsRefreshWarning") is not null, "refresh warning");
            Assert.NotNull(FindById(run, "AutomationExpander:morning-brief"));
            Capture(run, "automations-refresh-error.png", "AutomationsRefreshWarning");
            ExpandAutomation(run, "morning-brief");
            await WaitUiAsync(run, () => FindCommand(run, "Run now") is { Current.IsEnabled: true }
                && FindCommand(run, "Pause") is { Current.IsEnabled: true }
                && FindCommand(run, "Edit") is { Current.IsEnabled: true }, "authorized controls before disconnect");
            await run.Gateway.GoOfflineAsync();
            await WaitUiAsync(run, () => FindById(run, "AutomationsHeroAnswer")?.Current.Name.Contains("as last seen", StringComparison.Ordinal) == true, "offline last seen");
            Assert.NotNull(FindById(run, "AutomationExpander:morning-brief"));
            foreach (var action in new[] { "Run now", "Pause", "Edit" })
                Assert.False(FindCommand(run, action)!.Current.IsEnabled);
            Assert.False(FindById(run, "AutomationAgenda:morning-brief")!.Current.IsEnabled);
            Assert.Contains(AutomationsWindow(run).FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>(),
                element => element.Current.Name.StartsWith("Schedule as of ", StringComparison.Ordinal));
            Capture(run, "automations-offline.png", "AutomationsHeadline", "AutomationsOpenConnection");
            run.Gateway.GoOnline();
        }, scenario: scenario);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task Automations_LargeAgendaPagesBeyondBrowseAndJumpsToInlineRow()
    {
        var scenario = GatewayScenario.CreateAutomations(additionalJobs: 205);
        // Singleton day groups deliberately remain visible beyond the collapsed budget.
        // Keep this pagination proof on one reported date, independent of the current hour.
        var reportingZone = TimeZoneInfo.Local;
        var reportedDate = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, reportingZone).Date.AddDays(1).AddHours(12);
        var tomorrow = new DateTimeOffset(reportedDate, reportingZone.GetUtcOffset(reportedDate));
        for (var index = 0; index < 205; index++)
            scenario.SetCronJobNextRun($"scheduled-{index:D3}", tomorrow.AddMinutes(index).ToUnixTimeMilliseconds());
        scenario.SetCronJobNextRun("morning-brief", tomorrow.AddMinutes(205).ToUnixTimeMilliseconds());
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "agent:main:cron" });
            await WaitUiAsync(run, () => FindById(run, "AutomationExpander:scheduled-000") is not null, "large inventory");
            ((WindowPattern)AutomationsWindow(run).GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized);
            await WaitUiAsync(run, () => FindById(run, "AutomationsAgendaList") is not null, "paged agenda");
            var initialAgendaRows = FindById(run, "AutomationsAgendaList")!.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
                .Where(e => e.Current.AutomationId.StartsWith("AutomationAgenda:", StringComparison.Ordinal)).ToArray();
            Assert.InRange(initialAgendaRows.Length, 1, 6);
            Assert.Null(FindById(run, "AutomationExpander:scheduled-204"));
            var page = await WaitForPageScrollPeerAsync(run);
            var pageFrame = await ResetAutomationScrollAsync(run, page,
                (ScrollPattern)page.GetCurrentPattern(ScrollPattern.Pattern));
            Capture(run, "automations-large-agenda.png", "AutomationsHeadline");
            var list = FindById(run, "AutomationsAgendaList")!;
            var before = list.FindAll(TreeScope.Children, Condition.TrueCondition).Count;
            // Startup can cross midnight: use the rendered group, not a second clock sample.
            AutomationElement? more = null;
            await WaitUiAsync(run, () =>
            {
                var candidates = new[] { "Today", "Tomorrow" }
                    .Select(key => FindById(run, $"AutomationAgendaMore:{key}"))
                    .OfType<AutomationElement>().Where(element => element.Current.IsEnabled).ToArray();
                if (candidates.Length != 1) return false;
                more = candidates[0];
                return true;
            }, "one expandable group for the reported pagination date");
            Invoke(more!);
            await WaitUiAsync(run, () => FindById(run, "AutomationsAgendaList")!.FindAll(TreeScope.Children, Condition.TrueCondition).Count > before,
                "expanded agenda contains additional native items");
            list = FindById(run, "AutomationsAgendaList")!;
            var item = list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().First(e => e.Current.Name.Contains("Scheduled fixture 204", StringComparison.Ordinal));
            if (item.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualItem))
                ((VirtualizedItemPattern)virtualItem).Realize();
            ((ScrollItemPattern)item.GetCurrentPattern(ScrollItemPattern.Pattern)).ScrollIntoView();
            await WaitUiAsync(run, () => FindById(run, "AutomationAgenda:scheduled-204") is not null, "last paged agenda job");
            Invoke(FindById(run, "AutomationAgenda:scheduled-204")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationExpander:scheduled-204") is
                { Current.HasKeyboardFocus: true, Current.IsOffscreen: false }
                && FindById(run, "CronJobTitle:scheduled-204")?.Current.Name == "Scheduled fixture 204"
                && FindById(run, "AutomationScheduleSummary") is not null, "agenda jump reveals and focuses inventory");
            Assert.Equal(ExpandCollapseState.Expanded, ((ExpandCollapsePattern)FindById(run, "AutomationExpander:scheduled-204")!
                .GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState);
            Capture(run, "automations-agenda-jump.png", "CronJobTitle:scheduled-204");
            // Inspect the revealed row without rewinding through the entire paged inventory.
            await ScrollAutomationIntoViewAsync(run, "AutomationExpander:scheduled-204", pageFrame);
            Capture(run, "automations-agenda-jump-details.png", "CronJobTitle:scheduled-204", "AutomationScheduleSummary");
        }, scenario: scenario);
    }

    private static async Task SelectAppThemeAsync(GatewayFixtureRun run, string theme)
    {
        await run.InvokeAsync("app.navigate", new { page = "settings" });
        await WaitUiAsync(run, () => FindById(run, "SettingsPageAppTheme") is not null, "theme setting");
        ((ExpandCollapsePattern)FindById(run, "SettingsPageAppTheme")!.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        await WaitUiAsync(run, () => FindText(run, theme) is not null, "theme choice");
        var option = AppWindows(run).SelectMany(window => window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, theme)).Cast<AutomationElement>())
            .First(element => element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _));
        ((SelectionItemPattern)option.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
    }

    private static async Task OpenMainAutomationsAsync(GatewayFixtureRun run, bool maximize = true)
    {
        await run.InvokeAsync("app.navigate", new { page = "agent:main:cron" });
        await WaitUiAsync(run, () => FindById(run, "AutomationExpander:morning-brief") is not null, "main automations");
        if (maximize)
        {
            ((WindowPattern)AutomationsWindow(run).GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized);
            await WaitUiAsync(run, () => FindById(run, "AutomationExpander:morning-brief") is not null, "maximized automations");
        }
    }

    private static async Task OpenAutomationEditorAsync(GatewayFixtureRun run)
    {
        await OpenMainAutomationsAsync(run);
        FindById(run, "AutomationExpander:morning-brief")!.SetFocus();
        ExpandAutomation(run, "morning-brief");
        await InvokeWhenEnabledAsync(run, () => FindCommand(run, "Edit"), "invoked Edit command");
        await WaitUiAsync(run, () => FindById(run, "AutomationEditorName") is not null, "editor dialog");
    }

    private static void ExpandAutomation(GatewayFixtureRun run, string id)
    {
        var expander = FindById(run, $"AutomationExpander:{id}")
            ?? throw new InvalidOperationException($"Missing automation {id}.");
        ((ExpandCollapsePattern)expander.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
    }

    private static async Task InvokeWhenEnabledAsync(GatewayFixtureRun run, Func<AutomationElement?> find, string description)
    {
        AutomationElement? ready = null;
        await WaitUiAsync(run, () =>
        {
            if (find() is not { Current.IsEnabled: true } element) return false;
            ready = element;
            return true;
        }, description);
        // Never retry a mutation: a UIA error does not prove that invocation had no effect.
        Invoke(ready!);
    }

    private static int CronRunRequests(GatewayFixtureRun run) => run.Gateway.Requests.Count(request => request.Method == "cron.run");

    private static AutomationElement? FindCommand(GatewayFixtureRun run, string label)
    {
        // Multiple details stay open. Actions must belong to the most recently selected row.
        var selected = FindInApp(run, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ItemStatusProperty, "Selected")));
        for (var parent = selected; parent is not null; parent = TreeWalker.ControlViewWalker.GetParent(parent))
            if (parent.Current.AutomationId.StartsWith("AutomationCard:", StringComparison.Ordinal))
                return parent.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, label)));
        return FindButton(run, label);
    }
    private static AutomationElement? FindInvokable(GatewayFixtureRun run, string label) => FindInApp(run, new AndCondition(
        new PropertyCondition(AutomationElement.NameProperty, label),
        new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true)));

    private static async Task OpenAutomationMoreAsync(GatewayFixtureRun run, string id)
    {
        await WaitUiAsync(run, () => FindById(run, $"CronMore:{id}") is not null, "automation overflow");
        Invoke(FindById(run, $"CronMore:{id}")!.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
    }

    private const double AutomationScrollInsetPixels = 16;
    private const double AutomationScrollTolerancePixels = 1;
    private const int AutomationScrollCorrectionLimit = 3;

    private readonly record struct AutomationScrollFrame(
        global::System.Windows.Rect Window, global::System.Windows.Rect Aperture);

    private static async Task<AutomationScrollFrame> ResetAutomationScrollAsync(
        GatewayFixtureRun run, AutomationElement page, ScrollPattern scroll)
    {
        AutomationScrollFrame? previous = null;
        var observed = default(AutomationScrollFrame);
        if (scroll.Current.VerticallyScrollable)
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        await WaitUiAsync(run, () =>
        {
            if (scroll.Current.VerticallyScrollable && scroll.Current.VerticalScrollPercent != 0) return false;
            observed = OwnedWindowCapture.WithPhysicalCoordinates(() =>
            {
                var window = AutomationsWindow(run);
                var aperture = global::System.Windows.Rect.Intersect(page.Current.BoundingRectangle,
                    OwnedWindowCapture.ReadOwnedClientBounds(run, window));
                Assert.True(!aperture.IsEmpty && aperture.Width > 0 && aperture.Height > 0);
                return new AutomationScrollFrame(OwnedWindowCapture.ReadOwnedBounds(run, window), aperture);
            });
            var settled = previous == observed;
            previous = observed;
            return settled;
        }, "settled scroll-zero sampling aperture");
        return observed;
    }

    private static async Task<(global::System.Windows.Rect Bounds, global::System.Windows.Rect Title, global::System.Windows.Rect Chat)>
        WaitForAutomationRowLayoutAsync(GatewayFixtureRun run, string id, bool expanded)
    {
        var observed = (Bounds: global::System.Windows.Rect.Empty, Title: global::System.Windows.Rect.Empty,
            Chat: global::System.Windows.Rect.Empty);
        var previous = observed;
        string? previousIdentity = null;
        await WaitUiAsync(run, () =>
        {
            if (FindById(run, $"AutomationExpander:{id}") is not { } row) return false;
            if (FindById(run, $"CronJobTitle:{id}") is not { } title) return false;
            var state = ((ExpandCollapsePattern)row.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState;
            if (state != (expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed)) return false;
            observed = OwnedWindowCapture.WithPhysicalCoordinates(() => (row.Current.BoundingRectangle, title.Current.BoundingRectangle,
                FindById(run, $"AutomationRowChat:{id}")?.Current.BoundingRectangle ?? global::System.Windows.Rect.Empty));
            var identity = string.Join(",", row.GetRuntimeId());
            var settled = !observed.Bounds.IsEmpty && observed.Bounds.Width > 0 && observed.Bounds.Height > 0
                && !observed.Title.IsEmpty && observed.Title.Width > 0 && observed.Title.Height > 0
                && observed == previous && identity == previousIdentity;
            File.AppendAllText(Path.Combine(run.ArtifactsDirectory, "automations-row-layout.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    id, expanded, identity, settled, timestampUtc = DateTimeOffset.UtcNow,
                    bounds = observed.Bounds.ToString(), title = observed.Title.ToString(), chat = observed.Chat.ToString(),
                }) + Environment.NewLine);
            previous = observed;
            previousIdentity = identity;
            return settled;
        }, $"settled {(expanded ? "expanded" : "collapsed")} row {id}");
        return observed;
    }

    private static (double Percent, double ViewSize, global::System.Windows.Rect Viewport,
        global::System.Windows.Rect Target, bool Offscreen) ReadAutomationScroll(
        GatewayFixtureRun run, ScrollPattern scroll, AutomationScrollFrame frame, string? id) =>
        OwnedWindowCapture.WithPhysicalCoordinates(() =>
        {
            Assert.Equal(frame.Window, OwnedWindowCapture.ReadOwnedBounds(run, AutomationsWindow(run)));
            var info = scroll.Current;
            var target = id is null ? null : FindById(run, id);
            // This fixed aperture bounds sampling/reveal within an unchanged HWND, not
            // an observed post-scroll allocation. Mounted proof measures the real viewport.
            return (info.VerticallyScrollable ? info.VerticalScrollPercent : 0, info.VerticalViewSize,
                frame.Aperture, target?.Current.BoundingRectangle ?? global::System.Windows.Rect.Empty,
                id is not null && (target is null || target.Current.IsOffscreen));
        });

    private static async Task MoveAutomationScrollAsync(GatewayFixtureRun run, ScrollPattern scroll,
        AutomationScrollFrame frame, double requested, string? id = null)
    {
        var observed = ReadAutomationScroll(run, scroll, frame, id);
        var previous = observed;
        previous.Percent = double.NaN;
        if (scroll.Current.VerticallyScrollable)
            scroll.SetScrollPercent(ScrollPattern.NoScroll, requested);
        try
        {
            await WaitUiAsync(run, () =>
            {
                observed = ReadAutomationScroll(run, scroll, frame, id);
                var range = observed.ViewSize > 0
                    ? observed.Viewport.Height * (100 / observed.ViewSize - 1) : 0;
                var tolerance = range > 0 ? AutomationScrollTolerancePixels * 100 / range : 0;
                var arrived = Math.Abs(observed.Percent - requested) <= tolerance;
                var settled = previous.Equals(observed);
                previous = observed;
                return arrived && settled;
            }, $"settled page scroll at {requested:0.###}% for {id ?? "page"}");
        }
        finally
        {
            File.AppendAllText(Path.Combine(run.ArtifactsDirectory, "automations-scroll.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    id, requested, actual = observed.Percent, observed.ViewSize, observed.Offscreen,
                    samplingAperture = observed.Viewport.ToString(), target = observed.Target.ToString(),
                }) + Environment.NewLine);
        }
    }

    private static async Task ScrollAutomationIntoViewAsync(
        GatewayFixtureRun run, string id, AutomationScrollFrame? samplingFrame = null)
    {
        var page = await WaitForPageScrollPeerAsync(run);
        await WaitForElementByIdAsync(run, id, $"automation target {id}");
        var scroll = (ScrollPattern)page.GetCurrentPattern(ScrollPattern.Pattern);
        var frame = samplingFrame ?? await ResetAutomationScrollAsync(run, page, scroll);
        var observed = ReadAutomationScroll(run, scroll, frame, id);
        while (observed.Target.Width <= 0 || observed.Target.Height <= 0 || observed.Target.IsEmpty)
        {
            Assert.True(observed.Percent < 100 && observed.ViewSize is > 0 and < 100,
                $"No geometry for {id} after reaching the end of the page.");
            // Half a viewport keeps adjacent observations overlapping. Await the requested
            // destination, not the first geometry/progress reported during the native update.
            var next = Math.Min(100, observed.Percent + 50 * observed.ViewSize / (100 - observed.ViewSize));
            Assert.True(next > observed.Percent, $"Page scroll did not advance toward {id}.");
            await MoveAutomationScrollAsync(run, scroll, frame, next, id);
            observed = ReadAutomationScroll(run, scroll, frame, id);
        }

        var atBoundary = false;
        for (var correction = 0; correction < AutomationScrollCorrectionLimit; correction++)
        {
            var delta = observed.Target.Top - observed.Viewport.Top - AutomationScrollInsetPixels;
            if (Math.Abs(delta) <= AutomationScrollTolerancePixels) break;
            var range = observed.Viewport.Height * (100 / observed.ViewSize - 1);
            if (range <= 0) { atBoundary = true; break; }
            var next = Math.Clamp(observed.Percent + delta * 100 / range, 0, 100);
            atBoundary = next == 0 && delta < 0 || next == 100 && delta > 0;
            await MoveAutomationScrollAsync(run, scroll, frame, next, id);
            observed = ReadAutomationScroll(run, scroll, frame, id);
            if (atBoundary) break;
        }
        Assert.True(!observed.Offscreen && !observed.Target.IsEmpty && observed.Target.Width > 0
            && observed.Target.Height > 0 && observed.Target.Top >= observed.Viewport.Top
            && observed.Target.Bottom <= observed.Viewport.Bottom,
            $"Automation target {id} is clipped after settled scrolling: {observed.Target}; viewport {observed.Viewport}.");
        Assert.True(atBoundary || Math.Abs(observed.Target.Top - observed.Viewport.Top - AutomationScrollInsetPixels)
            <= AutomationScrollTolerancePixels, $"Automation target {id} did not settle at the requested inset.");
    }

    private static async Task ScrollAutomationsToTopAsync(GatewayFixtureRun run)
    {
        var page = await WaitForPageScrollPeerAsync(run);
        await ResetAutomationScrollAsync(run, page, (ScrollPattern)page.GetCurrentPattern(ScrollPattern.Pattern));
    }

    /// <summary>
    /// Samples the decorative wash inside the hero's padding, not the plain page margin.
    /// The hero scrolls away while the surrounding native backplate remains unchanged.
    /// </summary>
    private static async Task AssertAutomationsHeroWashAsync(GatewayFixtureRun run, string theme)
    {
        var page = await WaitForPageScrollPeerAsync(run);
        var scroll = (ScrollPattern)page.GetCurrentPattern(ScrollPattern.Pattern);
        var scrollFrame = await ResetAutomationScrollAsync(run, page, scroll);
        SaveAutomationsWindow(run, "automations-hero-top.png", "AutomationsHeadline", "AutomationsMast");
        Assert.True(scroll.Current.VerticallyScrollable, "The populated page must scroll to prove the hero placement.");
        var window = AutomationsWindow(run);
        var (frame, viewport, headline, hero) = OwnedWindowCapture.WithPhysicalCoordinates(() => (
            scrollFrame.Window, scrollFrame.Aperture,
            FindById(run, "AutomationsHeadline")!.Current.BoundingRectangle,
            FindById(run, "AutomationsMast")!.Current.BoundingRectangle));
        var scale = OwnedWindowCapture.OwnedDpiScale(run, window);
        var highContrast = new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        var range = viewport.Height * (100 / scroll.Current.VerticalViewSize - 1);
        Assert.True(range > hero.Bottom - viewport.Top,
            "The fixture must provide enough scroll range to move the complete hero out of view.");
        var shift = Math.Min(hero.Top - viewport.Top - scale, hero.Height / 4);
        Assert.True(range > 0 && shift >= 8 * scale, "A visible partial-scroll interval is required for wash tracking.");
        await MoveAutomationScrollAsync(run, scroll, scrollFrame, shift * 100 / range);
        var translatedHero = OwnedWindowCapture.WithPhysicalCoordinates(() =>
            FindById(run, "AutomationsMast")!.Current.BoundingRectangle);
        Assert.True(translatedHero.Top < hero.Top - scale);
        Assert.Equal(hero.Size, translatedHero.Size);
        Assert.True(translatedHero.Top >= viewport.Top && translatedHero.Bottom <= viewport.Bottom);
        SaveAutomationsWindow(run, "automations-hero-partial.png", "AutomationsMast");
        Assert.Equal(frame, OwnedWindowCapture.ReadOwnedBounds(run, window));
        using var atTop = new System.Drawing.Bitmap(Path.Combine(run.ArtifactsDirectory, "automations-hero-top.png"));
        using var partial = new System.Drawing.Bitmap(Path.Combine(run.ArtifactsDirectory, "automations-hero-partial.png"));
        (int TranslatedDelta, int StationaryDelta)? motion = null;
        if (!highContrast)
            motion = AssertHeroWashTranslation(atTop.GetPixel, partial.GetPixel,
                new global::System.Windows.Rect(hero.Left - frame.Left, hero.Top - frame.Top, hero.Width, hero.Height),
                new global::System.Windows.Rect(translatedHero.Left - frame.Left, translatedHero.Top - frame.Top,
                    translatedHero.Width, translatedHero.Height), (int)Math.Ceiling(12 * scale));
        await MoveAutomationScrollAsync(run, scroll, scrollFrame, 100);
        await WaitUiAsync(run, () => scroll.Current.VerticalScrollPercent >= 99.5 && OwnedWindowCapture.WithPhysicalCoordinates(() =>
            FindById(run, "AutomationsHeadline") is not { } moved || moved.Current.IsOffscreen ||
            moved.Current.BoundingRectangle is var area && (area.IsEmpty || area.Bottom <= viewport.Top + 1)),
            "header scrolled out with the content");
        Assert.Equal(frame, OwnedWindowCapture.ReadOwnedBounds(run, window));
        SaveAutomationsWindow(run, "automations-hero-scrolled.png");
        var scrolled = Path.Combine(run.ArtifactsDirectory, "automations-hero-scrolled.png");
        using var afterScroll = new System.Drawing.Bitmap(scrolled);
        static int Delta(System.Drawing.Color a, System.Drawing.Color b) =>
            Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
        var x = (int)((viewport.Left + hero.Left) / 2 - frame.Left);
        var bandTop = (int)Math.Ceiling(viewport.Top - frame.Top);
        var bandBottom = (int)(headline.Bottom - frame.Top);
        var drift = Enumerable.Range(bandTop, bandBottom - bandTop).Max(y => Delta(atTop.GetPixel(x, y), afterScroll.GetPixel(x, y)));
        var inset = 12 * scale;
        Assert.True(hero.Width > 2 * inset && hero.Height > 2 * inset);
        var wash = Delta(
            atTop.GetPixel((int)(hero.Left - frame.Left + inset), (int)(hero.Top - frame.Top + inset)),
            atTop.GetPixel((int)(hero.Right - frame.Left - inset), (int)(hero.Bottom - frame.Top - inset)));
        await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "automations-hero-proof.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                theme, highContrast, scroll.Current.VerticalScrollPercent, frame, samplingAperture = viewport, headline, hero, translatedHero,
                motion = motion is { } measured ? new { measured.TranslatedDelta, measured.StationaryDelta } : null,
                sampleX = x, bandTop, bandBottom, maxBackplateDelta = drift, heroWashDelta = wash,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Assert.True(hero.Left - viewport.Left >= 16 * scale, $"No empty page margin to sample: {viewport} vs {hero}.");
        if (!highContrast) Assert.True(wash >= 4, $"The {theme} hero wash is not visible (delta {wash}).");
        Assert.True(drift <= 3, $"The {theme} native backplate changed while scrolling (max delta {drift}).");
    }

    private static (int TranslatedDelta, int StationaryDelta) AssertHeroWashTranslation(
        Func<int, int, System.Drawing.Color> before, Func<int, int, System.Drawing.Color> after,
        global::System.Windows.Rect hero, global::System.Windows.Rect moved, int inset)
    {
        static int Delta(System.Drawing.Color a, System.Drawing.Color b) =>
            Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
        var dx = (int)Math.Round(moved.Left - hero.Left);
        var dy = (int)Math.Round(moved.Top - hero.Top);
        var x = (int)Math.Round(hero.Left) + inset;
        var first = (int)Math.Ceiling(hero.Top) + inset;
        // Include the original lower padding revealed beneath the translated hero.
        // Restricting both samples to their overlap cannot distinguish a shallow
        // diagonal gradient after a small vertical scroll.
        var last = (int)Math.Floor(hero.Bottom) - inset;
        Assert.True(dy < 0 && last > first, "A translated, overlapping hero interior is required.");
        var translated = 0;
        var stationary = 0;
        for (var y = first; y < last; y++)
        {
            var original = before(x, y);
            translated = Math.Max(translated, Delta(original, after(x + dx, y + dy)));
            stationary = Math.Max(stationary, Delta(original, after(x, y)));
        }
        Assert.True(translated <= 3, $"The wash did not follow the hero (translated delta {translated}).");
        Assert.True(stationary >= 4, $"The samples cannot distinguish a stationary wash (delta {stationary}).");
        return (translated, stationary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeroWashPixelProofRejectsAViewportAnchoredGradient(bool anchored)
    {
        static System.Drawing.Color Gradient(int x, int y) =>
            System.Drawing.Color.FromArgb(255, Math.Clamp(240 - y, 0, 255), 200, 160);
        System.Drawing.Color After(int x, int y) => Gradient(x, anchored ? y : y + 20);
        var original = new global::System.Windows.Rect(20, 40, 200, 120);
        var moved = new global::System.Windows.Rect(20, 20, 200, 120);
        if (anchored)
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertHeroWashTranslation(Gradient, After, original, moved, 12));
        else
            Assert.Equal((0, 20), AssertHeroWashTranslation(Gradient, After, original, moved, 12));
    }

    private static async Task<AutomationElement> WaitForPageScrollPeerAsync(GatewayFixtureRun run)
    {
        AutomationElement? page = null;
        await WaitUiAsync(run, () => (page = TryPageScrollPeer(run)) is not null, "exposed Automations page scroll peer");
        return page!;
    }

    private static AutomationElement PageScrollPeer(GatewayFixtureRun run, bool dumpAncestors = false) =>
        TryPageScrollPeer(run, dumpAncestors) ?? throw new InvalidOperationException("Missing owned Automations ScrollPresenter.");

    /// <summary>Finds the marker's own template presenter, never an arbitrary nested scroller.</summary>
    private static AutomationElement? TryPageScrollPeer(GatewayFixtureRun run, bool dumpAncestors = false)
    {
        var marker = FindById(run, "CronPageMarker");
        if (marker is null) return null;
        Assert.Equal(run.AppProcessId, marker.Current.ProcessId);
        var markerIdentity = marker.GetRuntimeId();
        bool IsMarker(AutomationElement? element) => element is not null
            && element.Current.ProcessId == run.AppProcessId
            && element.GetRuntimeId().SequenceEqual(markerIdentity);
        bool IsOwnedTemplatePart(AutomationElement candidate)
        {
            if (candidate.Current.ProcessId != run.AppProcessId) return false;
            var parent = TreeWalker.RawViewWalker.GetParent(candidate);
            if (IsMarker(parent)) return true;
            // WinUI's ScrollView template is PART_Root -> PART_ScrollPresenter.
            // The presentation Grid may be absent from UIA; no other ancestry is accepted.
            if (parent is null || parent.Current.ProcessId != run.AppProcessId
                || parent.Current.AutomationId != "PART_Root") return false;
            return IsMarker(TreeWalker.RawViewWalker.GetParent(parent));
        }
        var candidates = marker.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "PART_ScrollPresenter"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane)))
            .Cast<AutomationElement>().ToArray();
        var owned = candidates.Where(IsOwnedTemplatePart).ToArray();
        if (dumpAncestors || owned.Length != 1)
            File.WriteAllText(Path.Combine(run.ArtifactsDirectory, "automations-page-marker-ancestors.json"),
                System.Text.Json.JsonSerializer.Serialize(candidates.Select(element => new
                {
                    element.Current.ClassName,
                    element.Current.AutomationId,
                    element.Current.ProcessId,
                    runtimeId = element.GetRuntimeId(),
                    owned = IsOwnedTemplatePart(element),
                    patterns = element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName).ToArray(),
                }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (owned.Length == 0) return null; // Keep the existing bounded template-readiness wait.
        var presenter = Assert.Single(owned);
        Assert.True(presenter.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern),
            "The owned ScrollView template presenter must expose the native ScrollPattern.");
        Assert.IsType<ScrollPattern>(pattern);
        return presenter;
    }

    private static async Task ChooseAutomationAgentAsync(GatewayFixtureRun run, string agent)
        => await ChooseAutomationComboAsync(run, "AutomationsAgentFilter", agent);

    private static async Task ChooseAutomationComboAsync(GatewayFixtureRun run, string id, string label)
    {
        ((ExpandCollapsePattern)FindById(run, id)!.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        await WaitUiAsync(run, () => FindText(run, label) is not null, "combo choice");
        var choice = AppWindows(run).SelectMany(w => w.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, label)).Cast<AutomationElement>())
            .First(e => e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _));
        ((SelectionItemPattern)choice.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        int[]? previousPeer = null;
        await WaitUiAsync(run, () =>
        {
            if (FindById(run, id) is not { } current) { previousPeer = null; return false; }
            var identity = current.GetRuntimeId();
            var stable = previousPeer is not null && previousPeer.SequenceEqual(identity);
            previousPeer = identity;
            return stable && current.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var collapsed)
                && ((ExpandCollapsePattern)collapsed).Current.ExpandCollapseState == ExpandCollapseState.Collapsed;
        },
            "reacquired combo after selection");
    }

    private static bool HasMeasuredEditorBounds(global::System.Windows.Rect bounds) =>
        !bounds.IsEmpty && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom) &&
        bounds.Width > 0 && bounds.Height > 0;

    private static (double Percent, bool SearchedFromTop) NextEditorScrollTarget(
        global::System.Windows.Rect bounds, global::System.Windows.Rect viewport,
        double currentPercent, double viewSize, bool searchedFromTop)
    {
        Assert.True(HasMeasuredEditorBounds(viewport) && double.IsFinite(currentPercent) &&
            currentPercent is >= 0 and <= 100 && double.IsFinite(viewSize) && viewSize is > 0 and < 100,
            "Editor scroll metrics must be finite and describe a visible scrollable viewport.");
        if (!HasMeasuredEditorBounds(bounds))
        {
            if (!searchedFromTop) return (0, true);
            Assert.True(currentPercent < 100, "Editor field has no measured geometry at the end of the page.");
            return (Math.Min(100, currentPercent + 50 * viewSize / (100 - viewSize)), true);
        }
        var delta = bounds.Bottom > viewport.Bottom ? bounds.Bottom - viewport.Bottom
            : bounds.Top < viewport.Top ? bounds.Top - viewport.Top : 0;
        var range = viewport.Height * (100 / viewSize - 1);
        Assert.True(double.IsFinite(range) && range > 0, "Invalid editor scroll range.");
        return (Math.Clamp(currentPercent + delta * 100 / range, 0, 100), searchedFromTop);
    }

    [Fact]
    public void EditorScrollTarget_UnknownGeometryResetsOnceThenSearchesWithoutInventingBounds()
    {
        var viewport = new global::System.Windows.Rect(0, 0, 200, 200);
        foreach (var unknown in new[] { global::System.Windows.Rect.Empty,
            new global::System.Windows.Rect(0, double.NaN, 100, 20),
            new global::System.Windows.Rect(double.PositiveInfinity, 0, 100, 20),
            new global::System.Windows.Rect(0, 0, double.PositiveInfinity, 20),
            new global::System.Windows.Rect(0, 0, 100, 0) })
        {
            Assert.False(HasMeasuredEditorBounds(unknown));
            Assert.Equal((0d, true), NextEditorScrollTarget(unknown, viewport, 70, 50, false));
            Assert.Equal((50d, true), NextEditorScrollTarget(unknown, viewport, 0, 50, true));
            Assert.Equal((100d, true), NextEditorScrollTarget(unknown, viewport, 75, 50, true));
        }
    }

    [Theory]
    [InlineData(-40, 20, 30)]
    [InlineData(220, 20, 70)]
    [InlineData(50, 20, 50)]
    public void EditorScrollTarget_MeasuredGeometryPreservesVisibleOrOffscreenPosition(
        double top, double height, double expected)
    {
        var bounds = new global::System.Windows.Rect(0, top, 100, height);
        Assert.True(HasMeasuredEditorBounds(bounds));
        Assert.Equal((expected, false),
            NextEditorScrollTarget(bounds, new(0, 0, 200, 200), 50, 50, false));
    }

    private static async Task ScrollEditorFieldIntoViewAsync(GatewayFixtureRun run, string id)
    {
        await WaitUiAsync(run, () => FindById(run, id) is not null, $"reconciled editor field {id}");
        (AutomationElement Field, AutomationElement Owner, ScrollPattern Scroll, global::System.Windows.Rect Viewport) Read() =>
            OwnedWindowCapture.WithPhysicalCoordinates(() =>
            {
                var field = FindById(run, id) ?? throw new InvalidOperationException($"Missing editor field {id}.");
                var owner = TreeWalker.RawViewWalker.GetParent(field);
                while (owner is not null && owner.Current.ProcessId == run.AppProcessId)
                {
                    if (owner.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern)
                        && ((ScrollPattern)pattern).Current.VerticallyScrollable)
                    {
                        var viewport = owner.Current.BoundingRectangle;
                        viewport.Intersect(FindById(run, "AutomationEditorDialog")!.Current.BoundingRectangle);
                        viewport.Intersect(OwnedWindowCapture.ReadOwnedClientBounds(run, AutomationsWindow(run)));
                        viewport.Intersect(OwnedWindowCapture.ReadOwnedBounds(run, AutomationsWindow(run)));
                        Assert.True(!viewport.IsEmpty && viewport.Width > 0 && viewport.Height > 0,
                            $"Editor field {id} has no visible owned scroll viewport.");
                        var footer = FindById(run, "AutomationEditorSave")!.Current.BoundingRectangle;
                        var title = FindById(run, "AutomationEditorTitle")!.Current.BoundingRectangle;
                        viewport.Intersect(new global::System.Windows.Rect(viewport.Left, Math.Max(viewport.Top, title.Bottom),
                            viewport.Width, Math.Max(0, Math.Min(viewport.Bottom, footer.Top - 12) - Math.Max(viewport.Top, title.Bottom))));
                        return (field, owner, (ScrollPattern)pattern, viewport);
                    }
                    owner = TreeWalker.RawViewWalker.GetParent(owner);
                }
                throw new InvalidOperationException($"Editor field {id} has no owned vertical scroll ancestor.");
            });

        bool FullyVisible() => OwnedWindowCapture.WithPhysicalCoordinates(() =>
        {
            var current = Read();
            var bounds = current.Field.Current.BoundingRectangle;
            var viewport = current.Viewport;
            return HasMeasuredEditorBounds(bounds)
                && !current.Field.Current.IsOffscreen && !viewport.IsEmpty
                && bounds.Top >= viewport.Top && bounds.Bottom <= viewport.Bottom
                && bounds.Left >= viewport.Left && bounds.Right <= viewport.Right;
        });

        var initial = FindById(run, id) ?? throw new InvalidOperationException($"Missing editor field {id}.");
        if (initial.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var item))
            ((ScrollItemPattern)item).ScrollIntoView();
        var searchedFromTop = false;
        var receiptOptions = new JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        void RecordStep(int attempt, string phase, double requested, object observation) =>
            File.AppendAllText(Path.Combine(run.ArtifactsDirectory, "automation-editor-scroll.jsonl"),
                JsonSerializer.Serialize(new { id, attempt, phase, requested, searchedFromTop, observation },
                    receiptOptions) + Environment.NewLine);
        object Observe((AutomationElement Field, AutomationElement Owner, ScrollPattern Scroll,
            global::System.Windows.Rect Viewport) current) => new
        {
            bounds = current.Field.Current.BoundingRectangle, viewport = current.Viewport,
            measured = HasMeasuredEditorBounds(current.Field.Current.BoundingRectangle),
            offscreen = current.Field.Current.IsOffscreen, ownerClass = current.Owner.Current.ClassName,
            percent = current.Scroll.Current.VerticalScrollPercent, viewSize = current.Scroll.Current.VerticalViewSize,
        };
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (FullyVisible()) return;
            var next = OwnedWindowCapture.WithPhysicalCoordinates(() =>
            {
                var current = Read();
                var bounds = current.Field.Current.BoundingRectangle;
                var info = current.Scroll.Current;
                var target = NextEditorScrollTarget(bounds, current.Viewport,
                    info.VerticalScrollPercent, info.VerticalViewSize, searchedFromTop);
                searchedFromTop = target.SearchedFromTop;
                RecordStep(attempt, "before", target.Percent, Observe(current));
                current.Scroll.SetScrollPercent(ScrollPattern.NoScroll, target.Percent);
                return target.Percent;
            });
            try
            {
                await WaitUiAsync(run, () => FullyVisible() || Math.Abs(Read().Scroll.Current.VerticalScrollPercent - next) < 1,
                    "editor scroll reaches computed position");
            }
            catch (Exception failure)
            {
                try { RecordStep(attempt, "failed", next, OwnedWindowCapture.WithPhysicalCoordinates(() => Observe(Read()))); }
                catch (Exception observationError)
                {
                    failure.Data["editorScrollObservationError"] =
                        $"{observationError.GetType().Name}; hresult=0x{observationError.HResult:X8}";
                }
                throw;
            }
            RecordStep(attempt, "settled", next, OwnedWindowCapture.WithPhysicalCoordinates(() => Observe(Read())));
        }
        var final = Read();
        var finalBounds = OwnedWindowCapture.WithPhysicalCoordinates(() => final.Field.Current.BoundingRectangle);
        Assert.True(FullyVisible(), $"Editor field {id}: bounds={finalBounds}, clippedViewport={final.Viewport}, " +
            $"scrollClass={final.Owner.Current.ClassName}, percent={final.Scroll.Current.VerticalScrollPercent}, viewSize={final.Scroll.Current.VerticalViewSize}.");
    }

    private static AutomationElement? EditorValueElement(GatewayFixtureRun run, string id)
    {
        var element = FindById(run, id);
        return element is null || element.TryGetCurrentPattern(ValuePattern.Pattern, out _) ? element
            : element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
    }
    private static string? EditorValue(GatewayFixtureRun run, string id) => EditorValueElement(run, id) is { } e
        ? ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).Current.Value : null;
    private static void SetEditorValue(GatewayFixtureRun run, string id, string value)
    {
        var element = EditorValueElement(run, id) ?? throw new InvalidOperationException($"Missing {id}.");
        void Record(string phase) => File.AppendAllText(Path.Combine(run.ArtifactsDirectory, "automation-editor-input.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                phase, id, timestampUtc = DateTimeOffset.UtcNow,
                runtimeId = element.GetRuntimeId(), element.Current.ProcessId,
                element.Current.IsEnabled, element.Current.IsOffscreen, element.Current.HasKeyboardFocus,
                element.Current.ItemStatus,
                nativeLength = ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value.Length,
                requestedLength = value.Length,
            }) + Environment.NewLine);
        Record("before");
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
        Record("returned");
    }

    private static async Task ScanAutomationsAsync(GatewayFixtureRun run, string theme, string state)
    {
        await WaitUiAsync(run, () => run.Gateway.Requests.All(r => r.Outcome != "pending"), "completed Gateway reads");
        RecordTransientPeers(run, $"axe-{state}");
        AxeHelper.AssertNoAccessibilityErrors(run.AppProcessId,
            new IntPtr(AutomationsWindow(run).Current.NativeWindowHandle), context: $"Automations {theme}: {state}",
            diagnosticSink: message => File.AppendAllText(Path.Combine(run.ArtifactsDirectory, "automations-axe-diagnostics.log"),
                message + Environment.NewLine));
    }

    private static AutomationElement[] VisibleMenuPeers(GatewayFixtureRun run) =>
        AutomationsWindow(run).FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu)).Cast<AutomationElement>()
            .Where(menu => !menu.Current.IsOffscreen).ToArray();

    private static void RecordHeadline(GatewayFixtureRun run, string phase)
    {
        var headline = FindById(run, "AutomationsHeadline");
        File.AppendAllText(Path.Combine(run.ArtifactsDirectory, "automations-headline-observations.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                phase, observedUtc = DateTimeOffset.UtcNow, processId = run.AppProcessId,
                name = headline?.Current.Name, controlType = headline?.Current.ControlType.ProgrammaticName,
                bounds = headline?.Current.BoundingRectangle.ToString(),
                recentListReads = run.Gateway.Requests.Where(request => request.Method == "cron.list").TakeLast(4)
                    .Select(request => new { request.AgentId, request.Outcome }).ToArray(),
            }) + Environment.NewLine);
    }

    private static void RecordTransientPeers(GatewayFixtureRun run, string state)
    {
        var window = AutomationsWindow(run);
        var scale = OwnedWindowCapture.OwnedDpiScale(run, window);
        double? hostDip = null;
        string? hostUnavailable = null;
        try { hostDip = OwnedWindowCapture.WithPhysicalCoordinates(() => PageScrollPeer(run).Current.BoundingRectangle.Width / scale); }
        catch (InvalidOperationException error) { hostUnavailable = error.Message; }
        var peers = window.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu),
            new PropertyCondition(AutomationElement.ClassNameProperty, "InputSiteWindowClass"))).Cast<AutomationElement>()
            .Select(peer => new
            {
                peer.Current.ProcessId, peer.Current.Name, peer.Current.AutomationId, peer.Current.ClassName,
                type = peer.Current.ControlType.ProgrammaticName, peer.Current.IsOffscreen,
                peer.Current.IsKeyboardFocusable, bounds = peer.Current.BoundingRectangle.ToString(),
            }).ToArray();
        File.AppendAllText(Path.Combine(run.ArtifactsDirectory, "automations-transient-ui.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(new { state, processId = run.AppProcessId, hwnd = window.Current.NativeWindowHandle,
                pageHostDip = hostDip, hostUnavailable, tier = hostDip is { } width ? PageResponsiveLayout.TierFor(width).ToString() : null, peers }) + Environment.NewLine);
    }
    private static AutomationElement AutomationsWindow(GatewayFixtureRun run)
    {
        run.EnsureRunning();
        using var process = System.Diagnostics.Process.GetProcessById(run.AppProcessId);
        var handle = process.MainWindowHandle;
        Assert.NotEqual(IntPtr.Zero, handle);
        var window = AutomationElement.FromHandle(handle);
        Assert.Equal(run.AppProcessId, window.Current.ProcessId);
        return window;
    }
    private static AutomationElement? FindByIdPrefix(GatewayFixtureRun run, string prefix) => AppWindows(run)
        .SelectMany(w => w.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>())
        .FirstOrDefault(e => e.Current.AutomationId.StartsWith(prefix, StringComparison.Ordinal));
    private static void Capture(GatewayFixtureRun run, string name, params string[] assertedIds)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") != "1") return;
        SaveAutomationsWindow(run, name, assertedIds);
    }

    private static void SaveAutomationsWindow(GatewayFixtureRun run, string name, params string[] assertedIds)
    {
        var window = AutomationsWindow(run);
        var pattern = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
        if (pattern.Current.WindowVisualState != WindowVisualState.Maximized)
        {
            pattern.SetWindowVisualState(WindowVisualState.Maximized);
            SetForegroundWindow(new IntPtr(window.Current.NativeWindowHandle));
        }
        Thread.Sleep(400);
        OwnedWindowCapture.Save(run, window, name, assertedIds.Select(id =>
            FindById(run, id) ?? throw new InvalidOperationException($"Missing {id} for {name}.")).ToArray());
    }
}
