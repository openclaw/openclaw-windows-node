using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.Connection;
using OpenClaw.TestSupport.Ui;
using OpenClaw.TestSupport.Gateway;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests.Journeys;

public sealed class ContractJourneyFactAttribute : FactAttribute
{
    public ContractJourneyFactAttribute(string caseId)
    {
        DisplayName = caseId;
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_CONTRACT_JOURNEYS") != "1")
            Skip = "Opt in with test-gateway-fixture.ps1 -Suite Journeys on an authorized interactive desktop.";
    }
}

[Collection("Gateway fixture UI")]
public sealed partial class ContractJourneyTests(ITestOutputHelper output)
{
    private static int _isolationFailed;
    private readonly ConcurrentBag<LvtUiaSession> _drivers = [];
    private readonly ConcurrentQueue<ContractJourneyCheckpoint> _checkpoints = [];

    [ContractJourneyFact("C01.NavigationPreservesPreferences")]
    [Trait("Category", "ContractJourney")]
    public Task NavigationPreservesPreferences() => WithAppAsync("C01.NavigationPreservesPreferences", false, async (run, cancellation) =>
    {
        var expected = ReadPreferences(run);
        // MCP opens the initial window only. All navigation under test uses lvt/UIA.
        await run.InvokeAsync("app.navigate", new { page = "sessions" });
        var window = await WaitWindowAsync(run, "SessionsPageMarker", cancellation);
        await using var driver = await ConnectAsync(run, window, cancellation);
        await driver.ActAsync("select", "SettingsNavSettings", cancellation);
        await driver.WaitAsync("SettingsPageMarker", Visible, cancellation);
        Capture(run, window, "navigation-settings.png", "SettingsPageMarker");
        Assert.Equal(expected, ReadPreferences(run));
        Observe("view-only-navigation-preserved-preferences");
        await driver.ActAsync("invoke", "SettingsBack", cancellation);
        await driver.WaitAsync("SessionsPageMarker", Visible, cancellation);
        Assert.Null(await driver.FindAsync("SettingsPageMarker", cancellation));
        await driver.ActAsync("invoke", "SettingsForward", cancellation);
        await driver.WaitAsync("SettingsPageMarker", Visible, cancellation);
        Assert.Equal(expected, ReadPreferences(run));
        Assert.DoesNotContain(run.Gateway.Requests, request => request.Method is "chat.send" or "config.set");
        Capture(run, window, "navigation-restored.png", "SettingsPageShowToolCalls");
        Observe("back-forward-restored-settings-without-gateway-mutation");
    });

    [ContractJourneyFact("C04.PreferenceOffToOnSurvivesRestart")]
    [Trait("Category", "ContractJourney")]
    public Task PreferenceOffToOnSurvivesRestart() => PreferenceJourneyAsync("C04.PreferenceOffToOnSurvivesRestart", false);

    [ContractJourneyFact("C04.PreferenceOnToOffSurvivesRestart")]
    [Trait("Category", "ContractJourney")]
    public Task PreferenceOnToOffSurvivesRestart() => PreferenceJourneyAsync("C04.PreferenceOnToOffSurvivesRestart", true);

    [ContractJourneyFact("C01.McpOnlyStartsWithoutGatewayCredential")]
    [Trait("Category", "ContractJourney")]
    public Task McpOnlyStartsWithoutGatewayCredential() =>
        WithAppAsync("C01.McpOnlyStartsWithoutGatewayCredential", false, async (run, cancellation) =>
        {
            var window = await WaitWindowAsync(run, "WorkspaceNavigation", cancellation);
            Observe("no-argument-launch-opened-workspace-without-gateway");
            var status = await run.InvokeAsync("app.status");
            Assert.NotEqual("Connected", status.GetProperty("operatorState").GetString());
            Assert.False(status.GetProperty("nodeConnected").GetBoolean());
            Assert.Equal(0, status.GetProperty("sessionCount").GetInt32());
            var registry = new GatewayRegistry(run.Profile.DataDirectory);
            registry.Load();
            Assert.Empty(registry.GetAll());
            Assert.Empty(run.Gateway.Requests);
            await using var driver = await ConnectAsync(run, window, cancellation);
            await driver.WaitAsync("WorkspaceNavigation", Visible, cancellation);
            Observe("local-mcp-responsive-with-no-gateway-record-or-peer-requests");
            Capture(run, window, "mcp-only-startup.png", "WorkspaceNavigation");
        }, GatewayFixtureStartupMode.McpOnly);

