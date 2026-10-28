using System.Globalization;
using System.Xml.Linq;
using OpenClaw.Shared.Cron;
using OpenClawTray.Automations;

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
        Assert.False(new AutomationsNotice(AutomationsNoticeKind.RunNotStarted, "x", "already-running").IsError);
        Assert.True(new AutomationsNotice(AutomationsNoticeKind.RunNotStarted, "x", "invalid-spec").IsError);
    }

    [Fact]
    public void EveryValidationErrorHasWording()
    {
        var t = Text();
        foreach (var error in Enum.GetValues<CronFormError>())
            Assert.False(string.IsNullOrWhiteSpace(t.FormError(error)));
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
        foreach (var s in CronSuggestion.All)
            foreach (var part in new[] { "Name", "Tagline", "Prompt", "Schedule" })
                Assert.True(EnUs.Value.ContainsKey($"Automations_Suggestion_{s.Id}_{part}"), s.Id + part);
        foreach (var kind in new[] { CronScheduleKind.Every, CronScheduleKind.At, CronScheduleKind.Cron, CronScheduleKind.Event })
            Assert.True(EnUs.Value.ContainsKey($"Automations_KindHint_{kind}"), kind.ToString());
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
    public void AgentRoute_PassesTheRouteAgentToTheAutomationsPage()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var hub = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs"));
        Assert.Contains("cron.AgentId = _currentAgentId;", hub);
        var page = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "CronPage.xaml.cs"));
        Assert.Contains("new AutomationsStore(", page);
        Assert.Contains("agentId,", page);
        Assert.DoesNotContain("RequestCronListAsync", page);
    }

    private static CronJob CronJsonReaderJob(string json) =>
        CronJsonReader.ParseJob(System.Text.Json.JsonDocument.Parse(json).RootElement)!;
}
