using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[CollectionDefinition("Gateway fixture UI", DisableParallelization = true)]
public sealed class GatewayFixtureUiCollection { }

public sealed class GatewayFixtureUiFactAttribute : FactAttribute
{
    public GatewayFixtureUiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_GATEWAY_FIXTURE_UI") != "1")
            Skip = "Opt in with scripts\\test-gateway-fixture.ps1. This test launches a real isolated Windows app.";
    }
}

[Collection("Gateway fixture UI")]
public sealed class GatewayFixtureUiTests(ITestOutputHelper output)
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SessionPickerSwitchesRealHistoriesAndShowsMessage240AtBothWidths()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerSessionPicker") is not null, "native session picker");
            for (var iteration = 0; iteration < 3; iteration++)
            {
                await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey);
                // This must pass BEFORE driving the scrollbar: a manual jump could hide a broken initial-tail request.
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker), "natural tail at message 240");
                await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker), "other session's visible history");
                Assert.False(IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker));
            }
            await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey);
            var measurements = new List<object>();
            double narrowWidth = 0;
            foreach (var name in new[] { "narrow", "wide" })
            {
                var window = FindHub(run);
                var transform = (TransformPattern)window.GetCurrentPattern(TransformPattern.Pattern);
                Assert.True(transform.Current.CanResize);
                transform.Resize(name == "narrow" ? 900 : narrowWidth + 400, name == "narrow" ? 720 : 950);
                await ScrollToAsync(run, 0);
                await ScrollToAsync(run, 100);
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker), $"message 240 visible at {name} width");
                var bounds = window.Current.BoundingRectangle;
                if (name == "narrow")
                    narrowWidth = bounds.Width;
                else
                    Assert.True(bounds.Width >= narrowWidth + 200, "The host did not provide two meaningfully different window widths.");
                measurements.Add(new { name, width = bounds.Width, height = bounds.Height, finalMarker = GatewayScenario.LongHistoryFinalMarker });
                await CaptureIfRequestedAsync(run, $"long-history-{name}.png");
            }
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "layout-proof.json"), JsonSerializer.Serialize(measurements));
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task PopulatedPagesPreserveSelectionAndPreferencesStayInDisposableProfile()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
            foreach (var (page, marker) in new[]
            {
                ("sessions", "SessionsPageMarker"),
                ("settings", "SettingsPageMarker"),
                ("config", "ConfigPageMarker")
            })
            {
                await run.InvokeAsync("app.navigate", new { page });
                await WaitUiAsync(run, () => FindById(run, marker) is not null, page);
                if (page == "settings")
                {
                    var toggle = FindById(run, "SettingsPageShowToolCalls");
                    Assert.NotNull(toggle);
                    ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
                    await run.WaitForAsync(() =>
                    {
                        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Profile.DataDirectory, "settings.json")));
                        return Task.FromResult(!settings.RootElement.GetProperty("ShowChatToolCalls").GetBoolean());
                    }, "profile-only preference persistence");
                }
                if (page == "config")
                {
                    await run.WaitForAsync(async () =>
                    {
                        using var result = await run.Client.CallToolExpectSuccessAsync("app.config.get");
                        return !result.RootElement.TryGetProperty("error", out _);
                    }, "synthetic Gateway configuration");
                    await CaptureIfRequestedAsync(run, "gateway-configuration.png");
                }
            }
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => PickerShows(run, GatewayScenario.OtherSessionTitle)
                && IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker), "selected session after page navigation");
            await run.InvokeAsync("app.navigate", new { page = "sessions" });
            await WaitUiAsync(run, () => FindText(run, GatewayScenario.LongSessionTitle) is not null, "long session row");
            var row = FindText(run, GatewayScenario.LongSessionTitle)!;
            for (var depth = 0; depth < 15 && row.Current.ControlType != ControlType.ListItem; depth++)
                row = TreeWalker.ControlViewWalker.GetParent(row)
                    ?? throw new InvalidOperationException("Session title has no list-item ancestor.");
            var openChat = row.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Open in chat")));
            Assert.NotNull(openChat);
            Invoke(openChat);
            await WaitUiAsync(run, () => PickerShows(run, GatewayScenario.LongSessionTitle)
                && IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker), "Sessions-page action routes to long chat");
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task EmptySessionRendersConnectedComposerWithoutPreviousHistory()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
            await SelectSessionAsync(run, GatewayScenario.EmptyTitle, GatewayScenario.EmptySessionKey);
            await WaitUiAsync(run, () => RenderConsumedHistory(run, GatewayScenario.EmptySessionKey), "rendered empty-session history");
            Assert.True(PickerShows(run, GatewayScenario.EmptyTitle));
            Assert.NotNull(FindById(run, "ChatComposerInput"));
            Assert.False(IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker));
            var snapshot = await run.InvokeAsync("app.chat.snapshot", new { threadId = GatewayScenario.EmptySessionKey });
            Assert.Empty(snapshot.GetProperty("selectedTimeline").GetProperty("entries").EnumerateArray());
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task LateHistoryForPreviousSessionCannotReplaceSelectedTranscript()
    {
        await WithAppAsync(async run =>
        {
            run.Gateway.HoldHistory(GatewayScenario.LongSessionKey);
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey, waitForHistory: false);
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(request =>
                request.Method == "chat.history" && request.SessionKey == GatewayScenario.LongSessionKey)), "held long-history request");
            await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
            await run.Gateway.ReleaseHistoryAsync(GatewayScenario.LongSessionKey);
            await WaitHistoryAsync(run, GatewayScenario.LongSessionKey);
            await WaitUiAsync(run, () => RenderConsumedHistory(run, GatewayScenario.LongSessionKey),
                "native composer rendering the snapshot containing delayed A history");
            Assert.True(PickerShows(run, GatewayScenario.OtherSessionTitle));
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker), "other history after late response");
            Assert.False(IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker));
            await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker), "cached delayed history when actually selected");
        });
    }

    private async Task WithAppAsync(Func<GatewayFixtureRun, Task> test)
    {
        var appPath = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
            ?? throw new InvalidOperationException("Set OPENCLAW_GATEWAY_FIXTURE_APP to the freshly built app. No installed-app fallback is allowed.");
        await using var run = await GatewayFixtureRun.StartAsync(appPath,
            Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"));
        output.WriteLine($"Fixture run {run.Profile.RunId}, PID {run.AppProcessId}, artifacts: {run.ArtifactsDirectory}");
        try
        {
            // UIA is synchronous and can block inside a hung app. Keep the deadline
            // outside that worker so failure reporting and owned-process cleanup still run.
            await Task.Run(() => test(run)).WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            run.EnsureRunning();
            Assert.Empty(run.Gateway.UnexpectedRequests);
            var crashLog = Path.Combine(run.Profile.DataDirectory, "crash.log");
            Assert.False(File.Exists(crashLog) && new FileInfo(crashLog).Length > 0, "The real app wrote a crash log.");
            await run.WriteReportAsync("passed");
        }
        catch (Exception ex)
        {
            await run.WriteReportAsync("failed", ex);
            if (run.IsRunning)
            {
                try { await CaptureIfRequestedAsync(run, "failure.png"); }
                catch (Exception captureError)
                {
                    output.WriteLine($"Failure screenshot unavailable: {captureError.Message}");
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "screenshot-error.txt"), captureError.ToString());
                }
            }
            throw;
        }
    }

    private static async Task SelectSessionAsync(GatewayFixtureRun run, string title, string key, bool waitForHistory = true)
    {
        await WaitUiAsync(run, () => FindById(run, "ChatComposerSessionPicker") is not null, "session picker");
        Invoke(FindById(run, "ChatComposerSessionPicker")!);
        AutomationElement? item = null;
        await WaitUiAsync(run, () =>
        {
            item = FindInApp(run, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                new PropertyCondition(AutomationElement.NameProperty, title)));
            return item is not null;
        }, $"picker item {title}");
        Invoke(item!);
        await WaitUiAsync(run, () => PickerShows(run, title), $"selected session {title}");
        if (waitForHistory) await WaitHistoryAsync(run, key);
    }

    private static Task WaitHistoryAsync(GatewayFixtureRun run, string key) =>
        run.WaitForAsync(async () =>
        {
            var snapshot = await run.InvokeAsync("app.chat.snapshot", new { threadId = key });
            return snapshot.TryGetProperty("selectedTimeline", out var timeline)
                && timeline.ValueKind == JsonValueKind.Object
                && timeline.GetProperty("historyLoaded").GetBoolean();
        }, $"history loaded for {key}");

    private static bool PickerShows(GatewayFixtureRun run, string title) =>
        FindById(run, "ChatComposerSessionPicker")?.Current.Name.Contains(title, StringComparison.Ordinal) == true;

    private static bool RenderConsumedHistory(GatewayFixtureRun run, string key)
    {
        var picker = FindById(run, "ChatComposerSessionPicker");
        if (picker is null || string.IsNullOrEmpty(picker.Current.ItemStatus)) return false;
        using var rendered = JsonDocument.Parse(picker.Current.ItemStatus);
        return rendered.RootElement.GetProperty("loadedThreadIds").EnumerateArray()
            .Any(thread => thread.GetString() == key);
    }

    private static void Invoke(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            ((InvokePattern)invoke).Invoke();
        else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            ((SelectionItemPattern)selection).Select();
        else if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
            ((TogglePattern)toggle).Toggle();
        else
            throw new InvalidOperationException($"Control '{element.Current.Name}' has no activation pattern. Available: {string.Join(", ", element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName))}");
    }

    private static AutomationElement? FindById(GatewayFixtureRun run, string id) =>
        FindInApp(run, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static AutomationElement? FindText(GatewayFixtureRun run, string text)
    {
        foreach (var window in AppWindows(run))
        {
            var matches = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            foreach (AutomationElement element in matches)
                if (element.Current.Name.Contains(text, StringComparison.Ordinal))
                    return element;
        }
        return null;
    }

    private static AutomationElement? FindInApp(GatewayFixtureRun run, Condition condition)
    {
        foreach (var window in AppWindows(run))
            if (window.FindFirst(TreeScope.Descendants, condition) is { } match)
                return match;
        return null;
    }

    private static IEnumerable<AutomationElement> AppWindows(GatewayFixtureRun run)
    {
        run.EnsureRunning();
        return AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, run.AppProcessId)).Cast<AutomationElement>()
            .Where(window => !window.Current.IsOffscreen);
    }

    private static AutomationElement FindHub(GatewayFixtureRun run) =>
        AppWindows(run).First(window => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ChatComposerSessionPicker")) is not null);

    private static AutomationElement? FindTimeline(GatewayFixtureRun run) =>
        FindInApp(run, new PropertyCondition(AutomationElement.NameProperty, "Chat messages"));

    private static bool IsVisibleInTimeline(GatewayFixtureRun run, string text)
    {
        var timeline = FindTimeline(run);
        var message = FindText(run, text);
        if (timeline is null || message is null || message.Current.IsOffscreen)
            return false;
        var viewport = timeline.Current.BoundingRectangle;
        var bounds = message.Current.BoundingRectangle;
        return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
            && bounds.Top >= viewport.Top - 1 && bounds.Bottom <= viewport.Bottom + 1
            && bounds.Right > viewport.Left && bounds.Left < viewport.Right;
    }

    private static async Task ScrollToAsync(GatewayFixtureRun run, double percent)
    {
        var timeline = FindTimeline(run) ?? throw new InvalidOperationException("Chat timeline is not mounted.");
        var scrollElement = timeline;
        if (!scrollElement.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
        {
            scrollElement = timeline.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))
                ?? throw new InvalidOperationException("Chat timeline has no accessible scroll control.");
            pattern = scrollElement.GetCurrentPattern(ScrollPattern.Pattern);
        }
        ((ScrollPattern)pattern).SetScrollPercent(ScrollPattern.NoScroll, percent);
        await Task.Delay(100);
    }

    private static async Task WaitUiAsync(GatewayFixtureRun run, Func<bool> condition, string description)
    {
        await run.WaitForAsync(() =>
        {
            try { return Task.FromResult(condition()); }
            catch (ElementNotAvailableException) { return Task.FromResult(false); }
        }, description);
    }

    private static async Task CaptureIfRequestedAsync(GatewayFixtureRun run, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") != "1")
            return;
        var path = Path.Combine(run.ArtifactsDirectory, name);
        var start = new ProcessStartInfo("winapp")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "ui", "screenshot", "-a", run.AppProcessId.ToString(), "-o", path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start winapp screenshot capture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.True(process.ExitCode == 0 && File.Exists(path),
            $"Screenshot failed: {await stdout} {await stderr}");
    }
}
