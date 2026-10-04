using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.TestSupport;
using OpenClaw.Shared.Inference.Catalog;
using Xunit.Abstractions;
using CommunityToolkit.WinUI.Controls;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class OnboardingAiPageTests(UIThreadFixture ui, ITestOutputHelper output)
{
    private bool _nativeProof;

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task FocusedAi_DefaultSetupWindowKeepsCenteredHeaderAndScrollableActions(ElementTheme theme)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var directory = new TempDirectory("openclaw-ai-density-");
        var config = new SetupConfig();
        var configPath = directory.Combine("setup.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            AiSetupPage? page = null;
            IDisposable? navigation = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: configPath,
                    dataDir: directory.Combine("data"), localDataDir: directory.Combine("local"), commandLineArgs: []));
                var defaultSize = window.AppWindow.Size;
                var root = Assert.IsType<Grid>(window.Content);
                var frame = Assert.IsType<Frame>(root.FindName("RootFrame"));
                navigation = OnboardingNativeProof.TrackNavigation(frame);
                var transport = new PageTransport(true) { IncludeAdditionalCandidate = true, IncludeDensityChoices = true };
                var local = new PageLocalAiHost(LocalAiOnboardingState.Use);
                var completed = 0;
                frame.Navigate(typeof(AiSetupPage), new AiSetupPageArgs(config, directory.Path, directory.Path,
                    () => false, () => { completed++; return Task.CompletedTask; },
                    TransportFactory: () => transport, LocalAiHost: local));
                page = Assert.IsType<AiSetupPage>(frame.Content);
                OnboardingNativeProof.ActivateOwned(window);
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
                await WaitAsync(() => Find<ItemsControl>(page, "FeaturedSignInChoices").Items.Count == 2 &&
                    Find<SettingsCard>(page, "LocalAiCard").IsClickEnabled);
                root.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync();
                var scroll = Find<ScrollViewer>(page, "ChoicesScroller");
                var header = Find<StackPanel>(page, "AiHeader");
                var rows = new[] { Find<SettingsCard>(page, "LocalAiCard") }
                    .Concat(new[] { "CandidateChoices", "PrepareChoices", "FeaturedSignInChoices" }
                        .SelectMany(name => TestSupport.FindDescendants<SettingsCard>(Find<ItemsControl>(page, name))))
                    .Append(Find<SettingsCard>(page, "ApiKeysButton")).ToArray();
                Assert.Equal(6, rows.Length);
                var textScale = new Windows.UI.ViewManagement.UISettings().TextScaleFactor;
                Assert.Equal(defaultSize, window.AppWindow.Size);
                Assert.Equal(0, NativeProofLayout.PhysicalPixels(scroll.ScrollableWidth, root.XamlRoot.RasterizationScale));
                if (textScale <= 1)
                {
                    Assert.True(scroll.ViewportHeight >= 280);
                    Assert.InRange(header.ActualHeight, OnboardingMascot.HeroSize, 300);
                }
                OnboardingNativeProof.AssertHeroLayout(Find<OnboardingMascot>(page, "MascotHero"), output);
                OnboardingNativeProof.AssertFullyVisible(header, root);
                Assert.Empty(TestSupport.FindDescendants<ListViewItem>(scroll));
                using (await OnboardingNativeProof.CaptureAsync(window, $"ai-focused-default-{theme}", output,
                    ["Connect your AI", "Local AI on this PC", "Refresh"],
                    new { textScale, defaultSize, headerHeight = header.ActualHeight,
                        scroll.ViewportHeight, scroll.ExtentHeight, rowHeights = rows.Select(row => row.ActualHeight).ToArray() },
                    requiredContent: page)) { }
                if (scroll.ScrollableHeight > 0)
                {
                    scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                    await TestSupport.WaitForRenderedConditionAsync(
                        () => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 0.5, "AI last viewport");
                    using (await OnboardingNativeProof.CaptureAsync(window, $"ai-focused-default-{theme}-last", output,
                        ["Connect your AI", "API Keys", "Refresh"], new { textScale, scroll.VerticalOffset },
                        requiredContent: page)) { }
                }
                Assert.Equal(["openclaw.setup.detect"], transport.MethodCalls);
                Assert.Equal(0, completed);
                Assert.Equal(0, local.Actions);
                foreach (var row in rows)
                {
                    row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                    await OnboardingNativeProof.NextCompositionAsync();
                    OnboardingNativeProof.AssertFullyVisible(row, scroll);
                }
            }
            finally
            {
                if (page is not null) await page.CloseAsync();
                if (window is not null)
                {
                    window.Close();
                    await window.CleanupCompleted.WaitAsync(TimeSpan.FromSeconds(10));
                }
                navigation?.Dispose();
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [Fact]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task FocusedAi_RequiredNotesAndConfirmationStayInOneDialogThroughVerification()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            var dialog = GetDialog(page);
            var opened = 0;
            var closed = 0;
            dialog.Opened += (_, _) => opened++;
            dialog.Closed += (_, _) => closed++;
            await InvokeChoiceAsync(page);
            await WaitAsync(() => dialog.CanSubmit && opened == 1);
            Assert.Equal("Continue", dialog.PrimaryButtonText);
            Assert.Equal("", dialog.SecondaryButtonText);
            using (await OnboardingNativeProof.CaptureAsync(ui.TestWindow, "ai-focused-provider-note", output,
                ["Plugin capabilities", "Continue", "Cancel"], new { opened, closed },
                requiredContent: Assert.IsType<ScrollViewer>(dialog.Content))) { }
            Invoke(DialogButton(dialog, "PrimaryButton"));
            await WaitAsync(() => dialog.CanSubmit && dialog.Title?.ToString() == "Approve plugin");
            Assert.Equal("Submit", dialog.PrimaryButtonText);
            var confirm = Assert.IsType<CheckBox>(dialog.FindName("ConfirmInput"));
            Assert.False(confirm.IsChecked);
            using (await OnboardingNativeProof.CaptureAsync(ui.TestWindow, "ai-focused-provider-confirm", output,
                ["Approve plugin", "Submit", "Cancel"], new { opened, closed, confirmed = confirm.IsChecked },
                requiredContent: Assert.IsType<ScrollViewer>(dialog.Content))) { }
            confirm.IsChecked = true;
            Invoke(DialogButton(dialog, "PrimaryButton"));
            await WaitAsync(() => dialog.IsSecondaryButtonEnabled && transport.MethodCalls.Contains("openclaw.setup.verify"));
            Assert.Equal(1, opened);
            Assert.Equal(0, closed);
            Assert.Equal(2, transport.MethodCalls.Count(method => method == "wizard.next"));
            transport.FailVerification = false;
            Invoke(DialogButton(dialog, "SecondaryButton"));
            await WaitAsync(() => completed() == 1 && closed == 1);
            Assert.Equal(1, opened);
            Assert.Same(dialog, GetDialog(page));
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "openclaw.setup.activate.start"));
        }, nativeProof: true, configure: transport =>
        {
            transport.WizardStep = new() { Id = "note", Type = "note", Title = "Plugin capabilities", Executor = "client" };
            transport.FollowingSteps.Enqueue(new() { Id = "consent", Type = "confirm", Title = "Approve plugin",
                Executor = "client", InitialValue = JsonSerializer.SerializeToElement(false) });
            transport.FailVerification = true;
        });
    }

    [Fact]
    public async Task DetectionAndKeyboardFocus_DoNotActivateBeforeExplicitRowAction()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            var choices = Find<ItemsControl>(page, "CandidateChoices");
            Assert.Single(transport.MethodCalls);
            Assert.Equal("openclaw.setup.detect", transport.MethodCalls[0]);
            Assert.Equal(0, completed());
            Assert.False(string.IsNullOrWhiteSpace(Find<TextBlock>(page, "TitleText").Text));
            Assert.DoesNotContain("Onboarding_", Find<TextBlock>(page, "TitleText").Text);
            var card = Assert.Single(TestSupport.FindDescendants<SettingsCard>(choices));
            Assert.True(card.Focus(FocusState.Keyboard));
            Assert.Null(page.FindName("ContinueButton"));
            Assert.Single(transport.MethodCalls);
            Assert.Equal(0, completed());
            Assert.Contains(TestSupport.FindDescendants<TextBlock>(choices), text => text.Text == "Existing AI");
            foreach (var text in TestSupport.FindDescendants<TextBlock>(page).Where(text => !string.IsNullOrWhiteSpace(text.Text)).Take(8))
                output.WriteLine($"text={text.Text}; size={text.ActualWidth}x{text.ActualHeight}; foreground={(text.Foreground as SolidColorBrush)?.Color}; theme={text.ActualTheme}");
            await OnboardingArtworkRenderingTests.SaveProofAsync(page, "onboarding-ai-choices", output);
        });
    }

    [Fact]
    public async Task ExplicitCandidateActivation_InvokesNativeChatHandoffExactlyOnce()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeChoiceAsync(page);
            await WaitAsync(() => completed() == 1);
            Assert.Equal(
                ["openclaw.setup.detect", "openclaw.setup.activate.start",
                    "openclaw.setup.detect", "openclaw.setup.verify"],
                transport.MethodCalls);
            Assert.DoesNotContain("wizard.start", transport.MethodCalls);
            Assert.Equal("existing-model", transport.LastActivation.GetProperty("kind").GetString());
            Assert.Equal(1, completed());
        });
    }

    [Fact]
    public async Task ManualKey_IsExplicitAndWhitespaceCannotSubmit()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeCardAsync(Find<SettingsCard>(page, "ApiKeysButton"));
            await WaitAsync(() => Find<StackPanel>(page, "ApiKeyForm").Visibility == Visibility.Visible);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ApiKeyForm").Visibility);
            Assert.Equal(-1, Find<ComboBox>(page, "ApiProviderPicker").SelectedIndex);
            Find<ComboBox>(page, "ApiProviderPicker").SelectedIndex = 0;
            var input = Find<PasswordBox>(page, "ApiKeyInput");
            var connect = Find<Button>(page, "ApiKeyConnectButton");
            Assert.Null(page.FindName("ApiKeysChoices"));
            Assert.Equal("Connect with an API key or token", Find<TextBlock>(page, "ApiKeyFormHeading").Text);
            Assert.Equal(Visibility.Visible, input.Visibility);
            ui.Container.UpdateLayout();
            await ui.YieldToRenderAsync();
            Assert.False(connect.IsEnabled);
            input.Password = "   ";
            Assert.False(connect.IsEnabled);
            input.Password = "synthetic-onboarding-test-key";
            await WaitAsync(() => connect.IsEnabled);
            Assert.True(connect.IsEnabled);
            Assert.Single(transport.MethodCalls);
            Assert.Equal(0, completed());
            await OnboardingArtworkRenderingTests.SaveProofAsync(page, "onboarding-ai-api-key", output);
            Invoke(connect);
            await WaitAsync(() => completed() == 1);
            Assert.Equal("synthetic-onboarding-test-key", transport.LastActivation.GetProperty("apiKey").GetString());
            Assert.Equal("", input.Password);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderConversationDiscovery_UsesVisibleDefaultOffOptIn(bool optIn)
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            var preference = Find<CheckBox>(page, "CatalogPreference");
            Assert.Equal(Visibility.Visible, preference.Visibility);
            Assert.False(preference.IsChecked);
            Assert.Single(transport.MethodCalls);
            preference.IsChecked = optIn;
            await InvokeChoiceAsync(page);
            await WaitAsync(() => completed() == 1);
            Assert.Equal(optIn, transport.LastActivation.GetProperty("nativeSessionCatalogsEnabled").GetBoolean());
        }, configure: transport => transport.RequireCatalogConsent = true);
    }

    [Fact]
    public async Task LostActivationReply_ShowsErrorWithoutCompletionOrAutomaticRetry()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            transport.FailActivation = true;
            await InvokeChoiceAsync(page);
            await WaitAsync(() => Assert.IsType<InfoBar>(GetDialog(page).FindName("DialogError")).IsOpen);
            Assert.Equal(0, completed());
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "openclaw.setup.activate.start"));
            Assert.True(GetDialog(page).IsSecondaryButtonEnabled);
            Assert.True(Assert.IsType<Button>(GetDialog(page).FindName("CancelButton")).IsEnabled);
            await OnboardingArtworkRenderingTests.SaveProofAsync(page, "onboarding-ai-recovery", output);
        });
    }

    [Theory]
    [InlineData(ElementTheme.Light, 720)]
    [InlineData(ElementTheme.Dark, 720)]
    [InlineData(ElementTheme.Light, 600)]
    [InlineData(ElementTheme.Dark, 600)]
    public async Task ProviderPopup_PreservesBackdropSelectionScrollAndRestoresFocusAfterConfirmedCancel(
        ElementTheme theme, double width)
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            var more = Find<SettingsExpander>(page, "MoreExpander");
            more.IsExpanded = true;
            await InvokeCardAsync(Find<SettingsCard>(page, "ApiKeysButton"));
            await WaitAsync(() => Find<StackPanel>(page, "ApiKeyForm").Visibility == Visibility.Visible);
            var picker = Find<ComboBox>(page, "ApiProviderPicker");
            picker.SelectedIndex = 0;
            var selected = picker.SelectedItem;
            var input = Find<PasswordBox>(page, "ApiKeyInput");
            input.Password = "synthetic-popup-test-key";
            input.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            await OnboardingNativeProof.NextCompositionAsync();
            await WaitAsync(() => Find<Button>(page, "ApiKeyConnectButton").IsEnabled);
            var scroll = Find<ScrollViewer>(page, "ChoicesScroller");
            var viewportSettled = true;
            scroll.ViewChanged += (_, e) => viewportSettled = !e.IsIntermediate;
            var offset = scroll.VerticalOffset;
            var candidates = Find<ItemsControl>(page, "CandidateChoices").ItemsSource;
            var subtitle = Find<TextBlock>(page, "StatusText").Text;
            Invoke(Find<Button>(page, "ApiKeyConnectButton"));
            var dialog = GetDialog(page);
            await WaitAsync(() => dialog.CanSubmit);
            AssertBackdrop();
            var cancel = Assert.IsType<Button>(dialog.FindName("CancelButton"));
            Invoke(cancel);
            await WaitAsync(() => dialog.IsSecondaryButtonEnabled);
            AssertBackdrop();
            Assert.Equal(0, completed());
            transport.CancelStatus = "cancelled";
            Invoke(cancel);
            await WaitAsync(() => Find<Button>(page, "RefreshButton").IsEnabled &&
                ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(page.XamlRoot), input) &&
                viewportSettled && Math.Abs(scroll.VerticalOffset - offset) <= 1);
            Assert.InRange(Math.Abs(scroll.VerticalOffset - offset), 0, 1);
            Assert.Same(selected, picker.SelectedItem);
            Assert.Empty(input.Password);
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "openclaw.setup.activate.start"));
            Assert.Equal(2, transport.MethodCalls.Count(method => method == "wizard.cancel"));
            Assert.Equal(0, completed());
            Assert.True(Find<Button>(page, "RefreshButton").Focus(FocusState.Programmatic));
            await WaitAsync(() => scroll.BringIntoViewOnFocusChange);
            scroll.ChangeView(null, offset > 1 ? 0 : scroll.ScrollableHeight, null, disableAnimation: true);
            await WaitAsync(() => viewportSettled && Math.Abs(scroll.VerticalOffset - offset) > 1);

            void AssertBackdrop()
            {
                Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ChoicePanel").Visibility);
                Assert.Equal("Connect your AI", Find<TextBlock>(page, "TitleText").Text);
                Assert.Equal(subtitle, Find<TextBlock>(page, "StatusText").Text);
                Assert.Same(candidates, Find<ItemsControl>(page, "CandidateChoices").ItemsSource);
                Assert.Same(selected, picker.SelectedItem);
                Assert.True(more.IsExpanded);
                Assert.False(more.IsEnabled);
                Assert.InRange(Math.Abs(scroll.VerticalOffset - offset), 0, 1);
                Assert.False(picker.IsEnabled);
                Assert.False(Find<ItemsControl>(page, "CandidateChoices").IsEnabled);
                Assert.False(Find<Button>(page, "RefreshButton").IsEnabled);
                Assert.False(Find<Button>(page, "ApiKeyConnectButton").IsEnabled);
                Assert.Same(dialog, GetDialog(page));
            }
        }, theme: theme, width: width, configure: transport =>
        {
            transport.IncludeDensityChoices = true;
            transport.CancelStatus = "running";
            transport.WizardStep = new() { Id = "synthetic-confirm", Type = "confirm", Title = "Synthetic provider consent" };
        });
    }

    [Fact]
    public async Task ClosingDuringActivation_CancelsOwnedSessionAndPreventsHandoff()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            transport.HoldActivation = true;
            await InvokeChoiceAsync(page);
            await WaitAsync(() => transport.MethodCalls.Contains("openclaw.setup.activate.start"));
            var dialog = GetDialog(page);
            await WaitAsync(() => Find<StackPanel>(page, "ProviderActivity").Visibility == Visibility.Visible);
            Assert.False(Assert.IsType<ScrollViewer>(dialog.Content).IsLoaded);
            Assert.Equal(Visibility.Visible, Find<ProgressBar>(page, "ProviderActivityProgress").Visibility);
            Assert.False(string.IsNullOrWhiteSpace(Find<TextBlock>(page, "ProviderActivityStatus").Text));
            var sessionId = transport.LastActivation.GetProperty("sessionId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            var close = page.CloseAsync();
            await close;
            Assert.Same(close, page.CloseAsync());
            Assert.Equal(0, completed());
            Assert.Single(transport.MethodCalls, method => method == "wizard.cancel");
            Assert.Equal(sessionId, transport.LastCancel.GetProperty("sessionId").GetString());
            Assert.DoesNotContain("openclaw.setup.verify", transport.MethodCalls);
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "openclaw.setup.activate.start"));
        });
    }

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task ProviderLoading_StaysInlineAndCanCancelWithoutOpeningAPopup(ElementTheme theme)
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeChoiceAsync(page);
            var cancel = Find<Button>(page, "ProviderCancelButton");
            await WaitAsync(() => cancel.Visibility == Visibility.Visible && cancel.IsEnabled);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ProviderActivity").Visibility);
            Assert.Equal(Visibility.Visible, Find<ProgressBar>(page, "ProviderActivityProgress").Visibility);
            Assert.Equal("Synthetic provider loading", Find<TextBlock>(page, "ProviderActivityStatus").Text);
            Assert.False(Assert.IsType<ScrollViewer>(GetDialog(page).Content).IsLoaded);
            Assert.False(Find<ItemsControl>(page, "CandidateChoices").IsEnabled);
            Assert.Equal(0, completed());
            typeof(AiSetupPage).GetMethod("ShowError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(page, ["Failed"]);
            var error = Find<InfoBar>(page, "ProviderActivityError");
            Assert.True(error.IsOpen);
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
            Assert.False(Assert.IsType<ScrollViewer>(GetDialog(page).Content).IsLoaded);
            await OnboardingArtworkRenderingTests.SaveProofAsync(page, $"provider-inline-loading-{theme}", output);
            Invoke(cancel);
            await WaitAsync(() => Find<Button>(page, "RefreshButton").IsEnabled);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "ProviderActivity").Visibility);
            Assert.Equal(Visibility.Collapsed, cancel.Visibility);
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "wizard.cancel"));
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "openclaw.setup.activate.start"));
            Assert.Equal(0, completed());
        }, theme: theme, configure: transport => transport.WizardStep = new()
        {
            Id = "loading", Type = "progress", Executor = "gateway",
            Title = "Current model", Message = "Synthetic provider loading",
        });
    }

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task FollowupProof_ProviderInlineLoadingCancelAndInput(ElementTheme theme)
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeChoiceAsync(page);
            var cancel = Find<Button>(page, "ProviderCancelButton");
            await WaitAsync(() => cancel.Visibility == Visibility.Visible && cancel.IsEnabled);
            Assert.False(Assert.IsType<ScrollViewer>(GetDialog(page).Content).IsLoaded);
            using (await OnboardingNativeProof.CaptureAsync(ui.TestWindow, $"followup-provider-loading-{theme}", output,
                ["Connect your AI", "Synthetic provider loading", "Cancel"], requiredContent: page)) { }
            typeof(AiSetupPage).GetMethod("ShowError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(page, ["Failed"]);
            Assert.True(Find<InfoBar>(page, "ProviderActivityError").IsOpen);
            using (await OnboardingNativeProof.CaptureAsync(ui.TestWindow, $"followup-provider-inline-error-{theme}", output,
                ["Connect your AI", "Cancel"], requiredContent: page)) { }
            Invoke(cancel);
            await WaitAsync(() => Find<Button>(page, "RefreshButton").IsEnabled);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "ProviderActivity").Visibility);
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "wizard.cancel"));
            using (await OnboardingNativeProof.CaptureAsync(ui.TestWindow, $"followup-provider-cancelled-{theme}", output,
                ["Connect your AI", "Refresh"], requiredContent: page)) { }
            transport.WizardStep = new()
            {
                Id = "synthetic-input", Type = "text", Executor = "client",
                Title = "Provider input", Message = "Synthetic provider prompt. No provider contacted.",
                DeviceCode = new("TEST-CODE", 15, "Synthetic device code"),
            };
            await InvokeChoiceAsync(page);
            var dialog = GetDialog(page);
            await WaitAsync(() => dialog.CanSubmit && Assert.IsType<ScrollViewer>(dialog.Content).IsLoaded);
            Assert.Equal("TEST-CODE", Assert.IsType<TextBlock>(dialog.FindName("DeviceCode")).Text);
            using (await OnboardingNativeProof.CaptureAsync(ui.TestWindow, $"followup-provider-input-{theme}", output,
                ["Provider input", "TEST-CODE", "Cancel"],
                requiredContent: Assert.IsType<ScrollViewer>(dialog.Content))) { }
            Assert.Equal(0, completed());
        }, nativeProof: true, theme: theme, configure: transport => transport.WizardStep = new()
        {
            Id = "loading", Type = "progress", Executor = "gateway",
            Title = "Current model", Message = "Synthetic provider loading",
        });
    }

    [Fact]
    public async Task FailedPinnedModelVerification_RefreshRetriesTheSameModel()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            Assert.Equal(0, completed());
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "ChoicePanel").Visibility);
            transport.FailVerification = false;
            Invoke(Find<Button>(page, "RefreshButton"));
            await WaitAsync(() => completed() == 1);
            Assert.Equal(2, transport.MethodCalls.Count(method => method == "openclaw.setup.verify"));
            Assert.DoesNotContain("openclaw.setup.activate.start", transport.MethodCalls);
        }, expectedModelRef: "openai/test-model", configure: transport => transport.FailVerification = true);
    }

    [Fact]
    public async Task MissingFocusedMethods_OffersClassicWizardWithoutStartingIt()
    {
        await WithPageAsync((page, transport, completed) =>
        {
            Assert.Equal(Visibility.Visible, Find<Button>(page, "LegacyButton").Visibility);
            Assert.Empty(transport.MethodCalls);
            Assert.Equal(0, completed());
            return Task.CompletedTask;
        }, focusedSupported: false);
    }

    [Fact]
    public async Task EmptyDetection_ShowsInlineKeyFormWithoutSelectingOrTesting()
    {
        await WithPageAsync((page, transport, completed) =>
        {
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ApiKeyForm").Visibility);
            Assert.Equal(-1, Find<ComboBox>(page, "ApiProviderPicker").SelectedIndex);
            Assert.False(Find<Button>(page, "ApiKeyConnectButton").IsEnabled);
            Assert.Single(transport.MethodCalls);
            Assert.Equal(0, completed());
            return Task.CompletedTask;
        }, configure: transport => transport.EmptyCandidates = true);
    }

    [Fact]
    public async Task RepeatedChoiceCards_BindTheirTemplateNameAndRejectAnotherTemplatesHandler()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            var choices = Find<ItemsControl>(page, "CandidateChoices");
            await TestSupport.WaitForRenderedConditionAsync(
                () => TestSupport.FindDescendants<SettingsCard>(choices).Count(card => card.IsLoaded) == 2,
                "repeated choice template instances");
            var cards = TestSupport.FindDescendants<SettingsCard>(choices).ToArray();
            Assert.Equal(2, cards.Length);
            Assert.NotSame(cards[0], cards[1]);
            Assert.NotSame(cards[0].Tag, cards[1].Tag);
            Assert.Null(page.FindName("ChoiceActionCard"));
            var recommendedTemplate = Find<ItemsControl>(page, "RecommendedInstalls").ItemTemplate;
            var recommended = Assert.IsType<SettingsCard>(recommendedTemplate.LoadContent());
            var anotherRecommended = Assert.IsType<SettingsCard>(recommendedTemplate.LoadContent());
            Assert.Equal("RecommendedInstallCard", recommended.Name);
            Assert.Equal(recommended.Name, anotherRecommended.Name);
            Assert.NotSame(recommended, anotherRecommended);
            Assert.Null(page.FindName("RecommendedInstallCard"));
            foreach (var card in cards)
            {
                Assert.Equal("ChoiceActionCard", card.Name);
                Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
                    TestSupport.InvokeSettingsCardAction(page, card, "RecommendedInstall_Click"));
            }
            cards[0].Name = "";
            try
            {
                Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
                    TestSupport.InvokeSettingsCardAction(page, cards[0], "ChoiceAction_Click"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeCardAsync(cards[0]));
            }
            finally { cards[0].Name = "ChoiceActionCard"; }
            Assert.Equal(["openclaw.setup.detect"], transport.MethodCalls);
            Assert.Equal(0, completed());
        }, configure: transport => transport.IncludeAdditionalCandidate = true);
    }

    [Fact]
    public async Task OpeningApiKeys_DoesNotSubmitAnyCandidate()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
                TestSupport.InvokeSettingsCardAction(page, Find<SettingsCard>(page, "ApiKeysButton"), "LocalAi_Click"));
            await InvokeCardAsync(Find<SettingsCard>(page, "ApiKeysButton"));
            await WaitAsync(() => Find<StackPanel>(page, "ApiKeyForm").Visibility == Visibility.Visible);
            Assert.Equal(-1, Find<ComboBox>(page, "ApiProviderPicker").SelectedIndex);
            Assert.Null(page.FindName("ContinueButton"));
            Assert.False(Find<Button>(page, "ApiKeyConnectButton").IsEnabled);
            Assert.Single(transport.MethodCalls);
            Assert.Equal(0, completed());
        });
    }

    [Fact]
    public async Task ApiKeysAction_RequiresProviderAndKeyButNotAnExtraConsentAcknowledgment()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeCardAsync(Find<SettingsCard>(page, "ApiKeysButton"));
            await WaitAsync(() => Find<StackPanel>(page, "ApiKeyForm").Visibility == Visibility.Visible);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ApiKeyForm").Visibility);
            Assert.Equal(-1, Find<ComboBox>(page, "ApiProviderPicker").SelectedIndex);
            var connect = Find<Button>(page, "ApiKeyConnectButton");
            Assert.False(connect.IsEnabled);
            Find<ComboBox>(page, "ApiProviderPicker").SelectedIndex = 0;
            var input = Find<PasswordBox>(page, "ApiKeyInput");
            input.ApplyTemplate();
            ui.Container.UpdateLayout();
            await ui.YieldToRenderAsync();
            input.Password = "synthetic-key";
            await WaitAsync(() => connect.IsEnabled);
            Assert.True(connect.IsEnabled);
            Assert.False(Find<CheckBox>(page, "CatalogPreference").IsChecked);
            Assert.Single(transport.MethodCalls);
            Assert.Equal(0, completed());
            await InvokeChoiceAsync(page);
            await WaitAsync(() => completed() == 1);
            Assert.Equal("", input.Password);
        }, configure: transport => transport.RequireCatalogConsent = true);
    }

    [Fact]
    public async Task FailedScan_ShowsErrorNotAnEmptyCatalog()
    {
        await WithPageAsync((page, transport, completed) =>
        {
            Assert.True(Find<InfoBar>(page, "ErrorBar").IsOpen);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "ChoicePanel").Visibility);
            Assert.True(Find<Button>(page, "RefreshButton").IsEnabled);
            Assert.Equal(0, completed());
            return Task.CompletedTask;
        }, configure: transport => transport.FailDetection = true);
    }

    [Fact]
    public async Task ProviderSecret_UsesOneDialogAndCloseClearsInputs()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeChoiceAsync(page);
            var dialog = GetDialog(page);
            await WaitAsync(() => dialog.CanSubmit);
            var secret = Assert.IsType<PasswordBox>(dialog.FindName("SecretInput"));
            secret.ApplyTemplate();
            ui.Container.UpdateLayout();
            await ui.YieldToRenderAsync();
            Assert.Equal(Visibility.Visible, secret.Visibility);
            Assert.Equal("Provider credential", AutomationProperties.GetName(secret));
            Assert.False(string.IsNullOrWhiteSpace(dialog.PrimaryButtonText));
            Assert.DoesNotContain("Onboarding_", dialog.PrimaryButtonText);
            secret.Password = "synthetic-provider-secret";
            Assert.Same(dialog, GetDialog(page));
            Assert.Equal("synthetic-provider-secret", dialog.TakeAnswer()!.Value.GetString());
            Assert.Equal("", secret.Password);
            secret.Password = "synthetic-unsent-secret";
            var close = page.CloseAsync();
            await close;
            Assert.Same(close, page.CloseAsync());
            Assert.Equal("", secret.Password);
            Assert.False(dialog.CanSubmit);
            Assert.Equal(0, completed());
        }, configure: transport => transport.WizardStep = new()
        {
            Id = "secret", Type = "text", Sensitive = true, Title = "Provider credential",
        });
    }

    [Fact]
    public async Task ProviderDialog_SubmitsTypedAnswerThroughTheExistingClient()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeChoiceAsync(page);
            var dialog = GetDialog(page);
            await WaitAsync(() => dialog.CanSubmit);
            await ui.YieldToRenderAsync();
            Invoke(DialogButton(dialog, "PrimaryButton"));
            await WaitAsync(() => completed() == 1);
            Assert.Equal("choose-model", transport.LastNext.GetProperty("answer").GetProperty("stepId").GetString());
            Assert.Equal(7, transport.LastNext.GetProperty("answer").GetProperty("value").GetInt32());
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "openclaw.setup.activate.start"));
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "openclaw.setup.verify"));
        }, configure: transport => transport.WizardStep = new()
        {
            Id = "choose-model", Type = "select", Title = "Choose the model",
            InitialValue = JsonSerializer.SerializeToElement(7),
            Options = [new(JsonSerializer.SerializeToElement(7), "Server model")],
        });
    }

    [Fact]
    public async Task VerificationWithoutWizardSession_CanReconcileInTheSameDialog()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            transport.FailVerification = true;
            await InvokeChoiceAsync(page);
            var dialog = GetDialog(page);
            await WaitAsync(() => dialog.IsSecondaryButtonEnabled &&
                transport.MethodCalls.Contains("openclaw.setup.verify"));
            Assert.Equal(0, completed());
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Button>(dialog.FindName("CancelButton")).Visibility);
            transport.FailVerification = false;
            await ui.YieldToRenderAsync();
            Invoke(DialogButton(dialog, "SecondaryButton"));
            await WaitAsync(() => completed() == 1);
            Assert.Equal(2, transport.MethodCalls.Count(method => method == "openclaw.setup.verify"));
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "openclaw.setup.activate.start"));
            Assert.DoesNotContain("wizard.next", transport.MethodCalls);
        });
    }

    [Theory]
    [InlineData("text", false, false)]
    [InlineData("text", true, false)]
    [InlineData("select", false, false)]
    [InlineData("multiselect", false, false)]
    [InlineData("confirm", false, false)]
    [InlineData("note", false, false)]
    [InlineData("select", false, true)]
    public async Task ProviderDialog_PendingAnswerKeepsDisabledPromptMounted(
        string type, bool sensitive, bool failAnswer)
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeChoiceAsync(page);
            var dialog = GetDialog(page);
            var content = Assert.IsType<ScrollViewer>(dialog.Content);
            await WaitAsync(() => dialog.CanSubmit && content.IsLoaded);
            var closed = 0;
            dialog.Closed += (_, _) => closed++;
            var text = Assert.IsType<TextBox>(dialog.FindName("TextInput"));
            var secret = Assert.IsType<PasswordBox>(dialog.FindName("SecretInput"));
            var options = Assert.IsType<ListView>(dialog.FindName("StepOptions"));
            var confirm = Assert.IsType<CheckBox>(dialog.FindName("ConfirmInput"));
            if (type == "text")
            {
                if (sensitive) secret.Password = "synthetic-secret";
                else text.Text = "synthetic-answer";
            }
            await ui.YieldToRenderAsync();
            Invoke(DialogButton(dialog, "PrimaryButton"));
            await WaitAsync(() => transport.MethodCalls.Contains("wizard.next"));
            await ui.YieldToRenderAsync();
            Assert.Equal(0, closed);
            Assert.True(content.IsLoaded);
            Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(dialog.FindName("StepPanel")).Visibility);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "ProviderActivity").Visibility);
            Assert.False(dialog.CanSubmit);
            Assert.False(dialog.IsPrimaryButtonEnabled);
            Assert.False(text.IsEnabled);
            Assert.False(secret.IsEnabled);
            Assert.False(options.IsEnabled);
            Assert.False(confirm.IsEnabled);
            Assert.Empty(secret.Password);
            if (type == "text" && !sensitive) Assert.Equal("synthetic-answer", text.Text);
            if (type == "select") Assert.NotNull(options.SelectedItem);
            if (type == "multiselect") Assert.Single(options.SelectedItems);
            if (type == "confirm") Assert.True(confirm.IsChecked);
            Assert.Equal("TEST-CODE", Assert.IsType<TextBlock>(dialog.FindName("DeviceCode")).Text);
            Assert.Equal(Visibility.Visible, Assert.IsType<Button>(dialog.FindName("CopyCodeButton")).Visibility);
            Assert.Equal(0, completed());
            output.WriteLine($"Pending {type} answer: dialog loaded, step visible, inputs disabled, no Closed event.");
            await OnboardingArtworkRenderingTests.SaveProofAsync(
                content, $"provider-pending-answer-{type}-{sensitive}-{failAnswer}", output);

            transport.ReleaseAnswer(failAnswer);
            if (failAnswer)
            {
                await WaitAsync(() => dialog.IsSecondaryButtonEnabled);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(dialog.FindName("StepPanel")).Visibility);
                Assert.False(dialog.CanSubmit);
                Assert.Empty(secret.Password);
                Assert.Null(options.SelectedItem);
            }
            else
            {
                await WaitAsync(() => dialog.CanSubmit);
                Assert.Equal("Provider retry", dialog.Title);
                Assert.True(Assert.IsType<InfoBar>(dialog.FindName("DialogError")).IsOpen);
            }
            Assert.Equal(0, closed);
            Assert.True(content.IsLoaded);
            Assert.Equal(0, completed());
            Assert.Single(transport.MethodCalls, method => method == "wizard.next");
        }, configure: transport =>
        {
            transport.HoldAnswer = true;
            transport.WizardStep = new()
            {
                Id = "pending-answer", Type = type, Executor = "client", Sensitive = sensitive,
                Title = "Provider input", Message = "Synthetic provider prompt",
                DeviceCode = new("TEST-CODE"),
                InitialValue = type == "multiselect" ? JsonSerializer.SerializeToElement(new[] { 7 })
                    : type == "select" ? JsonSerializer.SerializeToElement(7)
                    : type == "confirm" ? JsonSerializer.SerializeToElement(true) : null,
                Options = [new(JsonSerializer.SerializeToElement(7), "Seven")],
            };
            transport.FollowingSteps.Enqueue(new()
            {
                Id = "pending-answer", Type = "confirm", Executor = "client", Title = "Provider retry",
            });
            transport.AnswerError = "Synthetic validation retry";
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderDialog_PendingAnswerCanCancelOrCloseWithoutHandoff(bool closePage)
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeChoiceAsync(page);
            var dialog = GetDialog(page);
            await WaitAsync(() => dialog.CanSubmit && Assert.IsType<ScrollViewer>(dialog.Content).IsLoaded);
            await ui.YieldToRenderAsync();
            Invoke(DialogButton(dialog, "PrimaryButton"));
            await WaitAsync(() => transport.MethodCalls.Contains("wizard.next"));
            if (closePage)
                await page.CloseAsync();
            else
            {
                Invoke(Assert.IsType<Button>(dialog.FindName("CancelButton")));
                await WaitAsync(() => Find<Button>(page, "RefreshButton").IsEnabled);
            }
            transport.ReleaseAnswer(fail: false);
            await ui.YieldToRenderAsync();
            Assert.Equal(0, completed());
            Assert.Single(transport.MethodCalls, method => method == "wizard.cancel");
            Assert.Equal(transport.LastActivation.GetProperty("sessionId").GetString(),
                transport.LastCancel.GetProperty("sessionId").GetString());
            Assert.DoesNotContain("openclaw.setup.verify", transport.MethodCalls);
            Assert.False(dialog.CanSubmit);
        }, configure: transport =>
        {
            transport.HoldAnswer = true;
            transport.WizardStep = new() { Id = "consent", Type = "confirm", Executor = "client", Title = "Provider consent" };
        });
    }

    [Fact]
    public async Task RejectedManualCredential_KeepsProviderAndSecureFormForExplicitRetry()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeCardAsync(Find<SettingsCard>(page, "ApiKeysButton"));
            await WaitAsync(() => Find<StackPanel>(page, "ApiKeyForm").Visibility == Visibility.Visible);
            Find<ComboBox>(page, "ApiProviderPicker").SelectedIndex = 0;
            var input = Find<PasswordBox>(page, "ApiKeyInput");
            input.ApplyTemplate();
            ui.Container.UpdateLayout();
            await ui.YieldToRenderAsync();
            input.Password = "synthetic-rejected-key";
            transport.RejectActivation = true;
            await WaitAsync(() => Find<Button>(page, "ApiKeyConnectButton").IsEnabled);
            Invoke(Find<Button>(page, "ApiKeyConnectButton"));
            await WaitAsync(() => Find<Button>(page, "RefreshButton").IsEnabled);
            Assert.Equal(0, completed());
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ChoicePanel").Visibility);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ApiKeyForm").Visibility);
            Assert.Equal(0, Find<ComboBox>(page, "ApiProviderPicker").SelectedIndex);
            Assert.Equal("", input.Password);
            Assert.False(Find<Button>(page, "ApiKeyConnectButton").IsEnabled);
            input.Password = "synthetic-retry-key";
            transport.RejectActivation = false;
            await WaitAsync(() => Find<Button>(page, "ApiKeyConnectButton").IsEnabled);
            Invoke(Find<Button>(page, "ApiKeyConnectButton"));
            await WaitAsync(() => completed() == 1);
            Assert.Equal(2, transport.MethodCalls.Count(method => method == "openclaw.setup.activate.start"));
            Assert.Equal("synthetic-retry-key", transport.LastActivation.GetProperty("apiKey").GetString());
        });
    }

    [Fact]
    public async Task UnconfirmedCancellation_KeepsDialogForReconciliation()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeChoiceAsync(page);
            var dialog = GetDialog(page);
            await WaitAsync(() => dialog.CanSubmit);
            var cancel = Assert.IsType<Button>(dialog.FindName("CancelButton"));
            Invoke(cancel);
            await WaitAsync(() => transport.MethodCalls.Contains("wizard.cancel") && dialog.IsSecondaryButtonEnabled);
            Assert.False(dialog.CanSubmit);
            Assert.True(cancel.IsEnabled);
            Assert.Equal(0, completed());
            Assert.Same(dialog, GetDialog(page));
            transport.CancelStatus = "cancelled";
            Invoke(cancel);
            await WaitAsync(() => Find<Button>(page, "RefreshButton").IsEnabled &&
                transport.MethodCalls.Count(method => method == "wizard.cancel") == 2);
            Assert.Equal(0, completed());
        }, configure: transport =>
        {
            transport.CancelStatus = "running";
            transport.WizardStep = new() { Id = "confirm", Type = "confirm", Title = "Provider consent" };
        });
    }

    [Theory]
    [InlineData("text", false)]
    [InlineData("text", true)]
    [InlineData("select", false)]
    [InlineData("multiselect", false)]
    [InlineData("confirm", false)]
    [InlineData("note", false)]
    [InlineData("progress", false)]
    public async Task ProviderDialog_RendersTypedModesAndAccessibleNames(string type, bool sensitive)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var step = new GatewayAiSetupWizardStep
            {
                Id = "typed", Type = type, Sensitive = sensitive, Title = "Provider input",
                Message = "Synthetic provider prompt", ExternalUrl = "https://provider.example/signin",
                DeviceCode = new("TEST-CODE", 5, "Synthetic device code"),
                InitialValue = type == "multiselect" ? JsonSerializer.SerializeToElement(new[] { 7 })
                    : type == "select" ? JsonSerializer.SerializeToElement(7)
                    : type == "confirm" ? JsonSerializer.SerializeToElement(true) : null,
                Options = [new(JsonSerializer.SerializeToElement(7), "Seven"),
                    new(JsonSerializer.SerializeToElement(new { id = 8 }), "Eight")],
            };
            var dialog = new ProviderSetupDialog();
            dialog.Update(step, GatewayAiSetupPhase.Running, type == "progress", true, false, "Provider setup", null);
            var showing = dialog.ShowOwnedAsync(ui.Container.XamlRoot, ElementTheme.Light);
            try
            {
                var content = Assert.IsType<ScrollViewer>(dialog.Content);
                await TestSupport.WaitForRenderedConditionAsync(() => content.IsLoaded, "provider dialog content Loaded");
                await ui.YieldToRenderAsync();
                ui.Container.UpdateLayout();
                var input = Assert.IsType<PasswordBox>(dialog.FindName("SecretInput"));
                input.ApplyTemplate();
                await ui.YieldToRenderAsync();
                Assert.Equal(type == "text" && sensitive ? Visibility.Visible : Visibility.Collapsed, input.Visibility);
                Assert.Equal("Provider input", AutomationProperties.GetName(input));
                Assert.Equal("Provider input", AutomationProperties.GetName(Assert.IsType<ListView>(dialog.FindName("StepOptions"))));
                Assert.Equal("TEST-CODE", Assert.IsType<TextBlock>(dialog.FindName("DeviceCode")).Text);
                Assert.Equal(type != "progress", dialog.CanSubmit);
                if (type == "text")
                {
                    if (sensitive)
                        input.Password = "synthetic-secret";
                    else
                        Assert.IsType<TextBox>(dialog.FindName("TextInput")).Text = "synthetic-text";
                    Assert.Equal(sensitive ? "synthetic-secret" : "synthetic-text", dialog.TakeAnswer()!.Value.GetString());
                    Assert.Equal("", input.Password);
                }

                else if (type == "select")
                    Assert.Equal(7, dialog.TakeAnswer()!.Value.GetInt32());
                else if (type == "multiselect")
                    Assert.Equal(7, Assert.Single(dialog.TakeAnswer()!.Value.EnumerateArray()).GetInt32());
                else if (type == "confirm")
                    Assert.True(dialog.TakeAnswer()!.Value.GetBoolean());
                else if (type == "note")
                    Assert.Null(dialog.TakeAnswer());
                await OnboardingArtworkRenderingTests.SaveNativeWindowProofAsync(
                    ui.TestWindow, $"onboarding-provider-{type}-{sensitive}", output, content);
            }
            finally { await dialog.CloseAsync(); }
            await showing;
            Assert.False(dialog.CanSubmit);
        });
    }

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task ProviderDialog_DeviceCodeUsesFormattedCardWithoutDuplicatePrompt(ElementTheme theme)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var step = new GatewayAiSetupWizardStep
            {
                Id = "device-code", Type = "progress", Executor = "gateway", Title = "Authorize GitHub Copilot",
                Message = "Enter this one-time code to authorize Copilot.\nCode: TEST-CODE\nCode expires in 15 minutes.",
                ExternalUrl = "https://github.com/login/device",
                DeviceCode = new("TEST-CODE", 15, "Enter this one-time code to authorize Copilot."),
            };
            var dialog = new ProviderSetupDialog();
            dialog.Update(step, GatewayAiSetupPhase.Running, true, true, false, "Signing in", null);
            var showing = dialog.ShowOwnedAsync(ui.Container.XamlRoot, theme);
            try
            {
                var content = Assert.IsType<ScrollViewer>(dialog.Content);
                await TestSupport.WaitForRenderedConditionAsync(() => content.IsLoaded, "device code dialog Loaded");
                var message = Assert.IsType<TextBlock>(dialog.FindName("StepMessage"));
                Assert.Equal(Visibility.Collapsed, message.Visibility);
                Assert.Empty(message.Text);
                var code = Assert.IsType<TextBlock>(dialog.FindName("DeviceCode"));
                Assert.Equal(Visibility.Visible, code.Visibility);
                Assert.Equal("TEST-CODE", code.Text);
                Assert.Equal(Visibility.Visible, Assert.IsType<Button>(dialog.FindName("CopyCodeButton")).Visibility);
                Assert.Equal(Visibility.Visible, Assert.IsType<Button>(dialog.FindName("ExternalLink")).Visibility);
                Assert.Equal("Enter this one-time code to authorize Copilot." + Environment.NewLine +
                    "This code expires in 15 minutes.",
                    Assert.IsType<TextBlock>(dialog.FindName("DeviceCodeDetail")).Text);
                Assert.False(dialog.CanSubmit);
                await OnboardingArtworkRenderingTests.SaveNativeWindowProofAsync(
                    ui.TestWindow, $"onboarding-device-code-deduplicated-{theme}", output, content);

                dialog.Update(step, GatewayAiSetupPhase.Uncertain, true, true, false, "Submitting", null,
                    isSubmittingAnswer: true);
                Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(dialog.FindName("StepPanel")).Visibility);
                Assert.Equal("TEST-CODE", code.Text);
                Assert.Equal(Visibility.Visible, Assert.IsType<Button>(dialog.FindName("ExternalLink")).Visibility);
                Assert.False(dialog.CanSubmit);

                dialog.Update(new()
                {
                    Id = "note", Type = "note", Message = "Additional provider instructions",
                }, GatewayAiSetupPhase.Running, false, true, false, "Signing in", "Sign-in failed");
                Assert.Equal(Visibility.Visible, message.Visibility);
                Assert.Equal("Additional provider instructions", message.Text);
                Assert.Equal(Visibility.Collapsed, code.Visibility);
                Assert.Empty(code.Text);
                Assert.True(Assert.IsType<InfoBar>(dialog.FindName("DialogError")).IsOpen);
                dialog.Update(new() { Id = "empty", Type = "note" },
                    GatewayAiSetupPhase.Running, false, true, false, "Signing in", null);
                Assert.Equal(Visibility.Collapsed, message.Visibility);
            }
            finally { await dialog.CloseAsync(); }
            await showing;
        });
    }

    [Fact]
    public async Task ProviderDialog_ClearsSingleSelectionAndClosesBeforeFirstShow()
    {
        await ui.RunOnUIAsync(async () =>
        {
            var dialog = new ProviderSetupDialog();
            dialog.ClearInputs();
            dialog.Update(new GatewayAiSetupWizardStep
            {
                Id = "choice", Type = "select", Title = "Choose",
                Options = [new(JsonSerializer.SerializeToElement("one"), "One")],
            }, GatewayAiSetupPhase.Running, false, true, false, "Choose", null);
            var options = Assert.IsType<ListView>(dialog.FindName("StepOptions"));
            options.SelectedIndex = 0;
            Assert.True(dialog.CanSubmit);
            dialog.ClearInputs();
            Assert.Equal(-1, options.SelectedIndex);
            Assert.False(dialog.CanSubmit);
            await dialog.CloseAsync();
        });
    }

    [Fact]
    public async Task GatewayOwnedInputAndCancellingState_CannotSubmitOrCancelAgain()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var dialog = new ProviderSetupDialog();
            var step = new GatewayAiSetupWizardStep
            {
                Id = "provider-progress", Type = "text", Executor = "gateway", Title = "Provider work",
            };
            dialog.Update(step, GatewayAiSetupPhase.Running, false, true, false, "Working", null);
            var showing = dialog.ShowOwnedAsync(ui.Container.XamlRoot, ElementTheme.Light);
            try
            {
                await ui.YieldToRenderAsync();
                Assert.False(dialog.CanSubmit);
                Assert.Throws<InvalidOperationException>(() => dialog.TakeAnswer());
                dialog.Update(step, GatewayAiSetupPhase.Uncertain, true, true, true, "Working", null);
                Assert.False(Assert.IsType<Button>(dialog.FindName("CancelButton")).IsEnabled);
                Assert.False(dialog.IsSecondaryButtonEnabled);
                Assert.False(dialog.CanSubmit);
            }
            finally { await dialog.CloseAsync(); }
            await showing;
        });
    }

    [Theory]
    [InlineData(LocalAiOnboardingState.Checking)]
    [InlineData(LocalAiOnboardingState.SetUp)]
    [InlineData(LocalAiOnboardingState.StartAndUse)]
    [InlineData(LocalAiOnboardingState.Use)]
    [InlineData(LocalAiOnboardingState.Repair)]
    [InlineData(LocalAiOnboardingState.Reconcile)]
    [InlineData(LocalAiOnboardingState.BusyGpu)]
    [InlineData(LocalAiOnboardingState.Unsupported)]
    [InlineData(LocalAiOnboardingState.Unknown)]
    [InlineData(LocalAiOnboardingState.UnsupportedGateway)]
    [InlineData(LocalAiOnboardingState.Working)]
    public async Task LocalAi_UsesNativeCardWithInjectedReadinessAndNoAutomaticAction(LocalAiOnboardingState state)
    {
        var host = new PageLocalAiHost(state);
        await WithPageAsync(async (page, transport, completed) =>
        {
            var card = Find<SettingsCard>(page, "LocalAiCard");
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "LocalAiSection").Visibility);
            Assert.Equal("OnboardingLocalAiCard", AutomationProperties.GetAutomationId(card));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(card)));
            Assert.False(string.IsNullOrWhiteSpace(Find<TextBlock>(page, "LocalAiDescription").Text));
            Assert.DoesNotContain("Onboarding_", Find<TextBlock>(page, "LocalAiDescription").Text);
            if (state == LocalAiOnboardingState.Reconcile)
            {
                Assert.Equal("Recover and use Local AI", Find<TextBlock>(page, "LocalAiActionText").Text);
                Assert.True(card.IsClickEnabled);
            }
            Assert.Equal(0, host.Actions);
            Assert.Equal(0, completed());
            Assert.Single(transport.MethodCalls);
            await OnboardingArtworkRenderingTests.SaveProofAsync(page, $"onboarding-ai-local-{state}", output);
        }, localAiHost: host);
    }

    [Fact]
    public async Task UnsupportedFreshDevice_HidesOnlyLocalRowAndKeepsGatewayChoices()
    {
        var host = new PageLocalAiHost(LocalAiOnboardingState.Unsupported) { FreshUnsupported = true };
        await WithPageAsync((page, transport, completed) =>
        {
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "LocalAiSection").Visibility);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "CandidatesSection").Visibility);
            Assert.NotEmpty(Find<ItemsControl>(page, "CandidateChoices").Items);
            Assert.Single(transport.MethodCalls);
            Assert.Equal(0, host.Actions);
            Assert.Equal(0, completed());
            return Task.CompletedTask;
        }, localAiHost: host);
    }

    [Theory]
    [InlineData(LocalAiOnboardingState.SetUp, ElementTheme.Light)]
    [InlineData(LocalAiOnboardingState.SetUp, ElementTheme.Dark)]
    [InlineData(LocalAiOnboardingState.Use, ElementTheme.Light)]
    [InlineData(LocalAiOnboardingState.Use, ElementTheme.Dark)]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task LocalAi_FullWindowCentersHeadingAndShowsStateGroupsAndFooter(
        LocalAiOnboardingState state, ElementTheme theme)
    {
        var localHost = new PageLocalAiHost(state);
        await WithPageAsync(async (page, transport, completed) =>
        {
            var root = ui.Container;
            var grid = Assert.IsType<Grid>(page.Content);
            var heading = Find<TextBlock>(page, "TitleText");
            var subtitle = Find<TextBlock>(page, "StatusText");
            var mascot = Find<OnboardingMascot>(page, "MascotHero");
            var scroll = Assert.Single(grid.Children.OfType<ScrollViewer>());
            var footer = Assert.Single(grid.Children.OfType<Grid>(), child => Grid.GetRow(child) == 2);
            var card = Find<SettingsCard>(page, "LocalAiCard");
            var scale = root.XamlRoot.RasterizationScale;
            Windows.Foundation.Rect Bounds(FrameworkElement element) => element.TransformToVisual(root)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var header = Find<StackPanel>(page, "AiHeader");
            Assert.Same(header, mascot.Parent);
            Assert.Equal(HorizontalAlignment.Stretch, header.HorizontalAlignment);
            foreach (var text in new[] { heading, subtitle })
            {
                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                Assert.False(text.IsTextTrimmed);
                OnboardingNativeProof.AssertFullyVisible(text, root);
            }
            OnboardingNativeProof.AssertHeroLayout(mascot, output);
            Assert.True(Bounds(mascot).Bottom <= Bounds(heading).Top);
            Assert.Equal(TextAlignment.Center, heading.TextAlignment);
            Assert.Equal(TextAlignment.Center, subtitle.TextAlignment);
            var textScale = new Windows.UI.ViewManagement.UISettings().TextScaleFactor;
            if (textScale <= 1)
            {
                Assert.InRange(header.ActualHeight, OnboardingMascot.HeroSize, 300);
                Assert.True(scroll.ViewportHeight >= 280, $"Actual AI body: {scroll.ViewportHeight} DIPs.");
            }
            Assert.Equal(0, NativeProofLayout.PhysicalPixels(scroll.ScrollableWidth, scale));
            Assert.Equal("Choose a model or connect an AI provider.", subtitle.Text);
            Assert.Equal(theme, page.ActualTheme);
            Assert.Equal(theme, heading.ActualTheme);
            OnboardingNativeProof.AssertFullyVisible(mascot, root);
            OnboardingNativeProof.AssertFullyVisible(footer, root);
            OnboardingNativeProof.AssertFullyVisible(card, scroll);
            OnboardingNativeProof.AssertFullyVisible(Find<TextBlock>(page, "LocalAiTitle"), scroll);
            Assert.Null(page.FindName("LocalAiHeading"));
            Assert.Equal(state == LocalAiOnboardingState.SetUp ? "Set up Local AI" : "Use this model",
                Find<TextBlock>(page, "LocalAiActionText").Text);
            var pageBounds = Bounds(page);
            Assert.Equal(NativeProofLayout.PhysicalPixels(root.ActualWidth, scale),
                NativeProofLayout.PhysicalPixels(pageBounds.Width, scale));
            Assert.Equal(NativeProofLayout.PhysicalPixels(root.ActualHeight, scale),
                NativeProofLayout.PhysicalPixels(pageBounds.Height, scale));
            using (await OnboardingNativeProof.CaptureAsync(ui.TestWindow,
                $"onboarding-ai-full-{state}-{theme}-local", output,
                ["Connect your AI", "Local AI on this PC", "Refresh"],
                new { state, theme, titleBounds = Bounds(heading), subtitleBounds = Bounds(subtitle),
                    heroBounds = Bounds(mascot), localBounds = Bounds(card), footerBounds = Bounds(footer),
                    headerHeight = header.ActualHeight, viewportHeight = scroll.ViewportHeight, textScale },
                requiredContent: page)) { }
            var originalOffset = scroll.VerticalOffset;
            try
            {
                foreach (var sectionName in new[] { "CandidatesSection", "ApiKeySection" })
                {
                    var section = Find<StackPanel>(page, sectionName);
                    Assert.Equal(Visibility.Visible, section.Visibility);
                    section.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
                    root.UpdateLayout();
                    await OnboardingNativeProof.NextCompositionAsync();
                    root.UpdateLayout();
                    var label = sectionName == "CandidatesSection"
                        ? (FrameworkElement)Find<TextBlock>(page, "CandidatesHeading")
                        : Find<SettingsCard>(page, "ApiKeysButton");
                    OnboardingNativeProof.AssertFullyVisible(label, scroll);
                    OnboardingNativeProof.AssertFullyVisible(heading, root);
                    OnboardingNativeProof.AssertFullyVisible(footer, root);
                    using (await OnboardingNativeProof.CaptureAsync(ui.TestWindow,
                        $"onboarding-ai-full-{state}-{theme}-{sectionName}", output,
                        ["Connect your AI", sectionName == "CandidatesSection" ? "Available on your Gateway" : "API Keys", "Refresh"],
                        new { state, theme, sectionBounds = Bounds(label), footerBounds = Bounds(footer) },
                        requiredContent: page)) { }
                }
            }
            finally { scroll.ChangeView(null, originalOffset, null, disableAnimation: true); }
            Assert.Equal(0, localHost.Actions);
            Assert.Equal(0, completed());
            Assert.Equal(["openclaw.setup.detect"], transport.MethodCalls);
            OnboardingNativeProof.AssertSourceUnchanged();

        }, localAiHost: localHost, nativeProof: true, theme: theme,
            configure: transport => transport.IncludeAdditionalCandidate = true);
    }

    [Fact]
    public async Task MoreProviders_ExpandsRealCardsWithoutSelectingOrStartingAnOperation()
    {
        await WithPageAsync(async (page, transport, completed) =>
        {
            var expander = Find<SettingsExpander>(page, "MoreExpander");
            var choices = Find<ItemsControl>(page, "MoreSignInChoices");
            Assert.Equal(Visibility.Visible, expander.Visibility);
            Assert.Same(choices, expander.ItemsFooter);
            Assert.Empty(expander.Items);
            expander.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            expander.IsExpanded = true;
            await TestSupport.WaitForSettingsExpanderSettledAsync(ui, expander, expanded: true);
            choices.UpdateLayout();
            await ui.YieldToRenderAsync();
            Assert.Single(choices.Items);
            Assert.Empty(TestSupport.FindDescendants<ListViewItem>(choices));
            var card = Assert.Single(TestSupport.FindDescendants<SettingsCard>(choices));
            await TestSupport.WaitForRenderedConditionAsync(() => card.IsLoaded, "expanded provider card Loaded");
            Assert.True(card.IsClickEnabled && card.IsLoaded);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(card)));
            Assert.Contains(TestSupport.FindDescendants<TextBlock>(card), text => text.Text == "Sign in");
            Assert.Equal(["openclaw.setup.detect"], transport.MethodCalls);
            Assert.Equal(0, completed());
        }, configure: transport => transport.IncludeMoreProvider = true);
    }

    [Fact]
    public async Task LocalAi_ExplicitReviewUsesSamePageHostCallback()
    {
        var host = new PageLocalAiHost(LocalAiOnboardingState.SetUp);
        var reviews = 0;
        await WithPageAsync(async (page, _, completed) =>
        {
            var card = Find<SettingsCard>(page, "LocalAiCard");
            Assert.True(card.IsClickEnabled);
            await InvokeLocalAiAsync(page);
            await WaitAsync(() => reviews == 1);
            Assert.Equal(0, completed());
            Assert.Equal(0, host.Actions);
        }, localAiHost: host, reviewLocalAi: _ => { reviews++; return Task.CompletedTask; });
    }

    [Fact]
    public async Task LocalAi_ExplicitUseVerifiesExactModelBeforeCompleting()
    {
        var host = new PageLocalAiHost(LocalAiOnboardingState.Use);
        await WithPageAsync(async (page, transport, completed) =>
        {
            var card = Find<SettingsCard>(page, "LocalAiCard");
            await InvokeLocalAiAsync(page);
            await WaitAsync(() => completed() == 1);
            Assert.Equal(1, host.Actions);
            Assert.Equal(["openclaw.setup.detect", "openclaw.setup.verify"], transport.MethodCalls);
        }, localAiHost: host);
    }

    [Fact]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task LocalAi_InstalledContinuationShowsProgressAndVerifiesWithoutRediscovery()
    {
        var finish = new TaskCompletionSource<SetupLocalAiUseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = new LocalAiInstallAndUseIntent(new("ui-proof", "", 18789, null, null, true, "binding"), "test-model", 0);
        var host = new PageLocalAiHost(LocalAiOnboardingState.StartAndUse)
        {
            ModelRef = intent.Expected.ModelRef,
            Use = ct => finish.Task.WaitAsync(ct)
        };
        await WithPageAsync(async (page, transport, completed) =>
        {
            try
            {
                Assert.Equal("Setting up Local AI", Find<TextBlock>(page, "TitleText").Text);
                Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "ChoicePanel").Visibility);
                Assert.Empty(transport.MethodCalls);
                Assert.Equal(0, host.Observations);
                Assert.Equal(0, completed());
                using (await OnboardingNativeProof.CaptureAsync(ui.TestWindow, "native-local-ai-install-continuation", output,
                    ["Setting up Local AI", "Starting llama-server"],
                    new { host.Actions, host.Observations, discoveryCalls = transport.MethodCalls.Count },
                    requiredContent: page)) { }
            }
            finally { finish.TrySetResult(intent.Expected); }
            await WaitAsync(() => completed() == 1);
            Assert.Equal(1, host.Actions);
            Assert.Equal(["openclaw.setup.verify"], transport.MethodCalls);
        }, localAiHost: host, installAndUse: intent, ready: () => host.Actions == 1,
            configure: transport => transport.VerificationModelRef = intent.Expected.ModelRef, nativeProof: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalAi_InstalledContinuationRefreshNeverRepeatsTheMutation(bool uncertain)
    {
        var intent = new LocalAiInstallAndUseIntent(new("ui-proof", "", 18789, null, null, true, "binding"), "test-model", 0);
        var host = new PageLocalAiHost(LocalAiOnboardingState.StartAndUse)
        {
            ModelRef = intent.Expected.ModelRef,
            UseFailure = uncertain ? new IOException("Lost publication response.") : new LocalAiStartFailedException("Start failed.")
        };
        await WithPageAsync(async (page, transport, completed) =>
        {
            Assert.Equal(1, host.Actions);
            Assert.True(intent.IsConsumed);
            if (uncertain)
                Assert.Empty(transport.MethodCalls);
            else
            {
                Assert.Equal(["openclaw.setup.detect"], transport.MethodCalls);
                Assert.Equal("Connect your AI", Find<TextBlock>(page, "TitleText").Text);
                Assert.Contains("Start failed.", Find<InfoBar>(page, "ErrorBar").Message);
                Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ChoicePanel").Visibility);
                Assert.Contains("Repair", Find<TextBlock>(page, "LocalAiActionText").Text);
                Assert.True(Find<SettingsCard>(page, "LocalAiCard").IsClickEnabled);
            }
            Invoke(Find<Button>(page, "RefreshButton"));
            await WaitAsync(() => Find<Button>(page, "RefreshButton").IsEnabled);
            Assert.Equal(1, host.Actions);
            Assert.Equal(0, completed());
            Assert.Equal(uncertain ? ["openclaw.setup.verify"] :
                new[] { "openclaw.setup.detect", "openclaw.setup.detect" }, transport.MethodCalls);
        }, localAiHost: host, installAndUse: intent, configure: transport => transport.FailVerification = true,
            reviewLocalAi: _ => Task.CompletedTask);
    }

    [Fact]
    public async Task LocalAi_AdmissionFailureRequiresExplicitUseAfterAuthorizationRecovers()
    {
        var intent = new LocalAiInstallAndUseIntent(new("ui-proof", "", 18789, null, null, true, "binding"), "test-model", 0);
        var host = new PageLocalAiHost(LocalAiOnboardingState.StartAndUse) { ModelRef = intent.Expected.ModelRef };
        await WithPageAsync(async (page, transport, completed) =>
        {
            await WaitAsync(() => host.Observations == 1);
            Assert.Equal("Connect your AI", Find<TextBlock>(page, "TitleText").Text);
            Assert.False(intent.IsConsumed);
            Assert.Equal(0, host.Actions);
            transport.OperatorScopes = ["operator.admin"];
            Invoke(Find<Button>(page, "RefreshButton"));
            await WaitAsync(() => Find<Button>(page, "RefreshButton").IsEnabled);
            Assert.Equal(["openclaw.setup.detect"], transport.MethodCalls);
            Assert.Equal(0, host.Actions);
            Assert.Equal(0, completed());
        }, localAiHost: host, installAndUse: intent, configure: transport => transport.OperatorScopes = []);
    }

    [Fact]
    public async Task LocalAi_ReconnectToOtherGatewayWithSameModelCannotVerifyOrComplete()
    {
        var host = new PageLocalAiHost(LocalAiOnboardingState.Use);
        var other = new PageTransport(true) { Route = new("other", "main", "other-authority") };
        await WithPageAsync(async (page, first, completed) =>
        {
            await InvokeLocalAiAsync(page);
            await WaitAsync(() => Find<InfoBar>(page, "ErrorBar").IsOpen);
            Assert.Empty(other.MethodCalls);
            Assert.DoesNotContain("openclaw.setup.verify", first.MethodCalls);
            Assert.Equal(0, completed());
            Assert.Equal(1, host.Actions);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "ChoicePanel").Visibility);
            Invoke(Find<Button>(page, "RefreshButton"));
            await WaitAsync(() => Find<Button>(page, "RefreshButton").IsEnabled);
            Assert.Empty(other.MethodCalls);
            Assert.Equal(1, host.Actions);
        }, localAiHost: host, reconnectTransport: other);
    }

    [Fact]
    public async Task LocalAi_RecoveryReturnRetainsExpectedGatewayAlongsideModel()
    {
        await WithPageAsync((page, transport, completed) =>
        {
            Assert.True(Find<InfoBar>(page, "ErrorBar").IsOpen);
            Assert.Empty(transport.MethodCalls);
            Assert.Equal(0, completed());
            return Task.CompletedTask;
        }, expectedModelRef: "openai/test-model", expectedGatewayId: "recovery-owner");
    }

    [Fact]
    public async Task LocalAi_ConfirmedFailureShowsRepairAndProviderChoices()
    {
        var host = new PageLocalAiHost(LocalAiOnboardingState.StartAndUse)
        { UseFailure = new LocalAiStartFailedException("Synthetic port conflict.") };
        await WithPageAsync(async (page, _, completed) =>
        {
            await InvokeLocalAiAsync(page);
            await WaitAsync(() => Find<InfoBar>(page, "ErrorBar").IsOpen && Find<Button>(page, "RefreshButton").IsEnabled);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ChoicePanel").Visibility);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "LocalAiSection").Visibility);
            Assert.Contains("Synthetic port conflict.", Find<InfoBar>(page, "ErrorBar").Message);
            Assert.Contains("Repair", Find<TextBlock>(page, "LocalAiActionText").Text);
            Assert.True(Find<SettingsCard>(page, "LocalAiCard").IsClickEnabled);
            Assert.Equal(1, host.Actions);
            Assert.Equal(0, completed());
        }, localAiHost: host, reviewLocalAi: _ => Task.CompletedTask);
    }

    [Fact]
    public async Task LocalAi_PreStartTargetRejectionRestoresChoicesInsteadOfVerificationOnly()
    {
        var host = new PageLocalAiHost(LocalAiOnboardingState.StartAndUse)
        {
            // The independent Gateway choice must not be deduplicated as this Local AI model.
            ModelRef = "llamacpp/synthetic-local-model",
            UseFailure = new LocalAiSelectionRejectedException("Synthetic pre-start Gateway switch."),
        };
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeLocalAiAsync(page);
            await WaitAsync(() => Find<InfoBar>(page, "ErrorBar").IsOpen && Find<Button>(page, "RefreshButton").IsEnabled);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "ChoicePanel").Visibility);
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "LocalAiSection").Visibility);
            Assert.Contains("Gateway", Find<InfoBar>(page, "ErrorBar").Message);
            Assert.DoesNotContain("openclaw.setup.verify", transport.MethodCalls);
            Assert.Equal(1, host.Actions);
            Assert.Equal(0, completed());
            var choices = Find<ItemsControl>(page, "CandidateChoices");
            choices.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            choices.UpdateLayout();
            await ui.YieldToRenderAsync();
            var candidate = Assert.Single(TestSupport.FindDescendants<SettingsCard>(choices));
            await InvokeCardAsync(candidate);
            await WaitAsync(() => completed() == 1);
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "openclaw.setup.activate.start"));
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "openclaw.setup.verify"));
            Assert.Equal("existing-model", transport.LastActivation.GetProperty("kind").GetString());
            Assert.Equal(1, host.Actions);
        }, localAiHost: host);
    }

    [Fact]
    public async Task LocalAi_UncertainFailureKeepsExactVerificationOnlyAndNeverReplays()
    {
        var host = new PageLocalAiHost(LocalAiOnboardingState.Use)
        { UseFailure = new IOException("Synthetic lost publication response.") };
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeLocalAiAsync(page);
            await WaitAsync(() => Find<InfoBar>(page, "ErrorBar").IsOpen && Find<Button>(page, "RefreshButton").IsEnabled);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "ChoicePanel").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(page, "LocalAiSection").Visibility);
            Invoke(Find<Button>(page, "RefreshButton"));
            await WaitAsync(() => Find<Button>(page, "RefreshButton").IsEnabled);
            Assert.Equal(1, host.Actions);
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "openclaw.setup.verify"));
            Assert.Equal(0, completed());
        }, localAiHost: host, configure: transport => transport.FailVerification = true);
    }

    [Fact]
    public async Task LocalAi_CloseWaitsForUncooperativeCancellationCleanup()
    {
        var cancellationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCleanup = new TaskCompletionSource<SetupLocalAiUseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new PageLocalAiHost(LocalAiOnboardingState.StartAndUse)
        {
            Use = async ct =>
            {
                using var registration = ct.Register(() => cancellationSeen.SetResult());
                return await finishCleanup.Task;
            },
        };
        await WithPageAsync(async (page, transport, completed) =>
        {
            await InvokeLocalAiAsync(page);
            await WaitAsync(() => host.Actions == 1);
            var close = page.CloseAsync();
            try
            {
                await cancellationSeen.Task;
                Assert.False(close.IsCompleted);
                Assert.Equal(0, completed());
            }
            finally { finishCleanup.SetResult(new("ui-proof", "openai/test-model")); }
            await close;
            Assert.Same(close, page.CloseAsync());
            Assert.DoesNotContain("openclaw.setup.verify", transport.MethodCalls);
            Assert.Equal(0, completed());
        }, localAiHost: host);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalAi_UncertainUseRefreshPreservesManagedCompletion(bool installedContinuation)
    {
        var intent = installedContinuation
            ? new LocalAiInstallAndUseIntent(new("ui-proof", "", 18789, null, null, true, "binding"), "test-model", 0)
            : null;
        var host = new PageLocalAiHost(LocalAiOnboardingState.Use)
        {
            ModelRef = intent?.Expected.ModelRef ?? "openai/test-model",
            UseFailure = new IOException("Synthetic lost publication response.")
        };
        GatewayAiSetupCompletion? completion = null;
        await WithPageAsync(async (page, transport, completed) =>
        {
            if (!installedContinuation)
                await InvokeLocalAiAsync(page);
            await WaitAsync(() => Find<InfoBar>(page, "ErrorBar").IsOpen && Find<Button>(page, "RefreshButton").IsEnabled);
            Invoke(Find<Button>(page, "RefreshButton"));
            await WaitAsync(() => completed() == 1);
            Assert.Equal(1, host.Actions);
            Assert.Equal(1, transport.MethodCalls.Count(method => method == "openclaw.setup.verify"));
            Assert.NotNull(completion);
            Assert.True(completion.RequiresManagedLocalAi);
            Assert.Equal(SetupCompletionIntent.CustodianOnboarding, completion.Intent);
            Assert.Equal(host.ModelRef, completion.ModelRef);
        }, localAiHost: host, installAndUse: intent,
            configure: transport => transport.VerificationModelRef = host.ModelRef,
            completedProof: proof => completion = proof);
    }

    [Fact]
    public async Task LocalAi_GatewayDiscoveryFailureDoesNotHideLocalReview()
    {
        var host = new PageLocalAiHost(LocalAiOnboardingState.SetUp);
        await WithPageAsync((page, _, completed) =>
        {
            Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "LocalAiSection").Visibility);
            Assert.True(Find<SettingsCard>(page, "LocalAiCard").IsClickEnabled);
            Assert.Equal(0, completed());
            return Task.CompletedTask;
        }, configure: transport => transport.FailDetection = true, localAiHost: host,
            reviewLocalAi: _ => Task.CompletedTask);
    }

    [Fact]
    public async Task LocalAi_ProviderInFlightMustSettleBeforeLocalReview()
    {
        var host = new PageLocalAiHost(LocalAiOnboardingState.SetUp);
        await WithPageAsync(async (page, transport, _) =>
        {
            transport.HoldActivation = true;
            await InvokeChoiceAsync(page);
            await WaitAsync(() => transport.MethodCalls.Contains("openclaw.setup.activate.start"));
            Assert.False(Find<SettingsCard>(page, "LocalAiCard").IsClickEnabled);
            Assert.Equal(0, host.Actions);
        }, localAiHost: host, reviewLocalAi: _ => throw new InvalidOperationException("Must settle provider first."));
    }

    private sealed class PageLocalAiHost(LocalAiOnboardingState state) : ISetupLocalAiHost, INativeSetupLocalAiHost
    {
        public int Actions { get; private set; }
        public int Observations { get; private set; }
        public bool HasNativeSelection => false;
        public void ConfigureNative(OpenClaw.Connection.GatewayRecord record, IGatewayAiSetupTransport transport,
            Func<CancellationToken, Task> authorize) => throw new InvalidOperationException();
        public void ReleaseNative(IGatewayAiSetupTransport transport) => throw new InvalidOperationException();
        public Task ReconcileNativeAsync(IGatewayAiSetupTransport transport, string modelRef, CancellationToken ct) =>
            throw new InvalidOperationException();
        public Task WithdrawNativeAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<SetupLocalAiUseResult> UseInstalledAsync(LocalAiInstallAndUseIntent intent, CancellationToken ct,
            IProgress<LocalAiSetupStage>? progress)
        {
            progress?.Report(LocalAiSetupStage.StartingRuntime);
            return UseAsync(new(LocalAiOnboardingState.StartAndUse, intent.Target, intent.Expected.ModelRef, "receipt"), ct);
        }
        public string ModelRef { get; init; } = "openai/test-model";
        public Exception? UseFailure { get; init; }
        public Func<CancellationToken, Task<SetupLocalAiUseResult>>? Use { get; init; }
        public bool FreshUnsupported { get; init; }
        public OpenClaw.Connection.GatewayRegistrySnapshot BeginGatewaySetup() => throw new InvalidOperationException();
        public Task ReconcileGatewaySetupAsync(OpenClaw.Connection.GatewayRegistrySnapshot expectedOutput, string? completedGatewayId) => throw new InvalidOperationException();
        public Task<LocalAiOnboardingSnapshot> ObserveAsync(CancellationToken ct)
        {
            Observations++;
            return Task.FromResult(
            FreshUnsupported
                ? LocalAiOnboardingSnapshot.Project(new("ui-proof", "Managed", 18789, null, null),
                    LocalInferenceEligibility.Evaluate(OnboardingSetupGalleryData.Hardware("unsupported")),
                    null, false, false, null)
                : new LocalAiOnboardingSnapshot(state, new("ui-proof", "Managed", 18789, "test", 18803),
                    ModelRef, "receipt", "Synthetic GPU", "Synthetic model", HasInstallationEvidence: true));
        }
        public Task<SetupLocalAiTarget> RevalidateReviewAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct) =>
            Task.FromResult(selected.Target!);
        public Task<SetupLocalAiUseResult> UseAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct)
        {
            Actions++;
            if (UseFailure is not null)
            {
                if (UseFailure is LocalAiStartFailedException)
                    state = LocalAiOnboardingState.Repair;
                else if (UseFailure is LocalAiSelectionRejectedException)
                    state = LocalAiOnboardingState.UnsupportedGateway;
                return Task.FromException<SetupLocalAiUseResult>(UseFailure);
            }
            if (Use is not null) return Use(ct);
            return Task.FromResult(new SetupLocalAiUseResult("ui-proof", ModelRef));
        }
    }

    private async Task WithPageAsync(
        Func<AiSetupPage, PageTransport, Func<int>, Task> assertion,
        bool focusedSupported = true,
        string? expectedModelRef = null,
        Action<PageTransport>? configure = null,
        ISetupLocalAiHost? localAiHost = null,
        Func<LocalAiOnboardingSnapshot, Task>? reviewLocalAi = null,
        PageTransport? reconnectTransport = null,
        string? expectedGatewayId = null, bool nativeProof = false, ElementTheme theme = ElementTheme.Light,
        double width = 720, LocalAiInstallAndUseIntent? installAndUse = null, Func<bool>? ready = null,
        Action<GatewayAiSetupCompletion>? completedProof = null)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var transport = new PageTransport(focusedSupported);
            _nativeProof = nativeProof;
            configure?.Invoke(transport);
            var completions = 0;
            var connects = 0;
            var originalSize = ui.TestWindow.AppWindow.Size;
            var originalPosition = ui.TestWindow.AppWindow.Position;
            var originalPadding = ui.Container.Padding;
            var originalStyle = ui.Container.Style;
            var originalBackground = ui.Container.ReadLocalValue(Panel.BackgroundProperty);
            var originalTheme = ui.Container.RequestedTheme;
            Frame? frame = null;
            AiSetupPage? page = null;
            IDisposable? navigation = null;
            try
            {
                if (nativeProof)
                {
                    OnboardingNativeProof.AssertIsolatedRoots();
                    OnboardingNativeProof.RequireProofDirectory();
                    var scale = ui.Container.XamlRoot.RasterizationScale;
                    ui.TestWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                        NativeProofLayout.PhysicalPixels(720, scale), NativeProofLayout.PhysicalPixels(820, scale)));
                    var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(ui.TestWindow.AppWindow.Id,
                        Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
                    ui.TestWindow.AppWindow.Move(new Windows.Graphics.PointInt32(work.X + 32, work.Y + 32));
                    ui.Container.Padding = new Thickness(0);
                    OnboardingNativeProof.ActivateOwned(ui.TestWindow);
                }
                else
                    ui.TestWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(816, 944));
                await OnboardingNativeProof.ApplyThemeSurfaceAsync(ui.Container, theme);
                var data = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")
                    ?? throw new InvalidOperationException("Set isolated tray data before onboarding UI tests.");
                var args = new AiSetupPageArgs(new SetupConfig(), data, data,
                    () => true, () => { completions++; return Task.CompletedTask; },
                    ExpectedConfiguredModelRef: expectedModelRef,
                    TransportFactory: () => connects++ == 0 ? transport : reconnectTransport ?? transport,
                    LocalAiHost: localAiHost, ReviewLocalAi: reviewLocalAi, ExpectedGatewayId: expectedGatewayId,
                    InstallAndUse: installAndUse,
                    CompleteVerifiedSetup: completedProof is null ? null : proof =>
                    {
                        completedProof(proof);
                        completions++;
                        return Task.CompletedTask;
                    });
                frame = nativeProof ? new Frame() : new Frame { Width = width, Height = 820 };
                navigation = OnboardingNativeProof.TrackNavigation(frame);
                ui.Container.Children.Add(frame);
                frame.Navigate(typeof(AiSetupPage), args);
                page = Assert.IsType<AiSetupPage>(frame.Content);
                await WaitAsync(() => ready is not null ? ready() : expectedModelRef is not null || installAndUse is not null
                    ? completions > 0 || Find<InfoBar>(page, "ErrorBar").IsOpen
                    : focusedSupported ? Find<StackPanel>(page, "ChoicePanel").Visibility == Visibility.Visible ||
                        Find<InfoBar>(page, "ErrorBar").IsOpen
                    : Find<Button>(page, "LegacyButton").Visibility == Visibility.Visible);
                ui.Container.UpdateLayout();
                await ui.YieldToRenderAsync();
                await assertion(page, transport, () => completions);
            }
            finally
            {
                try
                {
                    if (page is not null) await page.CloseAsync();
                    frame?.Navigate(typeof(Page));
                }
                finally
                {
                    ui.Container.Children.Clear();
                    navigation?.Dispose();
                    ui.Container.Padding = originalPadding;
                    ui.Container.Style = originalStyle;
                    ui.Container.RequestedTheme = originalTheme;
                    if (originalBackground == DependencyProperty.UnsetValue) ui.Container.ClearValue(Panel.BackgroundProperty);
                    else ui.Container.SetValue(Panel.BackgroundProperty, originalBackground);
                    ui.TestWindow.AppWindow.Resize(originalSize);
                    if (nativeProof) ui.TestWindow.AppWindow.Move(originalPosition);
                    _nativeProof = false;
                    await ui.YieldToRenderAsync();
                }
            }
        });
    }

    private async Task InvokeChoiceAsync(AiSetupPage page, string group = "CandidateChoices", int index = 0)
    {
        var choices = Find<ItemsControl>(page, group);
        choices.UpdateLayout();
        await ui.YieldToRenderAsync();
        var card = TestSupport.FindDescendants<SettingsCard>(choices).ElementAt(index);
        await InvokeCardAsync(card);
    }

    private async Task WaitAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate())
        {
            await ui.YieldToRenderAsync();
            await Task.Delay(20, timeout.Token);
        }
    }

    private static T Find<T>(AiSetupPage page, string name) where T : FrameworkElement =>
        Assert.IsType<T>(page.FindName(name));

    private static ProviderSetupDialog GetDialog(AiSetupPage page) =>
        Assert.IsType<ProviderSetupDialog>(typeof(AiSetupPage).GetField("_providerDialog",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page));

    private static Button DialogButton(ProviderSetupDialog dialog, string name) =>
        Assert.Single(TestSupport.FindDescendants<Button>(dialog), button => button.Name == name);

    private static void Invoke(Button button)
    {
        Assert.True(button.IsEnabled);
        var provider = Assert.IsAssignableFrom<IInvokeProvider>(
            new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke));
        provider.Invoke();
    }

    private Task InvokeLocalAiAsync(AiSetupPage page) =>
        InvokeCardAsync(Find<SettingsCard>(page, "LocalAiCard"));

    private async Task InvokeCardAsync(SettingsCard card)
    {
        if (_nativeProof)
        {
            await OnboardingNativeProof.InvokeSettingsCardAsync(ui.TestWindow, card);
            return;
        }
        var page = Assert.Single(TestSupport.FindDescendants<AiSetupPage>(ui.Container));
        var handler = card.Name switch
        {
            "ApiKeysButton" => "ApiKeys_Click",
            "LocalAiCard" => "LocalAi_Click",
            "ChoiceActionCard" => "ChoiceAction_Click",
            "RecommendedInstallCard" => "RecommendedInstall_Click",
            _ => throw new InvalidOperationException($"Unsupported AI setup card: {card.Name}")
        };
        // Choice visibility can precede Loaded and the asynchronous readiness update.
        await TestSupport.WaitForRenderedConditionAsync(
            () => card.IsLoaded && card.IsEnabled && card.IsClickEnabled && card.XamlRoot == page.XamlRoot,
            $"AI setup card '{card.Name}' to become actionable");
        TestSupport.InvokeSettingsCardAction(page, card, handler);
        await ui.YieldToRenderAsync();
    }

    private sealed class PageTransport(bool focusedSupported) : IGatewayAiSetupTransport
    {
        public GatewayAiSetupRoute Route { get; init; } = new("ui-proof", "main", "ui-proof-authority",
            EndpointBinding: new string('A', 64), IdentityBinding: new string('B', 64), SessionKey: "agent:main:main");
        public long Generation => 1;
        public bool IsConnected => true;
        public IReadOnlyCollection<string> OperatorScopes { get; set; } = ["operator.admin"];
        public IReadOnlyCollection<string> Methods => focusedSupported
            ? ["openclaw.setup.detect", "openclaw.setup.activate.start", "openclaw.setup.verify",
                "openclaw.setup.auth.start", "openclaw.setup.prepare.start", "wizard.next", "wizard.cancel", "wizard.status"]
            : ["wizard.start", "wizard.next", "wizard.cancel"];
        public List<string> MethodCalls { get; } = [];
        public JsonElement LastActivation { get; private set; }
        public bool FailActivation { get; set; }
        public bool RejectActivation { get; set; }
        public bool HoldActivation { get; set; }
        public bool HoldAnswer { get; set; }
        public string? AnswerError { get; set; }
        public bool FailVerification { get; set; }
        public string VerificationModelRef { get; set; } = "openai/test-model";
        public bool RequireCatalogConsent { get; set; }
        public bool EmptyCandidates { get; set; }
        public bool FailDetection { get; set; }
        public bool IncludeMoreProvider { get; set; }
        public bool IncludeAdditionalCandidate { get; set; }
        public bool IncludeDensityChoices { get; set; }
        public Queue<GatewayAiSetupWizardStep> FollowingSteps { get; } = [];
        public GatewayAiSetupWizardStep? WizardStep { get; set; }
        public string CancelStatus { get; set; } = "cancelled";
        public JsonElement LastNext { get; private set; }
        public JsonElement LastCancel { get; private set; }
        private readonly TaskCompletionSource<JsonElement> _heldActivation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _answerRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseAnswer(bool fail)
        {
            if (fail) _answerRelease.SetException(new IOException("Synthetic lost answer reply."));
            else _answerRelease.SetResult();
        }

        private async Task<JsonElement> AnswerAsync(CancellationToken ct)
        {
            if (HoldAnswer)
                await _answerRelease.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (FollowingSteps.TryDequeue(out var nextStep))
            {
                WizardStep = nextStep;
                return JsonSerializer.SerializeToElement(new
                {
                    sessionId = LastActivation.GetProperty("sessionId").GetString(),
                    done = false, status = "running", step = WizardStep, error = AnswerError,
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            return JsonSerializer.SerializeToElement(new
            {
                sessionId = LastActivation.GetProperty("sessionId").GetString(),
                done = true, status = "done",
                modelActivation = new { modelRef = "openai/test-model" },
            });
        }

        public Task<JsonElement> RequestAsync(
            string method, object parameters, int timeoutMs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MethodCalls.Add(method);
            if (method == "openclaw.setup.detect")
            {
                if (FailDetection)
                    throw new IOException("Synthetic discovery failure.");
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    candidates = new[] { new { kind = "existing-model", label = "Existing AI",
                        detail = "Configured gateway connection", modelRef = "openai/test-model",
                        recommended = true, credentials = true, brandId = "codex" },
                        new { kind = "saved-auth:other", label = "Other Gateway model",
                            detail = "Synthetic additional gateway choice", modelRef = "openai/another-model",
                            recommended = false, credentials = true, brandId = "codex" } }
                        .Take(EmptyCandidates ? 0 : IncludeAdditionalCandidate ? 2 : 1).ToArray(),
                    manualProviders = new[] { new { id = "openai-api-key", label = "OpenAI API key", brandId = "codex" } },
                    authOptions = IncludeDensityChoices
                        ? new[] { new { id = "one", label = "First provider", featured = true },
                            new { id = "two", label = "Second provider", featured = true } }
                        : new[] { new { id = "provider-login", label = "Synthetic sign-in", featured = false } }
                            .Take(IncludeMoreProvider ? 1 : 0).ToArray(),
                    prepareOptions = new[] { new { id = "local-setup", label = "Local service", hint = "Set up and use" } }
                        .Take(IncludeDensityChoices ? 1 : 0).ToArray(),
                    workspace = "/synthetic-workspace",
                    configuredModel = "openai/test-model",
                    setupComplete = true,
                    nativeSessionCatalogPreferenceRequired = RequireCatalogConsent,
                }));
            }
            if (method == "openclaw.setup.activate.start")
            {
                LastActivation = JsonSerializer.SerializeToElement(parameters);
                if (FailActivation)
                    throw new IOException("Synthetic lost activation reply.");
                if (HoldActivation)
                    return _heldActivation.Task.WaitAsync(cancellationToken);
                if (RejectActivation)
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        sessionId = LastActivation.GetProperty("sessionId").GetString(),
                        done = true, status = "error",
                        activationRejection = new { disposition = "rejected-before-promotion", status = "auth" },
                    }));
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    sessionId = LastActivation.GetProperty("sessionId").GetString(),
                    done = WizardStep is null,
                    status = WizardStep is null ? "done" : "running",
                    step = WizardStep,
                    modelActivation = WizardStep is null ? new { modelRef = "openai/test-model" } : null,
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            if (method == "wizard.next")
            {
                LastNext = JsonSerializer.SerializeToElement(parameters);
                if (LastNext.TryGetProperty("answer", out _))
                    return AnswerAsync(cancellationToken);
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    sessionId = LastActivation.GetProperty("sessionId").GetString(),
                    done = false,
                    status = "running",
                    step = WizardStep,
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            if (method == "wizard.cancel")
            {
                LastCancel = JsonSerializer.SerializeToElement(parameters);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { status = CancelStatus }));
            }
            if (method == "openclaw.setup.verify")
            {
                if (FailVerification)
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        ok = false, status = "unavailable", error = "Synthetic verification failure."
                    }));
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    ok = true, modelRef = VerificationModelRef, latencyMs = 1.0
                }));
            }
            throw new InvalidOperationException($"Unexpected proof request: {method}");
        }
    }
}
