using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Dialogs;
using OpenClawTray.Services;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

/// <summary>Production surfaces with inert synthetic state. No install, pairing decision, or live RPC.</summary>
[Collection(UICollection.Name)]
public sealed class OnboardingFollowupProofTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task FollowupProof_NativeInstallCompactActions(ElementTheme theme)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        var work = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")!,
            $"native-actions-{theme}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var configPath = Path.Combine(work, "config.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new SetupConfig()));
        await ui.RunOnUIAsync(async () =>
        {
            var oldPreview = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_PREVIEW_PAGE");
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            try
            {
                Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PREVIEW_PAGE", "native");
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: configPath,
                    dataDir: Path.Combine(work, "data"), localDataDir: Path.Combine(work, "local"), commandLineArgs: []));
                var root = Assert.IsType<Grid>(window.Content);
                var frame = Assert.IsType<Frame>(root.FindName("RootFrame"));
                using var navigation = OnboardingNativeProof.TrackNavigation(frame);
                if (frame.Content is not NativeGatewaySetupPage)
                    frame.Navigate(typeof(NativeGatewaySetupPage), new SetupConfig());
                var page = Assert.IsType<NativeGatewaySetupPage>(frame.Content);
                OnboardingNativeProof.ActivateOwned(window);
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
                await TestSupport.WaitForRenderedConditionAsync(() => page.IsLoaded, "native preview loaded");
                Assert.Null(Field(page, "_operation"));
                Assert.Contains("Preview only", Find<TextBlock>(page, "StatusText").Text);
                foreach (var action in new[] { "CancelButton", "RetryButton" })
                {
                    Find<Button>(page, "CancelButton").Visibility = action == "CancelButton" ? Visibility.Visible : Visibility.Collapsed;
                    Find<Button>(page, "RetryButton").Visibility = action == "RetryButton" ? Visibility.Visible : Visibility.Collapsed;
                    root.UpdateLayout();
                    await OnboardingNativeProof.NextCompositionAsync();
                    var back = Find<Button>(page, "BackButton");
                    var right = Find<Button>(page, action);
                    Assert.Equal(100, back.MinWidth);
                    Assert.Equal(100, right.MinWidth);
                    Assert.Equal(HorizontalAlignment.Left, back.HorizontalAlignment);
                    Assert.Equal(HorizontalAlignment.Right, right.HorizontalAlignment);
                    Assert.True(right.ActualWidth < 200);
                    using (await OnboardingNativeProof.CaptureAsync(window, $"followup-native-{action}-{theme}", output,
                        ["Install a local native gateway", "Back", action == "CancelButton" ? "Cancel setup" : "Retry setup"],
                        requiredContent: page)) { }
                }
                OnboardingNativeProof.AssertSourceUnchanged();
            }
            finally
            {
                if (window is not null) { window.Close(); await window.CleanupCompleted; }
                Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PREVIEW_PAGE", oldPreview);
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [Theory]
    [InlineData(ElementTheme.Light, false)]
    [InlineData(ElementTheme.Dark, false)]
    [InlineData(ElementTheme.Light, true)]
    [InlineData(ElementTheme.Dark, true)]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task FollowupProof_PairingOriginalLayoutScopesAndArming(ElementTheme theme, bool longScopes)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        await ui.RunOnUIAsync(async () =>
        {
            Assert.Equal(theme.ToString(), Application.Current.RequestedTheme.ToString());
            var coordinator = new PairingApprovalCoordinator(() => null, () => [], () => false, NullLogger.Instance);
            var queue = Assert.IsType<PairingApprovalQueue>(Field(coordinator, "_queue"));
            queue.Reconcile(new DevicePairingListInfo
            {
                Pending = [new DevicePairingRequest
                {
                    RequestId = "synthetic-request", DeviceId = "synthetic-device",
                    DisplayName = "Review Surface Laptop", Platform = "Windows", Role = "operator",
                    Scopes = longScopes
                        ? ["operator.read", "operator.write", "operator.approvals", "operator.pairing",
                            "synthetic.long-scope-description-for-visible-wrapping-proof"]
                        : ["operator.admin", "operator.pairing"],
                }],
            }, null, [], Environment.TickCount64);
            var started = Stopwatch.StartNew();
            var window = OnboardingNativeProof.CreateWindow(() => new PairingApprovalDialog(coordinator));
            try
            {
                var approve = Assert.IsType<Button>(Field(window, "_approveButton"));
                Assert.False(approve.IsEnabled);
                Assert.Equal(TimeSpan.FromMilliseconds(1500),
                    typeof(PairingApprovalDialog).GetField("ApproveArmDelay", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
                var root = Assert.IsType<Grid>(window.Content);
                OnboardingNativeProof.ActivateOwned(window);
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
                await TestSupport.WaitForRenderedConditionAsync(() => root.IsLoaded, "pairing loaded");
                var scroll = TestSupport.FindDescendants<ScrollViewer>(root).Single();
                await TestSupport.WaitForRenderedConditionAsync(() => approve.IsEnabled, "1500ms approval arm delay");
                Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(1500));
                foreach (var atBottom in new[] { false, true })
                {
                    scroll.ChangeView(null, atBottom ? scroll.ScrollableHeight : 0, null, true);
                    await OnboardingNativeProof.NextCompositionAsync();
                    using (await OnboardingNativeProof.CaptureAsync(window,
                        $"restored-pairing-{(longScopes ? "wrapping" : "normal")}-{theme}-{(atBottom ? "bottom" : "top")}", output,
                        ["Reject", "Decide later", "Approve device"],
                        new { syntheticQueue = true, longScopes, theme = theme.ToString(), noDecisionInvoked = true },
                        requiredContent: root)) { }
                }
                Assert.Single(coordinator.Current);
                OnboardingNativeProof.AssertSourceUnchanged();
            }
            finally { window.Close(); }
        });
    }

    private static object? Field(object value, string name) =>
        value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);

    private static T Find<T>(FrameworkElement page, string name) where T : FrameworkElement =>
        Assert.IsType<T>(page.FindName(name));
}