    [ContractJourneyFact("C01.UnavailableGatewayStillOpensWorkspace")]
    [Trait("Category", "ContractJourney")]
    public Task UnavailableGatewayStillOpensWorkspace() =>
        WithAppAsync("C01.UnavailableGatewayStillOpensWorkspace", false, async (run, cancellation) =>
        {
            var window = await WaitWindowAsync(run, "WorkspaceNavigation", cancellation);
            Observe("configured-unavailable-startup-opened-workspace");
            var status = await run.InvokeAsync("app.status");
            Assert.NotEqual("Connected", status.GetProperty("operatorState").GetString());
            Assert.Equal(0, status.GetProperty("sessionCount").GetInt32());
            Assert.True(run.Gateway.OperatorConnectionRejections > 0);
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Outcome == "ok");
            var registry = new GatewayRegistry(run.Profile.DataDirectory);
            registry.Load();
            var record = Assert.Single(registry.GetAll());
            Assert.Equal(run.Profile.GatewayId, registry.ActiveGatewayId);
            Assert.Equal(run.Gateway.Endpoint.AbsoluteUri, record.Url);
            await using var driver = await ConnectAsync(run, window, cancellation);
            await driver.WaitAsync("WorkspaceNavigation", Visible, cancellation);
            Observe("destination-preserved-with-no-fabricated-connected-session");
            Capture(run, window, "unavailable-gateway-startup.png", "WorkspaceNavigation");
        }, GatewayFixtureStartupMode.GatewayUnavailable);

    [ContractJourneyFact("C03.SendStreamsIntoSelectedConversation")]
    [Trait("Category", "ContractJourney")]
    public Task SendStreamsIntoSelectedConversation() => WithAppAsync(
        "C03.SendStreamsIntoSelectedConversation", false, async (run, cancellation) =>
        {
            var key = GatewayScenario.EmptySessionKey;
            run.Gateway.HoldChatResponse(key);
            run.Gateway.HoldChatDeltas(key, afterDeltaCount: 1);
            var (window, driver) = await OpenEmptyChatAsync(run, cancellation);
            await using (driver)
            {
                await EnterAndSendAsync(driver, "Fixture send journey", cancellation);
                await run.Gateway.WaitForChatRunAsync(key, GatewayFixtureChatPhase.ResponseHeld,
                    cancellationToken: cancellation);
                await driver.WaitAsync("ChatComposerPrimaryAction", element => ActionLabel(element) == "Stop", cancellation);
                Observe("send-pending-before-gateway-acknowledgement");
                Capture(run, window, "chat-pending.png", "ChatComposerPrimaryAction");
                run.Gateway.ReleaseChatResponse(key);
                await run.Gateway.WaitForChatRunAsync(key, GatewayFixtureChatPhase.DeltasHeld,
                    cancellationToken: cancellation);
                var partial = await WaitMessageAsync(run, window, GatewayScenario.ChatDeltas(key)[0].Trim(), cancellation);
                Observe("first-stream-fragment-visible-in-selected-conversation");
                Capture(run, window, "chat-streaming.png", "ChatComposerPrimaryAction", partial);
                run.Gateway.ReleaseChatDeltas(key);
                await run.Gateway.WaitForChatRunAsync(key, GatewayFixtureChatPhase.Completed,
                    cancellationToken: cancellation);
                var final = await WaitMessageAsync(run, window, string.Concat(GatewayScenario.ChatDeltas(key)), cancellation);
                await driver.WaitAsync("ChatComposerPrimaryAction", element => ActionLabel(element) == "Send", cancellation);
                var observed = Assert.Single(run.Gateway.ChatRuns);
                Assert.Equal(key, observed.SessionKey);
                Assert.True(observed.FinalPublished);
                Assert.Equal(2, observed.DeltaCount);
                Assert.Single(run.Gateway.Requests, request => request.Method == "chat.send" && request.SessionKey == key);
                Assert.True(IsSelected(window, $"WorkspaceSession:{key}"));
                Observe("single-accepted-send-produced-correct-session-final");
                Capture(run, window, "chat-final.png", "ChatComposerPrimaryAction", final);
            }
        }, scenario: GatewayScenario.CreateChat());

    [ContractJourneyFact("C03.CancelStopsOutputAndAllowsNextSend")]
    [Trait("Category", "ContractJourney")]
    public Task CancelStopsOutputAndAllowsNextSend() => WithAppAsync(
        "C03.CancelStopsOutputAndAllowsNextSend", false, async (run, cancellation) =>
        {
            var key = GatewayScenario.EmptySessionKey;
            run.Gateway.HoldChatDeltas(key, afterDeltaCount: 1);
            var (window, driver) = await OpenEmptyChatAsync(run, cancellation);
            await using (driver)
            {
                await EnterAndSendAsync(driver, "Fixture cancelled turn", cancellation);
                await run.Gateway.WaitForChatRunAsync(key, GatewayFixtureChatPhase.DeltasHeld,
                    cancellationToken: cancellation);
                var partial = await WaitMessageAsync(run, window, GatewayScenario.ChatDeltas(key)[0].Trim(), cancellation);
                await driver.WaitAsync("ChatComposerPrimaryAction", element => ActionLabel(element) == "Stop", cancellation);
                Capture(run, window, "chat-before-cancel.png", "ChatComposerPrimaryAction", partial);
                await driver.ActAsync("invoke", "ChatComposerPrimaryAction", cancellation);
                await run.Gateway.WaitForChatRunAsync(key, GatewayFixtureChatPhase.Aborted,
                    cancellationToken: cancellation);
                run.Gateway.ReleaseChatDeltas(key);
                await driver.WaitAsync("ChatComposerPrimaryAction", element => ActionLabel(element) == "Send", cancellation);
                Assert.False(run.Gateway.ChatRuns[0].FinalPublished);
                Assert.Null(FindVisibleMessage(window, string.Concat(GatewayScenario.ChatDeltas(key))));
                Observe("accepted-cancel-stopped-output-and-returned-composer-to-idle");
                Capture(run, window, "chat-cancelled.png", "ChatComposerPrimaryAction");
                await EnterAndSendAsync(driver, "Fixture next turn after cancellation", cancellation);
                await run.Gateway.WaitForChatRunAsync(key, GatewayFixtureChatPhase.Completed, occurrence: 2,
                    cancellationToken: cancellation);
                var final = await WaitMessageAsync(run, window, string.Concat(GatewayScenario.ChatDeltas(key)), cancellation);
                await driver.WaitAsync("ChatComposerPrimaryAction", element => ActionLabel(element) == "Send", cancellation);
                Assert.Equal(GatewayFixtureChatPhase.Aborted, run.Gateway.ChatRuns[0].Phase);
                Assert.False(run.Gateway.ChatRuns[0].FinalPublished);
                Assert.True(run.Gateway.ChatRuns[1].FinalPublished);
                Assert.Equal(2, run.Gateway.Requests.Count(request => request.Method == "chat.send" && request.SessionKey == key));
                Assert.Single(run.Gateway.Requests, request => request.Method == "chat.abort" && request.Outcome == "ok");
                Assert.True(IsSelected(window, $"WorkspaceSession:{key}"));
                Observe("next-user-send-succeeded-without-reviving-aborted-run");
                Capture(run, window, "chat-after-cancel.png", "ChatComposerPrimaryAction", final);
            }
        }, scenario: GatewayScenario.CreateChat());

    private async Task<(AutomationElement Window, LvtUiaSession Driver)> OpenEmptyChatAsync(
        GatewayFixtureRun run, CancellationToken cancellation)
    {
        await run.InvokeAsync("app.navigate", new { page = "chat" });
        await GatewayFixtureUiTests.SelectSessionAsync(run, GatewayScenario.EmptyTitle, GatewayScenario.EmptySessionKey);
        var window = await WaitWindowAsync(run, "WorkspaceNavigation", cancellation);
        var driver = await ConnectAsync(run, window, cancellation);
        try
        {
            await driver.WaitAsync("ChatComposerInput", Visible, cancellation);
            await run.Gateway.WaitForCompletedRequestAsync("chat.history", GatewayScenario.EmptySessionKey,
                cancellationToken: cancellation);
            Assert.True(IsSelected(window, $"WorkspaceSession:{GatewayScenario.EmptySessionKey}"));
            return (window, driver);
        }
        catch
        {
            await driver.DisposeAsync();
            throw;
        }
    }

    private static async Task EnterAndSendAsync(LvtUiaSession driver, string text, CancellationToken cancellation)
    {
        await driver.SetValueAsync("ChatComposerInput", text, cancellation);
        await driver.WaitAsync("ChatComposerPrimaryAction", element =>
            ActionLabel(element) == "Send" && bool.Parse(LvtProtocol.Property(element, "IsEnabled")), cancellation);
        await driver.ActAsync("invoke", "ChatComposerPrimaryAction", cancellation);
    }

    private static string ActionLabel(JsonElement element) => element.GetProperty("text").GetString()
        ?? throw new InvalidDataException("Composer action has no accessible label.");

    private static bool IsSelected(AutomationElement window, string id)
    {
        var item = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        return item is not null && item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
            && ((SelectionItemPattern)pattern).Current.IsSelected;
    }

    private static AutomationElement? FindVisibleMessage(AutomationElement window, string text)
    {
        var timeline = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Chat messages"));
        if (timeline is null) return null;
        var viewport = timeline.Current.BoundingRectangle;
        return timeline.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().FirstOrDefault(element =>
            {
                var bounds = element.Current.BoundingRectangle;
                return element.Current.Name.Contains(text, StringComparison.Ordinal) && !element.Current.IsOffscreen
                    && bounds.Width > 0 && bounds.Height > 0 && bounds.Top >= viewport.Top - 1
                    && bounds.Bottom <= viewport.Bottom + 1 && bounds.Right > viewport.Left && bounds.Left < viewport.Right;
            });
    }

    private static async Task<AutomationElement> WaitMessageAsync(GatewayFixtureRun run, AutomationElement window,
        string text, CancellationToken cancellation)
    {
        AutomationElement? found = null;
        try
        {
            await run.WaitForAsync(() =>
            {
                found = FindVisibleMessage(window, text);
                return Task.FromResult(found is not null);
            }, "fixture chat text rendered inside the selected transcript viewport", cancellationToken: cancellation);
        }
        catch (TimeoutException) { throw new ContractAssertionException("Expected fixture chat text was not rendered in the transcript viewport."); }
        return found!;
    }

    private Task PreferenceJourneyAsync(string caseId, bool initial) => WithAppAsync(caseId, initial, async (run, cancellation) =>
    {
        using var other = new GatewayFixtureProfile(run.Gateway.Endpoint, "synthetic-independent-profile", !initial);
        var otherFile = Path.Combine(other.DataDirectory, "settings.json");
        var otherHash = SHA256.HashData(await File.ReadAllBytesAsync(otherFile, cancellation));
        var expected = ReadPreferences(run);
        Assert.Equal(initial, expected.ShowChatToolCalls);
        await run.InvokeAsync("app.navigate", new { page = "settings" });
        var window = await WaitWindowAsync(run, "SettingsPageMarker", cancellation);
        await using (var driver = await ConnectAsync(run, window, cancellation))
        {
            await driver.WaitAsync("SettingsPageShowToolCalls", element => ToggleEquals(element, initial), cancellation);
            Assert.Equal(expected, ReadPreferences(run));
            await driver.ActAsync("toggle", "SettingsPageShowToolCalls", cancellation);
            await driver.WaitAsync("SettingsPageShowToolCalls", element => ToggleEquals(element, !initial), cancellation);
            expected = expected with { ShowChatToolCalls = !initial };
            try
            {
                await run.WaitForAsync(() => Task.FromResult(ReadPreferences(run) == expected),
                    "explicit preference persisted without changing unrelated fields", cancellationToken: cancellation);
            }
            catch (TimeoutException)
            {
                throw new ContractAssertionException(
                    "The visible preference changed, but the expected persisted preference and unrelated fields did not match.");
            }
            Observe("preference-persisted-with-unrelated-fields-unchanged");
            Capture(run, window, "preference-edited.png", "SettingsPageShowToolCalls");
        }
        var oldStart = run.AppProcessStartUtc;
        await run.RestartAppAsync(cancellation);
        Assert.NotEqual(oldStart, run.AppProcessStartUtc);
        Assert.Equal(expected, ReadPreferences(run));
        await run.InvokeAsync("app.navigate", new { page = "settings" });
        window = await WaitWindowAsync(run, "SettingsPageMarker", cancellation);
        await using var restarted = await ConnectAsync(run, window, cancellation);
        await restarted.WaitAsync("SettingsPageShowToolCalls", element => ToggleEquals(element, !initial), cancellation);
        Assert.Equal(expected, ReadPreferences(run));
        Assert.Equal(otherHash, SHA256.HashData(await File.ReadAllBytesAsync(otherFile, cancellation)));
        Observe("restart-restored-preference-and-independent-profile-unchanged");
        Capture(run, window, "preference-restarted.png", "SettingsPageShowToolCalls");
    });

    private async Task WithAppAsync(string caseId, bool initial, Func<GatewayFixtureRun, CancellationToken, Task> test,
        GatewayFixtureStartupMode startupMode = GatewayFixtureStartupMode.Connected,
        GatewayScenario? scenario = null,
        Func<string, string?, CancellationToken, Task<GatewayFixtureRun>>? runFactory = null)
    {
        if (Volatile.Read(ref _isolationFailed) != 0)
        {
            const string reason = "Not run: a previous journey could not establish isolation or cleanup.";
            await WriteCaseReportAsync(new(caseId, null, null, null, null, reason, []));
            throw new InvalidOperationException(reason);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        IReadOnlyDictionary<string, ProfileFileMetadata>? before = null;
        IReadOnlyList<ProfileMetadataChange> changes = [];
        GatewayFixtureRun? run = null;
        Task? worker = null;
        var failures = new List<Exception>();
        var failureDetails = new List<ContractJourneyFailure>();
        bool? behaviorPassed = null;
        bool? cleanupSucceeded = null;
        bool? isolationUnchanged = null;
        var isolationAuditAttempted = false;
        var setupAttempted = false;
        var behaviorAttempted = false;
        try
        {
            var executable = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
                ?? throw new InvalidOperationException("An explicit freshly built app is required.");
            LvtUiaSession.ValidateExecutable(LvtPath());
            isolationAuditAttempted = true;
            before = RealProfileMetadata();
            setupAttempted = true;
            run = runFactory is null
                ? await GatewayFixtureRun.StartAsync(executable,
                    Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"),
                    deadline.Token, scenario: scenario, showChatToolCalls: initial, startupMode: startupMode)
                : await runFactory(executable,
                    Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"), deadline.Token);
            cleanupSucceeded = true;
            output.WriteLine($"Contract run {run.Profile.RunId}; app PID {run.AppProcessId}; artifacts {run.ArtifactsDirectory}");
            behaviorAttempted = true;
            worker = Task.Run(() => test(run, deadline.Token));
            await worker.WaitAsync(deadline.Token);
            Assert.Empty(run.Gateway.UnexpectedRequests);
            Assert.False(File.Exists(Path.Combine(run.Profile.DataDirectory, "crash.log"))
                && new FileInfo(Path.Combine(run.Profile.DataDirectory, "crash.log")).Length > 0);
            behaviorPassed = true;
            await run.WriteReportAsync("contract-assertions-passed");
        }
        catch (Exception failure)
        {
            var detail = behaviorPassed == true ? new ContractJourneyFailure("report", failure.Message)
                : ContractJourneyFailure.FromException(failure, behaviorAttempted, failure is Xunit.Sdk.XunitException);
            failureDetails.Add(detail);
            if (detail.Category == "assertion") behaviorPassed = false;
            failures.Add(failure);
            if (run is not null)
            {
                try { await run.WriteReportAsync("contract-failed", failure); }
                catch (Exception reportFailure) { failures.Add(reportFailure); failureDetails.Add(new("report", reportFailure.Message)); }
            }
        }
        finally
        {
            await deadline.CancelAsync();
            if (worker is { IsCompleted: false })
            {
                try { await worker.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { output.WriteLine("Journey worker did not drain after cancellation; stopping owned drivers and app."); }
                catch (Exception workerFailure)
                {
                    if (!failures.Contains(workerFailure)) failures.Add(workerFailure);
                    failureDetails.Add(new("automation", "Journey worker failed while draining cancellation."));
                }
            }
            foreach (var driver in _drivers)
            {
                try { await driver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception cleanupFailure) { cleanupSucceeded = false; failures.Add(cleanupFailure); failureDetails.Add(new("cleanup", cleanupFailure.Message)); }
            }
            if (run is not null)
            {
                try { await run.DisposeAsync(); }
                catch (Exception cleanupFailure) { cleanupSucceeded = false; failures.Add(cleanupFailure); failureDetails.Add(new("cleanup", cleanupFailure.Message)); }
            }
            if (worker is { IsCompleted: false })
            {
                try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception workerFailure)
                {
                    cleanupSucceeded = false;
                    failures.Add(new InvalidOperationException("Timed-out journey worker could not finish cleanly.", workerFailure));
                    failureDetails.Add(new("cleanup", "Timed-out journey worker could not finish cleanly."));
                }
            }
            if (before is not null)
            {
                try
                {
                    changes = ProfileMetadataSnapshot.Compare(before, RealProfileMetadata());
                    isolationUnchanged = changes.Count == 0;
                    if (isolationUnchanged == false)
                    {
                        failures.Add(new InvalidOperationException(
                            $"Isolation check failed: {changes.Count} real-profile metadata entries changed. " +
                            "Changed paths and before/after metadata are in the case report. Writer attribution is unknown."));
                        failureDetails.Add(new("isolation", "Real-profile metadata changed; writer attribution is unknown."));
                    }
                }
                catch (Exception auditFailure) { failures.Add(auditFailure); failureDetails.Add(new("isolation-audit", auditFailure.Message)); }
            }
            if ((setupAttempted && cleanupSucceeded != true) || (isolationAuditAttempted && isolationUnchanged != true))
                Volatile.Write(ref _isolationFailed, 1);
            if (run is not null)
            {
                try
                {
                    await run.FinalizeContractReportAsync(cleanupSucceeded == true, isolationUnchanged == true,
                        failures.Count == 0 ? null : new AggregateException(failures));
                }
                catch (Exception reportFailure) { failures.Add(reportFailure); failureDetails.Add(new("report", reportFailure.Message)); }
            }
            var report = new ContractJourneyReport(caseId, run?.Profile.RunId, behaviorPassed, isolationUnchanged,
                cleanupSucceeded, failures.Count == 0 ? null : string.Join(Environment.NewLine, failures.Select(f => f.Message)),
                changes, isolationAuditAttempted, setupAttempted, behaviorAttempted, failureDetails, _checkpoints.ToArray());
            try
            {
                await WriteCaseReportAsync(report);
                output.WriteLine($"{caseId}: behavior={report.Behavior}; isolation={report.Isolation}; " +
                    $"cleanup={report.Cleanup}; qualification={report.Qualification}.");
            }
            catch (Exception reportFailure)
            {
                failures.Add(reportFailure);
                output.WriteLine($"{caseId}: case evidence could not be written; qualification is blocked.");
            }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Contract journey, cleanup or isolation checks failed.", failures);
    }

    private static string LvtPath() => Environment.GetEnvironmentVariable("OPENCLAW_LVT_PATH")
        ?? throw new InvalidOperationException("Set OPENCLAW_LVT_PATH to the pinned lvt.exe.");

    private async Task<LvtUiaSession> ConnectAsync(GatewayFixtureRun run, AutomationElement window,
        CancellationToken cancellation)
    {
        var driver = await LvtUiaSession.ConnectAsync(LvtPath(), run.AppProcessId, run.AppProcessStartUtc,
            new nint(window.Current.NativeWindowHandle), run.Profile.DataDirectory, run.Profile.RoamingRoot,
            run.Profile.LocalRoot, cancellation);
        _drivers.Add(driver);
        try
        {
            await File.AppendAllTextAsync(Path.Combine(run.ArtifactsDirectory, "lvt-ownership.jsonl"),
                JsonSerializer.Serialize(new
                {
                    pid = driver.ServerProcessId, startUtc = driver.ServerProcessStartUtc,
                    executable = LvtPath(), arguments = new[] { "mcp", "--allow-input" },
                    version = LvtUiaSession.Version, sha256 = LvtUiaSession.ExecutableSha256,
                    appPid = run.AppProcessId, appStartUtc = run.AppProcessStartUtc,
                    hwnd = window.Current.NativeWindowHandle
                }) + Environment.NewLine, cancellation);
            return driver;
        }
        catch
        {
            await driver.DisposeAsync();
            throw;
        }
    }

    private static async Task<AutomationElement> WaitWindowAsync(GatewayFixtureRun run, string marker,
        CancellationToken cancellation)
    {
        AutomationElement? found = null;
        try
        {
            await run.WaitForAsync(() =>
            {
                found = AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, run.AppProcessId))
                    .Cast<AutomationElement>().SingleOrDefault(window => !window.Current.IsOffscreen
                        && window.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, marker)) is not null);
                return Task.FromResult(found is not null);
            }, $"owned window containing {marker}", cancellationToken: cancellation);
        }
        catch (TimeoutException)
        {
            throw new ContractAssertionException($"Required owned startup/navigation window containing '{marker}' did not appear.");
        }
        return found!;
    }

    private static bool Visible(JsonElement element) =>
        !bool.Parse(LvtProtocol.Property(element, "IsOffscreen"));

    private static bool ToggleEquals(JsonElement element, bool expected) =>
        Visible(element) && LvtProtocol.Property(element, "Toggle.ToggleState") == (expected ? "On" : "Off");

    private void Capture(GatewayFixtureRun run, AutomationElement window, string name, string assertedId,
        params AutomationElement[] extraAssertions)
    {
        try
        {
            var control = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, assertedId))
                ?? throw new InvalidOperationException($"Cannot capture asserted control '{assertedId}'.");
            OwnedWindowCapture.Save(run, window, name, new[] { control }.Concat(extraAssertions).ToArray());
            Observe($"capture:{name}");
        }
        catch (Exception failure) { throw new ContractCaptureException($"Required capture '{name}' failed.", failure); }
    }

    private void Observe(string checkpoint) => _checkpoints.Enqueue(new(checkpoint, DateTimeOffset.UtcNow));

    private sealed record Preferences(bool ShowChatToolCalls, bool NotifyInfo, bool AutoStart,
        bool EnableNodeMode, bool NodeSystemRunEnabled, bool VoiceTtsEnabled);

    private static Preferences ReadPreferences(GatewayFixtureRun run)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Profile.DataDirectory, "settings.json")));
        var root = settings.RootElement;
        return new(root.GetProperty("ShowChatToolCalls").GetBoolean(), root.GetProperty("NotifyInfo").GetBoolean(),
            root.GetProperty("AutoStart").GetBoolean(), root.GetProperty("EnableNodeMode").GetBoolean(),
            root.GetProperty("NodeSystemRunEnabled").GetBoolean(), root.GetProperty("VoiceTtsEnabled").GetBoolean());
    }

    private static async Task WriteCaseReportAsync(ContractJourneyReport report)
    {
        var directory = Environment.GetEnvironmentVariable("OPENCLAW_CONTRACT_REPORTS_DIR")
            ?? throw new InvalidOperationException("The contract runner must supply an evidence directory.");
        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("The contract evidence directory must be absolute.");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{report.CaseId}.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IReadOnlyDictionary<string, ProfileFileMetadata> RealProfileMetadata() =>
        ProfileMetadataSnapshot.Capture(new Dictionary<string, string>
        {
            ["ApplicationData"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray"),
            ["LocalApplicationData"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClawTray")
        });
}
