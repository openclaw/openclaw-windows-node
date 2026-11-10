// Full-app proof for the Gateway Dashboard hand-offs that replaced the native Channels page.
//
// A fixture run never starts the user's browser. DashboardBrowser logs the redacted launch target
// instead, and these tests read that log from the run's isolated profile. A one-shot marker file
// makes the next launch fail so the visible retry paths can be exercised.
//
// Usage: opt in with scripts\test-gateway-fixture.ps1 like every other [GatewayFixtureUiFact] test.
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.Connection;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Services;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string ChannelsRoute = "channels";
    private const string TrayLogFileName = "openclaw-tray.log";
    private const string ScreenshotsVariable = "OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS";
    private const string SetupVerifyMethod = "openclaw.setup.verify";
    private const string RetryButtonName = "Retry";
    private const string PairDeviceMenuItemName = "Pair a device";
    // The account menu opens the Dashboard Devices page through its upstream /nodes alias.
    private const string PairDeviceRoute = "nodes";
    private const string OwnerButtonId = "WorkspaceOwner";
    private const string SetupFailureTitle = "Could not open your setup destination";
    private const string SetupChangedMessage = "The verified Gateway or agent changed.";
    private const string SetupUnavailableMessage = "The verified Gateway or app page is not ready.";
    private const string DashboardFailureMessage = "Dashboard could not open.";

    private enum DashboardOutcome { Opened, SimulatedFailure }

    private sealed record DashboardLaunch(DashboardOutcome Outcome, string Target);

    private static readonly (string Prefix, DashboardOutcome Outcome)[] DashboardLogMarkers =
    [
        (DashboardBrowser.SuppressedLaunchLogPrefix, DashboardOutcome.Opened),
        (DashboardBrowser.SimulatedFailureLogPrefix, DashboardOutcome.SimulatedFailure),
    ];

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task LegacyChannelsReceipt_OpensTheGatewayDashboardChannelsView()
    {
        await WithAppAsync(async run =>
        {
            await WaitForDashboardLaunchesAsync(run, 1, "verified Channels receipt opening the Dashboard");
            await WaitReceiptConsumedAsync(run);
            AssertDashboardLaunches(run, DashboardOutcome.Opened);
            Assert.Equal(1, SetupVerifications(run));
            Assert.Null(FindText(run, SetupFailureTitle));
            Assert.Null(FindText(run, DashboardFailureMessage));
        }, scenario: VerifyingScenario(),
            prepareSetupHandoff: profile => IssueSyntheticHandoff(profile, SetupNativeDestination.Channels));
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task LegacyChannelsReceipt_RevokedIdentityIsRejectedBeforeAnyDashboardLaunch()
    {
        using var releaseVerification = new ManualResetEventSlim();
        var verificationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = VerifyingScenario(() =>
        {
            verificationRequested.TrySetResult();
            if (!releaseVerification.Wait(HandoffSettleTimeout))
                throw new TimeoutException("The revocation proof did not release synthetic model verification.");
        });
        await WithAppAsync(async run =>
        {
            await OpenWorkspaceAsync(run);
            await SendHandoffAsync(run, SetupNativeDestination.Channels);
            await verificationRequested.Task.WaitAsync(HandoffSettleTimeout);
            RotatePersistedIdentity(run);
            releaseVerification.Set();

            // The dialog is only shown after the pipeline decided, so a missing launch line here is final.
            await WaitUiAsync(run, () => FindText(run, SetupChangedMessage) is { Current.IsOffscreen: false },
                "rejected Channels receipt dialog", HandoffSettleTimeout);
            Assert.NotNull(FindText(run, SetupFailureTitle));
            Assert.Empty(FindButtons(run, RetryButtonName));
            await WaitReceiptConsumedAsync(run);
            AssertDashboardLaunches(run);
            Assert.Equal(1, SetupVerifications(run));
            CaptureDialog(run, SetupFailureTitle, "channels-receipt-identity-revoked.png",
                FindText(run, SetupChangedMessage)!);
        }, scenario: scenario, releasePendingResponses: releaseVerification.Set);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task LegacyChannelsReceipt_BrowserFailureKeepsTheReceiptAndRetryOpensTheDashboard()
    {
        await WithAppAsync(async run =>
        {
            await OpenWorkspaceAsync(run);
            ArmSimulatedBrowserFailure(run);
            await SendHandoffAsync(run, SetupNativeDestination.Channels);
            await WaitUiAsync(run, () => FindText(run, SetupUnavailableMessage) is { Current.IsOffscreen: false },
                "Channels receipt failure dialog", HandoffSettleTimeout);
            Assert.NotNull(FindText(run, SetupFailureTitle));
            var retry = Assert.Single(FindButtons(run, RetryButtonName));
            AssertDashboardLaunches(run, DashboardOutcome.SimulatedFailure);
            Assert.True(File.Exists(PendingReceiptPath(run)),
                "A failed browser launch must keep the receipt for an explicit retry.");
            Assert.Equal(1, SetupVerifications(run));
            CaptureDialog(run, SetupFailureTitle, "channels-receipt-browser-failed.png",
                FindText(run, SetupUnavailableMessage)!, retry);

            Invoke(retry);
            await WaitForDashboardLaunchesAsync(run, 2, "explicit retry opening the Dashboard");
            await WaitReceiptConsumedAsync(run);
            AssertDashboardLaunches(run, DashboardOutcome.SimulatedFailure, DashboardOutcome.Opened);
            Assert.Equal(2, SetupVerifications(run));
            await WaitUiAsync(run, () => FindText(run, SetupFailureTitle) is null,
                "failure dialog closed by the retry", HandoffSettleTimeout);
        }, scenario: VerifyingScenario());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task AccountMenu_CurrentGatewayExposesItsStateWithoutAnAccessibleShortcut()
    {
        await WithAppAsync(async run =>
        {
            await OpenWorkspaceAsync(run);
            Invoke(FindById(run, OwnerButtonId)!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceOwnerGateway") is not null, "Gateway submenu ready");
            ((ExpandCollapsePattern)FindById(run, "WorkspaceOwnerGateway")!
                .GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            await WaitUiAsync(run, () => FindById(run, "WorkspaceOwnerGateway_0") is not null, "current Gateway row ready");
            var current = FindById(run, "WorkspaceOwnerGateway_0")!;
            Assert.Equal("Current gateway", current.Current.ItemStatus);
            Assert.Equal("Current gateway. Connected", current.Current.HelpText);
            Assert.Empty(current.Current.AcceleratorKey);
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ChannelsNavigation_OpensTheGatewayDashboardAndOffersRetryWhenTheBrowserFails()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false }
                && FindById(run, OwnerButtonId)?.Current.Name == "Fixture",
                "Workspace chat with the authenticated owner");

            await run.InvokeAsync("app.navigate", new { page = ChannelsRoute });
            await WaitForDashboardLaunchesAsync(run, 1, "Channels navigation opening the Dashboard");
            Assert.NotNull(FindById(run, "ChatComposerInput"));

            await InvokeOwnerMenuItemAsync(run, PairDeviceMenuItemName);
            await WaitForDashboardLaunchesAsync(run, 2, "Pair a device opening the Dashboard Devices page");

            ArmSimulatedBrowserFailure(run);
            await InvokeOwnerMenuItemAsync(run, PairDeviceMenuItemName);
            await WaitUiAsync(run, () => FindText(run, DashboardFailureMessage) is { Current.IsOffscreen: false },
                "Dashboard failure dialog", HandoffSettleTimeout);
            var retry = Assert.Single(FindButtons(run, RetryButtonName));
            AssertDashboardLaunchRoutes(run, (DashboardOutcome.Opened, ChannelsRoute), (DashboardOutcome.Opened, PairDeviceRoute),
                (DashboardOutcome.SimulatedFailure, PairDeviceRoute));
            CaptureDialog(run, DashboardFailureMessage, "channels-navigation-browser-failed.png", retry);

            Invoke(retry);
            await WaitForDashboardLaunchesAsync(run, 4, "Retry opening the Dashboard");
            AssertDashboardLaunchRoutes(run, (DashboardOutcome.Opened, ChannelsRoute), (DashboardOutcome.Opened, PairDeviceRoute),
                (DashboardOutcome.SimulatedFailure, PairDeviceRoute), (DashboardOutcome.Opened, PairDeviceRoute));
            await WaitUiAsync(run, () => FindText(run, DashboardFailureMessage) is null,
                "Dashboard failure dialog closed by the retry", HandoffSettleTimeout);
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ChannelsSearch_OpensDashboardFromWorkspaceAndSettings()
    {
        await WithAppAsync(async run =>
        {
            await OpenWorkspaceAsync(run);
            using var tools = await run.Client.ListToolsAsync();
            Assert.Contains(tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
                tool => tool.GetProperty("name").GetString() == "app.search");
            var matches = await run.InvokeAsync("app.search", new { query = "Go to Channels" });
            var match = Assert.Single(matches.EnumerateArray(),
                item => item.GetProperty("Title").GetString() == "Channels");
            Assert.Equal("Dashboard", match.GetProperty("Group").GetString());
            Assert.True(match.GetProperty("OpensDashboard").GetBoolean());
            Assert.True(match.GetProperty("HasDashboardCounterpart").GetBoolean());
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "channels-search-mcp.json"), matches.GetRawText());

            var window = FindHub(run);
            await SearchAndOpenAsync("WorkspaceSearch", "WorkspaceSearchBox", "Channels", "{ENTER}", 1);
            Assert.NotNull(FindById(run, "ChatComposerInput"));

            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is { Current.IsOffscreen: false }
                && FindById(run, "SettingsSearch") is { Current.IsOffscreen: false }, "Settings search ready");
            await SearchAndOpenAsync("SettingsSearch", "SettingsSearchBox", "Go to Channels", "%{ENTER}", 2);
            Assert.NotNull(FindById(run, "SettingsPageMarker"));
            AssertDashboardLaunches(run, DashboardOutcome.Opened, DashboardOutcome.Opened);
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "channels.status");

            async Task SearchAndOpenAsync(string buttonId, string inputId, string query, string keys, int launches)
            {
                SetForeground(window);
                Invoke(FindById(run, buttonId)!);
                await WaitUiAsync(run, () => FindById(run, inputId) is not null, $"{inputId} opened");
                var input = SearchEdit(run, inputId);
                ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue(query);
                await WaitUiAsync(run, () => FindById(run, "SearchResult_0") is { Current.IsOffscreen: false } result
                    && result.Current.Name.StartsWith("Channels.", StringComparison.Ordinal)
                    && result.Current.Name.Contains("Dashboard", StringComparison.Ordinal),
                    $"Channels Dashboard result for {inputId}");
                await WaitUiAsync(run, () => input.Current.HasKeyboardFocus ||
                    input.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true)) is not null,
                    $"{inputId} focused");
                OwnedWindowCapture.Save(run, window, $"{inputId}-channels-dashboard.png", FindById(run, "SearchResult_0")!);
                Assert.True(ForegroundBelongsTo(run.AppProcessId),
                    "The fixture lost foreground before search activation; no keys were sent.");
                Assert.NotNull(FindById(run, inputId));
                Send(run, window, keys);
                await WaitForDashboardLaunchesAsync(run, launches, $"{inputId} opening Dashboard Channels");
                await WaitUiAsync(run, () => FindById(run, inputId) is null, $"{inputId} closed");
            }
        }, scenario: GatewayScenario.CreateSearchNavigation());
    }

    private static GatewayScenario VerifyingScenario(Action? onVerify = null) =>
        GatewayScenario.CreateNativeSetup((method, _) =>
        {
            if (method != SetupVerifyMethod)
                throw new InvalidOperationException($"Unexpected synthetic setup request: {method}");
            onVerify?.Invoke();
            return new { ok = true, modelRef = "fixture/browse", latencyMs = 1 };
        });

    private static int SetupVerifications(GatewayFixtureRun run) =>
        run.Gateway.Requests.Count(request => request.Method == SetupVerifyMethod && request.Outcome == "ok");

    private static async Task OpenWorkspaceAsync(GatewayFixtureRun run)
    {
        await run.InvokeAsync("app.navigate", new { page = "chat" });
        await WaitVerifiedChatAsync(run);
    }

    // The failure dialogs need a mounted Workspace window, so these receipts reach the running app
    // through a forwarding launch exactly as an installer-started setup would.
    private static async Task SendHandoffAsync(GatewayFixtureRun run, SetupNativeDestination destination)
    {
        var start = run.Profile.CreateStartInfo(
            Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")!, run.McpPort,
            IssueSyntheticHandoff(run.Profile, destination));
        using var sender = Process.Start(start) ?? throw new InvalidOperationException("Handoff forwarding failed.");
        await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "handoff-sender.json"),
            JsonSerializer.Serialize(new { pid = sender.Id, startedUtc = sender.StartTime.ToUniversalTime(),
                executable = start.FileName, run.Profile.DataDirectory, run.McpPort }));
        try
        {
            await sender.WaitForExitAsync().WaitAsync(HandoffSettleTimeout);
            Assert.Equal(0, sender.ExitCode);
        }
        finally
        {
            if (!sender.HasExited)
            {
                sender.Kill(entireProcessTree: true);
                await sender.WaitForExitAsync();
            }
        }
    }

    // Replaces only the persisted signing identity, so the live connection stays up and the production
    // authority checks are the only thing that can notice.
    private static void RotatePersistedIdentity(GatewayFixtureRun run)
    {
        var identityPath = new GatewayRegistry(run.Profile.DataDirectory).GetIdentityDirectory(run.Profile.GatewayId);
        File.Delete(Path.Combine(identityPath, "device-key-ed25519.json"));
        new DeviceIdentity(identityPath).Initialize();
    }

    private static void ArmSimulatedBrowserFailure(GatewayFixtureRun run) =>
        File.WriteAllText(
            Path.Combine(run.Profile.DataDirectory, DashboardBrowser.SimulatedFailureMarkerFileName), string.Empty);

    private static async Task InvokeOwnerMenuItemAsync(GatewayFixtureRun run, string name)
    {
        var item = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.NameProperty, name));
        Invoke(FindById(run, OwnerButtonId)!);
        AutomationElement? menuItem = null;
        await WaitUiAsync(run, () => (menuItem = FindInApp(run, item)) is not null, $"owner menu item {name}");
        Invoke(menuItem!);
        // Reopening the menu must never find the previous, still closing, popup.
        await WaitUiAsync(run, () => FindInApp(run, item) is null, $"owner menu closed after {name}");
    }

    private static string PendingReceiptPath(GatewayFixtureRun run) =>
        Path.Combine(run.Profile.DataDirectory, "setup-dashboard-handoff", "pending.json");

    private static Task WaitReceiptConsumedAsync(GatewayFixtureRun run) =>
        run.WaitForAsync(() => Task.FromResult(!File.Exists(PendingReceiptPath(run))),
            "setup handoff receipt consumed", HandoffSettleTimeout);

    private static Task WaitForDashboardLaunchesAsync(GatewayFixtureRun run, int count, string description) =>
        WaitUiAsync(run, () => DashboardLaunches(run).Count >= count, description, HandoffSettleTimeout);

    private static void AssertDashboardLaunches(GatewayFixtureRun run, params DashboardOutcome[] expected) =>
        AssertDashboardLaunchRoutes(run, expected.Select(outcome => (outcome, ChannelsRoute)).ToArray());

    private static void AssertDashboardLaunchRoutes(
        GatewayFixtureRun run, params (DashboardOutcome Outcome, string Route)[] expected)
    {
        var launches = DashboardLaunches(run);
        Assert.Equal(expected.Select(item => item.Outcome), launches.Select(launch => launch.Outcome));
        Assert.All(launches.Zip(expected), pair => Assert.True(IsDashboardRoute(run, pair.First, pair.Second.Route),
            $"The Dashboard target is not this run's Gateway {pair.Second.Route} view: {pair.First.Target}"));
    }

    // The tray logs the URL without its host and credentials, so the Gateway port and route identify the target.
    private static bool IsDashboardRoute(GatewayFixtureRun run, DashboardLaunch launch, string route) =>
        launch.Target.StartsWith("http://", StringComparison.Ordinal)
        && launch.Target.EndsWith($":{run.Gateway.Endpoint.Port}/{route}", StringComparison.Ordinal);

    // DashboardBrowser logs every fixture launch, so the app's own log is the ordered record of browser hand-offs.
    private static List<DashboardLaunch> DashboardLaunches(GatewayFixtureRun run)
    {
        var launches = new List<DashboardLaunch>();
        foreach (var line in TrayLogLines(run))
        {
            foreach (var (prefix, outcome) in DashboardLogMarkers)
            {
                var start = line.IndexOf(prefix, StringComparison.Ordinal);
                if (start >= 0)
                    launches.Add(new DashboardLaunch(outcome, line[(start + prefix.Length)..].Trim()));
            }
        }
        return launches;
    }

    // The app keeps the log open for appending, so read it with a sharing mode that tolerates the writer.
    private static string[] TrayLogLines(GatewayFixtureRun run)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(run.Profile.DataDirectory, TrayLogFileName),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static List<AutomationElement> FindButtons(GatewayFixtureRun run, string name)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, name));
        var buttons = new List<AutomationElement>();
        foreach (var window in AppWindows(run))
        {
            foreach (AutomationElement button in window.FindAll(TreeScope.Descendants, condition))
            {
                try
                {
                    if (!button.Current.IsOffscreen)
                        buttons.Add(button);
                }
                catch (ElementNotAvailableException)
                {
                    // Gone since the tree snapshot, so it is not a visible button.
                }
            }
        }
        return buttons;
    }

    private static AutomationElement WindowWithText(GatewayFixtureRun run, string text) =>
        AppWindows(run).First(window => window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Any(element => element.Current.Name.Contains(text, StringComparison.Ordinal)));

    // A receipt run may have no chat composer, so the window is found by the dialog text it is showing.
    private static void CaptureDialog(GatewayFixtureRun run, string anchorText, string name,
        params AutomationElement[] elements)
    {
        if (Environment.GetEnvironmentVariable(ScreenshotsVariable) != "1")
            return;
        OwnedWindowCapture.Save(run, WindowWithText(run, anchorText), name, [FindText(run, anchorText)!, .. elements]);
    }
}
