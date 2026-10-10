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
            SetupReadyWindow? window = null;
            try
            {
                var route = await VerifySyntheticAsync(proof, CancellationToken.None);
                window = OnboardingNativeProof.CreateWindow(() => new SetupReadyWindow(
                    ct => VerifySyntheticAsync(proof, ct),
                    (_, _) => throw new InvalidOperationException("Must not navigate"),
                    _ => new Observation(), () => { }, () => { }));
                var size = window.AppWindow.Size;
                var frame = Assert.IsType<Frame>(window.Content);
                using var navigation = OnboardingNativeProof.TrackNavigation(frame);
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR")))
                    window.Activate();
                else
                    OnboardingNativeProof.ActivateOwned(window);
                // Synthetic verification exercises rendering, not a live Gateway or inference.
                await window.ShowReadyAsync(route, CancellationToken.None);
                await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is AiReadyPage { IsLoaded: true },
                    "native ready chooser mounted");
                var page = Assert.IsType<AiReadyPage>(frame.Content);
                var choices = Assert.IsType<StackPanel>(page.FindName("Choices"));
                Assert.Equal(Visibility.Collapsed, choices.Visibility);
                Assert.NotEqual("Your AI is ready", Assert.IsType<TextBlock>(page.FindName("Heading")).Text);
                window.Invalidate();
                var beforeConsumption = Assert.IsType<AiCompletionPage>(frame.Content);
                var returnButton = Assert.IsType<Button>(beforeConsumption.FindName("BackButton"));
                Assert.Equal(Visibility.Visible, returnButton.Visibility);
                Assert.True(returnButton.IsEnabled);
                Assert.Equal(Visibility.Collapsed,
                    Assert.IsType<Button>(beforeConsumption.FindName("RetryButton")).Visibility);
                await window.ShowReadyAsync(await VerifySyntheticAsync(proof, CancellationToken.None), CancellationToken.None);
                page = Assert.IsType<AiReadyPage>(frame.Content);
                choices = Assert.IsType<StackPanel>(page.FindName("Choices"));
                var root = Assert.IsType<Grid>(page.Content);
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
                window.CommitPresentation();
                Assert.Equal(Visibility.Visible, choices.Visibility);
                Assert.Equal(3, choices.Children.Count);
                Assert.Equal(["Chat", "Channels", "Skills"], choices.Children.Cast<FrameworkElement>().Select(item => item.Tag));
                Assert.All(choices.Children, item => Assert.True(Assert.IsAssignableFrom<Control>(item).IsEnabled));
                Assert.Null(page.FindName("SkipButton"));
                Assert.Null(page.FindName("ReturnButton"));
                Assert.Null(page.FindName("FinishButton"));
                var badge = Assert.IsType<RecommendedBadge>(page.FindName("RecommendedBadge"));
                Assert.Equal("Recommended", Assert.IsType<TextBlock>(badge.FindName("Label")).Text);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => badge.IsLoaded && badge.ActualWidth > 0 && badge.ActualHeight > 0,
                    "admitted Ready choices have completed layout");
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
                window.Invalidate();
                var failure = Assert.IsType<AiCompletionPage>(frame.Content);
                Assert.True(Assert.IsType<InfoBar>(failure.FindName("ErrorBar")).IsOpen);
                Assert.Equal(Visibility.Visible, Assert.IsType<Button>(failure.FindName("RetryButton")).Visibility);
                Assert.Equal(Visibility.Visible, Assert.IsType<Button>(failure.FindName("BackButton")).Visibility);
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
                    publishNativePreparation: (_, _) => throw new InvalidOperationException("Must not publish")));
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

    private static async Task<SetupVerifiedNativeRoute> VerifySyntheticAsync(
        GatewayAiSetupCompletion proof, CancellationToken ct)
    {
        var binding = await SetupNativeReadyBinding.VerifyAsync(new SyntheticTransport(proof), proof, ct);
        return new(binding.Proof, binding.Proof.SessionKey!, binding);
    }

    private sealed class SyntheticTransport(GatewayAiSetupCompletion proof) : IGatewayAiSetupTransport
    {
        public GatewayAiSetupRoute Route => new(proof.GatewayId, proof.AgentId, "synthetic",
            proof.EndpointBinding, proof.IdentityBinding, proof.SessionKey);
        public long Generation => proof.VerifiedGeneration;
        public bool IsConnected => true;
        public IReadOnlyCollection<string> Methods => ["openclaw.setup.verify"];
        public IReadOnlyCollection<string> OperatorScopes => ["operator.admin"];
        public Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(method switch
            {
                "config.get" => JsonSerializer.SerializeToElement(new { hash = "synthetic-revision", valid = true }),
                "openclaw.setup.verify" => JsonSerializer.SerializeToElement(new { ok = true, modelRef = proof.ModelRef, latencyMs = 1 }),
                _ => throw new InvalidOperationException("No mutations are permitted in rendering proof."),
            });
        }

    }

    private sealed class Observation : IDisposable
    {
        public void Dispose() { }
    }
}
