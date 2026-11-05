using System.Reflection;
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
public sealed class SetupNativeConnectionShellTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(ElementTheme.Light, 1120, 720)]
    [InlineData(ElementTheme.Dark, 1120, 720)]
    [InlineData(ElementTheme.Light, 720, 560)]
    [InlineData(ElementTheme.Dark, 720, 560)]
    public async Task MountedEditor_KeepsInputsFocusAndFixedFooterAcrossBreakpoints(
        ElementTheme theme, int width, int height)
    {
        await WithEditorAsync(new Host(), new(), async (page, state) =>
        {
            await OnboardingNativeProof.ApplyThemeSurfaceAsync(Assert.IsType<Grid>(page.Frame.Parent), theme);
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            var address = Find<TextBox>(page, "AddressInput");
            var code = Find<PasswordBox>(page, "CodeInput");
            var token = Find<PasswordBox>(page, "TokenInput");
            Assert.Equal("Connect to a gateway", Find<TextBlock>(page, "TitleText").Text);
            Assert.Equal(40, Find<TextBlock>(page, "TitleText").FontSize);
            Assert.Equal(18, Find<TextBlock>(page, "DescriptionText").FontSize);
            Assert.Equal(AutomationHeadingLevel.Level1,
                AutomationProperties.GetHeadingLevel(Find<TextBlock>(page, "TitleText")));
            Assert.Equal(2048, code.MaxLength);
            Assert.Empty(address.Text);
            Assert.Empty(code.Password);
            Assert.Empty(token.Password);
            Assert.True(Find<Button>(page, "CheckButton").IsEnabled);
            address.Text = "wss://gateway.invalid";
            token.Password = "test-auth-token";
            var ssh = Find<SettingsExpander>(page, "SshExpander");
            ssh.IsExpanded = true;
            Find<ToggleSwitch>(page, "SshEnabled").IsOn = true;
            Find<TextBox>(page, "SshHostInput").Text = "host.invalid";
            Find<TextBox>(page, "SshUserInput").Text = "fixture";
            await ui.YieldToRenderAsync();
            var draft = state.Drafts[^1];
            Assert.Equal("host.invalid", draft.SshTunnel!.Host);
            Assert.Equal("fixture", draft.SshTunnel.User);
            Assert.True(address.Focus(FocusState.Keyboard));
            foreach (var size in new[] { (width, height), (720, 560), (960, 599), (959, 600), (960, 600), (width, height) })
            {
                page.Width = size.Item1;
                page.Height = size.Item2;
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                Assert.Equal(size.Item1 >= 960 && size.Item2 >= 600, shell.IsWideLayout);
                Assert.Same(address, page.FindName("AddressInput"));
                Assert.Same(address, FocusManager.GetFocusedElement(page.XamlRoot));
                Assert.Same(draft, state.Drafts[^1]);
                Assert.Equal("test-auth-token", token.Password);
                Assert.True(ssh.IsExpanded);
                Assert.Equal(0, shell.Scroller.ScrollableWidth);
                AssertFooter(page, shell);
            }
            foreach (var name in new[] { "AddressInput", "CodeInput", "TokenInput", "SshEnabled",
                         "SshHostInput", "SshUserInput", "SshPortInput", "RemotePortInput", "LocalPortInput" })
            {
                var input = Find<Control>(page, name);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(input)));
                input.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
                await ui.YieldToRenderAsync();
                page.UpdateLayout();
                OnboardingNativeProof.AssertFullyVisible(input, shell.Scroller);
                AssertFooter(page, shell);
            }
            Assert.Empty(state.Connected);
        });
    }

    [Fact]
    public async Task CheckIsVerifyOnly_NextRevalidatesSameCapturedDraftAndBackPreservesCommit()
    {
        var host = new Host();
        await WithEditorAsync(host, new("wss://gateway.invalid", SharedToken: "test-auth-token"),
            async (page, state) =>
        {
            Invoke(page, "CheckButton");
            await Pending(page);
            Assert.Single(host.Checks);
            Assert.Empty(host.Connects);
            Assert.Empty(state.Connected);
            Assert.Equal(InfoBarSeverity.Success, Find<InfoBar>(page, "ResultBar").Severity);
            Assert.Contains("Nothing was saved", Find<InfoBar>(page, "ResultBar").Message);
            Find<TextBox>(page, "AddressInput").Text = "wss://edited.invalid";
            await TestSupport.WaitForRenderedConditionAsync(() => !Find<InfoBar>(page, "ResultBar").IsOpen,
                "edited draft invalidates checked result");
            Assert.False(Find<InfoBar>(page, "ResultBar").IsOpen);
            var edited = state.Drafts[^1];
            Invoke(page, "CheckButton");
            await Pending(page);
            Invoke(page, "NextButton");
            await Pending(page);
            Assert.Same(edited, host.Checks[^1]);
            Assert.Same(edited, Assert.Single(host.Connects));
            Assert.True(Assert.Single(state.Connected).GatewayCommitted);
            Assert.Equal("wss://edited.invalid", state.Drafts[^1].GatewayUrl.TrimEnd('/'));
            Assert.Null(state.Drafts[^1].SharedToken);
            Invoke(page, "BackButton");
            Assert.Equal(1, state.BackCount);
            await page.DisposeAsync();
            Assert.Single(state.Connected);
            Assert.Empty(Find<PasswordBox>(page, "TokenInput").Password);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("wss://gateway.invalid")]
    public async Task FailedChecks_RemainEditableAndNeverAdvance(string address)
    {
        var host = new Host { Fail = true };
        await WithEditorAsync(host, new(address), async (page, state) =>
        {
            Invoke(page, "CheckButton");
            await Pending(page);
            var result = Find<InfoBar>(page, "ResultBar");
            Assert.True(result.IsOpen);
            Assert.Equal(InfoBarSeverity.Error, result.Severity);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
            Assert.True(Find<ContentControl>(page, "Editor").IsEnabled);
            Assert.True(Find<Button>(page, "NextButton").IsEnabled);
            Assert.Empty(state.Connected);
            Assert.Empty(host.Connects);
            Find<TextBox>(page, "AddressInput").Text = "wss://revised.invalid";
            await TestSupport.WaitForRenderedConditionAsync(() => !result.IsOpen, "edited draft clears failure");
            Assert.False(result.IsOpen);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingOperation_CancelDrainsAndRetainsDraftWithoutCommit(bool connect)
    {
        var host = new Host { WaitForCancellation = true };
        await WithEditorAsync(host, new("wss://gateway.invalid", SharedToken: "test-auth-token"),
            async (page, state) =>
        {
            Invoke(page, connect ? "NextButton" : "CheckButton");
            Assert.False(Find<ContentControl>(page, "Editor").IsEnabled);
            Assert.False(Find<Button>(page, "BackButton").IsEnabled);
            Assert.False(Find<Button>(page, "CheckButton").IsEnabled);
            Assert.False(Find<Button>(page, "NextButton").IsEnabled);
            Assert.Equal(Visibility.Visible, Find<Button>(page, "CancelButton").Visibility);
            Assert.Equal("Checking the gateway connection...", Find<InfoBar>(page, "ResultBar").Message);
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            AssertFooter(page, Assert.IsType<SetupPageShell>(page.Content));
            Invoke(page, "CancelButton");
            await Pending(page);
            Assert.True(host.CancellationDrained);
            Assert.Empty(state.Connected);
            Assert.Empty(state.Drafts);
            Assert.Equal("test-auth-token", Find<PasswordBox>(page, "TokenInput").Password);
            Assert.Equal(InfoBarSeverity.Informational, Find<InfoBar>(page, "ResultBar").Severity);
            Assert.True(Find<ContentControl>(page, "Editor").IsEnabled);
            await page.DisposeAsync();
            Assert.True(host.DiscardAfterDrain);
        });
    }

    private async Task WithEditorAsync(Host host, SetupNativeConnectionRequest request,
        Func<SetupNativeConnectionPage, State, Task> assertion)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            var originalSize = ui.TestWindow.AppWindow.Size;
            ui.TestWindow.AppWindow.Resize(new(1800, 1400));
            var root = new Grid();
            var frame = new Frame();
            root.Children.Add(frame);
            ui.Container.Children.Add(root);
            SetupNativeConnectionPage? page = null;
            try
            {
                var state = new State();
                Assert.True(frame.Navigate(typeof(SetupNativeConnectionPage),
                    new SetupNativeConnectionNavigationArgs(host, request, state.Drafts.Add,
                        state.Connected.Add, () => state.BackCount++)));
                page = Assert.IsType<SetupNativeConnectionPage>(frame.Content);
                await TestSupport.WaitForRenderedConditionAsync(() => page.IsLoaded, "native editor mounted");
                page.Width = 1120;
                page.Height = 720;
                Find<OnboardingMascot>(page, "MascotHero").IsAnimationEnabled = false;
                var progress = Find<SetupStepIndicator>(page, "FlowProgress");
                progress.IsAnimationEnabled = false;
                progress.Update(OnboardingFlowPolicy.GetStages(SetupGatewayRoute.Existing, new SetupConfig()),
                    OnboardingStage.Gateway);
                Assert.Equal("Step 1 of 3", AutomationProperties.GetName(progress));
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                await assertion(page, state);
            }
            finally
            {
                if (page is not null) await page.DisposeAsync();
                ui.Container.Children.Remove(root);
                ui.TestWindow.AppWindow.Resize(originalSize);
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    private static void AssertFooter(Page page, SetupPageShell shell)
    {
        var footer = Find<Grid>(page, "NavigationFooter");
        Assert.Same(footer, shell.Footer);
        OnboardingNativeProof.AssertFullyVisible(footer, shell);
        var right = 0d;
        foreach (var button in footer.Children.OfType<Button>().Where(button => button.Visibility == Visibility.Visible))
        {
            var bounds = button.TransformToVisual(footer).TransformBounds(new(0, 0, button.ActualWidth, button.ActualHeight));
            Assert.True(bounds.Width > 0 && bounds.Height >= 40);
            Assert.True(bounds.Left >= right);
            Assert.True(bounds.Right <= footer.ActualWidth + 0.1);
            right = bounds.Right;
        }
    }

    private static T Find<T>(Page page, string name) where T : class => Assert.IsAssignableFrom<T>(page.FindName(name));

    private static void Invoke(Page page, string name)
    {
        var button = Find<Button>(page, name);
        Assert.True(button.IsEnabled);
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static Task Pending(SetupNativeConnectionPage page) =>
        Assert.IsAssignableFrom<Task>(typeof(SetupNativeConnectionPage)
            .GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));

    private sealed class State
    {
        public List<SetupNativeConnectionRequest> Drafts { get; } = [];
        public List<SetupNativeConnectionResult> Connected { get; } = [];
        public int BackCount { get; set; }
    }

    private sealed class Host : ISetupNativeConnectionHost
    {
        public List<SetupNativeConnectionRequest> Checks { get; } = [];
        public List<SetupNativeConnectionRequest> Connects { get; } = [];
        public bool Fail { get; init; }
        public bool WaitForCancellation { get; init; }
        public bool CancellationDrained { get; private set; }
        public bool DiscardAfterDrain { get; private set; }

        public Task<SetupNativeConnectionResult> CheckAsync(SetupNativeConnectionRequest request, CancellationToken cancellationToken)
        {
            Checks.Add(request);
            return RunAsync(request, false, cancellationToken);
        }

        public Task<SetupNativeConnectionResult> ConnectAsync(SetupNativeConnectionRequest request, CancellationToken cancellationToken)
        {
            Connects.Add(request);
            return RunAsync(request, true, cancellationToken);
        }

        private async Task<SetupNativeConnectionResult> RunAsync(SetupNativeConnectionRequest request, bool connect,
            CancellationToken cancellationToken)
        {
            if (WaitForCancellation)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                finally { CancellationDrained = true; }
            }
            try
            {
                var input = SetupNativeConnectionInputResolver.Resolve(request);
                return Fail ? new(false, Error: "Fixture connection failed.")
                    : new(true, connect, connect ? "fixture-gateway" : null, input.GatewayUrl);
            }
            catch (ArgumentException error) { return new(false, Error: error.Message); }
        }

        public Task DiscardCheckAsync()
        {
            DiscardAfterDrain = !WaitForCancellation || CancellationDrained;
            return Task.CompletedTask;
        }
    }
}
