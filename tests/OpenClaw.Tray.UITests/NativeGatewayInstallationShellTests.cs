using System.Reflection;
using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class NativeGatewayInstallationShellTests(UIThreadFixture ui)
{
    [Fact]
    public async Task NativeActions_KeepPackageStagesCancellationAndPreviewRetryWithExistingOwner()
    {
        await WithNativeAsync(async (_, _, page) =>
        {
            var rows = Find<StackPanel>(page, "StepsPanel").Children.Cast<SettingsCard>().ToArray();
            Assert.Equal(4, rows.Length);
            Assert.Equal(new[] { "Check device support", "Install or verify Gateway package",
                "Prepare your Gateway profile", "Start and verify the Gateway" }, rows.Select(row => row.Header));
            Assert.All(rows, row => Assert.Equal("Waiting", Status(row)));
            InvokePrivate(page, "SetCurrentStep", 1);
            Assert.Equal("Complete", Status(rows[0]));
            Assert.Equal("In progress", Status(rows[1]));
            Assert.Equal("Waiting", Status(rows[2]));
            Assert.Equal("Waiting", Status(rows[3]));
            InvokePrivate(page, "SetBusy", true);
            Assert.False(Find<Button>(page, "BackButton").IsEnabled);
            Assert.False(Find<Button>(page, "RetryButton").IsEnabled);
            var cancel = Find<Button>(page, "CancelButton");
            Assert.Equal(Visibility.Visible, cancel.Visibility);
            using var cancellation = new CancellationTokenSource();
            var cancellationField = typeof(NativeGatewaySetupPage).GetField(
                "_operationCts", BindingFlags.NonPublic | BindingFlags.Instance)!;
            cancellationField.SetValue(page, cancellation);
            try
            {
                Invoke(cancel);
                Assert.True(cancellation.IsCancellationRequested);
                await Assert.IsAssignableFrom<Task>(InvokePrivate(page, "CancelAndWaitAsync"));
            }
            finally { cancellationField.SetValue(page, null); }
            Assert.IsType<SetupPhaseStatus>(rows[1].Content).Apply(SetupInstallationStatus.Cancelled);
            Assert.Equal("Cancelled", Status(rows[1]));
            InvokePrivate(page, "SetBusy", false);
            var retry = Find<Button>(page, "RetryButton");
            retry.Visibility = Visibility.Visible;
            Assert.True(retry.IsEnabled);
            Assert.True(Find<Button>(page, "BackButton").IsEnabled);
            Assert.Equal(Visibility.Collapsed, cancel.Visibility);
            Invoke(retry);
            Assert.Contains("Preview only", Find<TextBlock>(page, "StatusText").Text);
            Assert.Null(Field(page, "_operation"));
        });
    }

    [Theory]
    [InlineData(ElementTheme.Light, 1120, 720)]
    [InlineData(ElementTheme.Dark, 1120, 720)]
    [InlineData(ElementTheme.Light, 720, 560)]
    [InlineData(ElementTheme.Dark, 720, 560)]
    public async Task NativeShell_KeepsPendingRunningTerminalAndRetryStatesAndActionsReachable(
        ElementTheme theme, int width, int height)
    {
        await WithNativeAsync(async (window, frame, page) =>
        {
            await OnboardingNativeProof.ApplyThemeSurfaceAsync(Assert.IsType<Grid>(window.Content), theme);
            page.Width = width;
            page.Height = height;
            page.UpdateLayout();
            await OnboardingNativeProof.NextCompositionAsync();
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            Assert.Equal(width >= 960 && height >= 600, shell.IsWideLayout);
            var title = Find<TextBlock>(page, "TitleText");
            Assert.Equal(40, title.FontSize);
            Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(title));
            var rail = Find<SetupStepIndicator>(page, "FlowProgress");
            rail.Update(OnboardingFlowPolicy.GetStages(SetupGatewayRoute.Native, window.AccessDraft.Config),
                OnboardingStage.Install);
            Assert.Equal("Step 4 of 6", AutomationProperties.GetName(rail));
            Assert.Equal(0, shell.Scroller.ScrollableWidth);
            var notice = Find<TextBlock>(page, "AcquisitionNotice");
            Assert.Contains("five-minute limit", notice.Text);
            Assert.Contains("Windows may finish deployment after cancellation", notice.Text);
            Assert.Contains("Retry checks registration again", notice.Text);
            var rows = Find<StackPanel>(page, "StepsPanel").Children.Cast<SettingsCard>().ToArray();
            Assert.All(rows, row =>
            {
                Assert.Equal(new Thickness(0, 1, 0, 0), row.BorderThickness);
                Assert.Equal(new CornerRadius(0), row.CornerRadius);
                Assert.Equal(new Thickness(0, 14, 0, 14), row.Padding);
                Assert.False(row.IsClickEnabled);
            });
            var status = Find<TextBlock>(page, "StatusText");
            var back = Find<Button>(page, "BackButton");
            var cancel = Find<Button>(page, "CancelButton");
            var retry = Find<Button>(page, "RetryButton");
            foreach (var state in new[] { "pending", "running", "failed", "cancelled", "retry" })
            {
                foreach (var row in rows) Assert.IsType<SetupPhaseStatus>(row.Content).Apply(SetupInstallationStatus.Pending);
                var running = state == "running";
                var terminal = state is "failed" or "cancelled" or "retry";
                retry.Visibility = terminal ? Visibility.Visible : Visibility.Collapsed;
                status.Text = $"Synthetic native package state: {state}. No installer or Gateway ran.";
                if (state != "pending") InvokePrivate(page, "SetCurrentStep", 1);
                if (terminal) Assert.IsType<SetupPhaseStatus>(rows[1].Content).Apply(
                    state == "cancelled" ? SetupInstallationStatus.Cancelled : SetupInstallationStatus.Failed);
                InvokePrivate(page, "SetBusy", running);
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                await OnboardingNativeProof.NextCompositionAsync();
                page.UpdateLayout();
                Assert.Equal(!running, back.IsEnabled);
                Assert.Equal(running ? Visibility.Visible : Visibility.Collapsed, cancel.Visibility);
                Assert.Equal(terminal ? Visibility.Visible : Visibility.Collapsed, retry.Visibility);
                OnboardingNativeProof.AssertFullyVisible(back, shell);
                var action = running ? cancel : terminal ? retry : back;
                OnboardingNativeProof.AssertFullyVisible(action, shell);
                if (terminal) OnboardingNativeProof.AssertFullyVisible(status, shell.Scroller);
                Assert.Equal(0, shell.Scroller.ScrollableWidth);
                var text = string.Join("\n", TestSupport.FindDescendants<TextBlock>(page).Select(block => block.Text));
                Assert.DoesNotContain("WSL", text);
                Assert.DoesNotContain("Local AI", text);
                Assert.DoesNotContain("Tailscale", text);
                Assert.Same(page, frame.Content);
            }
            Assert.True(retry.Focus(FocusState.Keyboard));
            var mascot = page.FindName("ProgressMascot");
            page.Width = width == 1120 ? 720 : 1120;
            page.Height = height == 720 ? 560 : 720;
            page.UpdateLayout();
            await OnboardingNativeProof.NextCompositionAsync();
            Assert.Same(mascot, page.FindName("ProgressMascot"));
            Assert.Same(retry, FocusManager.GetFocusedElement(page.XamlRoot));
            OnboardingNativeProof.AssertFullyVisible(retry, shell);
            Assert.Null(Field(page, "_operation"));
        });
    }

    private static string Status(SettingsCard row) => Assert.IsType<TextBlock>(
        Assert.IsType<SetupPhaseStatus>(row.Content).FindName("StatusText")).Text;

    private static void Invoke(Control control)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(control);
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static object? InvokePrivate(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    private static T Find<T>(Page page, string name) => Assert.IsType<T>(page.FindName(name));

    private async Task WithNativeAsync(Func<SetupWindow, Frame, NativeGatewaySetupPage, Task> assertion)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var temp = new TempDirectory("native-installation-shell-");
        using var preview = new EnvironmentScope("OPENCLAW_SETUP_PREVIEW_PAGE", "native");
        var configPath = temp.Combine("setup.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new SetupConfig()));
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: configPath,
                    dataDir: temp.Combine("data"), localDataDir: temp.Combine("local"),
                    commandLineArgs: [], startupRegistrationAllowed: false));
                window.SelectGatewayRoute(SetupGatewayRoute.Native);
                window.AppWindow.Resize(new(1800, 1400));
                window.AppWindow.Move(new(-32000, -32000));
                var frame = Assert.IsType<Frame>(Assert.IsType<Grid>(window.Content).FindName("RootFrame"));
                if (frame.Content is not NativeGatewaySetupPage)
                    Assert.True(frame.Navigate(typeof(NativeGatewaySetupPage), new SetupConfig()));
                window.Activate();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => frame.Content is NativeGatewaySetupPage { IsLoaded: true }, "native installation preview mounted");
                var page = Assert.IsType<NativeGatewaySetupPage>(frame.Content);
                Find<OnboardingMascot>(page, "ProgressMascot").IsAnimationEnabled = false;
                Assert.Null(Field(page, "_operation"));
                await assertion(window, frame, page);
                Assert.False(File.Exists(temp.Combine("data", "gateways.json")));
                Assert.False(File.Exists(temp.Combine("data", "settings.json")));
            }
            finally
            {
                if (window is not null) { window.Close(); await window.CleanupCompleted; }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }
}
