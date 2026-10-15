using System.Reflection;
using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Services;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

/// <summary>Real product UI and operator protocol with synthetic provider replies and a non-launching native host.</summary>
[Collection(UICollection.Name)]
public sealed class NativeSharedFlowTests(UIThreadFixture ui, ITestOutputHelper output)
{
    private const string Token = "native-ui-loopback-fixture-not-a-production-token";
    private const string Model = "fixture/browse";
    private const string Family = "OpenClaw.Gateway_123456789abcd";

    [Theory]
    [InlineData(SetupNativeDestination.Chat, ElementTheme.Light, false)]
    [InlineData(SetupNativeDestination.Channels, ElementTheme.Light, false)]
    [InlineData(SetupNativeDestination.Skills, ElementTheme.Light, false)]
    [InlineData(SetupNativeDestination.Chat, ElementTheme.Dark, false)]
    [InlineData(SetupNativeDestination.Channels, ElementTheme.Dark, false)]
    [InlineData(SetupNativeDestination.Skills, ElementTheme.Dark, false)]
    [InlineData(SetupNativeDestination.Chat, ElementTheme.Light, true)]
    [InlineData(SetupNativeDestination.Channels, ElementTheme.Light, true)]
    [InlineData(SetupNativeDestination.Skills, ElementTheme.Light, true)]
    [InlineData(SetupNativeDestination.Chat, ElementTheme.Dark, true)]
    [InlineData(SetupNativeDestination.Channels, ElementTheme.Dark, true)]
    [InlineData(SetupNativeDestination.Skills, ElementTheme.Dark, true)]
    public async Task NativeInstall_UsesSharedAiVerificationAndAllThreeDestinations(
        SetupNativeDestination destination, ElementTheme theme, bool isolated)
    {
        await WithNativeAsync(async (window, frame, registry, runtime, server, completed) =>
        {
            var page = Assert.IsType<AiSetupPage>(frame.Content);
            await WaitAsync(() => Find<ItemsControl>(page, "CandidateChoices").IsEnabled &&
                TestSupport.FindDescendants<SettingsCard>(Find<ItemsControl>(page, "CandidateChoices")).Any(),
                "native candidates");
            Assert.Empty(registry.GetAll());
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "LocalAiSection").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Expander>(page, "NativeRecovery").Visibility);
            Assert.Empty(Find<TextBlock>(page, "NativeOutput").Text);
            Assert.Equal(Visibility.Collapsed, Find<Button>(page, "LegacyButton").Visibility);
            var progress = Find<OpenClaw.SetupEngine.UI.Controls.SetupProgressIndicator>(page, "FlowProgress");
            Assert.Equal(6, progress.Children.Count);
            Assert.Equal(20, Assert.IsType<Border>(progress.Children[4]).Width);
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            await SaveViewportsAsync(window, page, $"native-shared-ai-{destination}-{theme}");
            var card = Assert.Single(TestSupport.FindDescendants<SettingsCard>(Find<ItemsControl>(page, "CandidateChoices")));
            TestSupport.InvokeSettingsCardAction(page, card, "ChoiceAction_Click");
            await WaitAsync(() => completed.Count == 1, "native preparation publication before Ready");
            Assert.IsType<AiCompletionPage>(frame.Content);
            Assert.False(runtime.Running);
            Assert.Equal("native-ui", registry.ActiveGatewayId);
            if (isolated)
            {
                Assert.Equal(NativeGatewayPackageClient.IsolatedContract, registry.GetActive()!.NativeRuntimeContract);
                Assert.DoesNotContain(server.Requests, request => request.Method == "system.run");
                Assert.Contains(server.Requests, request => request.Method == "logs.tail");
                Assert.False(File.Exists(NativeGatewayPaths.GetConfigPath(registry, "native-ui")));
            }
            Assert.True(server.Requests.Count(request => request.Method == "openclaw.setup.verify") >= 3);
            Assert.DoesNotContain(server.Requests, request => request.Method == "wizard.start");
            await WithPublishedReadyAsync(window, registry, server, Assert.Single(completed), theme,
                async (readyWindow, ready, navigated, normalRuntime) =>
                {
                    var choices = Find<StackPanel>(ready, "Choices");
                    Assert.Equal(["Chat", "Channels", "Skills"],
                        choices.Children.Cast<FrameworkElement>().Select(element => element.Tag));
                    Assert.Empty(navigated);
                    Assert.Null(ready.FindName("NativeSummary"));
                    Assert.Null(ready.FindName("NativeGatewaySummary"));
                    Assert.Null(ready.FindName("NativeCapabilitiesSummary"));
                    await SaveViewportsAsync(readyWindow, ready, $"native-shared-ready-{destination}-{theme}");
                    var probes = server.Requests.Count(request => request.Method == "openclaw.setup.verify");
                    var starts = normalRuntime.Starts;
                    var stops = normalRuntime.Stops;
                    var target = Assert.Single(choices.Children.Cast<SettingsCard>(),
                        choice => (string)choice.Tag == destination.ToString());
                    TestSupport.InvokeSettingsCardAction(ready, target, "Choose_Click");
                    await WaitAsync(() => navigated.Count == 1, "native destination navigation");
                    Assert.Equal(destination, Assert.Single(navigated).Target.Destination);
                    Assert.Single(completed);
                    Assert.Equal(probes, server.Requests.Count(request => request.Method == "openclaw.setup.verify"));
                    Assert.Equal(starts, normalRuntime.Starts);
                    Assert.Equal(stops, normalRuntime.Stops);
                });
        }, theme: theme, isolated: isolated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationChangeWithdrawsReadyWithoutReplayingSetup(bool duringMount)
    {
        await WithNativeAsync(async (window, frame, registry, _, server, completed) =>
        {
            var ai = Assert.IsType<AiSetupPage>(frame.Content);
            await WaitAsync(() => Find<ItemsControl>(ai, "CandidateChoices").IsEnabled &&
                TestSupport.FindDescendants<SettingsCard>(Find<ItemsControl>(ai, "CandidateChoices")).Any(),
                "AI candidate loaded");
            TestSupport.InvokeSettingsCardAction(ai,
                Assert.Single(TestSupport.FindDescendants<SettingsCard>(Find<ItemsControl>(ai, "CandidateChoices"))),
                "ChoiceAction_Click");
            await WaitAsync(() => completed.Count == 1, "setup preparation published");
            await WithPublishedReadyAsync(window, registry, server, Assert.Single(completed), ElementTheme.Light,
                async (readyWindow, _, navigated, runtime) =>
                {
                    var before = server.Requests.Count;
                    var starts = runtime.Starts;
                    var stops = runtime.Stops;
                    await server.NotifyConfigurationChangedAsync();
                    var readyFrame = Assert.IsType<Frame>(readyWindow.Content);
                    await WaitAsync(() => readyFrame.Content is AiCompletionPage recovery &&
                        Find<InfoBar>(recovery, "ErrorBar").IsOpen, "configuration event withdraws Ready");
                    Assert.Empty(navigated);
                    Assert.Equal(before, server.Requests.Count);
                    Assert.Equal(starts, runtime.Starts);
                    Assert.Equal(stops, runtime.Stops);
                }, invalidateDuringMount: duringMount, exerciseUnrelatedMetadata: true);
        }, isolated: true);
    }

