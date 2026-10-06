using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class WizardPageShellTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(ElementTheme.Light, 1120, 720)]
    [InlineData(ElementTheme.Dark, 1120, 720)]
    [InlineData(ElementTheme.Light, 720, 560)]
    [InlineData(ElementTheme.Dark, 720, 560)]
    public async Task MountedPrompt_KeepsDraftFocusNamesAndRouteAcrossBreakpoints(
        ElementTheme theme, int width, int height)
    {
        await WithPageAsync(theme, width, height, async page =>
        {
            await ApplyAsync(page, "text", initial: "");
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            var input = Find<TextBox>(page, "TextInput");
            var mascot = Find<OnboardingMascot>(page, "MascotHero");
            var indicator = Find<SetupStepIndicator>(page, "FlowProgress");
            Assert.Equal("OpenClaw onboard", Find<TextBlock>(page, "HeadingText").Text);
            Assert.Equal(40, Find<TextBlock>(page, "HeadingText").FontSize);
            Assert.Equal(18, Find<TextBlock>(page, "StatusText").FontSize);
            Assert.Equal(AutomationHeadingLevel.Level1,
                AutomationProperties.GetHeadingLevel(Find<TextBlock>(page, "HeadingText")));
            Assert.IsType<StackPanel>(page.FindName("MainScroller"));
            Assert.Equal("Step 5 of 5", AutomationProperties.GetName(indicator));
            Assert.DoesNotContain(TestSupport.FindDescendants<TextBlock>(indicator),
                text => text.Text == "Start chatting");
            input.Text = "draft answer";
            await TestSupport.WaitForRenderedConditionAsync(
                () => Find<Button>(page, "PrimaryButton").IsEnabled, "draft answer enables Continue");
            input.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            await ui.YieldToRenderAsync();
            Assert.True(input.Focus(FocusState.Keyboard));
            var connection = Field(page, "_connection");
            foreach (var size in new[] { (width, height), (720, 560), (960, 599), (959, 600), (960, 600), (width, height) })
            {
                page.Width = size.Item1;
                page.Height = size.Item2;
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                Assert.Equal(size.Item1 >= 960 && size.Item2 >= 600, shell.IsWideLayout);
                Assert.Same(input, page.FindName("TextInput"));
                Assert.Same(mascot, page.FindName("MascotHero"));
                Assert.Same(input, FocusManager.GetFocusedElement(page.XamlRoot));
                Assert.Equal("draft answer", input.Text);
                Assert.Equal("fixture-render", Field(page, "_sessionId"));
                Assert.Same(connection, Field(page, "_connection"));
                Assert.Equal(0, shell.Scroller.ScrollableWidth);
                AssertFooter(page);
            }
        });
    }

    [Fact]
    public async Task DynamicPromptKinds_PreserveTypedAnswersAndExplicitConsent()
    {
        await WithPageAsync(ElementTheme.Light, 1120, 720, async page =>
        {
            await ApplyAsync(page, "confirm", title: "Security consent");
            Assert.Equal("Security consent", Find<TextBlock>(page, "TitleText").Text);
            Assert.Equal("Yes", Find<Button>(page, "PrimaryButton").Content);
            Assert.Equal("No", Find<Button>(page, "SecondaryButton").Content);
            Assert.Equal(Visibility.Visible, Find<Button>(page, "SecondaryButton").Visibility);
            Assert.True(Assert.IsType<bool>(Answer(page)));

            object[] options =
            [
                new { value = true, label = "Boolean choice" },
                new { value = 42, label = "Numeric choice" },
            ];
            await ApplyAsync(page, "select", initial: 42, options: options);
            Assert.Equal(42, Assert.IsType<JsonElement>(Answer(page)).GetInt32());
            Assert.Equal("Numeric choice", AutomationProperties.GetName(
                Assert.IsType<ListViewItem>(Find<ListView>(page, "SelectOptions").SelectedItem)));

            await ApplyAsync(page, "multiselect", initial: new object[] { true, 42 }, options: options);
            var answers = Assert.IsType<JsonElement[]>(Answer(page));
            Assert.True(answers[0].GetBoolean());
            Assert.Equal(42, answers[1].GetInt32());

            await ApplyAsync(page, "provider-choice", initial: true, options: options);
            Assert.Equal(Visibility.Visible, Find<ListView>(page, "SelectOptions").Visibility);
            Assert.True(Assert.IsType<JsonElement>(Answer(page)).GetBoolean());

            await ApplyAsync(page, "text", sensitive: true, initial: "test-auth-token");
            var secret = Find<PasswordBox>(page, "SecretInput");
            Assert.Equal(Visibility.Visible, secret.Visibility);
            Assert.Equal(Visibility.Collapsed, Find<TextBox>(page, "TextInput").Visibility);
            Assert.Equal("test-auth-token", secret.Password);
            Assert.Equal("test-auth-token", Answer(page));
            Assert.Same(Find<TextBlock>(page, "TitleText"), AutomationProperties.GetLabeledBy(secret));
            Assert.DoesNotContain("test-auth-token", AutomationProperties.GetName(secret));

            await ApplyAsync(page, "text", initial: "");
            Assert.False(Find<Button>(page, "PrimaryButton").IsEnabled);
            Find<TextBox>(page, "TextInput").Text = "new answer";
            await TestSupport.WaitForRenderedConditionAsync(
                () => Find<Button>(page, "PrimaryButton").IsEnabled, "text prompt answered");
            Assert.Equal("new answer", Answer(page));

            await ApplyAsync(page, "note", message: "A Gateway-returned note.");
            Assert.Equal(Visibility.Collapsed, Find<Button>(page, "SecondaryButton").Visibility);
            Assert.True(Find<Button>(page, "PrimaryButton").IsEnabled);
            Assert.Equal(Visibility.Visible, Find<RichTextBlock>(page, "MessageBlock").Visibility);
        });
    }

    [Theory]
    [InlineData(ElementTheme.Light, 1120, 720)]
    [InlineData(ElementTheme.Dark, 1120, 720)]
    [InlineData(ElementTheme.Light, 720, 560)]
    [InlineData(ElementTheme.Dark, 720, 560)]
    public async Task LongTranscriptConsoleAndRecovery_UseOneVerticalViewportAndFixedWrappedActions(
        ElementTheme theme, int width, int height)
    {
        await WithPageAsync(theme, width, height, async page =>
        {
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            for (var index = 0; index < 30; index++)
                Invoke(page, "AppendTranscriptTurn", $"Answered question {index}", "Preserved prior answer.");
            await ApplyAsync(page, "text", message: "Current authorization guidance stays selectable.", initial: "");
            for (var index = 0; index < 120; index++)
                Invoke(page, "AppendConsoleLine", $"Fixture log line {index}. " + new string('x', 100));
            var issueType = typeof(WizardPage).GetMethod("ShowConsoleIssue",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetParameters()[0].ParameterType;
            Invoke(page, "ShowConsoleIssue", Enum.Parse(issueType, "Skipped"));
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ConsoleRecovery").Visibility);
            Assert.Equal("WizardConsoleRecoveryTerminal", AutomationProperties.GetAutomationId(
                Find<Button>(page, "ConsoleRecoveryTerminalButton")));
            Assert.True(Find<RichTextBlock>(page, "ConsoleBannerBlock").IsTextSelectionEnabled);
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            Invoke(page, "ScrollActiveIntoView");
            await TestSupport.WaitForRenderedConditionAsync(() => shell.Scroller.VerticalOffset > 0, "active question scroll");
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            OnboardingNativeProof.AssertFullyVisible(Find<TextBlock>(page, "TitleText"), shell.Scroller);
            Assert.Equal(0, shell.Scroller.ScrollableWidth);
            AssertFooter(page);
            foreach (var name in new[] { "ConsoleRecoveryTerminalButton", "TextInput" })
            {
                var input = Find<Control>(page, name);
                input.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
                await ui.YieldToRenderAsync();
                page.UpdateLayout();
                OnboardingNativeProof.AssertFullyVisible(input, shell.Scroller);
                AssertFooter(page);
            }
            await ApplyAsync(page, "note");
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ConsoleRecovery").Visibility);
            SetField(page, "_hostAccessPlan", GatewayHostAccessClassifier.Classify(new GatewayRecord
            {
                Id = "fixture-unlaunched", Url = "ws://127.0.0.1:1", SetupManagedDistroName = "Fixture-Distro",
            }));
            Invoke(page, "ShowError", "Fixture failure remains visible and selectable.");
            Assert.Equal(Visibility.Visible, Find<TextBlock>(page, "ErrorText").Visibility);
            Assert.True(Find<TextBlock>(page, "ErrorText").IsTextSelectionEnabled);
            Assert.Equal(Visibility.Visible, Find<Grid>(page, "GatewayRecovery").Visibility);
            Assert.Equal("Start wizard again", Find<Button>(page, "PrimaryButton").Content);
            Assert.True(Find<Button>(page, "PrimaryButton").IsEnabled);
            var more = Find<DropDownButton>(page, "MoreOptionsButton");
            Assert.Equal(["Restart onboard", "Skip & exit"],
                Assert.IsType<MenuFlyout>(more.Flyout).Items.Cast<MenuFlyoutItem>().Select(item => item.Text));
            Find<Button>(page, "PrimaryButton").Content = "Retry the current Gateway question without losing earlier answers";
            more.Content = "More options for the current compatibility setup";
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            AssertFooter(page);
            var error = Find<TextBlock>(page, "ErrorText");
            error.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            await ui.YieldToRenderAsync();
            page.UpdateLayout();
            await OnboardingNativeProof.NextCompositionAsync();
            OnboardingNativeProof.AssertFullyVisible(error, shell.Scroller);
            foreach (var action in Find<Grid>(page, "GatewayRecovery").Children.OfType<Button>())
            {
                action.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                await ui.YieldToRenderAsync();
                page.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync();
                OnboardingNativeProof.AssertFullyVisible(action, shell.Scroller);
            }
            Invoke(page, "SetBusy", "Waiting for the current Gateway.");
            Assert.False(Find<Button>(page, "PrimaryButton").IsEnabled);
            Assert.True(Find<ProgressRing>(page, "BusyRing").IsActive);
            Assert.Equal(Visibility.Collapsed, Find<Grid>(page, "GatewayRecovery").Visibility);
        });
    }

    [Fact]
    public async Task PageCancellation_DrainsOnceAndCannotRestartDepartedCompatibilityPage()
    {
        await WithPageAsync(ElementTheme.Light, 720, 560, async page =>
        {
            await ApplyAsync(page, "text", initial: "draft answer");
            var close = Assert.IsAssignableFrom<Task>(Invoke(page, "CancelAndWaitAsync"));
            Assert.Same(close, Invoke(page, "CancelAndWaitAsync"));
            await close;
            Assert.True(Assert.IsType<bool>(Field(page, "_leaving")));
            Assert.Null(Field(page, "_connection"));
            await Assert.IsAssignableFrom<Task>(Invoke(page, "StartWizardAsync", true));
            Assert.Null(Field(page, "_startTask"));
        });
    }

    private async Task WithPageAsync(ElementTheme theme, int width, int height, Func<WizardPage, Task> assertion)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var temp = new TempDirectory("wizard-shell-");
        await ui.RunOnUIAsync(async () =>
        {
            var oldPreview = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_PREVIEW_PAGE");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PREVIEW_PAGE", "wizard");
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            var originalSize = ui.TestWindow.AppWindow.Size;
            ui.TestWindow.AppWindow.Resize(new(1800, 1400));
            var root = new Grid();
            var frame = new Frame();
            root.Children.Add(frame);
            ui.Container.Children.Add(root);
            WizardPage? page = null;
            try
            {
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
                Assert.True(frame.Navigate(typeof(WizardPage), new SetupConfig()));
                page = Assert.IsType<WizardPage>(frame.Content);
                await TestSupport.WaitForRenderedConditionAsync(() => page.IsLoaded, "compatibility page mounted");
                var client = new OpenClawGatewayClient("ws://127.0.0.1:1", "test-auth-token",
                    identityPath: temp.Combine("identity"));
                var connectionType = typeof(WizardPage).GetNestedType("WizardConnection", BindingFlags.NonPublic)!;
                SetField(page, "_connection", Activator.CreateInstance(connectionType, [client, null]));
                Find<StackPanel>(page, "TranscriptPanel").Children.Clear();
                Find<OnboardingMascot>(page, "MascotHero").IsAnimationEnabled = false;
                var indicator = Find<SetupStepIndicator>(page, "FlowProgress");
                indicator.IsAnimationEnabled = false;
                indicator.Update(OnboardingFlowPolicy.GetStages(SetupGatewayRoute.Native,
                    new SetupConfig(), includeReadyChoice: false), OnboardingStage.AiSetup);
                page.Width = width;
                page.Height = height;
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                await assertion(page);
            }
            finally
            {
                if (page is not null) await Assert.IsAssignableFrom<Task>(Invoke(page, "CancelAndWaitAsync"));
                ui.Container.Children.Remove(root);
                ui.TestWindow.AppWindow.Resize(originalSize);
                Application.Current.Resources.MergedDictionaries.Remove(resources);
                Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PREVIEW_PAGE", oldPreview);
            }
        });
    }

    private static Task ApplyAsync(WizardPage page, string type, object? initial = null, object[]? options = null,
        bool sensitive = false, string title = "Fixture question", string message = "Synthetic Gateway guidance.")
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            sessionId = "fixture-render", done = false,
            step = new { id = "fixture-" + Guid.NewGuid().ToString("N"), type, title, message, sensitive,
                initialValue = initial, options = options ?? [] },
        });
        return Assert.IsAssignableFrom<Task>(Invoke(page, "ApplyPayloadAsync", payload));
    }

    private static object Answer(WizardPage page)
    {
        object?[] arguments = [null];
        Assert.True(Assert.IsType<bool>(typeof(WizardPage)
            .GetMethod("TryBuildAnswerValue", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, arguments)));
        return Assert.IsAssignableFrom<object>(arguments[0]);
    }

    private static void AssertFooter(WizardPage page)
    {
        var shell = Assert.IsType<SetupPageShell>(page.Content);
        var footer = Find<Grid>(page, "NavigationFooter");
        Assert.Same(footer, shell.Footer);
        OnboardingNativeProof.AssertFullyVisible(footer, shell);
        var right = 0d;
        foreach (var action in footer.Children.OfType<Control>().Where(action => action.Visibility == Visibility.Visible))
        {
            var bounds = action.TransformToVisual(footer).TransformBounds(new(0, 0, action.ActualWidth, action.ActualHeight));
            Assert.True(bounds.Width > 0 && bounds.Height >= 40);
            Assert.True(bounds.Left >= right && bounds.Right <= footer.ActualWidth + 0.1);
            Assert.All(TestSupport.FindDescendants<TextBlock>(action), text => Assert.False(text.IsTextTrimmed));
            right = bounds.Right;
        }
    }

    private static T Find<T>(Page page, string name) where T : class => Assert.IsAssignableFrom<T>(page.FindName(name));
    private static object? Field(WizardPage page, string name) =>
        typeof(WizardPage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
    private static void SetField(WizardPage page, string name, object? value) =>
        typeof(WizardPage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);
    private static object? Invoke(WizardPage page, string name, params object?[] arguments) =>
        typeof(WizardPage).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, arguments);
}
