using System.Globalization;
using System.Reflection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClaw.Shared.Usage;
using OpenClaw.TestSupport;
using OpenClawTray.Helpers;
using OpenClawTray.Pages.Usage;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Usage;
using OpenClawTray.Services;
using Xunit.Abstractions;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Mounts the real Reactor Usage page and controller over a recording Gateway double, with the page's production
/// <see cref="UsageHandoffs"/> wired to recording app commands. Covers the server overview (2026.10.10 betas), the
/// "basic" schema every released Gateway setup installs (2026.9.36 through 2026.10.9) and an older, unsupported Gateway.
/// </summary>
[Collection(UICollection.Name)]
public sealed class UsagePageRenderingTests(UIThreadFixture ui, ITestOutputHelper output)
{
    private const double PageWidth = 1300;
    private const double PageHeight = 900;

    [Fact]
    public void Viewport_PreservesSubDipTierChangesAndIndependentInsetsWithoutDuplicateNotifications()
    {
        var viewport = new UsageViewport();
        var changes = 0;
        viewport.Changed += () => changes++;
        viewport.Update(999.99994);
        Assert.Equal(0, changes);
        viewport.Update(999.99);
        Assert.Equal(PageWidthTier.Medium, PageResponsiveLayout.TierFor(viewport.Width));
        viewport.Update(720);
        viewport.Update(719.99);
        Assert.Equal(PageWidthTier.Narrow, PageResponsiveLayout.TierFor(viewport.Width));
        Assert.Equal(3, changes);
        viewport.UpdateTopInset(56);
        viewport.UpdateTopInset(56);
        Assert.Equal(4, changes);
        Assert.Equal(56, viewport.TopInset);
        Assert.Equal(PageWidthTier.Narrow, PageResponsiveLayout.TierFor(viewport.Width));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(56)]
    public async Task ContentInset_RetainsPreMountValueAndMovesOnlyScrollWithoutResettingTheReport(double initialInset)
    {
        var viewport = new UsageViewport();
        viewport.UpdateTopInset(initialInset);
        var changedInset = initialInset == 0 ? 56 : 0;
        await WithUsagePageAsync(Report(false), new RecordingCommands(), async (host, gateway) =>
        {
            await WaitForAsync(host, "UsageDailyChart");
            ScrollViewer scroll = null!;
            Border backdrop = null!;
            Grid chart = null!;
            double contentTop = 0, backdropTop = 0;
            SessionsUsageReport report = null!;
            await ui.RunOnUIAsync(() =>
            {
                scroll = Get<ScrollViewer>(host, "UsagePageRoot");
                backdrop = Get<Border>(host, "UsageMastBackdrop");
                chart = Get<Grid>(host, "UsageDailyChart");
                Assert.Equal(initialInset, scroll.Margin.Top);
                contentTop = Get<FrameworkElement>(host, "UsageContentColumn").TransformToVisual(host).TransformPoint(new()).Y;
                backdropTop = backdrop.TransformToVisual(host).TransformPoint(new()).Y;
                report = Assert.IsType<SessionsUsageReport>(gateway.Controller.State.Report);
                viewport.UpdateTopInset(changedInset);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => scroll.Margin.Top == changedInset &&
                    Math.Abs(Get<FrameworkElement>(host, "UsageContentColumn").TransformToVisual(host).TransformPoint(new()).Y - contentTop - (changedInset - initialInset)) <= 1,
                "only retained scrolling content moves below Settings chrome"));
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(scroll, Get<ScrollViewer>(host, "UsagePageRoot"));
                Assert.Same(chart, Get<Grid>(host, "UsageDailyChart"));
                Assert.Same(report, gateway.Controller.State.Report);
                Assert.Equal(backdropTop, backdrop.TransformToVisual(host).TransformPoint(new()).Y);
                Assert.Equal(1000, viewport.Width);
                viewport.UpdateTopInset(initialInset);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => scroll.Margin.Top == initialInset &&
                    Math.Abs(Get<FrameworkElement>(host, "UsageContentColumn").TransformToVisual(host).TransformPoint(new()).Y - contentTop) <= 1,
                "expanded Settings restores the original content origin"));
        }, pageWidth: 1000, viewport: viewport);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DailyTokenTable_DistinguishesMissingTotalsFromReportedZero(bool reported)
    {
        var date = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var day = new UsageTotals { TotalCost = 1, TotalTokens = 0, Report = UsageReportState.Available,
            ReportedFields = reported ? new HashSet<string> { "totalCost", "totalTokens" } : new HashSet<string> { "totalCost" } };
        var report = Report(false) with { Aggregates = new UsageAggregates { CostDaily = [new(date, day)] } };
        await WithUsagePageAsync(report, new RecordingCommands(), async (host, gateway) =>
        {
            await WaitForAsync(host, "UsageDailyChart");
            await ui.RunOnUIAsync(() =>
            {
                gateway.Controller.SetMetric(UsageMetric.Tokens);
                var view = Get<SelectorBar>(host, "UsageChartView");
                view.SelectedItem = view.Items[1];
            });
            await WaitForAsync(host, "UsageDailyTable");
            await ui.RunOnUIAsync(() => Assert.Equal(reported ? "0" : UsageUi.Dash,
                Get<TextBlock>(host, "UsageDailyRowValue_" + date).Text));
        });
    }

    public class UsageGateway : DispatchProxy
    {
        public SessionsUsageReport Overview = new();
        /// <summary>When set, sessions.usage is unknown, as on a Gateway older than any setup installs.</summary>
        public bool Unsupported;
        public bool Connected = true;
        public UsageReadStatus? Failure;
        public TaskCompletionSource<UsageReadResult<SessionsUsageReport>>? Pending;
        internal UsagePageController Controller = null!;
        internal UsageExportWorkflow ExportWorkflow = null!;
        public readonly List<string> SavedNames = [];
        public readonly List<string> OpenedFolders = [];
        public bool SaveFails;
        public readonly List<string> SaveFailures = [];
        public IReadOnlyList<ProviderUsageSnapshot> Providers =
        [
            new() { Provider = "anthropic", DisplayName = "Claude", Plan = "Max",
                Windows = [new("Week", null, 78, DateTimeOffset.Now.AddDays(2)), new("Five hours", null, 41, DateTimeOffset.Now.AddHours(2))] },
            new() { Provider = "openai", DisplayName = "OpenAI", Plan = "Plus",
                Windows = [new("Week", null, 34, DateTimeOffset.Now.AddDays(4))] },
            new() { Provider = "google", DisplayName = "Google", Plan = "API",
                Billing = [new("budget", "Balance", 42.1, 100, "USD", null)] },
        ];
        public readonly List<SessionsUsageQuery> Queries = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_IsConnectedToGateway" => Connected,
            "get_SessionMutationConnectionEpoch" => Connected ? (long?)1 : null,
            "GetSessionsUsageAsync" => Read((SessionsUsageQuery)args![0]!),
            "GetProviderUsageAsync" => Task.FromResult(UsageReadResult<ProviderUsageSummary>.Ok(
                new ProviderUsageSummary(DateTimeOffset.Now, Providers, false))),
            _ => throw new NotSupportedException(method.Name),
        };

        private Task<UsageReadResult<SessionsUsageReport>> Read(SessionsUsageQuery query)
        {
            lock (Queries) Queries.Add(query);
            if (Pending is not null) return Pending.Task;
            if (Failure == UsageReadStatus.Forbidden)
                return Task.FromResult(UsageReadResult<SessionsUsageReport>.Forbidden("operator.read is required"));
            if (Failure == UsageReadStatus.Failed)
                return Task.FromResult(UsageReadResult<SessionsUsageReport>.Failed("Synthetic Gateway unavailable"));
            if (Unsupported)
                return Task.FromResult(UsageReadResult<SessionsUsageReport>.Unsupported());
            var report = query.Key is { } key
                ? Overview with { Sessions = Overview.Sessions.Where(s => s.Key == key).ToList() }
                : Overview;
            return Task.FromResult(UsageReadResult<SessionsUsageReport>.Ok(report));
        }
    }

    /// <summary>Records the app commands the page's handoffs issue. Any other command fails the test.</summary>
    private sealed class RecordingCommands : IAppCommands
    {
        public readonly List<string?> DashboardPaths = [];
        public readonly List<string> Navigations = [];
        public readonly List<string> PendingChatKeys = [];

        public void OpenDashboard(string? path = null) => DashboardPaths.Add(path);
        public void Navigate(string pageTag) => Navigations.Add(pageTag);
        public void Reconnect() => throw new NotSupportedException();
        public void Disconnect() => throw new NotSupportedException();
        public void ShowVoiceOverlay() => throw new NotSupportedException();
        public void CheckForUpdates() => throw new NotSupportedException();
        public void ShowOnboarding() => throw new NotSupportedException();
        public void ShowLocalAiSetup() => throw new NotSupportedException();
        public void ShowGatewayWizard() => throw new NotSupportedException();
        public void ShowConnectionStatus() => throw new NotSupportedException();
        public void NotifySettingsSaved() => throw new NotSupportedException();
        public Task<bool> ApplyAutoStart(SettingsWriteOrigin origin, bool autoStart) => throw new NotSupportedException();
        public Task<bool> ResendOpenTelemetryProbeAsync() => throw new NotSupportedException();
    }

    private static SessionUsageRow Row(string key, string label, string agent, string model, long tokens, double cost, DateTimeOffset last) => new()
    {
        Key = key,
        Label = label,
        AgentId = agent,
        ModelProvider = "anthropic",
        Model = model,
        Usage = new SessionUsageSummary
        {
            Totals = new UsageTotals { Input = tokens / 2, Output = tokens / 4, CacheRead = tokens / 4, TotalTokens = tokens, TotalCost = cost, Report = UsageReportState.Available },
            MessageCounts = new UsageMessageCounts(10, 5, 5, 3, 3, 1),
            LastActivity = last,
        },
    };

    private static readonly DateTimeOffset Anchor = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static SessionUsageRow[] NamedRows() =>
    [
        Row("agent:main:webchat:main", "Main conversation", "main", "claude-sonnet-4.5", 400_000, 1.2, Anchor),
        Row("agent:research:discord:42", "Research digest", "research", "claude-opus-4.1", 250_000, 2.4, Anchor.AddDays(2)),
        Row("agent:ops:slack:C01", "Incident review", "ops", "claude-sonnet-4.5", 90_000, 0.3, Anchor.AddDays(1)),
    ];

    private static readonly UsageTotals RangeTotals = new()
    {
        Input = 370_000, Output = 185_000, CacheRead = 185_000, TotalTokens = 740_000, TotalCost = 3.9, Report = UsageReportState.Available,
    };

    private static IReadOnlyList<UsageDailyTotals> Daily()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        double[] costs = [0.45, 0.32, 0.78, 0.62, 0.49, 0.93, 0.31];
        long[] tokens = [100_000, 90_000, 120_000, 110_000, 80_000, 140_000, 100_000];
        return Enumerable.Range(0, 7).Select(i => new UsageDailyTotals(UsageFilters.Iso(today.AddDays(-i)),
            new UsageTotals { TotalTokens = tokens[i], TotalCost = costs[i], Report = UsageReportState.Available })).ToList();
    }

    private static SessionsUsageReport Report(bool serverOverview, SessionUsageRow[]? rows = null, int? sessionCount = -1)
    {
        rows ??= NamedRows();
        return new SessionsUsageReport
        {
            Sessions = rows,
            Totals = RangeTotals,
            CacheStatus = new UsageCacheStatus("fresh", 0, 0, null),
            Aggregates = new UsageAggregates
            {
                // -1 means "report the loaded count"; null means the Gateway reported no count.
                SessionCount = sessionCount == -1 ? rows.Length : sessionCount,
                Messages = new UsageMessageCounts(30, 15, 15, 9, 9, 3),
                ByAgent = NamedRows().Select(r => new UsageNamedTotals(r.AgentId!, r.Usage!.Totals)).ToList(),
                ByModel = NamedRows().Select(r => new UsageModelBreakdown("anthropic", r.Model, 5, r.Usage!.Totals)).ToList(),
                ByProvider = [new UsageModelBreakdown("anthropic", null, 15, RangeTotals)],
                CostDaily = Daily(),
            },
            Overview = serverOverview
                ? new UsageOverview { Total = rows.Length, Limit = 50,
                    FilterOptions = new Dictionary<string, IReadOnlyList<string>> { ["agent"] = ["main", "ops", "research"] } }
                : null,
        };
    }

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task ThirtyDaySelection_NamesThe28ReportedDaysAndTheTableGrowsWithItsSelectedMetric(ElementTheme theme)
    {
        var start = new DateOnly(2026, 10, 3);
        var end = new DateOnly(2026, 10, 30);
        var days = Enumerable.Range(0, 28).Select(index => new UsageDailyTotals(
            start.AddDays(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            new UsageTotals { TotalCost = index == 27 ? 3.56 : 2.5,
                TotalTokens = index == 27 ? 2800 : 1000, Report = UsageReportState.Available })).ToArray();
        var session = Row("agent:main:webchat:main", "30-day analysis", "main", "claude-sonnet-4.5",
            29_800, 71.06, new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        var report = Report(false) with
        {
            StartDate = "2026-10-03", EndDate = "2026-10-30",
            Sessions = [session], Totals = session.Usage!.Totals,
            Aggregates = new UsageAggregates
            {
                SessionCount = 1, CostDaily = days,
                ByAgent = [new("main", session.Usage.Totals)],
                ByModel = [new("anthropic", session.Model, 10, session.Usage.Totals)],
                ByProvider = [new("anthropic", null, 10, session.Usage.Totals)],
            },
        };
        await WithUsagePageAsync(report, new RecordingCommands(), async (host, gateway) =>
        {
            await ui.RunOnUIAsync(() => gateway.Controller.SetPreset(UsagePreset.Last30Days));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find<TextBlock>(host, "UsageSubtitle")?.Text.Contains("28", StringComparison.Ordinal) == true,
                "28 reported days instead of an invented 30"));
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("Oct 3 to Oct 30", Get<TextBlock>(host, "UsageSummaryRange").Text);
                Assert.Equal("$71.06", Get<TextBlock>(host, "UsageSummaryCostValue").Text);
                Assert.Equal(71.06, gateway.Controller.State.Dashboard!.Chart.Days.Sum(day => day.Cost), 8);
                Get<SelectorBar>(host, "UsageChartView").SelectedItem = Get<SelectorBar>(host, "UsageChartView").Items[1];
            });
            await WaitForAsync(host, "UsageDailyTable");
            await ui.RunOnUIAsync(async () =>
            {
                var table = Get<StackPanel>(host, "UsageDailyTable");
                var headers = FindDescendants<TextBlock>(Get<Grid>(host, "UsageDailyTableHeader"))
                    .Select(text => text.Text).ToArray();
                Assert.Equal(new[] { LocalizationHelper.GetString("Usage_Table_Date"),
                    LocalizationHelper.GetString("Usage_Summary_EstimatedCost") }, headers);
                var rows = FindDescendants<Grid>(table).Where(row =>
                    AutomationProperties.GetAutomationId(row).StartsWith("UsageDailyRow_", StringComparison.Ordinal)).ToArray();
                Assert.Equal(28, rows.Length);
                Assert.Null(Find<FrameworkElement>(table, "UsageDailyRow_2026-10-01"));
                Assert.Empty(FindDescendants<ScrollViewer>(table));
                Assert.True(double.IsNaN(table.Height));
                Assert.True(table.ActualHeight >= rows.Sum(row => row.ActualHeight));
                Assert.Equal("$3.56", Get<TextBlock>(table, "UsageDailyRowValue_2026-10-30").Text);
                await VisualTestCapture.CaptureAsync(ui.Container, $"Usage-designer-{theme}-1400-30day-table");
                gateway.Controller.SetMetric(UsageMetric.Tokens);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find<TextBlock>(host, "UsageDailyRowValue_2026-10-30")?.Text == "2.8K",
                "Daily table follows the Tokens metric"));
        }, theme: theme, pageWidth: 1400, today: end);
    }

    [NativeKeyboardTheory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task ChartArrowKeys_SelectAndFocusAdjacentDaysWithoutLeavingTheChart(ElementTheme theme)
    {
        await WithUsagePageAsync(Report(false), new RecordingCommands(), async (host, _) =>
        {
            await WaitForAsync(host, "UsageDailyChart");
            var window = WinRT.Interop.WindowNative.GetWindowHandle(ui.TestWindow);
            await NativeKeyboardProof.ActivateAsync(window);
            Button first = null!;
            Button second = null!;
            await ui.RunOnUIAsync(() =>
            {
                var bars = FindDescendants<Button>(Get<Grid>(host, "UsageDailyChart")).ToArray();
                Assert.Equal(7, bars.Length);
                first = bars[0];
                second = bars[1];
                Assert.All(bars, bar => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(bar))));
                Assert.True(first.Focus(FocusState.Keyboard));
            });
            NativeKeyboardProof.Press(window, Windows.System.VirtualKey.Right);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => ReferenceEquals(FocusManager.GetFocusedElement(host.XamlRoot), second)
                    && AutomationProperties.GetItemStatus(second) == LocalizationHelper.GetString("Usage_Selected"),
                "Right arrow selects and focuses the second day"));
            NativeKeyboardProof.Press(window, Windows.System.VirtualKey.Left);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => ReferenceEquals(FocusManager.GetFocusedElement(host.XamlRoot), first)
                    && AutomationProperties.GetItemStatus(first) == LocalizationHelper.GetString("Usage_Selected"),
                "Left arrow returns selection and focus to the first day"));
            NativeKeyboardProof.Press(window, Windows.System.VirtualKey.Left);
            await ui.RunOnUIAsync(() => Assert.Same(first, FocusManager.GetFocusedElement(host.XamlRoot)));
        }, theme: theme, pageWidth: 1400);
    }

    [Fact]
    public async Task HeadlinePartialReasonAndSessionCountsAreExplicitBesideTheirValues()
    {
        var report = Report(false, sessionCount: 1500) with { Partial = new("time-budget", "Reading stopped early.") };
        await WithUsagePageAsync(report, new RecordingCommands(), async (host, _) =>
        {
            await WaitForAsync(host, "UsageIncompleteReason");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("Reading stopped early.", Get<TextBlock>(host, "UsageIncompleteReason").Text);
                Assert.Contains("1,500", Get<TextBlock>(host, "UsageSubtitle").Text);
                Assert.Equal("3 shown · 3 loaded", Get<TextBlock>(host, "UsageSessionsShownLoaded").Text);
                Assert.Contains(FindDescendants<TextBlock>(Get<Border>(host, "UsageSessionsCount")), text => text.Text == "3");
            });
        });
    }

    [Fact]
    public async Task ContributorExpandersKeepIndependentStateAndDoNotScrollOnOrdinaryExpansion()
    {
        await WithUsagePageAsync(Report(false), new RecordingCommands(), async (host, _) =>
        {
            await WaitForAsync(host, "UsageContributor_Agent_main");
            await ui.RunOnUIAsync(() =>
            {
                var scroll = Get<ScrollViewer>(host, "UsagePageRoot");
                var before = scroll.VerticalOffset;
                var main = Get<Expander>(host, "UsageContributor_Agent_main");
                var research = Get<Expander>(host, "UsageContributor_Agent_research");
                main.IsExpanded = true;
                research.IsExpanded = true;
                Assert.True(main.IsExpanded);
                Assert.True(research.IsExpanded);
                Assert.Equal(before, scroll.VerticalOffset);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(Get<Expander>(host, "UsageContributor_Agent_main").IsExpanded);
                Assert.True(Get<Expander>(host, "UsageContributor_Agent_research").IsExpanded);
            });
        });
    }

    [Fact]
    public async Task SessionExpandersKeepLoadedSummariesOpenWhenAnotherDetailIsSelected()
    {
        await WithUsagePageAsync(Report(false), new RecordingCommands(), async (host, gateway) =>
        {
            await WaitForAsync(host, "UsageSession_agent:main:webchat:main");
            await ui.RunOnUIAsync(() => Invoke(Get<Button>(host, "UsageSession_agent:main:webchat:main")));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Get<Expander>(host, "UsageSessionExpander_agent:main:webchat:main").IsExpanded,
                "First session expanded"));
            await ui.RunOnUIAsync(() => Invoke(Get<Button>(host, "UsageSession_agent:research:discord:42")));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => gateway.Controller.State.Detail?.Key == "agent:research:discord:42"
                    && Get<Expander>(host, "UsageSessionExpander_agent:research:discord:42").IsExpanded,
                "Second detail selected without closing first"));
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(Get<Expander>(host, "UsageSessionExpander_agent:main:webchat:main").IsExpanded);
                Assert.NotNull(Find<FrameworkElement>(host, "UsageSessionDetails_agent:main:webchat:main"));
            });
        });
    }

    [Theory]
    [InlineData(480, 2)]
    [InlineData(520, 2)]
    [InlineData(576, 3)]
    public async Task NarrowHost_SummaryFactsWrapWithoutExceedingTheActualCardWidth(double width, int columns)
    {
        await WithUsagePageAsync(Report(false), new RecordingCommands(), async (host, _) =>
        {
            await WaitForAsync(host, "UsageDailyFacts");
            await ui.RunOnUIAsync(() =>
            {
                var facts = Get<Grid>(host, "UsageDailyFacts");
                Assert.Equal(columns, facts.ColumnDefinitions.Count);
                Assert.Equal(3, facts.Children.Count);
                foreach (FrameworkElement fact in facts.Children)
                {
                    var bounds = fact.TransformToVisual(facts).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, fact.ActualWidth, fact.ActualHeight));
                    Assert.True(bounds.Left >= 0 && bounds.Right <= facts.ActualWidth + 1,
                        $"Summary fact extends beyond {facts.ActualWidth} DIP: {bounds}");
                }
                AssertUnclippedText(facts);
            });
        }, pageWidth: width);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingGatewayCounters_RenderUnavailableRatherThanInventedZeroOrEmptySuccess(bool reportedSessionCount)
    {
        using var json = System.Text.Json.JsonDocument.Parse(reportedSessionCount ? """{"overview":{"total":3}}""" : "{}");
        var report = UsageJsonParser.ParseSessionsUsage(json.RootElement);
        await WithUsagePageAsync(report, new RecordingCommands(), async (host, _) =>
        {
            await WaitForAsync(host, "UsageSummaryTokensValue");
            await ui.RunOnUIAsync(() =>
            {
                foreach (var id in new[] { "UsageSummaryCostValue", "UsageSummaryTokensValue",
                    "UsageStatMessagesValue", "UsageStatToolCallsValue", "UsageStatErrorsValue" })
                    Assert.Equal(UsageUi.Dash, Get<TextBlock>(host, id).Text);
                Assert.Equal(reportedSessionCount ? UsageUi.Count(3) : UsageUi.Dash, Get<TextBlock>(host, "UsageSummarySessionsValue").Text);
                Assert.Null(Find<FrameworkElement>(host, "UsageEmptyState"));
                Assert.Null(Find<FrameworkElement>(host, "UsageDailyAverageCaption"));
                Assert.Equal(LocalizationHelper.GetString("Usage_Daily_NotLoaded"), Get<TextBlock>(host, "UsageSessionsEmpty").Text);
            });
        });
    }

    [Theory]
    [InlineData(false, "totalCost", "totalTokens")]
    [InlineData(true, "totalTokens", "totalCost")]
    public async Task IncompleteCounters_DoNotInventChartTooltipValuesContributorSharesOrComputingStatus(
        bool tokensMetric, string dailyField, string contributorField)
    {
        var date = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var json = System.Text.Json.JsonDocument.Parse($$$"""
            {"sessions":[{"key":"agent:main:missing"}],"totals":{"totalTokens":12,"totalCost":12},
             "aggregates":{"byAgent":[{"agentId":"main","totals":{"{{{contributorField}}}":12}}],
             "costDaily":[{"date":"{{{date}}}","{{{dailyField}}}":12}]}}
            """);
        var report = UsageJsonParser.ParseSessionsUsage(json.RootElement);
        await WithUsagePageAsync(report, new RecordingCommands(), async (host, gateway) =>
        {
            await ui.RunOnUIAsync(() => gateway.Controller.SetMetric(tokensMetric ? UsageMetric.Tokens : UsageMetric.Cost));
            await WaitForAsync(host, "UsageContributor_Agent_main");
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Null(Find<FrameworkElement>(host, "UsageContributorShares"));
                var contributor = Get<Expander>(host, "UsageContributor_Agent_main");
                var header = Assert.IsType<Grid>(contributor.Header);
                var missingValue = Assert.Single(FindDescendants<TextBlock>(header), text => text.Text == UsageUi.Dash);
                Assert.Equal(UsageUi.Dash, ToolTipService.GetToolTip(missingValue));
                var day = Get<Button>(host, "UsageDailyChart_" + date);
                Assert.Contains(UsageUi.Dash, AutomationProperties.GetName(day));
                var row = Get<Grid>(host, "UsageRowLayout_agent:main:missing");
                Assert.Contains(FindDescendants<TextBlock>(row), text => text.Text == UsageUi.Dash);
                Assert.DoesNotContain(FindDescendants<TextBlock>(row),
                    text => text.Text == LocalizationHelper.GetString("Usage_Sessions_Computing"));
            });
        });
    }

    [Theory]
    [InlineData(1400, 1200)]
    [InlineData(1000, 936)]
    public async Task MastBackdrop_StaysAtTheViewportTopWhileCentredContentScrolls(double width, double contentWidth)
    {
        await WithUsagePageAsync(Report(serverOverview: false), new RecordingCommands(), async (host, _) =>
        {
            await WaitForAsync(host, "UsageDaily");
            await ui.RunOnUIAsync(() =>
            {
                var backdrop = Get<Border>(host, "UsageMastBackdrop");
                var content = Get<StackPanel>(host, "UsageContentColumn");
                Assert.InRange(Math.Abs(backdrop.ActualWidth - host.ActualWidth), 0, 0.1);
                Assert.InRange(Math.Abs(backdrop.TransformToVisual(host).TransformPoint(new()).X), 0, 0.1);
                Assert.InRange(Math.Abs(Get<StackPanel>(host, "UsageBody").ActualWidth - contentWidth), 0, 0.1);
                Assert.InRange(Math.Abs(content.TransformToVisual(host).TransformPoint(new()).X -
                    (width - content.ActualWidth) / 2), 0, 0.1);
                Assert.InRange(Math.Abs(backdrop.ActualHeight - 320), 0, 0.1);
                Assert.False(backdrop.IsHitTestVisible);
                Assert.InRange(Math.Abs(backdrop.TransformToVisual(host).TransformPoint(new()).Y), 0, 0.1);
                Assert.Equal(32, content.Padding.Left);
            });
            await ui.RunOnUIAsync(() =>
            {
                var scroll = Get<ScrollViewer>(host, "UsagePageRoot");
                Assert.True(scroll.ChangeView(null, 200, null, disableAnimation: true));
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                Get<ScrollViewer>(host, "UsagePageRoot").VerticalOffset >= 199 &&
                Get<StackPanel>(host, "UsageContentColumn").TransformToVisual(host).TransformPoint(new()).Y < -199,
                "Usage content scrolling beneath its viewport backdrop"));
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var backdrop = Get<Border>(host, "UsageMastBackdrop");
                var content = Get<StackPanel>(host, "UsageContentColumn");
                Assert.InRange(Math.Abs(backdrop.TransformToVisual(host).TransformPoint(new()).Y), 0, 0.1);
                Assert.InRange(Math.Abs(backdrop.ActualWidth - host.ActualWidth), 0, 0.1);
                Assert.True(content.TransformToVisual(host).TransformPoint(new()).Y < -199);
                Assert.True(Get<FrameworkElement>(host, "UsageHeader").TransformToVisual(host).TransformPoint(new()).Y < 0);
            });
        }, theme: ElementTheme.Light, pageWidth: width);
    }

    [Theory]
    [InlineData(1400, 1200, 2, 1)]
    [InlineData(1000, 936, 2, 1)]
    [InlineData(760, 696, 1, 2)]
    [InlineData(520, 480, 1, 3)]
    public async Task Resizing_ReflowsMountedColumnsActionsAndTextWithoutOverlap(
        double width, double contentWidth, int columns, int filterRows)
    {
        var rows = NamedRows();
        var longTitle = new string('W', 160);
        rows[1] = rows[1] with { Label = longTitle };
        await WithUsagePageAsync(Report(serverOverview: true, rows), new RecordingCommands(), async (host, gateway) =>
        {
            await WaitForAsync(host, "UsageSession_agent:research:discord:42");
            await ui.RunOnUIAsync(() => Invoke(Get<Button>(host, "UsageSession_agent:research:discord:42")));
            await WaitForAsync(host, "UsageDetailLayout");
            await ui.RunOnUIAsync(() => host.Width = width);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                Math.Abs(host.ActualWidth - width) < 0.1 &&
                Math.Abs(Get<Grid>(host, "UsageStage").ActualWidth - contentWidth) < 0.1 &&
                Get<Grid>(host, "UsageStage").ColumnDefinitions.Count == columns, "Responsive Usage reflow"));
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => gateway.ExportWorkflow.ExportAsync(UsageExportFormat.DailyCsv));
            await WaitForAsync(host, "UsageExportToast");
            await ui.RunOnUIAsync(async () =>
            {
                foreach (var id in new[] { "UsageStage", "UsageAttentionGrid", "UsageContributorLayout", "UsageDetailLayout" })
                    Assert.Equal(columns, Get<Grid>(host, id).ColumnDefinitions.Count);
                Assert.Equal(filterRows, Get<Grid>(host, "UsageFilterRow").RowDefinitions.Count);
                var attention = Get<Border>(host, "UsageAttentionCard_anthropic");
                var heading = Get<TextBlock>(host, "UsageNeedsHeading");
                Assert.InRange(Math.Abs(attention.TransformToVisual(host).TransformPoint(new()).X -
                    heading.TransformToVisual(host).TransformPoint(new()).X), 0, 1);
                var warningTitle = Get<TextBlock>(host, "UsageAttentionTitle_anthropic");
                var warningMessage = Get<TextBlock>(host, "UsageAttentionMessage_anthropic");
                Assert.InRange(Math.Abs(warningTitle.TransformToVisual(attention).TransformPoint(new()).X -
                    warningMessage.TransformToVisual(attention).TransformPoint(new()).X), 0, 1);
                var cardWidth = columns == 2 ? (contentWidth - 12) / 2 : contentWidth;
                Assert.InRange(Math.Abs(attention.ActualWidth - cardWidth), 0, 1);
                foreach (var id in new[] { "UsageAttentionIcon_anthropic", "UsageAttentionTitle_anthropic", "UsageInspectProvider_anthropic" })
                {
                    var item = Get<FrameworkElement>(host, id);
                    var rect = item.TransformToVisual(attention).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, item.ActualWidth, item.ActualHeight));
                    Assert.True(rect.Top >= 8 && rect.Bottom <= attention.ActualHeight - 8,
                        $"{id} needs vertical card padding, rect {rect}, card height {attention.ActualHeight}.");
                }
                var title = Get<TextBlock>(host, "UsageRowTitle_agent:research:discord:42");
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                Assert.True(title.IsTextTrimmed);
                Assert.Equal(longTitle, ToolTipService.GetToolTip(title));
                foreach (var id in new[] { "UsageMast", "UsageFilterRow", "UsageStage", "UsageAttentionGrid",
                    "UsageDailyControls", "UsageDailyFacts", "UsageActivity", "UsageContributorLayout", "UsageDetailLayout" })
                    AssertSeparatedChildren(Get<Grid>(host, id));
                var activity = Get<Grid>(host, "UsageActivity");
                Assert.Equal(2, activity.ColumnDefinitions.Count);
                Assert.Equal(2, activity.RowDefinitions.Count);
                Assert.Equal(4, activity.Children.Count);
                Assert.All(FindDescendants<TextBlock>(activity), text => Assert.Equal(TextWrapping.NoWrap, text.TextWrapping));
                foreach (var label in FindDescendants<TextBlock>(activity).Where(text =>
                    !AutomationProperties.GetAutomationId(text).EndsWith("Value", StringComparison.Ordinal)))
                {
                    Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
                    Assert.Equal(label.Text, ToolTipService.GetToolTip(label));
                }
                if (Find<Grid>(host, "UsageDailyMetricRow") is { } metricRow)
                {
                    AssertSeparatedChildren(metricRow);
                    var metric = Get<SelectorBar>(host, "UsageMetric");
                    var firstLabel = Assert.Single(FindDescendants<TextBlock>(metric),
                        text => text.Text == LocalizationHelper.GetString("Usage_Summary_EstimatedCost"));
                    var perDay = Get<TextBlock>(host, "UsagePerDayTitle");
                    Assert.InRange(Math.Abs(firstLabel.TransformToVisual(host).TransformPoint(new()).X -
                        perDay.TransformToVisual(host).TransformPoint(new()).X), 0, 1);
                }
                var chart = Get<Grid>(host, "UsageDailyChart");
                Assert.Null(Find<FrameworkElement>(host, "UsageDailyAverageLegend"));
                Assert.Equal(AutomationProperties.GetHelpText(chart), Get<TextBlock>(host, "UsageDailyAverageLabel").Text);
                var marks = FindDescendants<FrameworkElement>(chart).Where(element =>
                    AutomationProperties.GetAutomationId(element).Contains("_mark_", StringComparison.Ordinal))
                    .Select(element => element.TransformToVisual(chart).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight))).ToArray();
                Assert.NotEmpty(marks);
                foreach (var text in FindDescendants<TextBlock>(chart))
                {
                    var rect = text.TransformToVisual(chart).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, text.ActualWidth, text.ActualHeight));
                    Assert.DoesNotContain(marks, mark =>
                        Math.Min(rect.Right, mark.Right) - Math.Max(rect.Left, mark.Left) > 1 &&
                        Math.Min(rect.Bottom, mark.Bottom) - Math.Max(rect.Top, mark.Top) > 1);
                }
                foreach (var row in FindDescendants<Grid>(host).Where(grid =>
                    AutomationProperties.GetAutomationId(grid).StartsWith("UsageRowLayout_", StringComparison.Ordinal)))
                    AssertSeparatedChildren(row);
                var actions = Get<StackPanel>(host, "UsageMastActions");
                var buttons = FindDescendants<Button>(actions).Where(button => button.ActualWidth > 0).ToArray();
                Assert.Equal(2, buttons.Length);
                var y = buttons[0].TransformToVisual(actions).TransformPoint(new()).Y + buttons[0].ActualHeight / 2;
                Assert.All(buttons, button => Assert.InRange(
                    Math.Abs(button.TransformToVisual(actions).TransformPoint(new()).Y + button.ActualHeight / 2 - y), 0, 1));
                AssertUnclippedText(host);
                await VisualTestCapture.CaptureAsync(host, $"Usage-Light-responsive-{width:0}");
            });
            await ui.RunOnUIAsync(() => host.Width = 1300);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                Get<Grid>(host, "UsageStage").ColumnDefinitions.Count == 2 &&
                Get<Grid>(host, "UsageFilterRow").RowDefinitions.Count == 1, "Usage reflow back to wide"));
        }, theme: ElementTheme.Light, pageWidth: 1400);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomRange_KeepsCompactMastAndInlineDatesOnWideHostsAcrossReflow(bool serverOverview)
    {
        await WithUsagePageAsync(Report(serverOverview), new RecordingCommands(), async (host, gateway) =>
        {
            await WaitForAsync(host, "UsageDaily");
            var start = new DateOnly(2026, 10, 30);
            var end = new DateOnly(2026, 11, 5);
            await ui.RunOnUIAsync(() => gateway.Controller.SetRange(start, end));
            await WaitForAsync(host, "UsageCustomDates");
            // Custom dates share the wide preset row. Below wide they follow the presets/options and,
            // on overview Gateways, the search row. These are Custom counts, not the shorter preset layout.
            foreach (var (width, contentWidth, columns, basicRows, overviewRows) in
                new (double Width, double ContentWidth, int Columns, int BasicRows, int OverviewRows)[]
                {
                    (1400, 1200, 3, 1, 2),
                    (1000, 936, 3, 1, 2),
                    (760, 696, 2, 2, 3),
                    (520, 480, 1, 3, 4),
                    (1000, 936, 3, 1, 2),
                })
            {
                var filterRows = serverOverview ? overviewRows : basicRows;
                var dateRow = width >= 1000 ? 0 : filterRows - 1;
                var dateColumn = width >= 1000 ? 1 : 0;
                string LayoutState() =>
                    $"preset={gateway.Controller.State.Filters.Preset}; " +
                    $"range={gateway.Controller.State.Filters.Start:yyyy-MM-dd}/{gateway.Controller.State.Filters.End:yyyy-MM-dd}; " +
                    $"phase={gateway.Controller.State.Phase}; overview={gateway.Controller.State.Report?.Overview is not null}; " +
                    $"expectedRows={filterRows}; expectedColumns={columns}; expectedDateRow={dateRow}; {FilterRowState(host)}";
                await ui.RunOnUIAsync(() => host.Width = width);
                // The header tier is Reactor state that follows the host's SizeChanged, so the host width alone does not
                // show that the filter row has re-rendered and been laid out for this width.
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                        gateway.Controller.State.Phase == UsageLoadPhase.Loaded &&
                        gateway.Controller.State.Filters.Preset == UsagePreset.Custom &&
                        gateway.Controller.State.Filters.Start == start && gateway.Controller.State.Filters.End == end &&
                        (gateway.Controller.State.Report?.Overview is not null) == serverOverview &&
                        HasLayoutWidth(host, width) &&
                        Find<Grid>(host, "UsageFilterRow") is { } filterRow &&
                        HasLayoutWidth(filterRow, contentWidth) &&
                        filterRow.RowDefinitions.Count == filterRows && filterRow.ColumnDefinitions.Count == columns &&
                        Find<Grid>(host, "UsageCustomDates") is { ActualHeight: > 0 } dates &&
                        Microsoft.UI.Xaml.Controls.Grid.GetRow(dates) == dateRow &&
                        Microsoft.UI.Xaml.Controls.Grid.GetColumn(dates) == dateColumn &&
                        Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(dates) == (width >= 1000 ? 1 : columns),
                    $"custom-range filter row at {width} DIP", LayoutState));
                await ui.YieldToRenderAsync();
                await ui.RunOnUIAsync(() =>
                {
                    var state = LayoutState();
                    var row = Get<Grid>(host, "UsageFilterRow");
                    var presets = Get<SelectorBar>(host, "UsagePresets");
                    var dates = Get<Grid>(host, "UsageCustomDates");
                    var options = Get<StackPanel>(host, "UsageFilterOptions");
                    Windows.Foundation.Rect Bounds(FrameworkElement element) =>
                        element.TransformToVisual(row).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
                    var presetBounds = Bounds(presets);
                    var dateBounds = Bounds(dates);
                    var optionsBounds = Bounds(options);
                    AssertSeparatedChildren(row);
                    if (width >= 1000)
                    {
                        Assert.InRange(Math.Abs(dateBounds.Top + dateBounds.Height / 2 -
                            (presetBounds.Top + presetBounds.Height / 2)), 0, 1);
                        Assert.True(dateBounds.Left >= presetBounds.Right, $"Dates {dateBounds}, presets {presetBounds}. {state}");
                        Assert.InRange(dateBounds.Left - presetBounds.Right, 0, 13);
                        Assert.True(dateBounds.Right <= optionsBounds.Left, $"Dates {dateBounds}, options {optionsBounds}. {state}");
                        var amount = Get<TextBlock>(host, "UsageSummaryCostValue");
                        var range = Get<TextBlock>(host, "UsageSummaryRange");
                        Assert.False(range.IsTextTrimmed);
                        Assert.InRange(range.ActualHeight, 1, amount.ActualHeight + 1);
                        Assert.DoesNotContain("2026", range.Text);
                        Assert.Contains("2026", Assert.IsType<string>(ToolTipService.GetToolTip(range)));
                    }
                    else
                    {
                        Assert.True(dateBounds.Top >= presetBounds.Bottom, $"Dates {dateBounds}, presets {presetBounds}. {state}");
                    }
                    if (serverOverview)
                    {
                        var queryBounds = Bounds(Get<AutoSuggestBox>(host, "UsageQuery"));
                        Assert.True(width >= 1000 ? queryBounds.Top >= dateBounds.Bottom : dateBounds.Top >= queryBounds.Bottom,
                            $"Dates {dateBounds}, query {queryBounds}. {state}");
                    }
                    Assert.True(dateBounds.Left >= 0 && dateBounds.Right <= row.ActualWidth + 1, $"Dates {dateBounds}. {state}");
                });
            }
        }, theme: ElementTheme.Light, pageWidth: 1400);
    }

    [Theory]
    [InlineData(1400, false)]
    [InlineData(1400, true)]
    [InlineData(937.14, false)]
    [InlineData(937.14, true)]
    public async Task DailyHeader_SharesTextCentresOnFreshMountAndAfterCompactTransition(double width, bool transition)
    {
        await WithUsagePageAsync(Report(serverOverview: false), new RecordingCommands(), async (host, _) =>
        {
            await WaitForAsync(host, "UsageDailyAverageCaption");
            if (transition)
            {
                await WaitForAsync(host, "UsageDailyMetricRow");
                double previousWidth = 0;
                await ui.RunOnUIAsync(() =>
                {
                    Assert.True(host.UseLayoutRounding);
                    previousWidth = host.ActualWidth;
                    host.Width = width;
                });
                // Layout settles on whole physical pixels: 937.14 DIP lays out as 937 DIP at 100% scale.
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                        HasLayoutWidth(host, width) && Find<Grid>(host, "UsageDailyMetricRow") is null,
                    "non-compact daily header after resize", () =>
                        $"previous={previousWidth:R}; requested={width:R}; configured={host.Width:R}; " +
                        $"actual={host.ActualWidth:R}; scale={host.XamlRoot?.RasterizationScale:R}; " +
                        $"layoutRounding={host.UseLayoutRounding}; " +
                        $"daily={Find<FrameworkElement>(host, "UsageDaily")?.ActualWidth:R}; " +
                        $"metricRowPresent={Find<Grid>(host, "UsageDailyMetricRow") is not null}; " +
                        $"renderError={Find<TextBlock>(host, "UsageRenderError")?.Text ?? "none"}"));
            }
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var header = Get<Grid>(host, "UsageDailyControls");
                var metric = Get<SelectorBar>(host, "UsageMetric");
                var view = Get<SelectorBar>(host, "UsageChartView");
                var metricText = Assert.Single(FindDescendants<TextBlock>(metric),
                    text => text.Text == LocalizationHelper.GetString("Usage_Summary_EstimatedCost"));
                var viewText = Assert.Single(FindDescendants<TextBlock>(view),
                    text => text.Text == LocalizationHelper.GetString("Usage_Chart"));
                var title = Get<TextBlock>(host, "UsagePerDayTitle");
                double Centre(FrameworkElement element) =>
                    element.TransformToVisual(header).TransformPoint(new()).Y + element.ActualHeight / 2;
                var diagnostics = $"Header={header.ActualHeight}; metric centre={Centre(metricText)}, height={metric.ActualHeight}; " +
                    $"view centre={Centre(viewText)}, height={view.ActualHeight}; title={Centre(title)}; " +
                    $"groups={string.Join(';', header.Children.OfType<FrameworkElement>().Select(child => $"{child.GetType().Name} margin={child.Margin} align={child.VerticalAlignment}"))}";
                output.WriteLine(diagnostics);
                Assert.True(Math.Abs(Centre(metricText) - Centre(viewText)) <= 1, diagnostics);
                Assert.True(Math.Abs(Centre(metricText) - Centre(title)) <= 2, diagnostics);
                Assert.True(header.ActualHeight <= Math.Max(metric.ActualHeight, view.ActualHeight) + 1, diagnostics);
            });
        }, theme: ElementTheme.Light, pageWidth: transition ? 760 : width);
    }

    [Theory]
    [InlineData(1400)]
    [InlineData(1000)]
    [InlineData(760)]
    [InlineData(520)]
    public async Task CardLists_KeepEightDipGapsBeforeAndAfterSessionExpansion(double width)
    {
        await WithUsagePageAsync(Report(serverOverview: false), new RecordingCommands(), async (host, _) =>
        {
            await WaitForAsync(host, "UsageSession_agent:main:webchat:main");
            await ui.RunOnUIAsync(() =>
            {
                AssertCardGaps(host, "UsageSessionCard_");
                AssertCardGaps(host, "UsageProvider_");
                Invoke(Get<Button>(host, "UsageSession_agent:main:webchat:main"));
            });
            await WaitForAsync(host, "UsageSessionDetails");
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                AssertCardGaps(host, "UsageSessionCard_");
                AssertCardGaps(host, "UsageProvider_");
                Assert.True(Get<Border>(host, "UsageSessionCard_agent:main:webchat:main").ActualHeight >
                    Get<Border>(host, "UsageSessionCard_agent:ops:slack:C01").ActualHeight);
                Assert.True(Get<Button>(host, "UsageChat_agent:main:webchat:main").IsEnabled);
            });
        }, theme: ElementTheme.Light, pageWidth: width);
    }

    private static void AssertCardGaps(FrameworkElement host, string prefix)
    {
        var cards = FindDescendants<Border>(host).Where(card =>
            AutomationProperties.GetAutomationId(card).StartsWith(prefix, StringComparison.Ordinal))
            .Select(card => (Card: card, Bounds: card.TransformToVisual(host).TransformBounds(
                new Windows.Foundation.Rect(0, 0, card.ActualWidth, card.ActualHeight))))
            .OrderBy(item => item.Bounds.Top).ToArray();
        Assert.True(cards.Length >= 3);
        foreach (var item in cards)
        {
            Assert.NotNull(item.Card.Background);
            Assert.NotNull(item.Card.BorderBrush);
            Assert.Equal(new Thickness(1), item.Card.BorderThickness);
            Assert.Equal(new CornerRadius(8), item.Card.CornerRadius);
        }
        for (var index = 1; index < cards.Length; index++)
            Assert.InRange(Math.Abs(cards[index].Bounds.Top - cards[index - 1].Bounds.Bottom - 8), 0, 0.1);
    }

    // WinUI lays elements out on whole physical pixels, so compare widths at the raster scale instead of in DIPs.
    private static bool HasLayoutWidth(FrameworkElement element, double width) =>
        element.XamlRoot is { } root &&
        NativeProofLayout.PhysicalPixels(element.ActualWidth, root.RasterizationScale) ==
        NativeProofLayout.PhysicalPixels(width, root.RasterizationScale);

    private static string FilterRowState(FrameworkElement host)
    {
        var row = Find<Grid>(host, "UsageFilterRow");
        string Place(string id) => Find<FrameworkElement>(host, id) is { } element
            ? $"{id} row={Microsoft.UI.Xaml.Controls.Grid.GetRow(element)} column={Microsoft.UI.Xaml.Controls.Grid.GetColumn(element)} " +
                $"span={Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(element)} size={element.ActualWidth:R}x{element.ActualHeight:R}"
            : $"{id} missing";
        return $"requested={host.Width:R}; host={host.ActualWidth:R}; scale={host.XamlRoot?.RasterizationScale:R}; " +
            $"filterRow={row?.ActualWidth:R} " +
            $"rows={row?.RowDefinitions.Count} columns={row?.ColumnDefinitions.Count}; " +
            string.Join("; ", new[] { "UsagePresets", "UsageCustomDates", "UsageFilterOptions", "UsageQuery" }.Select(Place)) +
            $"; renderError={Find<TextBlock>(host, "UsageRenderError")?.Text ?? "none"}";
    }

    private static void AssertSeparatedChildren(Grid grid)
    {
        var children = grid.Children.OfType<FrameworkElement>()
            .Where(child => child.Visibility == Visibility.Visible && child.ActualWidth > 0 && child.ActualHeight > 0)
            .Select(child => (Child: child, Rect: child.TransformToVisual(grid).TransformBounds(
                new Windows.Foundation.Rect(0, 0, child.ActualWidth, child.ActualHeight)))).ToArray();
        foreach (var child in children)
        {
            Assert.True(child.Rect.Left >= -1 && child.Rect.Right <= grid.ActualWidth + 1,
                $"{AutomationProperties.GetAutomationId(grid)} clips {AutomationProperties.GetAutomationId(child.Child)}: {child.Rect} in {grid.ActualWidth}.");
            foreach (var other in children.Where(other => !ReferenceEquals(other.Child, child.Child)))
                Assert.False(Math.Min(child.Rect.Right, other.Rect.Right) - Math.Max(child.Rect.Left, other.Rect.Left) > 1 &&
                    Math.Min(child.Rect.Bottom, other.Rect.Bottom) - Math.Max(child.Rect.Top, other.Rect.Top) > 1,
                    $"{AutomationProperties.GetAutomationId(grid)} overlaps children: {child.Rect}, {other.Rect}.");
        }
    }

    private static void AssertUnclippedText(FrameworkElement host)
    {
        foreach (var text in FindDescendants<TextBlock>(host).Where(text => text.ActualWidth > 0 && text.ActualHeight > 0 &&
            text.Visibility == Visibility.Visible && !string.IsNullOrEmpty(text.Text) && text.TextTrimming == TextTrimming.None))
        {
            var actual = new Windows.Foundation.Size(text.ActualWidth, text.ActualHeight);
            try
            {
                text.Measure(new Windows.Foundation.Size(
                    text.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : actual.Width + 1, double.PositiveInfinity));
                Assert.True(text.DesiredSize.Width - text.Margin.Left - text.Margin.Right <= actual.Width + 1 &&
                    text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom <= actual.Height + 1,
                    $"Text clips without ellipsis: '{text.Text}' ({AutomationProperties.GetAutomationId(text)}), needs {text.DesiredSize}, margin {text.Margin}, has {actual}.");
            }
            finally
            {
                text.Measure(actual);
            }
        }
    }

    [Fact]
    public async Task SessionDetails_OpenNativeChatForThatSessionAndTheUsageRouteThroughAppCommands()
    {
        var commands = new RecordingCommands();
        await WithUsagePageAsync(Report(serverOverview: true), commands, async (host, gateway) =>
        {
            await WaitForAsync(host, "UsageSession_agent:research:discord:42");
            await ui.RunOnUIAsync(() =>
            {
                // The refresh slot swaps a progress indicator for a button; it must not inherit the browser action's help.
                var refresh = Get<Button>(host, "UsageRefresh");
                Assert.NotEqual(LocalizationHelper.GetString("Usage_OpenWebHint"), AutomationProperties.GetHelpText(refresh));
                Assert.NotNull(Find<FrameworkElement>(host, "UsageQuery"));
                Assert.NotNull(Find<FrameworkElement>(host, "UsageSessionsSort"));
                Invoke(Get<Button>(host, "UsageSession_agent:research:discord:42"));
            });
            await WaitForAsync(host, "UsageSessionDetails");
            await ui.RunOnUIAsync(async () =>
            {
                var detailRead = Assert.Single(gateway.Queries, q => q.Key is not null);
                Assert.Equal(("agent:research:discord:42", "research"), (detailRead.Key, detailRead.AgentId));
                Assert.Null(Find<FrameworkElement>(host, "UsageDetailTitle"));
                Assert.NotNull(Find<FrameworkElement>(host, "UsageDetailCostValue"));
                Assert.Null(Find<FrameworkElement>(host, "UsageDetailOpenWeb"));
                foreach (var key in new[] { "Usage_Facet_model", "Usage_Detail_InputOutput", "Usage_Detail_CacheSplit",
                    "Usage_Overview_Messages", "Usage_Overview_ToolCalls", "Usage_Overview_Errors", "Usage_Details_Duration",
                    "Usage_Summary_EstimatedCost", "Usage_Providers_Title", "Usage_Contributors_Title" })
                    Assert.Contains(FindDescendants<TextBlock>(host), text => text.Text == LocalizationHelper.GetString(key));
                Assert.Equal(LocalizationHelper.GetString("Usage_PerDay"), Get<TextBlock>(host, "UsagePerDayTitle").Text);
                Invoke(Get<Button>(host, "UsageChat_agent:research:discord:42"));
                Invoke(Get<Button>(host, "UsageOpenWeb"));
                await VisualTestCapture.CaptureAsync(host, "UsagePage-ServerOverview");
            });
        });

        Assert.Equal(["agent:research:discord:42"], commands.PendingChatKeys);
        Assert.Equal(["chat"], commands.Navigations);
        // Only the route: no range, filter or session is appended for the web page.
        Assert.Equal(["usage"], commands.DashboardPaths);
    }

    [Fact]
    public async Task BasicGateway_IsTheNormalPageWithReportedCoverageAndWorkingRecoveryAndHandoff()
    {
        var commands = new RecordingCommands();
        await WithUsagePageAsync(Report(serverOverview: false, sessionCount: 1500), commands, async (host, gateway) =>
        {
            await WaitForAsync(host, "UsageSessionsTruncated");
            await ui.RunOnUIAsync(() =>
            {
                // The current stable Gateway is not a degraded state: no update banner, just a newest-first list.
                Assert.Null(Find<FrameworkElement>(host, "UsageUnsupportedPanel"));
                Assert.DoesNotContain(FindDescendants<InfoBar>(host), bar => bar.IsOpen && bar.Visibility == Visibility.Visible);
                Assert.NotNull(Find<FrameworkElement>(host, "UsageSessionsNewestFirst"));
                Assert.NotNull(Find<FrameworkElement>(host, "UsageContributors"));
                Assert.NotNull(Find<FrameworkElement>(host, "UsageOptions"));
                Assert.Null(Find<FrameworkElement>(host, "UsageTimeZone"));
                Assert.Null(Find<FrameworkElement>(host, "UsageScope"));
                Assert.Null(Find<FrameworkElement>(host, "UsageQuery"));
                Assert.Null(Find<FrameworkElement>(host, "UsageFacetRow"));
                Assert.Null(Find<FrameworkElement>(host, "UsageSessionsSort"));
                Assert.Null(Find<FrameworkElement>(host, "UsageSessionsNext"));
                Assert.Null(Find<FrameworkElement>(host, "UsageSessionsLimitReached"));
                // "N of M" uses the Gateway's reported count, not the loaded rows.
                Assert.Equal(LocalizationHelper.Format("Usage_Sessions_TruncatedFormat", Count(3), Count(1500)),
                    Get<TextBlock>(host, "UsageSessionsTruncated").Text);
                Assert.Equal(Count(1500), Get<TextBlock>(host, "UsageSummarySessionsValue").Text);
                // Newest activity first, because the Gateway applied no sort.
                Assert.Equal(["UsageSession_agent:research:discord:42", "UsageSession_agent:ops:slack:C01", "UsageSession_agent:main:webchat:main"],
                    SessionRowIds(host));
            });
            // Recovery: Refresh issues a new range read on the Basic contract.
            var reads = OverviewReads(gateway);
            await ui.RunOnUIAsync(() => Invoke(Get<Button>(host, "UsageRefresh")));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => OverviewReads(gateway) > reads, "Usage refresh read"));
            await WaitForAsync(host, "UsageRefresh");
            await ui.RunOnUIAsync(async () =>
            {
                Invoke(Get<Button>(host, "UsageOpenWeb"));
                await VisualTestCapture.CaptureAsync(host, "UsagePage-BasicGateway");
            });
        });

        Assert.Equal(["usage"], commands.DashboardPaths);
    }

    [Fact]
    public async Task BasicGateway_AtTheRowLimitWithoutACount_ShowsAFloorInsteadOfAnExactTotal()
    {
        var limit = SessionsUsageQuery.FullRowLimit;
        var rows = Enumerable.Range(0, limit)
            .Select(i => Row($"agent:main:bulk:{i:D4}", $"Bulk {i}", "main", "claude-sonnet-4.5", 1_000, 0.01, Anchor.AddMinutes(-i)))
            .ToArray();
        await WithUsagePageAsync(Report(serverOverview: false, rows, sessionCount: null), new RecordingCommands(), async (host, _) =>
        {
            await WaitForAsync(host, "UsageSessionsLimitReached");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Null(Find<FrameworkElement>(host, "UsageSessionsTruncated"));
                Assert.Equal(LocalizationHelper.Format("Usage_Sessions_LimitReachedFormat", Count(limit)),
                    Get<TextBlock>(host, "UsageSessionsLimitReached").Text);
                Assert.Equal(LocalizationHelper.Format("Usage_AtLeastFormat", Count(limit)),
                    Get<TextBlock>(host, "UsageSummarySessionsValue").Text);
                // The list previews the newest rows; it never claims the loaded page is every session.
                Assert.Equal(UsageSessionsSection.PreviewRows, SessionRowIds(host).Count);
                Assert.Equal("UsageSession_agent:main:bulk:0000", SessionRowIds(host)[0]);
                Invoke(Get<Button>(host, "UsageSessionsShowAll"));
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => SessionRowIds(host).Count == 20, "More loaded rows"));
        });
    }

    [Fact]
    public async Task UnsupportedGateway_AsksForAnUpdateWithoutFiguresAndKeepsProviderPlans()
    {
        var commands = new RecordingCommands();
        var connection = 0;
        await WithUsagePageAsync(new SessionsUsageReport(), commands, async (host, gateway) =>
        {
            await WaitForAsync(host, "UsageUnsupportedPanel");
            await WaitForAsync(host, "UsageProviders");
            await ui.RunOnUIAsync(async () =>
            {
                Assert.Equal(LocalizationHelper.GetString("Usage_Unsupported_Message"),
                    Get<TextBlock>(host, "UsageSubtitle").Text);
                // No figures, including zero-valued stand-ins.
                Assert.Equal(LocalizationHelper.GetString("Usage_Unsupported_Title"), Get<TextBlock>(host, "UsageHeadline").Text);
                Assert.Null(Find<FrameworkElement>(host, "UsageSummaryTokensValue"));
                Assert.Null(Find<FrameworkElement>(host, "UsageSummarySessionsValue"));
                foreach (var absent in new[] { "UsageDaily", "UsageSessions", "UsageContributors", "UsageActivity", "UsageQuery", "UsageErrorBanner" })
                    Assert.Null(Find<FrameworkElement>(host, absent));
                Assert.Null(Find<FrameworkElement>(host, "UsageExport"));
                Invoke(Get<Button>(host, "UsageAccessOpenConnection"));
                await VisualTestCapture.CaptureAsync(host, "UsagePage-Unsupported");
            });
        }, unsupported: true, openConnection: () => connection++);

        Assert.Equal(1, connection);
        Assert.Empty(commands.DashboardPaths);
    }

    [Fact]
    public async Task OptionsChartAndContributorControlsActOnTheMountedPage()
    {
        await WithUsagePageAsync(Report(serverOverview: true), new RecordingCommands(), async (host, gateway) =>
        {
            await WaitForAsync(host, "UsageDaily");
            await ui.RunOnUIAsync(() =>
            {
                var button = Get<Button>(host, "UsageOptions");
                var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
                var radios = flyout.Items.OfType<RadioMenuFlyoutItem>().ToList();
                Assert.Equal(4, radios.Count);
                Assert.Equal(2, radios.Count(item => item.IsChecked));
                Assert.Equal(2, radios.Select(item => item.GroupName).Distinct().Count());
                flyout.ShowAt(button);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var flyout = Assert.IsType<MenuFlyout>(Get<Button>(host, "UsageOptions").Flyout);
                var utc = flyout.Items.OfType<RadioMenuFlyoutItem>()
                    .Single(item => item.Text == LocalizationHelper.GetString("Usage_TimeZone_Utc"));
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(utc);
                Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle)).Toggle();
                flyout.Hide();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => gateway.Controller.State.Filters.TimeZone == UsageTimeZoneMode.Utc, "UTC day boundaries"));
            await ui.RunOnUIAsync(() =>
            {
                var view = Get<SelectorBar>(host, "UsageChartView");
                view.SelectedItem = view.Items[1];
            });
            await WaitForAsync(host, "UsageDailyTable");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Null(Find<FrameworkElement>(host, "UsageDailyChart"));
                Invoke(Get<Button>(host, "UsageInspectProvider_anthropic"));
            });
            await WaitForAsync(host, "UsageContributor_Provider_anthropic");
            await ui.RunOnUIAsync(() =>
            {
                Assert.NotNull(Find<FrameworkElement>(host, "UsageContributorSession_agent:research:discord:42"));
                var kinds = Get<SelectorBar>(host, "UsageContributorsKind");
                kinds.SelectedItem = kinds.Items[0];
            });
            await WaitForAsync(host, "UsageContributor_Agent_research");
            await ui.RunOnUIAsync(() => Get<Expander>(host, "UsageContributor_Agent_research").IsExpanded = true);
            await WaitForAsync(host, "UsageContributorFilter_research");
            await ui.RunOnUIAsync(() => Invoke(Get<Button>(host, "UsageContributorFilter_research")));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => gateway.Queries.Last().Query == "agent:research", "Agent drilldown query"));
        });
    }

    [Theory]
    [InlineData(520, 1)]
    [InlineData(1400, 2)]
    public async Task ResizingAnOpenExportDialog_ReflowsItsFooterAndRetainsFrozenFacts(double width, int columns)
    {
        await WithUsagePageAsync(Report(serverOverview: false, sessionCount: 1500), new RecordingCommands(), async (host, gateway) =>
        {
            await WaitForAsync(host, "UsageSessionsTruncated");
            await OpenExportMenuAsync(host, "Usage_Export_SessionsCsv");
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                ExportPopup(host) is { } popup && Find<Grid>(popup, "UsageExportDialogActions") is not null,
                "Open responsive export dialog"));
            await ui.RunOnUIAsync(() => host.Width = width);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                ExportPopup(host) is { } popup && Get<Grid>(popup, "UsageExportDialogActions").ColumnDefinitions.Count == columns,
                "Export footer reflow"));
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var popup = ExportPopup(host)!;
                var footer = Get<Grid>(popup, "UsageExportDialogActions");
                AssertSeparatedChildren(footer);
                var primary = Get<Button>(popup, "UsageExportDialogPrimary");
                var close = Get<Button>(popup, "UsageExportDialogClose");
                if (columns == 1) Assert.InRange(Math.Abs(primary.ActualWidth - close.ActualWidth), 0, 1);
                Assert.Contains(FindDescendants<TextBlock>(popup), text =>
                    text.Text == LocalizationHelper.Format("Usage_Export_CoverageFormat", "3", "1,500"));
                AssertUnclippedText(Get<FrameworkElement>(popup, "UsageExportBlockedContent"));
                Invoke(close);
                Assert.Empty(gateway.SavedNames);
            });
        }, theme: ElementTheme.Light, pageWidth: 1400);
    }

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task ExportBlock_OffersDailyTotalsThenShowsTheSavedToastAndFolderAction(ElementTheme theme)
    {
        await WithUsagePageAsync(Report(serverOverview: false, sessionCount: 1500), new RecordingCommands(), async (host, gateway) =>
        {
            await WaitForAsync(host, "UsageSessionsTruncated");
            await OpenExportMenuAsync(host, "Usage_Export_SessionsCsv");
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                ExportPopup(host) is { } popup && Find<TextBlock>(popup, "UsageExportBlockedTitle") is { ActualHeight: > 0 },
                "Blocked export dialog"));
            await ui.RunOnUIAsync(async () =>
            {
                var popup = ExportPopup(host)!;
                var dialog = popup as ContentDialog ?? Assert.Single(FindDescendants<ContentDialog>(popup));
                Assert.Equal(theme, dialog.ActualTheme);
                var close = Get<Button>(popup, "UsageExportDialogClose");
                Assert.Equal(LocalizationHelper.GetString("Usage_Export_Close"), AutomationProperties.GetName(close));
                Assert.Equal(LocalizationHelper.GetString("Usage_Export_BlockedTitle"), Get<TextBlock>(popup, "UsageExportBlockedTitle").Text);
                Assert.Contains(FindDescendants<TextBlock>(popup),
                    text => text.Text == LocalizationHelper.GetString("Usage_Export_Range7d"));
                Assert.Contains(FindDescendants<TextBlock>(popup),
                    text => text.Text == LocalizationHelper.Format("Usage_Export_CoverageFormat", "3", "1,500"));
                Assert.Empty(gateway.SavedNames);
                await VisualTestCapture.CaptureAsync(popup, $"Usage-{theme}-export-blocked");
                Invoke(Get<Button>(popup, "UsageExportDialogPrimary"));
            });
            await WaitForAsync(host, "UsageExportToast");
            await ui.RunOnUIAsync(async () =>
            {
                var toast = Get<Border>(host, "UsageExportToast");
                Assert.Equal(HorizontalAlignment.Center, toast.HorizontalAlignment);
                Assert.Equal(480, toast.MaxWidth);
                Assert.Equal(20, toast.Margin.Bottom);
                Assert.NotNull(toast.Background);
                Assert.NotNull(toast.Shadow);
                Assert.Contains("usage-daily-", Get<TextBlock>(toast, "UsageExportToastMessage").Text);
                Assert.Single(gateway.SavedNames);
                Invoke(Get<Button>(toast, "UsageExportOpenFolder"));
                Assert.Equal([@"C:\synthetic-usage-export"], gateway.OpenedFolders);
                await VisualTestCapture.CaptureAsync(ui.Container, $"Usage-{theme}-export-saved");
            });
        }, theme: theme);
    }

    [Fact]
    public async Task FailedExport_IsAnnouncedAsAnErrorRatherThanASavedFile()
    {
        await WithUsagePageAsync(Report(serverOverview: false), new RecordingCommands(), async (host, gateway) =>
        {
            await WaitForAsync(host, "UsageDaily");
            await OpenExportMenuAsync(host, "Usage_Export_DailyCsv");
            await WaitForAsync(host, "UsageExportToast");
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                AutomationProperties.GetLiveSetting(Get<TextBlock>(host, "UsageResultAnnouncements")) == AutomationLiveSetting.Assertive,
                "Assertive export failure announcement"));
            await ui.RunOnUIAsync(() =>
            {
                var toast = Get<Border>(host, "UsageExportToast");
                Assert.Equal(LocalizationHelper.GetString("Usage_Export_Error_Failed"), Get<TextBlock>(toast, "UsageExportToastMessage").Text);
                Assert.Null(Find<FrameworkElement>(toast, "UsageExportOpenFolder"));
                Assert.Empty(gateway.SavedNames);
                Assert.Single(gateway.SaveFailures);
            });
        }, configure: gateway => gateway.SaveFails = true);
    }

    private async Task OpenExportMenuAsync(FrameworkElement host, string key)
    {
        await ui.RunOnUIAsync(() =>
        {
            var button = Get<Button>(host, "UsageExport");
            Assert.IsType<MenuFlyout>(button.Flyout).ShowAt(button);
        });
        await ui.YieldToRenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            var flyout = Assert.IsType<MenuFlyout>(Get<Button>(host, "UsageExport").Flyout);
            var item = Assert.Single(flyout.Items.OfType<MenuFlyoutItem>(), entry => entry.Text == LocalizationHelper.GetString(key));
            Assert.IsAssignableFrom<IInvokeProvider>(new MenuFlyoutItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
        });
    }

    private static FrameworkElement? ExportPopup(FrameworkElement host) =>
        Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(host.XamlRoot)
            .Select(popup => popup.Child).OfType<FrameworkElement>()
            .FirstOrDefault(popup => Find<TextBlock>(popup, "UsageExportBlockedTitle") is not null);

    public static IEnumerable<object[]> MainStates =>
        from theme in new[] { ElementTheme.Light, ElementTheme.Dark }
        from state in new[] { "installed", "advanced", "sparse", "large", "empty", "offline", "first-load", "load-failed",
            "refresh-failed", "no-read", "unsupported", "computing", "read-only", "partial", "stale", "missing-price", "provider-unavailable" }
        select new object[] { theme, state };

    [Theory]
    [MemberData(nameof(MainStates))]
    public async Task MainStates_RenderTheApprovedHierarchyInBothThemes(ElementTheme theme, string scenario)
    {
        var report = scenario == "empty" ? new SessionsUsageReport { Totals = new UsageTotals() } : Report(serverOverview: scenario == "advanced");
        if (scenario == "sparse")
        {
            var row = report.Sessions[0];
            report = report with
            {
                Sessions = [row], Totals = row.Usage!.Totals,
                Aggregates = report.Aggregates with
                {
                    SessionCount = 1, ByAgent = [new(row.AgentId!, row.Usage.Totals)],
                    ByModel = [new(row.EffectiveProvider, row.EffectiveModel, 5, row.Usage.Totals)],
                    ByProvider = [new(row.EffectiveProvider, null, 5, row.Usage.Totals)],
                },
            };
        }
        if (scenario == "large")
            report = report with
            {
                Sessions = Enumerable.Range(0, 1000).Select(index =>
                    Row($"agent:main:cron:job:run:{index}", $"Scheduled report {index}", "main", "claude-sonnet-4.5",
                        1000, 0.01, Anchor.AddMinutes(-index))).ToList(),
                Aggregates = report.Aggregates with { SessionCount = null },
            };
        if (scenario == "computing")
            report = report with
            {
                Sessions = report.Sessions.Select(row => row with { Computing = true }).ToList(),
                CacheStatus = new("refreshing", 3, 0, null),
            };
        if (scenario == "partial") report = report with { Partial = new("time-budget", "Synthetic report is still incomplete.") };
        if (scenario == "stale") report = report with { CacheStatus = new("stale", 0, 2, null) };
        if (scenario == "missing-price")
        {
            var totals = new UsageTotals { Report = UsageReportState.Available, TotalTokens = 100_000, Input = 80_000, Output = 20_000, MissingCostEntries = 3 };
            report = report with
            {
                Totals = totals,
                Sessions = [new() { Key = "agent:main:webchat:main", AgentId = "main", ModelProvider = "local", Model = "llama", Label = "Local model conversation",
                    Usage = new() { Totals = totals } }],
                Aggregates = new()
                {
                    SessionCount = 1, ByAgent = [new("main", totals)], ByModel = [new("local", "llama", 1, totals)],
                    ByProvider = [new("local", null, 1, totals)],
                    CostDaily = [new(UsageFilters.Iso(DateOnly.FromDateTime(DateTime.Now)), totals)],
                },
            };
        }
        var commands = new RecordingCommands();
        await WithUsagePageAsync(report, commands, async (host, gateway) =>
        {
            var marker = scenario switch
            {
                "first-load" => "UsageLoading",
                "load-failed" => "UsageErrorBanner",
                "no-read" => "UsageForbiddenPanel",
                "unsupported" => "UsageUnsupportedPanel",
                "empty" => "UsageEmptyState",
                _ => "UsageDaily",
            };
            await WaitForAsync(host, marker);
            if (scenario is not "unsupported" and not "no-read")
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                {
                    var selected = Get<SelectorBar>(host, "UsagePresets").SelectedItem;
                    return selected is not null && FindDescendants<Microsoft.UI.Xaml.Shapes.Rectangle>(selected)
                        .Any(rectangle => rectangle.Name == "PART_SelectionVisual" && rectangle.Opacity >= 0.99);
                }, "Selected reporting range underline"));
            if (scenario is "offline" or "refresh-failed")
            {
                await ui.RunOnUIAsync(() =>
                {
                    if (scenario == "offline") gateway.Connected = false;
                    else gateway.Failure = UsageReadStatus.Failed;
                    gateway.Controller.Refresh();
                });
                await WaitForAsync(host, scenario == "offline" ? "UsageDisconnectedBanner" : "UsageErrorBanner");
            }
            await ui.RunOnUIAsync(async () =>
            {
                Assert.Null(Find<FrameworkElement>(host, "UsageRenderError"));
                Assert.NotNull(Find<FrameworkElement>(host, marker));
                if (scenario == "refresh-failed")
                {
                    Assert.NotNull(Find<FrameworkElement>(host, "UsageDaily"));
                    Assert.Equal(InfoBarSeverity.Warning, Get<InfoBar>(host, "UsageErrorBanner").Severity);
                }
                if (scenario is "unsupported" or "no-read")
                    Assert.Null(Find<FrameworkElement>(host, "UsageDaily"));
                if (scenario == "offline") Assert.False(Get<Button>(host, "UsageExport").IsEnabled);
                if (scenario == "missing-price")
                {
                    Assert.Contains(LocalizationHelper.GetString("Usage_Overview_Unpriced"), Get<TextBlock>(host, "UsageSummaryCostValue").Text);
                    Assert.Null(Find<FrameworkElement>(host, "UsageDailyAverage"));
                }
                await VisualTestCapture.CaptureAsync(ui.Container, $"Usage-{theme}-{scenario}-top");
            });
            if (scenario == "installed")
            {
                await ui.RunOnUIAsync(() =>
                {
                    var pill = Get<Border>(host, "UsageSessionsCount");
                    var probe = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                        """<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="{ThemeResource UsageCountPillBrush}" Width="24" Height="24"/>""");
                    probe.RequestedTheme = theme;
                    ui.Container.Children.Add(probe);
                    probe.UpdateLayout();
                    var nativeFill = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(probe.Background);
                    ui.Container.Children.Remove(probe);
                    var fill = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(pill.Background);
                    Assert.Equal(nativeFill.Color, fill.Color);
                    Assert.Equal(nativeFill.Opacity, fill.Opacity);
                    Assert.True(fill.Opacity * fill.Color.A / 255 >= 0.1, "The count needs a visible fill, not a bare number.");
                    Assert.True(theme == ElementTheme.Dark ? fill.Color.R > 128 : fill.Color.R < 128,
                        "The neutral count fill must follow the actual page theme.");
                });
                await CaptureSectionAsync(host, "UsageDaily", $"Usage-{theme}-installed-per-day");
                await CaptureSectionAsync(host, "UsageContributors", $"Usage-{theme}-installed-contributors");
                await ui.RunOnUIAsync(() => Invoke(Get<Button>(host, "UsageSession_agent:research:discord:42")));
                await WaitForAsync(host, "UsageSessionDetails");
                await CaptureSectionAsync(host, "UsageSessions", $"Usage-{theme}-installed-sessions-detail");
            }
            if (scenario == "first-load")
                gateway.Pending!.TrySetResult(UsageReadResult<SessionsUsageReport>.Ok(report));
        }, unsupported: scenario == "unsupported", theme: theme, configure: gateway =>
        {
            if (scenario == "first-load") gateway.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (scenario == "load-failed") gateway.Failure = UsageReadStatus.Failed;
            if (scenario == "no-read") gateway.Failure = UsageReadStatus.Forbidden;
            if (scenario == "provider-unavailable")
                gateway.Providers = gateway.Providers.Select(provider => provider with { Windows = [], Billing = [], Error = "Synthetic provider unavailable" }).ToList();
            if (scenario == "sparse") gateway.Providers = gateway.Providers.Take(1).ToList();
            if (scenario == "large")
                gateway.Providers = gateway.Providers.Concat(Enumerable.Range(1, 4)
                    .Select(index => new ProviderUsageSnapshot { Provider = "synthetic" + index, DisplayName = "Provider " + index,
                        Windows = [new("Week", null, 75 + index, DateTimeOffset.Now.AddDays(1))] })).ToList();
        });
    }

    private async Task CaptureSectionAsync(FrameworkElement host, string marker, string name)
    {
        await ui.RunOnUIAsync(() =>
        {
            var scroll = Get<ScrollViewer>(host, "UsagePageRoot");
            var section = Get<FrameworkElement>(host, marker);
            var offset = section.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y + scroll.VerticalOffset;
            scroll.ChangeView(null, Math.Max(0, offset - 20), null, disableAnimation: true);
        });
        await ui.YieldToRenderAsync();
        await ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(ui.Container, name));
    }

    private static int OverviewReads(UsageGateway gateway)
    {
        lock (gateway.Queries) return gateway.Queries.Count(q => q.Key is null);
    }

    private static string Count(long value) => UsageUi.Count(value);

    private static List<string> SessionRowIds(DependencyObject host) =>
        FindDescendants<Button>(host)
            .Select(AutomationProperties.GetAutomationId)
            .Where(id => id.StartsWith("UsageSession_agent:", StringComparison.Ordinal))
            .ToList();

    private async Task WithUsagePageAsync(
        SessionsUsageReport overview, RecordingCommands commands,
        Func<ReactorHostControl, UsageGateway, Task> body, bool unsupported = false, Action? openConnection = null,
        ElementTheme theme = ElementTheme.Default, Action<UsageGateway>? configure = null, double pageWidth = PageWidth, DateOnly? today = null,
        UsageViewport? viewport = null)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        UsagePageController? controller = null;
        var client = DispatchProxy.Create<IOperatorGatewayClient, UsageGateway>();
        var gateway = (UsageGateway)client;
        gateway.Overview = overview;
        gateway.Unsupported = unsupported;
        configure?.Invoke(gateway);
        var active = true;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                UsageThemeProofResources.Ensure();
                var dispatcher = ui.Dispatcher;
                controller = new UsagePageController(() => client, () => today ?? DateOnly.FromDateTime(DateTime.Now),
                    action => dispatcher.TryEnqueue(() => action()));
                gateway.Controller = controller;
                gateway.ExportWorkflow = new UsageExportWorkflow(controller, (name, _) =>
                {
                    if (gateway.SaveFails) throw new UnauthorizedAccessException("Synthetic save permission failure");
                    gateway.SavedNames.Add(name);
                    return Task.FromResult<UsageSavedExport?>(new(name, @"C:\synthetic-usage-export"));
                }, () => active, gateway.SaveFailures.Add);
                var activeViewport = viewport ?? new UsageViewport();
                activeViewport.Update(pageWidth);
                // The same handoff owner the XAML host wires, with recording app commands in place of App.
                var (openWebUsage, openConversation) = UsageHandoffs.For(commands, commands.PendingChatKeys.Add);
                var props = new UsagePageProps(controller, activeViewport, openConnection ?? (() => { }), openWebUsage, openConversation,
                    gateway.ExportWorkflow.ExportAsync, () => commands.Navigate("chat"), folder =>
                    {
                        gateway.OpenedFolders.Add(folder);
                        return Task.CompletedTask;
                    });
                var scale = ui.Container.XamlRoot.RasterizationScale;
                ui.TestWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)((Math.Max(pageWidth, 1300) + 100) * scale), (int)(1000 * scale)));
                ui.Container.RequestedTheme = theme;
                host = new ReactorHostControl { Width = pageWidth, Height = PageHeight, RequestedTheme = theme };
                host.SizeChanged += (_, args) => activeViewport.Update(args.NewSize.Width);
                // A render exception fails the wait with its message instead of crashing the shared test host.
                host.Mount(_ => ErrorBoundary(Component<ReactorUsagePage, UsagePageProps>(props),
                    ex => TextBlock(ex.ToString()).AutomationId("UsageRenderError")));
                ui.Container.Children.Add(host);
                controller.Activate();
            });
            await body(host!, gateway);
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                active = false;
                controller?.Dispose();
                if (host is not null)
                {
                    ui.Container.Children.Remove(host);
                    host.Dispose();
                }
            });
        }
    }

    private Task WaitForAsync(FrameworkElement root, string automationId) =>
        ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
            () => Find<FrameworkElement>(root, automationId) is { ActualHeight: > 0 }, automationId,
            () => Find<TextBlock>(root, "UsageRenderError")?.Text ?? "No render error."));

    private static T? Find<T>(DependencyObject root, string automationId) where T : DependencyObject =>
        FindDescendants<T>(root).FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == automationId);

    private static T Get<T>(DependencyObject root, string automationId) where T : DependencyObject =>
        Assert.IsType<T>(Find<T>(root, automationId), exactMatch: false);

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
}
