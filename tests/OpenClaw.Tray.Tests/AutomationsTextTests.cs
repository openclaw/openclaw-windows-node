using System.Globalization;
using System.Xml.Linq;
using OpenClaw.Shared.Cron;
using OpenClawTray.Automations;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests;

public sealed class AutomationsTextTests
{
    private static readonly Lazy<Dictionary<string, string>> EnUs = new(() =>
        XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!));

    private static readonly DateTimeOffset Now = new(2030, 1, 7, 12, 0, 0, TimeSpan.Zero);

    private static AutomationsText Text() =>
        new(key => EnUs.Value.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key), () => Now);

    private static T WithCulture<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("2027-01-01T00:30:00Z", "Today 2:30 AM")]
    [InlineData("2027-01-01T23:30:00Z", "Tomorrow 1:30 AM")]
    [InlineData("2027-01-04T10:00:00Z", "Jan 4, 12:00 PM")]
    [InlineData("2028-01-04T10:00:00Z", "Tue, Jan 4, 2028, 12:00 PM")]
    public void FriendlyNextRun_UsesLocalCalendarAcrossUtcYearBoundary(string instant, string expected)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Fixture+02", TimeSpan.FromHours(2), "Fixture+02", "Fixture+02");
        var t = new AutomationsText(key => EnUs.Value[key],
            () => DateTimeOffset.Parse("2026-12-31T22:30:00Z", CultureInfo.InvariantCulture), zone);
        Assert.Equal(expected, WithCulture(() => t.FriendlyDateTime(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds())));
    }

    [Theory]
    [InlineData("2030-01-07T14:30:00Z", "2:30 PM")]
    [InlineData("2030-01-08T15:00:00Z", "Jan 8, 3:00 PM")]
    [InlineData("2030-02-15T16:00:00Z", "Feb 15, 4:00 PM")]
    public void NextUpTime_UsesClockTodayAndCalendarDateForOtherDays(string instant, string expected)
    {
        var t = new AutomationsText(key => EnUs.Value[key], () => Now, TimeZoneInfo.Utc);
        var value = WithCulture(() => t.NextUpTime(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds()));

        Assert.Equal(expected, value);
        Assert.DoesNotContain("in ", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" min", value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NextUpHeader_PassesAbsoluteLocalTimeToLocalizedCopy()
    {
        var page = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Automations", "AutomationsRoot.cs"));

        Assert.Contains("t.Format(\"Redesign_Next\", next.Title, t.NextUpTime(at))", page, StringComparison.Ordinal);
        Assert.DoesNotContain("t.Format(\"Redesign_Next\", next.Title, t.Relative(at))", page, StringComparison.Ordinal);
    }

    [Fact]
    public void OneTimeSchedule_UsesLocalizedWeekdayMonthAndPcZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Fixture+02", TimeSpan.FromHours(2), "Fixture+02", "Fixture+02");
        var t = new AutomationsText(key => EnUs.Value[key],
            () => DateTimeOffset.Parse("2026-12-31T22:30:00Z", CultureInfo.InvariantCulture), zone);
        var schedule = new CronSchedule(CronScheduleKind.At, "at", At: "2027-01-01T23:30:00Z");
        Assert.Equal("Once on Sat, Jan 2 at 1:30 AM", WithCulture(() => t.Schedule(schedule)));
        var form = CronJobForm.CreateDefault("main") with { ScheduleKind = CronScheduleKind.At, ScheduleAt = DateTimeOffset.Parse(schedule.At!, CultureInfo.InvariantCulture) };
        Assert.Equal("Once on Sat, Jan 2 at 1:30 AM", WithCulture(() => t.FormSchedule(form)));
        Assert.Equal("Once today at 2:30 AM", WithCulture(() => t.Schedule(schedule with { At = "2027-01-01T00:30:00Z" })));
    }

    [Theory]
    [InlineData(1, 0, 1, "1 on.")]
    [InlineData(0, 1, 1, "1 paused or off.")]
    [InlineData(2, 1, 2, "2 on, 1 paused or off, across 2 agents.")]
    [InlineData(0, 0, 0, "")]
    public void InventorySummary_OmitsZeroCountsAndSingleAgentScope(int on, int paused, int agents, string expected) =>
        Assert.Equal(expected, WithCulture(() => Text().InventorySummary(on, paused, agents)));

    [Fact]
    public void SchedulerOff_ExplainsGatewayOwnershipInPlainLanguage() =>
        Assert.Equal("Scheduled automations won't run until the Gateway scheduler is on.", Text()["SchedulerOff_Body"]);

    [Theory]
    [InlineData("0 9 * * *", null, "Every day at 9:00 AM")]
    [InlineData("0 9 * * 1-5", null, "Weekdays at 9:00 AM")]
    [InlineData("30 18 * * 0,6", null, "Weekends at 6:30 PM")]
    [InlineData("0 9 * * 1,3", null, "Monday, Wednesday at 9:00 AM")]
    [InlineData("0 9 1 * *", null, "Monthly on day 1 at 9:00 AM")]
    [InlineData("0 * * * *", null, "Every hour")]
    [InlineData("15 * * * *", null, "Every hour at minute 15")]
    [InlineData("*/15 * * * *", null, "Every 15 minutes")]
    [InlineData("0 */6 * * *", null, "Every 6 hours")]
    [InlineData("0 9 * 1 *", "UTC", "Cron schedule 0 9 * 1 * (UTC)")]
    [InlineData("0 7 * * 1-5", "America/Los_Angeles", "Weekdays at 7:00 AM (America/Los_Angeles)")]
    public void CronSummaries_AreReadable(string expr, string? tz, string expected) =>
        Assert.Equal(expected, WithCulture(() => Text().Cron(expr, tz)));

    [Theory]
    [InlineData(1_000L, "Every second")]
    [InlineData(1_800_000L, "Every 30 minutes")]
    [InlineData(3_600_000L, "Every hour")]
    [InlineData(172_800_000L, "Every 2 days")]
    [InlineData(1_500L, "Every 1.5 seconds")]
    public void IntervalSummaries_UseTheLargestExactUnit(long everyMs, string expected) =>
        Assert.Equal(expected, Text().Every(everyMs));

    [Fact]
    public void ScheduleSummaries_CoverEventAndProcessKinds()
    {
        var t = Text();
        Assert.Equal("When pull_request.opened arrives from github",
            t.Schedule(new CronSchedule(CronScheduleKind.Event, "event", EventSource: CronProtocol.McpEventsSource, EventServer: "github", EventName: "pull_request.opened")));
        Assert.Equal("On output from tail -f", t.Schedule(new CronSchedule(CronScheduleKind.Stream, "stream", Command: "tail -f")));
        Assert.Equal("When make exits", t.Schedule(new CronSchedule(CronScheduleKind.OnExit, "on-exit", Command: "make")));
        Assert.Null(t.FormSchedule(CronJobForm.CreateDefault(null) with { EveryAmount = "nope" }));
        Assert.Equal("Every 30 minutes", t.FormSchedule(CronJobForm.CreateDefault(null)));
    }

    [Fact]
    public void RelativeTimesAndDurations()
    {
        var t = Text();
        var now = Now.ToUnixTimeMilliseconds();
        Assert.Equal("now", t.Relative(now + 10_000));
        Assert.Equal("in 5 min", t.Relative(now + 5 * 60_000));
        Assert.Equal("3 h ago", t.Relative(now - 3 * 3_600_000));
        Assert.Equal("in 2 d", t.Relative(now + 2 * 86_400_000));
        Assert.Equal("250 ms", t.Duration(250));
        Assert.Equal("2 min 5 s", t.Duration(125_000));
        Assert.StartsWith("Today ", WithCulture(() => t.RunTimestamp(now)));
        Assert.Matches(@"[A-Za-z]{3} \d{1,2}, ", WithCulture(() => t.RunTimestamp(now - 86_400_000)));
        Assert.DoesNotContain("2030", WithCulture(() => t.RunTimestamp(now - 86_400_000)));
    }

    [Fact]
    public void StatusAndNoticeWording()
    {
        var t = Text();
        var job = CronJsonReaderJob("""{"id":"a","name":"Brief","enabled":false,"schedule":{"kind":"every","everyMs":60000},"payload":{"kind":"agentTurn","message":"m"},"state":{"autoDisabled":{"reason":"consecutive-failures","atMs":1,"consecutiveErrors":5}}}""");
        Assert.Equal("Auto-disabled after 5 failed runs", t.JobState(job, running: false));
        Assert.Equal("Running", t.JobState(job, running: true));
        Assert.Equal("This automation is not due yet.", t.RunNotStarted("not-due"));
        Assert.Equal("This automation could not be started.", t.RunNotStarted("something-new"));
        var (title, message) = t.Notice(new AutomationsNotice(AutomationsNoticeKind.JobCompleted, "Dentist"));
        Assert.Equal("Automation finished", title);
        Assert.Equal("Dentist ran and removed itself.", message);
        Assert.Equal("Run queued · run-7", t.Notice(new(AutomationsNoticeKind.RunQueued, "Brief", RunId: "run-7")).Message);
        Assert.Equal("Not started: “Brief” is already running.", t.Notice(new(AutomationsNoticeKind.RunNotStarted, "Brief", "already-running")).Message);
        Assert.Equal("Removed “Brief”", t.Notice(new(AutomationsNoticeKind.Removed, "Brief")).Message);
        Assert.False(new AutomationsNotice(AutomationsNoticeKind.RunNotStarted, "x", "already-running").IsError);
        Assert.True(new AutomationsNotice(AutomationsNoticeKind.RunNotStarted, "x", "invalid-spec").IsError);
    }

    [Fact]
    public void UnavailableJob_SharesTheHubSearchWordingAndIsNeitherSuccessNorFailure()
    {
        var notice = new AutomationsNotice(AutomationsNoticeKind.JobUnavailable, "deleted-elsewhere");
        var (title, message) = Text().Notice(notice);
        Assert.Equal(EnUs.Value["Search_TargetUnavailable"], message);
        Assert.Equal(message, title);
        Assert.DoesNotContain("deleted-elsewhere", message);
        Assert.False(notice.IsError);
        Assert.True(notice.IsInformational);

        // Only a request that cannot be honored as asked is informational; results and failures are not.
        Assert.True(new AutomationsNotice(AutomationsNoticeKind.RunNotStarted, "x", "not-due").IsInformational);
        Assert.False(new AutomationsNotice(AutomationsNoticeKind.RunNotStarted, "x", "invalid-spec").IsInformational);
        Assert.False(new AutomationsNotice(AutomationsNoticeKind.Saved, "x").IsInformational);
        Assert.False(new AutomationsNotice(AutomationsNoticeKind.OperationFailed, "x").IsInformational);
    }

    [Theory]
    [InlineData("FORBIDDEN", "cron.run requires operator.admin.")]
    [InlineData("invalid-spec", "delivery.to is missing.")]
    public void RejectedRun_PreservesGatewayExplanationInToastAndAnnouncement(string reason, string detail)
    {
        var notice = new AutomationsNotice(AutomationsNoticeKind.RunNotStarted, "Dentist", reason, detail);
        if (reason == "FORBIDDEN")
            Assert.Equal("Couldn't start “Dentist”. This connection is missing the operator.admin permission.", Text().Notice(notice).Message);
        else Assert.EndsWith(detail, Text().Notice(notice).Message);
        Assert.Equal(detail, notice.Detail);
        Assert.True(notice.IsError);
    }

    [Fact]
    public void PageOwnedOutcomes_DoNotPublishDuplicateGlobalNotifications()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "CronPage.xaml.cs"));
        Assert.DoesNotContain("AppNotificationPublisher.Show", source);
        Assert.DoesNotContain("AppNotification_ActionOpenCron", source);
    }

    [Fact]
    public void ModalPresentation_RetiresNativeTransientSurfacesWithoutIgnoringAccessibility()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Automations", "AutomationsRoot.cs"));
        var visuals = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Automations", "AutomationsVisuals.cs"));
        Assert.Contains("await CloseTransientSurfacesAsync(page)", page);
        Assert.Contains(".WithFlyout(", page);
        Assert.DoesNotContain("native.Flyout = menu", page);
        Assert.Contains("bar.Closed +=", visuals);
        Assert.Contains("flyout.Hide()", visuals);
    }

    [Theory]
    [InlineData("en-us")]
    [InlineData("fr-fr")]
    [InlineData("nl-nl")]
    [InlineData("pt-br")]
    [InlineData("zh-cn")]
    [InlineData("zh-tw")]
    public void ObjectCopy_UsesTypographicQuotesWithoutExamplePlaceholdersInEveryLocale(string locale)
    {
        var catalog = XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
        foreach (var item in catalog.Root!.Elements("data").Where(item => ((string)item.Attribute("name")!).StartsWith("Automations_", StringComparison.Ordinal)))
        {
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch((string)item.Element("value")!, "\"\\{[0-9]+\\}\""),
                $"{locale}: {item.Attribute("name")} uses straight object quotes.");
            if (((string)item.Attribute("name")!).StartsWith("Automations_Placeholder_", StringComparison.Ordinal))
                Assert.DoesNotContain("America/Los_Angeles", (string)item.Element("value")!);
        }
    }

    [Fact]
    public void EveryValidationErrorHasWording()
    {
        var t = Text();
        foreach (var error in Enum.GetValues<CronFormError>())
            Assert.False(string.IsNullOrWhiteSpace(t.FormError(error)));
        Assert.Equal(t.FormError(CronFormError.WebhookUrlRequired), t.FormError(CronFormError.WebhookUrlInvalid));
    }

    [Fact]
    public void EveryResourceKeyUsedByTheAutomationsTreeExists()
    {
        var dir = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Automations");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            var source = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source, @"(?:\bt|this|text)\[""([A-Za-z0-9_]+)""\]|Format\(""([A-Za-z0-9_]+)"""))
                keys.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        }
        Assert.NotEmpty(keys);
        Assert.DoesNotContain(keys, key => !EnUs.Value.ContainsKey("Automations_" + key));
        Assert.DoesNotContain(EnUs.Value.Keys, key => key.StartsWith("Automations_Suggestion_", StringComparison.Ordinal));
        Assert.DoesNotContain("Automations_Placeholder_Name", EnUs.Value.Keys);
        Assert.Equal("Channel ID", EnUs.Value["Automations_Placeholder_ChannelLast"]);
        Assert.DoesNotContain("https://example.com/cron", File.ReadAllText(Path.Combine(dir, "AutomationEditor.cs")));
        Assert.DoesNotContain(EnUs.Value, kv => kv.Key.StartsWith("Automations_", StringComparison.Ordinal) && kv.Value.Contains('\u2014'));
    }

    [Fact]
    public void AutomationsPalette_DefinesEveryReferencedKeyInEveryTheme()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = XDocument.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Themes", "AutomationsResources.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var themes = xaml.Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(x + "Key") is not null)
            .ToDictionary(
                e => e.Attribute(x + "Key")!.Value,
                e => e.Elements().Select(r => r.Attribute(x + "Key")?.Value).OfType<string>().ToHashSet(StringComparer.Ordinal));
        Assert.Equal(["Default", "HighContrast", "Light"], themes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(themes["Default"], themes["Light"]);
        Assert.Equal(themes["Default"], themes["HighContrast"]);

        // High contrast must only alias system colors, never fixed hex values.
        var highContrast = xaml.Descendants().Single(e => e.Attribute(x + "Key")?.Value == "HighContrast");
        Assert.DoesNotContain(highContrast.Descendants(), e => e.Attribute("Color")?.Value.StartsWith('#') == true);

        var dir = Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Automations");
        var used = Directory.GetFiles(dir, "*.cs")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"Theme\.Ref\(""(Automations\w+)""\)"))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.Empty(used.Except(themes["Default"]));

        var app = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "App.xaml"));
        Assert.Contains("Themes/AutomationsResources.xaml", app);
    }

    [Fact]
    public void InlineInventory_DoesNotReintroduceSplitNavigation()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Automations", "AutomationsRoot.cs"));
        Assert.DoesNotContain("RenderDetailPane", page);
        Assert.DoesNotContain("AutomationsBack", page);
        Assert.DoesNotContain("ListPaneWidth", page);
        var detail = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Automations", "AutomationDetail.cs"));
        Assert.Contains("CommandBar(primaryCommands:", detail);
        Assert.DoesNotContain("SelectorBar(", detail);
        Assert.Contains("runs.Take(3)", detail);
        Assert.Contains("AutomationRunResult.Render(", detail);
        Assert.Contains("host.OpenWeb(job.Id)", detail);
        // Ledger closure: retire when mounted navigation covers absence of the full-history surface.
        Assert.False(File.Exists(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Automations", "AutomationRuns.cs")));
        var store = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Shared", "Cron", "AutomationsStore.cs"));
        foreach (var retired in new[] { "enum AutomationsView", "SetRunsFilter(", "RunsLoadingMore", "RunsHasMore", "RunsTotal" })
            Assert.DoesNotContain(retired, store);
    }

    [Fact]
    public void PageHero_OwnsTheHeaderWashWithoutReintroducingTheViewportBackdrop()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Automations", "AutomationsRoot.cs"));
        var helper = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Presentation", "Adapters", "PageHero.cs"));
        var visuals = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Automations", "AutomationsVisuals.cs"));
        Assert.Contains("PageHero.Header(", page);
        Assert.Contains("PageHero.SummaryTiles(", page);
        Assert.DoesNotContain("RenderAttention(", page);
        Assert.DoesNotContain("AutomationsBackdrop", page);
        Assert.Contains("private static Element AccentWash(", helper);
        Assert.Contains("AccentGradient(native.ActualTheme)", helper);
        Assert.Contains(".Padding(narrow ? 16 : 24", helper);
        Assert.DoesNotContain("AutomationsPageWashBrush", helper);
        Assert.DoesNotContain("SeenBadgeEntrances", helper);
        Assert.Contains("badge.Transition(Microsoft.UI.Reactor.Animation.Transition.Fade", helper);
        Assert.Contains("reconciliationKey ?? automationId", helper);
        Assert.Contains("reconciliationKey: \"AutomationRowStatusBadge\"", visuals);
        Assert.Contains("new UISettings().AnimationsEnabled", helper);
        Assert.Contains("ChildrenTransitions", helper);
    }

    [Fact]
    public void AgentRoute_PassesTheRouteAgentToTheAutomationsPage()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs"));
        Assert.Contains("cron.AgentId = _currentAgentId;", shell);
        var page = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "CronPage.xaml.cs"));
        Assert.Contains("new AutomationsStore(", page);
        Assert.Contains("agentId,", page);
        Assert.DoesNotContain("RequestCronListAsync", page);
        // Web handoff reuses the Dashboard launcher (tunnel, credential policy, visible failures).
        Assert.Contains("OpenDashboard(CronProtocol.WebPath(jobId))", page);
        Assert.DoesNotContain("LaunchUriAsync", page);
    }

    [Fact]
    public void HubSearchHandoff_ReachesTheMountedRootThroughThePageChannel()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs"));
        Assert.Contains("cron.RevealSearchJob(target.Focus)", shell);
        var page = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "CronPage.xaml.cs"));
        Assert.Contains("internal void RevealSearchJob(string jobId)", page);
        Assert.Contains("_reveals = host.Reveals;", page);
        Assert.Contains("reveals.RevealAsync(jobId)", page);
        // The root serves the page's requests with its latest render, which resets browse state and may widen to All agents.
        var automations = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Automations", "AutomationsRoot.cs"));
        Assert.Contains("host.Reveals.Attach(", automations);
        Assert.Contains("store.RevealAsync(id, widenToAllAgents)", automations);
    }

    [Fact]
    public void AgentScope_NamesAllAgentsSeparatelyFromStatus()
    {
        var t = Text();
        Assert.Equal("All agents", t.AgentScope(null));
        Assert.Equal("Agent: ops", t.AgentScope("ops"));

        var state = new AutomationsSnapshot
        {
            AgentId = "main",
            AgentFilter = null,
            Jobs = [CronJsonReaderJob("""{"id":"b","agentId":"zeta","name":"B","enabled":false,"schedule":{"kind":"every","everyMs":60000},"payload":{"kind":"agentTurn","message":"m"}}""")],
        };
        Assert.Equal(["main", "alpha", "zeta"], state.FilterAgents(["alpha", "main"]));
        Assert.Equal("main", state.DefaultAgentId);
        Assert.Equal("zeta", (state with { AgentFilter = "zeta" }).DefaultAgentId);
    }

    [Fact]
    public void Editor_NamesSettingsItKeepsButDoesNotShow()
    {
        var t = Text();
        var plain = CronJsonReaderJob("""{"id":"a","name":"A","enabled":true,"schedule":{"kind":"cron","expr":"0 * * * *","staggerMs":300000},"payload":{"kind":"agentTurn","message":"m"},"delivery":{"mode":"announce","channel":"slack","to":"#a"}}""");
        Assert.Empty(t.PreservedSettings(plain));

        var advanced = CronJsonReaderJob("""
        {"id":"b","name":"B","enabled":true,"sessionKey":"route","idleOnly":true,
         "schedule":{"kind":"cron","expr":"0 9 * * *","staggerMs":0},
         "activeHours":{"start":"09:00","end":"17:00"},"trigger":{"script":"x"},
         "payload":{"kind":"agentTurn","message":"m","model":"p/1","thinking":"low","timeoutSeconds":5,"toolsAllow":["a"]},
         "delivery":{"mode":"announce","accountId":"work"},"failureAlert":false}
        """);
        Assert.Equal(
            ["Stagger window", "Active hours", "Condition trigger", "Model", "Thinking", "Timeout", "Options", "Allowed tools", "Session key", "Delivery routing", "Failure alerts"],
            t.PreservedSettings(advanced));
    }

    [Theory]
    [InlineData(CronPayloadKind.AgentTurn, "Prompt")]
    [InlineData(CronPayloadKind.SystemEvent, "Main timeline message")]
    [InlineData(CronPayloadKind.Command, "Command")]
    [InlineData(CronPayloadKind.Script, "Script")]
    [InlineData(CronPayloadKind.Unknown, "Payload")]
    public void PayloadField_NamesTheGatewayField(CronPayloadKind kind, string label) =>
        Assert.Equal(label, Text().PayloadField(kind));

    [Fact]
    public void MissingGatewayValues_StayUnknownInsteadOfInventedCounts()
    {
        var t = Text();
        var partial = CronJsonReaderJob("""{"id":"p","name":"P","enabled":false,"schedule":{"kind":"every","everyMs":60000},"payload":{"kind":"agentTurn","message":"m"},"state":{"autoDisabled":{}}}""");
        Assert.Equal("Turned off by the scheduler", t.JobState(partial, running: false));
        Assert.Equal("All", t.CountLabel("Redesign_All", null));
        Assert.Equal("Paused or off", t.CountLabel("Redesign_Off", null));
        Assert.Equal("On (0)", t.CountLabel("Redesign_On", 0));

        var active = CronJsonReaderJob("""{"id":"a","name":"A","enabled":true,"schedule":{"kind":"every","everyMs":60000},"payload":{"kind":"agentTurn","message":"m"}}""");
        // Before the complete overview arrives, a filtered browse page is not the inventory.
        var browsing = new AutomationsSnapshot { AgentId = "main", Jobs = [active], Total = 1, ListQuery = new() { Enabled = CronEnabledFilter.Enabled } };
        Assert.Null(browsing.InventoryTotal);
        Assert.Null(browsing.ActiveCount);
        Assert.Null(browsing.PausedCount);
        Assert.Equal(7, (browsing with { ScopedTotal = 7 }).InventoryTotal);

        var loaded = browsing with { ScopedTotal = 7, OverviewLoaded = true, OverviewJobs = [active, partial] };
        Assert.Equal((2, 1, 1), (loaded.InventoryTotal, loaded.ActiveCount, loaded.PausedCount));
    }

    private static CronJob CronJsonReaderJob(string json) =>
        CronJsonReader.ParseJob(System.Text.Json.JsonDocument.Parse(json).RootElement)!;
}