    [Fact]
    public async Task FailedReturnFromFinishingStillOffersCloseWithoutRetryingSetup()
    {
        await WithNativeAsync(async (window, frame, _, runtime, _, completed) =>
        {
            await Assert.IsType<AiSetupPage>(frame.Content).CloseAsync();
            var proof = new GatewayAiSetupCompletion(SetupCompletionIntent.CustodianOnboarding,
                "native-ui", new string('A', 64), Model, "main", 1,
                IdentityBinding: new string('B', 64), SessionKey: "agent:main:main");
            using var owner = new SetupCompletionPreparation(proof,
                _ => Task.CompletedTask,
                (_, _) => throw new InvalidOperationException("Must not verify"),
                (_, _) => throw new InvalidOperationException("Must not finalize"),
                (_, _) => throw new InvalidOperationException("Must not publish"));
            typeof(SetupWindow).GetField("_completionPreparation", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(window, owner);
            frame.Navigate(typeof(AiCompletionPage));
            runtime.DisposeFailure = new IOException("Synthetic native release failure");
            await Assert.IsAssignableFrom<Task>(typeof(SetupWindow)
                .GetMethod("ReturnFromFinishingAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [owner]));
            var recovery = Assert.IsType<AiCompletionPage>(frame.Content);
            Assert.True(Find<InfoBar>(recovery, "ErrorBar").IsOpen);
            Assert.Equal(Visibility.Collapsed, Find<Button>(recovery, "RetryButton").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Button>(recovery, "BackButton").Visibility);
            var close = Find<Button>(recovery, "CloseButton");
            Assert.Equal(Visibility.Visible, close.Visibility);
            Assert.True(close.IsEnabled);
            await WaitAsync(() => close.IsLoaded, "failed cleanup Close action loaded");
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR")))
            {
                window.AppWindow.Move(new(120, 120));
                OnboardingNativeProof.ActivateOwned(window);
                using (await OnboardingNativeProof.CaptureAsync(window, "finishing-cleanup-failed-close", output,
                    ["Finishing setup", "Close"], requiredContent: recovery)) { }
            }
            Invoke(close);
            await WaitAsync(() => window.IsClosed, "failed cleanup can close setup");
            Assert.Empty(completed);
        }, isolated: true);
    }

    [Fact]
    public async Task NativeApiUnavailable_StaysOnExplicitFallbackWithoutPublishing()
    {
        await WithNativeAsync(async (_, frame, registry, _, server, completed) =>
        {
            var page = Assert.IsType<AiSetupPage>(frame.Content);
            await WaitAsync(() => Find<Button>(page, "LegacyButton").Visibility == Visibility.Visible, "explicit unsupported fallback");
            Assert.Empty(registry.GetAll());
            Assert.Empty(completed);
            Assert.DoesNotContain(server.Requests, request => request.Method == "wizard.start");
        }, supported: false);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task NativeStartupUsesVisibleChoiceAndRetainsItAcrossRegistrationRetry(
        bool selectedStartup, bool failOnce, bool isolated)
    {
        var applications = new List<bool>();
        await WithNativeAsync(async (window, frame, registry, runtime, server, completed) =>
        {
            var ai = Assert.IsType<AiSetupPage>(frame.Content);
            await WaitAsync(() => Find<ItemsControl>(ai, "CandidateChoices").IsEnabled &&
                TestSupport.FindDescendants<SettingsCard>(Find<ItemsControl>(ai, "CandidateChoices")).Any(), "native AI choice");
            Assert.Empty(applications);
            var candidate = Assert.Single(TestSupport.FindDescendants<SettingsCard>(Find<ItemsControl>(ai, "CandidateChoices")));
            TestSupport.InvokeSettingsCardAction(ai, candidate, "ChoiceAction_Click");
            (int Stops, int Verifications)? beforeRetry = null;
            if (failOnce)
            {
                await WaitAsync(() => frame.Content is AiCompletionPage finishing &&
                    Find<InfoBar>(finishing, "ErrorBar").IsOpen &&
                    Find<Button>(finishing, "RetryButton").IsEnabled, "startup failure before Ready");
                var finishing = Assert.IsType<AiCompletionPage>(frame.Content);
                Assert.Empty(completed);
                Assert.Equal([selectedStartup], applications);
                Assert.Equal(selectedStartup, window.AutoStartAfterSetup);
                beforeRetry = (runtime.Stops,
                    server.Requests.Count(request => request.Method == "openclaw.setup.verify"));
                Assert.True(beforeRetry.Value.Stops > 0);
                Assert.True(beforeRetry.Value.Verifications >= 3);
                Invoke(Find<Button>(finishing, "RetryButton"));
            }
            await WaitAsync(() => completed.Count == 1, "startup choice applied before preparation publication");
            if (beforeRetry is { } finalized)
            {
                Assert.Equal(finalized.Stops, runtime.Stops);
                Assert.Equal(finalized.Verifications,
                    server.Requests.Count(request => request.Method == "openclaw.setup.verify"));
            }
            Assert.IsType<AiCompletionPage>(frame.Content);
            Assert.Equal(failOnce ? [selectedStartup, selectedStartup] : new[] { selectedStartup }, applications);
            Assert.Equal("native-ui", registry.ActiveGatewayId);
            var data = Assert.IsType<string>(typeof(SetupWindow).GetProperty("DataDir",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "settings.json")));
            Assert.Equal(selectedStartup, saved.RootElement.GetProperty("AutoStart").GetBoolean());
        }, isolated: isolated, startupRegistrationAllowed: true,
            applyStartup: (enabled, _) =>
            {
                applications.Add(enabled);
                return failOnce && applications.Count == 1
                    ? Task.FromException(new IOException("Synthetic startup registration failure"))
                    : Task.CompletedTask;
            },
            beforeAi: async (window, frame) =>
            {
                await WaitAsync(() => frame.Content is CapabilitiesPage { IsLoaded: true }, "native capability choice");
                var page = Assert.IsType<CapabilitiesPage>(frame.Content);
                var row = Find<SettingsCard>(page, "StartupPreferenceRow");
                Assert.Equal(Visibility.Visible, row.Visibility);
                var toggle = Find<ToggleSwitch>(page, "StartupPreferenceToggle");
                row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                await ui.YieldToRenderAsync();
                await OnboardingNativeProof.NextCompositionAsync();
                page.UpdateLayout();
                var scroll = Assert.Single(Assert.IsType<Grid>(page.Content).Children.OfType<ScrollViewer>());
                OnboardingNativeProof.AssertFullyVisible(toggle, scroll);
                toggle.IsOn = !selectedStartup;
                toggle.IsOn = selectedStartup;
                Assert.Equal(selectedStartup, window.AutoStartAfterSetup);
                Assert.Empty(applications);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedConsoleGapOrFailureUsesExistingErrorRecoveryWithoutHostLog(bool unavailable)
    {
        const string oldLine = """{"_meta":{"name":"openclaw","path":{"method":"console.log"}},"message":"stale sign-in prompt"}""";
        const string currentLine = """{"_meta":{"name":"openclaw","path":{"method":"console.log"}},"message":"current provider guidance"}""";
        await WithNativeAsync(async (_, frame, registry, _, server, completed) =>
        {
            var page = Assert.IsType<AiSetupPage>(frame.Content);
            await WaitAsync(() => Find<InfoBar>(page, "ErrorBar").IsOpen &&
                Find<Expander>(page, "NativeRecovery").Visibility == Visibility.Visible, "isolated console recovery");
            var error = Find<InfoBar>(page, "ErrorBar").Message;
            Assert.Contains(unavailable ? "unavailable" : "skipped", error);
            Assert.Contains("terminal", error);
            Assert.DoesNotContain("stale sign-in prompt", Find<TextBlock>(page, "NativeOutput").Text);
            if (!unavailable) Assert.Contains("current provider guidance", Find<TextBlock>(page, "NativeOutput").Text);
            Assert.Contains(server.Requests, request => request.Method == "logs.tail");
            Assert.DoesNotContain(server.Requests, request => request.Method == "wizard.start");
            Assert.False(File.Exists(NativeGatewayPaths.GetConfigPath(registry, "native-ui")));
            Assert.Empty(registry.GetAll());
            Assert.Empty(completed);
        }, isolated: true, logReply: parameters =>
        {
            if (!parameters.TryGetProperty("cursor", out _))
                return new { file = "agent.log", cursor = 100, size = 100, lines = new[] { oldLine } };
            if (unavailable)
                return new { malformed = true };
            return new { file = "agent.log", cursor = 200, size = 200, skippedBytes = 10, lines = new[] { currentLine } };
        });
    }

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task MinimumWindow_SharedAiAndVerifiedChoicesRemainReachable(ElementTheme theme)
    {
        await WithNativeAsync(async (window, frame, registry, _, server, completed) =>
        {
            var page = Assert.IsType<AiSetupPage>(frame.Content);
            await WaitAsync(() => Find<ItemsControl>(page, "CandidateChoices").IsEnabled &&
                TestSupport.FindDescendants<SettingsCard>(Find<ItemsControl>(page, "CandidateChoices")).Any(), "minimum AI ready");
            await SetupWindowMinimumSizeTests.ResizeBelowMinimumAsync(window, ui, output, $"native-ai-{theme}");
            Assert.Equal(Visibility.Collapsed, Find<Expander>(page, "NativeRecovery").Visibility);
            page.UpdateLayout();
            await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromMilliseconds(400));
            var root = Assert.IsType<Grid>(window.Content);
            OnboardingNativeProof.AssertFullyVisible(Find<Button>(page, "RefreshButton"), root);
            await SettleCaptureAsync(window);
            await SetupWindowMinimumSizeTests.SaveViewportsAsync(window, page, ui, output, $"setup-minimum-shared-ai-{theme}");
            var card = Assert.Single(TestSupport.FindDescendants<SettingsCard>(Find<ItemsControl>(page, "CandidateChoices")));
            TestSupport.InvokeSettingsCardAction(page, card, "ChoiceAction_Click");
            await WaitAsync(() => completed.Count == 1, "minimum preparation completed");
            Assert.Equal("native-ui", registry.ActiveGatewayId);
            await WithPublishedReadyAsync(window, registry, server, Assert.Single(completed), theme,
                async (readyWindow, ready, navigated, _) =>
                {
                    await SetupWindowMinimumSizeTests.ResizeBelowMinimumAsync(readyWindow, ui, output, $"native-ready-{theme}");
                    Assert.Null(ready.FindName("NativeSummary"));
                    ready.UpdateLayout();
                    await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromMilliseconds(400));
                    var readyRoot = Assert.IsAssignableFrom<FrameworkElement>(readyWindow.Content);
                    Assert.Equal(3, Find<StackPanel>(ready, "Choices").Children.Count);
                    OnboardingNativeProof.AssertFullyVisible(
                        Find<OpenClaw.SetupEngine.UI.Controls.SetupProgressIndicator>(ready, "FlowProgress"), readyRoot);
                    await SetupWindowMinimumSizeTests.SaveViewportsAsync(
                        readyWindow, ready, ui, output, $"setup-minimum-ready-{theme}");
                    Assert.Empty(navigated);
                });
        }, theme: theme);
    }

    [Fact]
    public async Task NativeCancel_RetainsProfileAndNodeIdentityWithoutPublishing()
    {
        await WithNativeAsync(async (window, frame, registry, runtime, _, completed) =>
        {
            var page = Assert.IsType<AiSetupPage>(frame.Content);
            await WaitAsync(() => Find<InfoBar>(page, "ErrorBar").IsOpen &&
                Find<Expander>(page, "NativeRecovery").Visibility == Visibility.Visible, "native error before cancel");
            var recovery = Find<Expander>(page, "NativeRecovery");
            recovery.IsExpanded = true;
            await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromMilliseconds(400));
            Invoke(Assert.Single(TestSupport.FindDescendants<Button>(recovery), button => (string?)button.Tag == "CancelSetup"));
            await WaitAsync(() => frame.Content is CapabilitiesPage { IsLoaded: true }, "native cancel returns to access");
            Assert.Null(typeof(SetupWindow).GetProperty("NativeSetupSession", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window));
            Assert.False(runtime.Running);
            Assert.Empty(completed);
            Assert.Empty(registry.GetAll());
            Assert.True(File.Exists(NativeGatewayPaths.GetConfigPath(registry, "native-ui")));
            var identity = new DeviceIdentity(registry.GetIdentityDirectory("native-ui"));
            identity.LoadExisting();
            Assert.Equal("fixture-node-token", identity.NodeDeviceToken);
        }, failDetectionOnce: true);
    }

    [Fact]
    public async Task UncertainProviderCancellation_DoesNotRestartOrPublish()
    {
        await WithNativeAsync(async (_, frame, registry, runtime, server, completed) =>
        {
            var page = Assert.IsType<AiSetupPage>(frame.Content);
            await WaitAsync(() => Find<ItemsControl>(page, "CandidateChoices").IsEnabled &&
                TestSupport.FindDescendants<SettingsCard>(Find<ItemsControl>(page, "CandidateChoices")).Any(), "candidate");
            var card = Assert.Single(TestSupport.FindDescendants<SettingsCard>(Find<ItemsControl>(page, "CandidateChoices")));
            TestSupport.InvokeSettingsCardAction(page, card, "ChoiceAction_Click");
            var dialog = Assert.IsType<OpenClaw.SetupEngine.UI.Controls.ProviderSetupDialog>(
                typeof(AiSetupPage).GetField("_providerDialog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
            await WaitAsync(() => dialog.IsLoaded && dialog.CanSubmit &&
                dialog.Title?.ToString() == "Native provider input", "provider prompt");
            Assert.Equal(Visibility.Collapsed, Find<Expander>(dialog, "NativeRecovery").Visibility);
            var stops = runtime.Stops;
            Invoke(Find<Button>(dialog, "CancelButton"));
            await WaitAsync(() => server.Requests.Any(request => request.Method == "wizard.cancel"), "cancel request");
            var recovery = Find<Expander>(dialog, "NativeRecovery");
            await WaitAsync(() => recovery.Visibility == Visibility.Visible, "uncertain recovery");
            recovery.IsExpanded = true;
            await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromMilliseconds(400));
            Invoke(Assert.Single(TestSupport.FindDescendants<Button>(recovery), button => (string?)button.Tag == "RestartGateway"));
            await WaitAsync(() => Find<InfoBar>(dialog, "DialogError").IsOpen, "uncertain cancellation");
            Assert.Equal(stops, runtime.Stops);
            Assert.Single(server.Requests, request => request.Method == "openclaw.setup.activate.start");
            Assert.Empty(registry.GetAll());
            Assert.Empty(completed);
        }, holdProvider: true);
    }

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task NativeRecovery_ErrorOnlyAndHiddenAgainAfterSuccessfulRefresh(ElementTheme theme)
    {
        await WithNativeAsync(async (window, frame, registry, _, _, completed) =>
        {
            var page = Assert.IsType<AiSetupPage>(frame.Content);
            var recovery = Find<Expander>(page, "NativeRecovery");
            await WaitAsync(() => Find<InfoBar>(page, "ErrorBar").IsOpen &&
                recovery.Visibility == Visibility.Visible, "native recovery error");
            await SetupWindowMinimumSizeTests.ResizeBelowMinimumAsync(window, ui, output, $"native-error-{theme}");
            recovery.IsExpanded = true;
            page.UpdateLayout();
            await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromMilliseconds(400));
            Assert.Equal(new[] { "OpenTerminal", "RestartGateway", "RestartAi", "CancelSetup" },
                TestSupport.FindDescendants<Button>(recovery).Where(button => button.Tag is string).Select(button => (string)button.Tag));
            await SettleCaptureAsync(window);
            await SetupWindowMinimumSizeTests.SaveViewportsAsync(window, page, ui, output, $"native-error-recovery-{theme}");
            Invoke(Find<Button>(page, "RefreshButton"));
            await WaitAsync(() => Find<ItemsControl>(page, "CandidateChoices").IsEnabled &&
                !Find<InfoBar>(page, "ErrorBar").IsOpen, "healthy native choices restored");
            Assert.Equal(Visibility.Collapsed, recovery.Visibility);
            Assert.False(recovery.IsExpanded);
            Assert.Empty(Find<TextBlock>(page, "NativeOutput").Text);
            var dialog = Assert.IsType<OpenClaw.SetupEngine.UI.Controls.ProviderSetupDialog>(
                typeof(AiSetupPage).GetField("_providerDialog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
            Assert.Equal(Visibility.Collapsed, Find<Expander>(dialog, "NativeRecovery").Visibility);
            await SettleCaptureAsync(window);
            await SetupWindowMinimumSizeTests.SaveViewportsAsync(window, page, ui, output, $"native-normal-after-recovery-{theme}");
            Assert.Empty(registry.GetAll());
            Assert.Empty(completed);
        }, theme: theme, failDetectionOnce: true);
    }

    private async Task WithNativeAsync(
        Func<SetupWindow, Frame, GatewayRegistry, Runtime, FixtureGatewayServer, List<SetupNativePreparation>, Task> assertion,
        ElementTheme theme = ElementTheme.Light, bool supported = true, bool holdProvider = false, bool failDetectionOnce = false,
        Func<string, JsonElement, object>? wizardReply = null, bool isolated = false,
        Func<JsonElement, object>? logReply = null, bool startupRegistrationAllowed = false,
        Func<bool, CancellationToken, Task>? applyStartup = null,
        Func<SetupWindow, Frame, Task>? beforeAi = null)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        var detectionAttempts = 0;
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateNativeSetup((method, parameters) =>
        {
            if (method == "openclaw.setup.detect" && failDetectionOnce && detectionAttempts++ == 0)
                return new { malformed = true };
            if (wizardReply is not null && method.StartsWith("wizard.", StringComparison.Ordinal))
                return wizardReply(method, parameters);
            return method switch
            {
                "logs.tail" => logReply?.Invoke(parameters) ??
                    new { file = "fixture-agent.log", cursor = 0, size = 0, lines = Array.Empty<string>() },
                "openclaw.setup.detect" => new
                {
                    candidates = new[] { new { kind = "existing-model", label = "Fixture native AI", detail = "Synthetic provider", modelRef = Model, recommended = true } },
                    manualProviders = Array.Empty<object>(), workspace = "fixture", setupComplete = true, configuredModel = Model,
                },
                "openclaw.setup.activate.start" when holdProvider => JsonSerializer.SerializeToElement(new
                {
                    sessionId = parameters.GetProperty("sessionId").GetString(), done = false, status = "running",
                    step = new { id = "native-input", type = "text", title = "Native provider input", executor = "client" },
                }),
                "openclaw.setup.activate.start" => new
                {
                    sessionId = parameters.GetProperty("sessionId").GetString(), done = true, status = "done",
                    modelActivation = new { modelRef = Model, gatewayRestartRequired = false },
                },
                "openclaw.setup.verify" => new { ok = true, modelRef = Model, latencyMs = 1 },
                "wizard.cancel" => new { status = holdProvider ? "running" : "cancelled" },
                _ => throw new InvalidOperationException("Unexpected native UI fixture method."),
            };
        }, supported), Token);
        using var temp = new TempDirectory("native-shared-ui-");
        var data = temp.Combine("data");
        var registry = new GatewayRegistry(data);
        var configPath = NativeGatewayPaths.GetConfigPath(registry, "native-ui");
        if (!isolated)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, JsonSerializer.Serialize(new
            {
                gateway = new { mode = "local", bind = "loopback", port = server.Endpoint.Port,
                    auth = new { mode = "token", token = Token } },
            }));
        }
        var identity = new DeviceIdentity(registry.GetIdentityDirectory("native-ui"));
        identity.Initialize();
        identity.StoreDeviceTokenForRole("node", "fixture-node-token");
        var runtime = new Runtime(server);
        var service = new NativeGatewaySetupService(registry, new Resolver(), new Host(isolated, server.Endpoint.Port), () => runtime);
        await using var native = await service.PrepareAsync(new("native-ui", server.Endpoint.Port, Family)
        {
            Contract = isolated ? NativeGatewayContract.IsolatedSessionV1 : NativeGatewayContract.Legacy,
        }, default);
        var setupConfig = temp.Combine("setup.json");
        File.WriteAllText(setupConfig, JsonSerializer.Serialize(new SetupConfig()));
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            var completed = new List<SetupNativePreparation>();
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: setupConfig,
                    dataDir: data, localDataDir: temp.Combine("local"), commandLineArgs: [],
                    startupRegistrationAllowed: startupRegistrationAllowed,
                    applyNativeStartup: applyStartup ??
                        ((_, _) => throw new InvalidOperationException("No OS startup changes in this fixture.")),
                    publishNativePreparation: (preparation, _) =>
                    {
                        Assert.Equal(native.Record.Id, registry.ActiveGatewayId);
                        Assert.False(runtime.Running);
                        Assert.IsType<AiCompletionPage>(Find<Frame>(Assert.IsType<Grid>(window!.Content), "RootFrame").Content);
                        completed.Add(preparation);
                        return Task.CompletedTask;
                    }));
                window.SelectGatewayRoute(SetupGatewayRoute.Native);
                if (beforeAi is null)
                    await NavigatePreparedAiAsync(window, native);
                else
                    window.NavigateToCapabilities();
                window.Activate();
                window.AppWindow.Move(new(-32000, -32000));
                var root = Assert.IsType<Grid>(window.Content);
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
                var frame = Find<Frame>(root, "RootFrame");
                if (beforeAi is not null)
                {
                    await beforeAi(window, frame);
                    await NavigatePreparedAiAsync(window, native);
                }
                await WaitAsync(() => frame.Content is AiSetupPage { IsLoaded: true }, "shared native AI mounted");
                await assertion(window, frame, registry, runtime, server, completed);
            }
            finally
            {
                if (window is not null) { window.Close(); await window.CleanupCompleted; }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    private static async Task NavigatePreparedAiAsync(SetupWindow window, NativeGatewaySetupSession native)
    {
        await using var preparation = await GatewayAiPreparation.PrepareNativeAsync(native, CancellationToken.None);
        var page = new NativeGatewaySetupPage();
        Action<NativeAiPreparation> navigate = prepared =>
            typeof(SetupWindow).GetMethod("NavigateToNativeAiSetup", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [native, prepared]);
        Func<CancellationToken, Task> authorize = native.AuthorizeAsync;
        await Assert.IsAssignableFrom<Task>(
            typeof(NativeGatewaySetupPage).GetMethod("CompleteAiPreparationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(page, [preparation, null, native.IsIsolated ? native.Record : null,
                    authorize, native.LifetimeToken, navigate, CancellationToken.None]));
    }

    private async Task WithPublishedReadyAsync(SetupWindow setupWindow, GatewayRegistry registry,
        FixtureGatewayServer server, SetupNativePreparation preparation, ElementTheme theme,
        Func<SetupReadyWindow, AiReadyPage, List<SetupNativeCompletion>, Runtime, Task> assertion,
        bool invalidateDuringMount = false, bool exerciseUnrelatedMetadata = false)
    {
        var data = Assert.IsType<string>(typeof(SetupWindow).GetProperty("DataDir",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(setupWindow));
        var normalRuntime = new Runtime(server);
        await using var manager = new GatewayConnectionManager(
            new CredentialResolver(DeviceIdentityFileReader.Instance), new GatewayClientFactory(),
            registry, NullLogger.Instance, nativeGatewayRuntime: normalRuntime);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var startupOccurrence = server.Requests.Count(request => request.Method == "agents.list") + 1;
        await manager.ConnectAsync(preparation.Verification.GatewayId, deadline.Token);
        // The handshake precedes the normal client's initial reads. Drain them before
        // asserting that Ready observation and invalidation issue no new requests.
        await server.WaitForRequestAsync("agents.list", null, startupOccurrence, deadline.Token);
        Task<SetupVerifiedNativeRoute> VerifyAsync(CancellationToken ct) =>
            SetupNativeCompletionVerifier.VerifyAsync(data, preparation.Verification, ct, manager, captureReadiness: true);
        var store = new SetupDashboardHandoffStore(data);
        var handle = store.IssuePreparation(preparation);
        var navigated = new List<SetupNativeCompletion>();
        var failures = new List<SetupNativeLaunchFailure>();
        SetupReadyWindow? readyWindow = null;
        SetupReadyObservation? observation = null;
        try
        {
            readyWindow = OnboardingNativeProof.CreateWindow(() => new SetupReadyWindow(VerifyAsync,
                (choice, _) => { navigated.Add(choice); return Task.CompletedTask; },
                binding => observation = new SetupReadyObservation(manager, registry, null, binding,
                    () => readyWindow!.DispatcherQueue.TryEnqueue(() => readyWindow.Invalidate())),
                () => Assert.IsType<SetupReadyObservation>(observation).RequireCurrent(),
                () => throw new InvalidOperationException("No Connection navigation expected.")));
            SetupWindowMinimumSizeTests.AssertInitialWindowSize(readyWindow, output);
            readyWindow.Activate();
            readyWindow.AppWindow.Move(new(-32000, -32000));
            var readyFrame = Assert.IsType<Frame>(readyWindow.Content);
            async Task AssertUnrelatedMetadataPreservesReadyAsync()
            {
                var page = Assert.IsType<AiReadyPage>(readyFrame.Content);
                var requests = server.Requests.Count;
                var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var client = Assert.IsType<OpenClawGatewayClient>(manager.OperatorClient);
                EventHandler observer = (_, _) => received.TrySetResult();
                client.ModelCatalogInvalidated += observer;
                try
                {
                    await server.NotifyChatMetadataChangedAsync();
                    await received.Task.WaitAsync(deadline.Token);
                    await ui.YieldToRenderAsync();
                    Assert.Same(page, readyFrame.Content);
                    Assert.Equal(requests, server.Requests.Count);
                    Assert.Empty(navigated);
                }
                finally { client.ModelCatalogInvalidated -= observer; }
            }
            var launcher = new SetupNativeHandoffLauncher(() => registry.GetActive(),
                (_, ct) => VerifyAsync(ct),
                (_, _) => throw new InvalidOperationException("Preparation must not select a destination."),
                failures.Add,
                showPreparing: (_, _) => { readyWindow.ShowPreparing(); return Task.CompletedTask; },
                verifyPreparation: (_, ct) => readyWindow.VerifyAsync(ct),
                showReady: async (route, ct) =>
                {
                    await readyWindow.ShowReadyAsync(route, ct);
                    var pending = Assert.IsType<AiReadyPage>(readyFrame.Content);
                    Assert.NotEqual("Your AI is ready", Find<TextBlock>(pending, "Heading").Text);
                    Assert.Equal(Visibility.Collapsed, Find<StackPanel>(pending, "Choices").Visibility);
                    Assert.Empty(navigated);
                    if (exerciseUnrelatedMetadata)
                        await AssertUnrelatedMetadataPreservesReadyAsync();
                    if (invalidateDuringMount)
                    {
                        var requests = server.Requests.Count;
                        await server.NotifyConfigurationChangedAsync();
                        await WaitAsync(() => readyFrame.Content is AiCompletionPage,
                            "configuration event invalidates pending mount");
                        Assert.Equal(requests, server.Requests.Count);
                    }
                },
                readyConsumed: readyWindow.CommitPresentation);
            var opened = await launcher.OpenAsync(store, handle, ct: deadline.Token);
            if (invalidateDuringMount)
            {
                Assert.False(opened);
                Assert.Single(failures);
                Assert.Empty(navigated);
                Assert.IsType<AiCompletionPage>(readyFrame.Content);
                return;
            }
            Assert.True(opened);
            Assert.Empty(failures);
            Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle).Status);
            var ready = Assert.IsType<AiReadyPage>(readyFrame.Content);
            Assert.Equal("Your AI is ready", Find<TextBlock>(ready, "Heading").Text);
            if (exerciseUnrelatedMetadata)
            {
                await AssertUnrelatedMetadataPreservesReadyAsync();
                Assert.Equal(Visibility.Visible, Find<StackPanel>(ready, "Choices").Visibility);
            }
            await OnboardingNativeProof.ApplyThemeSurfaceAsync(Assert.IsType<Grid>(ready.Content), theme);
            await ui.YieldToRenderAsync();
            await assertion(readyWindow, ready, navigated, normalRuntime);
        }
        finally
        {
            observation?.Dispose();
            if (readyWindow is not null)
            {
                if (!readyWindow.IsClosed) readyWindow.Close();
                await readyWindow.CleanupCompleted;
            }
        }
    }

    private static T Find<T>(FrameworkElement owner, string name) where T : FrameworkElement => Assert.IsType<T>(owner.FindName(name));
    private static Task WaitAsync(Func<bool> condition, string label) => TestSupport.WaitForRenderedConditionAsync(condition, label);
    private static void Invoke(Button button) =>
        Assert.IsAssignableFrom<IInvokeProvider>(
            FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private async Task<WizardPage> EnterFallbackAsync(Frame frame)
    {
        var ai = Assert.IsType<AiSetupPage>(frame.Content);
        var legacy = Find<Button>(ai, "LegacyButton");
        await WaitAsync(() => legacy.Visibility == Visibility.Visible && legacy.IsEnabled, "explicit fallback offered");
        Invoke(legacy);
        await WaitAsync(() => frame.Content is WizardPage { IsLoaded: true }, "native fallback mounted");
        var wizard = Assert.IsType<WizardPage>(frame.Content);
        await WaitAsync(() => Find<Button>(wizard, "PrimaryButton").IsEnabled &&
            Find<TextBlock>(wizard, "TitleText").Text == "Native fallback question", "native fallback started");
        return wizard;
    }

    private static object FallbackQuestion(string sessionId = "native-fallback") => new
    {
        sessionId, done = false, status = "running",
        step = new { id = "native-question", type = "note", title = "Native fallback question", message = "Synthetic local fixture." },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFallback_RejectsNextAndCancelAfterOwnershipChanges(bool isolated)
    {
        await WithNativeAsync(async (_, frame, registry, runtime, server, completed) =>
        {
            var wizard = await EnterFallbackAsync(frame);
            Assert.Single(server.Requests, request => request.Method == "wizard.start");
            runtime.Provenance = GatewayEndpointProvenanceKind.UnknownListener;
            Invoke(Find<Button>(wizard, "PrimaryButton"));
            await WaitAsync(() => Find<TextBlock>(wizard, "ErrorText").Visibility == Visibility.Visible, "ownership rejection");
            Assert.DoesNotContain(server.Requests, request => request.Method is "wizard.next" or "wizard.cancel");
            Assert.Equal(0, runtime.Disposals);
            Assert.Empty(registry.GetAll());
            Assert.Empty(completed);
        }, supported: false, isolated: isolated, wizardReply: (method, _) => method == "wizard.cancel"
            ? new { status = "cancelled" } : FallbackQuestion());
    }

    [Fact]
    public async Task NativeFallback_PositiveNextAndPageCleanupKeepTheWindowRuntimeOwner()
    {
        Runtime? ownedRuntime = null;
        await WithNativeAsync(async (window, frame, registry, runtime, server, completed) =>
        {
            ownedRuntime = runtime;
            var wizard = await EnterFallbackAsync(frame);
            Invoke(Find<Button>(wizard, "PrimaryButton"));
            await WaitAsync(() => server.Requests.Any(request => request.Method == "wizard.next") &&
                Find<Button>(wizard, "PrimaryButton").IsEnabled, "native fallback next");
            frame.Navigate(typeof(Page));
            var cleanup = typeof(WizardPage).GetMethod("CancelAndWaitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var closing = Assert.IsAssignableFrom<Task>(cleanup.Invoke(wizard, null));
            Assert.Same(closing, cleanup.Invoke(wizard, null));
            await closing;
            await WaitAsync(() => server.ActiveConnectionCount == 0, "fallback socket disposed");
            Assert.Single(server.Requests, request => request.Method == "wizard.next");
            Assert.Single(server.Requests, request => request.Method == "wizard.cancel");
            Assert.Equal(0, runtime.Disposals);
            Assert.NotNull(typeof(SetupWindow).GetProperty("NativeSetupSession", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window));
            Assert.Empty(registry.GetAll());
            Assert.Empty(completed);
        }, supported: false, wizardReply: (method, _) => method == "wizard.cancel"
            ? new { status = "cancelled" } : FallbackQuestion());
        Assert.Equal(1, Assert.IsType<Runtime>(ownedRuntime).Disposals);
    }

    [Fact]
    public async Task NativeFallback_GatewayMoreRejectsAnOverlappingContinue()
    {
        using var nextStarted = new ManualResetEventSlim();
        using var releaseNext = new ManualResetEventSlim();
        try
        {
            await WithNativeAsync(async (_, frame, _, _, server, _) =>
            {
                var wizard = await EnterFallbackAsync(frame);
                var moreField = typeof(WizardPage)
                    .GetField("_moreOptionsButton", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Button? more = null;
                await WaitAsync(() => (more = moreField.GetValue(wizard) as Button) is { IsLoaded: true },
                    "Gateway More button loaded");
                var continueButton = Find<Button>(wizard, "PrimaryButton");

                Invoke(more!);
                Assert.True(nextStarted.Wait(TimeSpan.FromSeconds(5)));
                await WaitAsync(() => !continueButton.IsEnabled, "Continue disabled while More is in flight");

                var overlapping = Assert.IsAssignableFrom<Task>(typeof(WizardPage)
                    .GetMethod("SendCurrentAnswerAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(wizard, [false]));
                await overlapping;
                Assert.Single(server.Requests, request => request.Method == "wizard.next");

                releaseNext.Set();
                await WaitAsync(() => continueButton.IsEnabled, "Continue restored after More completes");
                Assert.Single(server.Requests, request => request.Method == "wizard.next");
            }, supported: false, wizardReply: (method, _) => method switch
            {
                "wizard.start" => new
                {
                    sessionId = "native-fallback",
                    done = false,
                    status = "running",
                    step = new
                    {
                        id = "native-question",
                        type = "select",
                        title = "Native fallback question",
                        message = "Synthetic local fixture.",
                        options = new[]
                        {
                            new { value = "skip", label = "Skip for now" },
                            new { value = "__more", label = "More" },
                        },
                    },
                },
                "wizard.next" => WaitForMoreReply(),
                "wizard.cancel" => new { status = "cancelled" },
                _ => throw new InvalidOperationException($"Unexpected wizard method: {method}"),
            });
        }
        finally
        {
            releaseNext.Set();
        }

        object WaitForMoreReply()
        {
            nextStarted.Set();
            Assert.True(releaseNext.Wait(TimeSpan.FromSeconds(10)));
            return new
            {
                sessionId = "native-fallback",
                done = false,
                status = "running",
                step = new
                {
                    id = "native-question-expanded",
                    type = "select",
                    title = "Native fallback question",
                    message = "Synthetic local fixture.",
                    options = new[]
                    {
                        new { value = "skip", label = "Skip for now" },
                        new { value = "provider", label = "Provider" },
                    },
                },
            };
        }
    }

    [Fact]
    public async Task NativeFallback_StaleBindingCannotSendThroughReplacement()
    {
        var starts = 0;
        await WithNativeAsync(async (_, frame, _, _, server, _) =>
        {
            var wizard = await EnterFallbackAsync(frame);
            var connection = typeof(WizardPage).GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wizard);
            var generation = typeof(WizardPage).GetField("_operationGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wizard);
            await Assert.IsAssignableFrom<Task>(typeof(WizardPage).GetMethod("StartOverAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(wizard, null));
            Assert.NotSame(connection, typeof(WizardPage).GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wizard));
            Assert.Equal(2, server.Requests.Count(request => request.Method == "wizard.start"));
            var before = server.Requests.Count;
            var request = Assert.IsAssignableFrom<Task>(typeof(WizardPage).GetMethod("SendWizardRequestAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(wizard,
                    [connection, generation, "wizard.next", new { sessionId = "native-fallback-1" }, 1000]));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(before, server.Requests.Count);
        }, supported: false, wizardReply: (method, _) => method == "wizard.cancel"
            ? new { status = "cancelled" }
            : FallbackQuestion("native-fallback-" + Interlocked.Increment(ref starts)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFallback_OptionalPolicyUsesAuthenticatedWrapperForCancelConfigAndHealth(bool loseAfterCancel)
    {
        Runtime? nativeRuntime = null;
        await WithNativeAsync(async (_, frame, registry, runtime, server, completed) =>
        {
            nativeRuntime = runtime;
            var wizard = await EnterFallbackAsync(frame);
            Invoke(Find<Button>(wizard, "PrimaryButton"));
            await WaitAsync(() => loseAfterCancel
                ? Find<TextBlock>(wizard, "ErrorText").Visibility == Visibility.Visible
                : frame.Content is CompletePage { IsLoaded: true }, "compatibility policy outcome");
            var requests = server.Requests.ToArray();
            var cancel = Array.FindIndex(requests, request => request.Method == "wizard.cancel");
            Assert.True(cancel >= 0);
            if (loseAfterCancel)
            {
                Assert.DoesNotContain(requests.Skip(cancel + 1), request => request.Method is "config.get" or "health");
                Assert.Empty(registry.GetAll());
            }
            else
            {
                Assert.Contains(requests.Skip(cancel + 1), request => request.Method == "config.get");
                Assert.Contains(requests.Skip(cancel + 1), request => request.Method == "health");
                Assert.Equal("native-ui", registry.ActiveGatewayId);
            }
            Assert.Empty(completed);
        }, supported: false, wizardReply: (method, _) =>
        {
            if (method == "wizard.cancel" && loseAfterCancel)
                nativeRuntime!.Provenance = GatewayEndpointProvenanceKind.UnknownListener;
            return method switch
            {
                "wizard.start" => FallbackQuestion(),
                "wizard.cancel" => new { status = "cancelled" },
                _ => new { sessionId = "native-fallback", done = false, status = "running",
                    step = new { id = "optional", type = "note", title = "Optional apps" } },
            };
        });
    }

    private static async Task SettleCaptureAsync(Window window)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        foreach (var mascot in TestSupport.FindDescendants<OpenClaw.SetupEngine.UI.Controls.OnboardingMascot>(root))
            mascot.IsAnimationEnabled = false;
        root.UpdateLayout();
        await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromMilliseconds(400));
    }

    private async Task SaveViewportsAsync(Window window, Page page, string name)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR"))) return;
        await SettleCaptureAsync(window);
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var scroll = Assert.Single(Assert.IsType<Grid>(page.Content).Children.OfType<ScrollViewer>());
        var original = scroll.VerticalOffset;
        var offset = 0d;
        var index = 0;
        try
        {
            do
            {
                scroll.ChangeView(null, offset, null, disableAnimation: true);
                await WaitAsync(() => Math.Abs(scroll.VerticalOffset - offset) < 1, "native proof viewport");
                root.UpdateLayout();
                await ui.YieldToRenderAsync();
                await OnboardingArtworkRenderingTests.SaveProofAsync(root, $"{name}-{++index:D2}", output);
                if (offset >= scroll.ScrollableHeight) break;
                offset = Math.Min(offset + scroll.ViewportHeight, scroll.ScrollableHeight);
            } while (index < 8);
            Assert.True(offset >= scroll.ScrollableHeight);
        }
        finally
        {
            scroll.ChangeView(null, original, null, disableAnimation: true);
            await ui.YieldToRenderAsync();
        }
    }

    private sealed class Resolver : INativeGatewayPackageResolver
    {
        public Task<NativeGatewayPackage> ResolveAsync(CancellationToken ct) =>
            Task.FromResult(new NativeGatewayPackage(Family, "0.0.0.1", @"C:\fixture\openclaw.exe", @"C:\fixture\clawctl.exe"));
        public string ResolveDataPath(string path) => path;
    }

    private sealed class Host(bool isolated, int port) : INativeGatewaySetupHost
    {
        public Task<NativeGatewayContract> DetectContractAsync(NativeGatewayPackage p, CancellationToken ct) =>
            Task.FromResult(isolated ? NativeGatewayContract.IsolatedSessionV1 : NativeGatewayContract.Legacy);
        public Task<IsolatedGatewayConfiguration> PrepareIsolatedConfigurationAsync(
            NativeGatewayPackage p, int preferred, CancellationToken ct) => Task.FromResult(new IsolatedGatewayConfiguration(port, Token));
        public Task<IsolatedGatewayConfiguration> CheckIsolatedPairingConfigurationAsync(
            NativeGatewayPackage p, CancellationToken ct) => Task.FromResult(new IsolatedGatewayConfiguration(port, Token));
        public Task ApplyIsolatedCapabilitiesAsync(NativeGatewayPackage p, IReadOnlyList<string> commands, CancellationToken ct) => Task.CompletedTask;
        public Task PreparePackageAsync(NativeGatewayPackage p, IReadOnlyDictionary<string, string> env, CancellationToken ct) => Task.CompletedTask;
        public Task ValidateConfigurationAsync(NativeGatewayPackage p, IReadOnlyDictionary<string, string> env, CancellationToken ct) => Task.CompletedTask;
        public Task VerifyHealthAsync(NativeGatewayPackage p, IReadOnlyDictionary<string, string> env, CancellationToken ct) => Task.CompletedTask;
        public Task<string> ListDevicePairingRequestsAsync(NativeGatewayPackage p, IReadOnlyDictionary<string, string> env, CancellationToken ct) => throw new InvalidOperationException("No package CLI in fixture.");
        public Task<string> ApproveDevicePairingAsync(NativeGatewayPackage p, string id, IReadOnlyDictionary<string, string> env, CancellationToken ct) => throw new InvalidOperationException("No package CLI in fixture.");
        public IDisposable OpenRecoveryTerminal(NativeGatewayPackage p, IReadOnlyDictionary<string, string> env) => throw new InvalidOperationException("No terminal in fixture.");
    }

    private sealed class Runtime(FixtureGatewayServer server) : INativeGatewayRuntime
    {
        public bool Running { get; private set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int Disposals { get; private set; }
        public Exception? DisposeFailure { get; set; }
        public GatewayEndpointProvenanceKind Provenance { get; set; } = GatewayEndpointProvenanceKind.ExpectedManagedGateway;
        public GatewayEndpointProvenance Inspect(GatewayRecord record) => new(Provenance, server.Endpoint.Port);
        public Task<GatewayEndpointProvenance> InspectAsync(GatewayRecord record, CancellationToken ct) => Task.FromResult(Inspect(record));
        public Task EnsureRunningAsync(GatewayRecord record, CancellationToken ct) { Starts++; Running = true; return Task.CompletedTask; }
        public async Task StopAsync(CancellationToken ct) { Stops++; Running = false; await server.CloseConnectionsAsync(ct); }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            await StopAsync(default);
            if (DisposeFailure is { } failure)
            {
                DisposeFailure = null;
                throw failure;
            }
        }
    }
}
