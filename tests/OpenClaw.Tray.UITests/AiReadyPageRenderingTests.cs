using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.TestSupport;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

/// <summary>Synthetic rendering only. Never activates a destination, connects a Gateway, or finalizes setup.</summary>
[Collection(UICollection.Name)]
public sealed class AiReadyPageRenderingTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task VerifiedChooser_ShowsNativeChoicesWithoutIssuingOrFinalizing(ElementTheme theme)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var temp = new TempDirectory("native-ready-proof-");
        var data = temp.Combine("data");
        var config = new SetupConfig();
        config.WindowsNodeContext.Enabled = false;
        var configPath = temp.Combine("config.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
        var registry = new GatewayRegistry(data);
        var gateway = registry.AddOrUpdate(new() { Id = "synthetic-ready", Url = "wss://synthetic.example/control/" });
        registry.SetActive(gateway.Id);
        registry.Save();
        var identity = new OpenClaw.Shared.DeviceIdentity(registry.GetIdentityDirectory(gateway.Id));
        identity.Initialize();
        var proof = new GatewayAiSetupCompletion(SetupCompletionIntent.CustodianOnboarding,
            gateway.Id, GatewayDashboardBinding.Capture(gateway), "synthetic/model", "synthetic", 1,
            IdentityBinding: SetupCompletionAuthority.CaptureIdentity(registry.GetIdentityDirectory(gateway.Id), identity.DeviceId),
            SessionKey: "agent:synthetic:main");
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: configPath,
                    dataDir: data, localDataDir: temp.Combine("local"), commandLineArgs: []));
                var size = window.AppWindow.Size;
                var root = Assert.IsType<Grid>(window.Content);
                var frame = Assert.IsType<Frame>(root.FindName("RootFrame"));
                using var navigation = OnboardingNativeProof.TrackNavigation(frame);
                // Feed only the trusted host boundary in this fixture. This is not live AI-verification proof.
                var completed = typeof(SetupWindow).GetMethod("CompleteVerifiedAiSetupAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                await Assert.IsAssignableFrom<Task>(completed.Invoke(window, [proof]));
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR")))
                    window.Activate();
                else
                    OnboardingNativeProof.ActivateOwned(window);
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
                await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is AiReadyPage { IsLoaded: true },
                    "native ready chooser mounted");
                var page = Assert.IsType<AiReadyPage>(frame.Content);
                var choices = Assert.IsType<StackPanel>(page.FindName("Choices"));
                Assert.Equal(3, choices.Children.Count);
                Assert.Equal(["Chat", "Channels", "Skills"], choices.Children.Cast<FrameworkElement>().Select(item => item.Tag));
                Assert.All(choices.Children, item => Assert.True(Assert.IsAssignableFrom<Control>(item).IsEnabled));
                Assert.Null(page.FindName("SkipButton"));
                Assert.Null(page.FindName("ReturnButton"));
                Assert.Null(page.FindName("FinishButton"));
                var badge = Assert.IsType<RecommendedBadge>(page.FindName("RecommendedBadge"));
                Assert.Equal("Recommended", Assert.IsType<TextBlock>(badge.FindName("Label")).Text);
                Assert.True(badge.ActualWidth > 0 && badge.ActualHeight > 0);
                Assert.Equal("Talk to my agent, recommended",
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(choices.Children[0]));
                Assert.False(Assert.IsType<InfoBar>(page.FindName("ErrorBar")).IsOpen);
                Assert.False(Directory.Exists(Path.Combine(data, "setup-dashboard-handoff")));
                Assert.False(File.Exists(Path.Combine(data, "settings.json")));
                Assert.Equal(size, window.AppWindow.Size);
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR")))
                {
                    using (await OnboardingNativeProof.CaptureAsync(window, $"followup-native-ready-{theme}", output,
                        ["Recommended", "Talk to my agent"], requiredContent: page)) { }
                    foreach (var choice in choices.Children.Cast<Control>()) choice.IsEnabled = false;
                    Assert.False(badge.IsEnabled);
                    using (await OnboardingNativeProof.CaptureAsync(window, $"followup-native-ready-disabled-{theme}", output,
                        ["Recommended", "Talk to my agent"], requiredContent: page)) { }
                    foreach (var choice in choices.Children.Cast<Control>()) choice.IsEnabled = true;
                }
                // Fail at the coordinator boundary without touching a Gateway or publishing a handoff.
                var argsField = typeof(AiReadyPage).GetField("_args", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var originalArgs = argsField.GetValue(page)!;
                ((SetupNativeCompletionCoordinator)originalArgs.GetType().GetProperty("Coordinator")!.GetValue(originalArgs)!).Dispose();
                using var failed = new SetupNativeCompletionCoordinator(proof, _ => Task.CompletedTask,
                    (_, _) => Task.FromException<SetupVerifiedNativeRoute>(new SetupNativeOwnershipException()),
                    (_, _) => throw new InvalidOperationException("Must not finalize"),
                    (_, _) => throw new InvalidOperationException("Must not publish"));
                typeof(SetupWindow).GetField("_readyChoice", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, failed);
                var failedArgs = Activator.CreateInstance(originalArgs.GetType(), failed, window)!;
                argsField.SetValue(page, failedArgs);
                await Assert.IsAssignableFrom<Task>(typeof(AiReadyPage).GetMethod("ChooseAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [failedArgs, SetupNativeDestination.Skills]));
                Assert.True(Assert.IsType<InfoBar>(page.FindName("ErrorBar")).IsOpen);
                Assert.True(Assert.IsType<Button>(page.FindName("RecoveryButton")).IsEnabled);
                Assert.All(choices.Children, item => Assert.True(Assert.IsAssignableFrom<Control>(item).IsEnabled));
                Assert.False(File.Exists(Path.Combine(data, "settings.json")));
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR")))
                    OnboardingNativeProof.AssertSourceUnchanged();
            }
            finally
            {
                if (window is not null)
                {
                    window.Close();
                    await window.CleanupCompleted;
                }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task StartupAvailabilityControlsPreferencePersistenceAndNativeFinalization(
        bool startupRegistrationAllowed, bool selectedStartup, bool preservePreference)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var temp = new TempDirectory("native-startup-policy-");
        var data = temp.Combine("data");
        var config = new SetupConfig();
        config.WindowsNodeContext.Enabled = false;
        config.Settings.AutoStart = true;
        var configPath = temp.Combine("config.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
        var registry = new GatewayRegistry(data);
        var gateway = registry.AddOrUpdate(new() { Id = "synthetic-startup", Url = "wss://synthetic.example/" });
        registry.SetActive(gateway.Id);
        registry.Save();
        var identity = new OpenClaw.Shared.DeviceIdentity(registry.GetIdentityDirectory(gateway.Id));
        identity.Initialize();
        var proof = new GatewayAiSetupCompletion(SetupCompletionIntent.CustodianOnboarding,
            gateway.Id, GatewayDashboardBinding.Capture(gateway), "synthetic/model", "synthetic", 1,
            IdentityBinding: SetupCompletionAuthority.CaptureIdentity(registry.GetIdentityDirectory(gateway.Id), identity.DeviceId),
            SessionKey: "agent:synthetic:main");
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            try
            {
                var startupCalls = new List<bool>();
                var failStartupOnce = startupRegistrationAllowed && !preservePreference;
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: configPath,
                    dataDir: data, localDataDir: temp.Combine("local"), commandLineArgs: [],
                    startupRegistrationAllowed: startupRegistrationAllowed,
                    applyNativeStartup: (enabled, _) =>
                    {
                        startupCalls.Add(enabled);
                        if (!failStartupOnce) return Task.CompletedTask;
                        failStartupOnce = false;
                        return Task.FromException(new IOException("Synthetic startup registration failure"));
                    },
                    publishNativeCompletion: (_, _) => throw new InvalidOperationException("Must not publish")));
                window.SelectGatewayRoute(SetupGatewayRoute.Existing, gatewayAvailable: true);
                window.AutoStartAfterSetup = selectedStartup;
                if (preservePreference)
                {
                    typeof(SetupWindow).GetField("_persistStartupPreferenceOnComplete",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
                    File.WriteAllText(Path.Combine(data, "settings.json"), """{"AutoStart":false}""");
                }
                Assert.Equal(startupRegistrationAllowed, window.ShowStartupPreference);
                Assert.Equal(startupRegistrationAllowed && selectedStartup, window.AutoStartAfterSetup);
                var finalize = typeof(SetupWindow).GetMethod("FinalizeNativeChoiceAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                if (failStartupOnce)
                    await Assert.ThrowsAsync<IOException>(() =>
                        Assert.IsAssignableFrom<Task>(finalize.Invoke(window, [proof, CancellationToken.None])));
                await Assert.IsAssignableFrom<Task>(finalize.Invoke(window, [proof, CancellationToken.None]));
                await Assert.IsAssignableFrom<Task>(finalize.Invoke(window, [proof, CancellationToken.None]));
                Assert.Equal(startupRegistrationAllowed && !preservePreference
                    ? [selectedStartup, selectedStartup] : Array.Empty<bool>(), startupCalls);
                using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "settings.json")));
                Assert.Equal(startupRegistrationAllowed && !preservePreference && selectedStartup,
                    settings.RootElement.GetProperty("AutoStart").GetBoolean());
            }
            finally
            {
                if (window is not null)
                {
                    window.Close();
                    await window.CleanupCompleted;
                }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }
}
