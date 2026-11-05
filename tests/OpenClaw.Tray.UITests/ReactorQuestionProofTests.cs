using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Adapters;
using OpenClawTray.Services;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ReactorQuestionProofTests(UIThreadFixture ui)
{
    [Fact]
    public async Task QuestionSubmission_UsesLatestTextBeforeQueuedReconciliation()
    {
        await ui.ResetContainerAsync();
        var gateway = new QuestionGateway();
        using var coordinator = new ChatQuestionCoordinator(gateway);
        coordinator.Start();
        gateway.Request(new("latest", "proof", "main", "run",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            "pending",
            [
                new("notes", "Notes", "Add your notes.", [],
                    false, true, false, false, false, ["Earlier answer"], null, false),
            ]));
        ReactorHostControl? host = null;
        ChatThemeProofScope? scope = null;
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                scope = new("Light");
                host = new() { Width = 480, Height = 780 };
                host.Mount(_ => Component<ReactorQuestionPanel, ReactorQuestionPanelProps>(
                    new(coordinator, "proof", "main", 480, 780)));
                ui.Container.Children.Add(host);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var input = Control<TextBox>(host!, "ChatQuestionInput_notes");
                var submit = Control<Button>(host!, "ChatQuestionSubmit_latest");
                Assert.Equal("Earlier answer", input.Text);
                Assert.True(submit.IsEnabled);
                TextChangedEventHandler? changed = null;
                changed = (_, _) =>
                {
                    if (input.Text != "Latest answer") return;
                    input.TextChanged -= changed;
                    try
                    {
                        Invoke(submit);
                        invoked.SetResult();
                    }
                    catch (Exception error)
                    {
                        invoked.SetException(error);
                    }
                };
                input.TextChanged += changed;
                input.Text = "Latest answer";
            });
            await invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await SettleAsync();
            Assert.NotNull(gateway.Answers);
            Assert.Equal(["Latest answer"], gateway.Answers["notes"]);
        }
        finally
        {
            gateway.Reply.TrySetResult("answered");
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.Children.Clear();
                host?.Dispose();
                scope?.Dispose();
            });
        }
    }

    [Fact]
    public async Task ProductionRoot_QuestionExpansionRetainsNativeComposerAndViewModelDraft()
    {
        using var temp = new TempDirectory();
        using var settingsStore = new SettingsStore(
            new SettingsManager(temp.Combine("settings")), new WinUIDispatcher(ui.Dispatcher));
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateInteractive(), token);
        server.Interactive!.Playback.Select("question-choice");
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, sessions) => { if (sessions.Length == 5) ready.TrySetResult(); };
        await client.ConnectAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var bridge = new GatewayClientChatBridge(client);
        await using var provider = new OpenClawChatDataProvider(bridge, null,
            temp.Combine("tools.json"), temp.Combine("attachments.json"), temp.Combine("last-chat.json"));
        await ui.ResetContainerAsync();
        const string key = GatewayScenario.EmptySessionKey;
        const string draft = "Keep this unsent draft.";
        ReactorHostControl? host = null;
        Window? window = null;
        Border? surface = null;
        ChatThemeProofScope? scope = null;
        ChatComposerSession? session = null;
        TextBox? input = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                scope = new("Light");
                session = new ChatComposerFactory(new WinUIDispatcher(ui.Dispatcher), settingsStore, new SandboxStatusStore())
                    .Create(provider, new(null, null, null, null, null), false);
                host = new();
                host.Mount(_ => Component<OpenClawReactorChatRoot, OpenClawReactorChatRootProps>(
                    new(provider, session, InitialThreadId: key)));
                surface = new() { Width = 800, Height = 720, Child = host, RequestedTheme = scope.ElementTheme };
                window = new() { Content = surface };
                window.AppWindow.MoveAndResize(new(-32000, -32000, 1040, 1000));
                window.Activate();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindDescendants<TextBlock>(surface!).Any(text =>
                    AutomationProperties.GetAutomationId(text) == "ChatWelcomeAgent"),
                "production empty-conversation composer"));
            await ui.RunOnUIAsync(() =>
            {
                input = Control<TextBox>(surface!, "ChatComposerInput");
                input.Text = draft;
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(input, Control<TextBox>(surface!, "ChatComposerInput"));
                Assert.Equal(draft, input!.Text);
                Assert.Equal(draft, session!.ViewModel.Draft);
            });
            await provider.SendMessageAsync(key, "Synthetic native retention proof.");
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindDescendants<RadioButton>(surface!).Any(radio =>
                    AutomationProperties.GetAutomationId(radio) == "ChatQuestionOption_fixture_choice_0"),
                "native pending question"));
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(input, Control<TextBox>(surface!, "ChatComposerInput"));
                Assert.Equal(draft, session!.ViewModel.Draft);
                Assert.Equal(Visibility.Collapsed, Control<ScrollViewer>(surface!, "ChatComposerArea").Visibility);
                Invoke(FindDescendants<Button>(surface!).Single(button =>
                    AutomationProperties.GetName(button) == "Collapse question"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(input, Control<TextBox>(surface!, "ChatComposerInput"));
                Assert.Equal(draft, input!.Text);
                Assert.Equal(draft, session!.ViewModel.Draft);
                Assert.Equal(Visibility.Visible, Control<ScrollViewer>(surface!, "ChatComposerArea").Visibility);
            });
        }
        finally
        {
            await client.DisconnectAsync();
            await ui.RunOnUIAsync(() =>
            {
                session?.Dispose();
                if (surface is not null) surface.Child = null;
                host?.Dispose();
                window?.Close();
                scope?.Dispose();
            });
        }
    }

    [NativeKeyboardTheory]
    [InlineData(480)]
    [Trait("Category", "NativeKeyboard")]
    public async Task QuestionNavigation_PreservesDraftsAndUsesRealNumberAndControlEnterKeys(int width)
    {
        await ui.ResetContainerAsync();
        var gateway = new QuestionGateway();
        using var coordinator = new ChatQuestionCoordinator(gateway);
        coordinator.Start();
        gateway.Request(new("steps", "proof", "main", "run",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            "pending",
            [
                new("platform", "Platform", "Which platform?",
                    [new("Windows", "win", null), new("Linux", "linux", null)],
                    false, false, false, false, false, [], null, false),
                new("features", "Features", "Which features?",
                    [new("Chat", "chat", null), new("Tools", "tools", null)],
                    true, false, false, false, false, [], null, false),
                new("notes", "Notes", "Add your notes.", [],
                    false, true, false, false, false, [], null, false),
            ]));
        ReactorHostControl? host = null;
        Window? window = null;
        Border? surface = null;
        ChatThemeProofScope? scope = null;
        nint handle = 0;
        Task PressAsync(global::Windows.System.VirtualKey key, bool control = false) =>
            ui.RunOnUIAsync(() => NativeKeyboardProof.Press(handle, key, control: control));
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                scope = new("Light");
                host = new();
                host.Mount(_ => Component<ReactorQuestionPanel, ReactorQuestionPanelProps>(new(coordinator, "proof", "main", width, 780)));
                surface = new() { Width = width, Height = 780, Child = host, RequestedTheme = scope.ElementTheme };
                window = new() { Content = surface };
                window.AppWindow.MoveAndResize(new(100, 100, width + 32, 860));
                window.Activate();
                handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            });
            await SettleAsync();
            System.Windows.Automation.AutomationElement.FromHandle(handle).SetFocus();
            await NativeKeyboardProof.ActivateAsync(handle);
            await ui.RunOnUIAsync(() => Assert.True(Control<RadioButton>(surface!, "ChatQuestionOption_platform_0").Focus(FocusState.Keyboard)));
            await PressAsync(global::Windows.System.VirtualKey.Down);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("1/3", Control<TextBlock>(surface!, "ChatQuestionProgress_steps").Text);
                Assert.True(Control<RadioButton>(surface!, "ChatQuestionOption_platform_1").IsChecked);
            });
            await PressAsync(global::Windows.System.VirtualKey.Number2);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("2/3", Control<TextBlock>(surface!, "ChatQuestionProgress_steps").Text);
                Assert.True(Control<CheckBox>(surface!, "ChatQuestionOption_features_0").Focus(FocusState.Keyboard));
            });
            await PressAsync(global::Windows.System.VirtualKey.Number1);
            await SettleAsync();
            await PressAsync(global::Windows.System.VirtualKey.Number2);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(Control<CheckBox>(surface!, "ChatQuestionOption_features_0").IsChecked);
                Assert.True(Control<CheckBox>(surface!, "ChatQuestionOption_features_1").IsChecked);
                Invoke(Control<Button>(surface!, "ChatQuestionBack_steps"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(Control<RadioButton>(surface!, "ChatQuestionOption_platform_1").IsChecked);
                Invoke(Control<Button>(surface!, "ChatQuestionSubmit_steps"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.True(Control<CheckBox>(surface!, "ChatQuestionOption_features_0").Focus(FocusState.Keyboard)));
            await PressAsync(global::Windows.System.VirtualKey.Enter, control: true);
            await SettleAsync();
            TextBox? input = null;
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("3/3", Control<TextBlock>(surface!, "ChatQuestionProgress_steps").Text);
                input = Control<TextBox>(surface!, "ChatQuestionInput_notes");
                input.Text = "First";
                input.SelectionStart = input.Text.Length;
                Assert.True(input.Focus(FocusState.Keyboard));
            });
            await SettleAsync();
            await PressAsync(global::Windows.System.VirtualKey.Number1);
            await PressAsync(global::Windows.System.VirtualKey.Enter);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.StartsWith("First1", input!.Text);
                Assert.Contains("\r", input.Text);
                Assert.Null(gateway.Answers);
                Invoke(Control<Button>(surface!, "ChatQuestionCollapse_steps"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => Invoke(Control<Button>(surface!, "ChatQuestionCollapse_steps")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(input, Control<TextBox>(surface!, "ChatQuestionInput_notes"));
                Assert.StartsWith("First1", input!.Text);
                input.Focus(FocusState.Keyboard);
            });
            await PressAsync(global::Windows.System.VirtualKey.Enter, control: true);
            await SettleAsync();
            Assert.Equal(["linux"], gateway.Answers!["platform"]);
            Assert.Equal(["chat", "tools"], gateway.Answers["features"]);
            Assert.Equal(["First1"], gateway.Answers["notes"]);
            gateway.Reply.SetResult("answered");
            await SettleAsync();
            Assert.Equal("answered", Assert.Single(coordinator.Snapshot().Prompts).Request.Status);
            await ui.RunOnUIAsync(() => Assert.DoesNotContain(
                FindDescendants<FrameworkElement>(surface!),
                element => AutomationProperties.GetAutomationId(element).StartsWith("ChatQuestion", StringComparison.Ordinal)));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null) surface.Child = null;
                host?.Dispose();
                window?.Close();
                scope?.Dispose();
            });
        }
    }

    [Fact]
    public async Task Claw_UsesFilledUpstreamGeometryAndStopsOnUnmount()
    {
        await ui.ResetContainerAsync();
        NativeWorkingClaw? claw = null;
        Window? window = null;
        Border? surface = null;
        ChatThemeProofScope? scope = null;
        Microsoft.UI.Xaml.Shapes.Path? jaw = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                scope = new("Light");
                claw = new() { Width = 18, Height = 18 };
                claw.Update(true, false, "native-motion-proof");
                surface = new()
                {
                    Width = 120, Height = 120, Child = claw, RequestedTheme = scope.ElementTheme,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                };
                window = new() { Content = surface };
                window.AppWindow.MoveAndResize(new(-32000, -32000, 160, 200));
                window.Activate();
            });
            await SettleAsync();
            double firstAngle = 0;
            await ui.RunOnUIAsync(() =>
            {
                var paths = FindDescendants<Microsoft.UI.Xaml.Shapes.Path>(claw!).ToArray();
                Assert.Equal(2, paths.Length);
                var expectedColor = new global::Windows.UI.ViewManagement.UISettings()
                    .GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentDark1);
                foreach (var path in paths)
                {
                    Assert.Equal(0, path.StrokeThickness);
                    Assert.Equal(expectedColor, Assert.IsType<SolidColorBrush>(path.Fill).Color);
                }
                jaw = paths.Single(path => path.RenderTransform is RotateTransform);
                var transform = Assert.IsType<RotateTransform>(jaw.RenderTransform);
                Assert.Equal(8.6, transform.CenterX, 5);
                Assert.Equal(11, transform.CenterY);
                firstAngle = transform.Angle;
            });
            var animated = new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            var changed = false;
            // Observe longer than the 2.4-second cycle, including its deliberate rest.
            for (var frame = 0; frame < 12; frame++)
            {
                await Task.Delay(250);
                await ui.YieldToRenderAsync();
                await ui.RunOnUIAsync(async () =>
                {
                    changed |= Math.Abs(((RotateTransform)jaw!.RenderTransform).Angle - firstAngle) > .5;
                    await VisualTestCapture.CaptureAsync(surface!, $"Claw-motion-frame-{frame}");
                });
            }
            Assert.Equal(animated, changed);
            double stoppedAngle = 0;
            await ui.RunOnUIAsync(() =>
            {
                surface!.Child = null;
                stoppedAngle = ((RotateTransform)jaw!.RenderTransform).Angle;
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Equal(stoppedAngle, ((RotateTransform)jaw!.RenderTransform).Angle));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null) surface.Child = null;
                window?.Close();
                scope?.Dispose();
            });
        }
    }

    [Theory]
    [InlineData(320, "Light", false)]
    [InlineData(800, "Dark", false)]
    [InlineData(320, "Light", true)]
    [InlineData(320, "HighContrast", false)]
    public async Task PendingQuestion_NativeSelectionSubmitsAndWaitsForAcknowledgement(int width, string theme, bool unavailable)
    {
        await ui.ResetContainerAsync();
        var gateway = new QuestionGateway();
        using var coordinator = new ChatQuestionCoordinator(gateway);
        coordinator.Start();
        gateway.Request(new("proof-question", "proof", "main", "run",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            "pending", [new("platform", "Platform", "Which platform should I target?",
                [new("Windows", "win", "Build a native Windows app"), new("Linux", "linux", null)],
                false, true, false, false, false, [], null, false)]));
        ReactorHostControl? host = null;
        Window? window = null;
        Border? surface = null;
        ChatThemeProofScope? scope = null;
        var canvasColor = theme == "Dark" ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                scope = new ChatThemeProofScope(theme);
                host = new ReactorHostControl();
                host.Mount(_ => VStack(12,
                    TextBlock("I can build that. I need one choice before continuing.").TextWrapping(TextWrapping.Wrap),
                    Component<ReactorRunProgress, ReactorRunProgressProps>(new(new(
                        DateTimeOffset.UtcNow.AddSeconds(-65), null, "Waiting for your answer", true, true))),
                    Component<ReactorQuestionPanel, ReactorQuestionPanelProps>(new(coordinator, "proof", "main", width))));
                surface = new Border
                {
                    Width = width, Height = 520, Child = host, RequestedTheme = scope.ElementTheme,
                    Background = new SolidColorBrush(canvasColor),
                };
                window = new Window { Content = surface };
                window.AppWindow.MoveAndResize(new(-32000, -32000, width + 32, 600));
                window.Activate();
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var scale = surface!.XamlRoot.RasterizationScale;
                window!.AppWindow.Resize(new(
                    (int)Math.Ceiling(surface.Width * scale) + 32,
                    (int)Math.Ceiling(surface.Height * scale) + 80));
                Assert.False(Control<Button>(surface!, "ChatQuestionSubmit_proof-question").IsEnabled);
                var option = Control<RadioButton>(surface!, "ChatQuestionOption_platform_0");
                option.IsChecked = true;
                Assert.NotNull(Control<Grid>(surface!, "ChatRunProgressClaw"));
                Assert.Contains("Waiting for your answer", AutomationProperties.GetName(Control<TextBlock>(surface!, "ChatRunProgressText")));
                Assert.Equal(AutomationLiveSetting.Off,
                    AutomationProperties.GetLiveSetting(Control<Grid>(surface!, "ChatRunProgress")));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(async () =>
            {
                var panel = Control<ScrollViewer>(surface!, "ChatQuestionPanel");
                Assert.InRange(panel.ActualHeight, 1, 360);
                Assert.InRange(panel.ActualWidth, 1, width);
                foreach (var button in FindDescendants<Button>(panel))
                {
                    var bounds = button.TransformToVisual(surface).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
                    Assert.InRange(bounds.Right, 1, surface!.ActualWidth);
                }
                Assert.True(Control<Button>(surface!, "ChatQuestionSubmit_proof-question").IsEnabled);
                AssertReadable(Control<TextBlock>(surface!, "ChatRunProgressText"), canvasColor);
                var card = Control<Border>(surface!, "ChatQuestion_proof-question");
                Assert.Equal(new CornerRadius(20), card.CornerRadius);
                Assert.Equal(new Thickness(14), card.Padding);
                Assert.Equal("1/1", Control<TextBlock>(surface!, "ChatQuestionProgress_proof-question").Text);
                var option = Control<RadioButton>(surface!, "ChatQuestionOption_platform_0");
                Assert.Contains(FindDescendants<TextBlock>(option), text => text.Text == "Build a native Windows app" && text.FontSize == 11);
                Assert.Contains(FindDescendants<TextBlock>(option), text => text.Text == "1");
                Assert.Equal("Type your own answer here", Control<TextBox>(surface!, "ChatQuestionInput_platform").PlaceholderText);
                var submit = Control<Button>(surface!, "ChatQuestionSubmit_proof-question");
                var skip = Control<Button>(surface!, "ChatQuestionCancel_proof-question");
                Assert.Equal("Submit", submit.Content);
                Assert.Equal("Skip", skip.Content);
                Assert.InRange(submit.TransformToVisual(skip).TransformPoint(new()).Y, -1, 1);
                Assert.True(submit.TransformToVisual(skip).TransformPoint(new()).X > 0);
                var cardColor = Composite(Assert.IsType<SolidColorBrush>(card.Background), canvasColor);
                AssertReadable(Assert.Single(FindDescendants<TextBlock>(card),
                    text => text.Text == "Which platform should I target?"), cardColor);
                await VisualTestCapture.CaptureAsync(surface!, $"Reactor-question-{width}-{theme}");
                Invoke(Control<Button>(surface!, "ChatQuestionSubmit_proof-question"));
            });
            await SettleAsync();
            Assert.Equal("win", gateway.Answers!["platform"][0]);
            Assert.True(Assert.Single(coordinator.Snapshot().Prompts).Submitting);
            await ui.RunOnUIAsync(() =>
                Assert.False(Control<Button>(surface!, "ChatQuestionSubmit_proof-question").IsEnabled));
            if (unavailable)
                gateway.Reply.SetException(new GatewayQuestionUnavailableException());
            else
                gateway.Reply.SetResult("answered");
            await SettleAsync();
            Assert.Equal(unavailable ? "unavailable" : "answered",
                Assert.Single(coordinator.Snapshot().Prompts).Request.Status);
            await ui.RunOnUIAsync(() =>
            {
                if (unavailable)
                    Assert.Equal("Question no longer available",
                        Control<TextBlock>(surface!, "ChatQuestionResult_proof-question").Text);
                else
                    Assert.DoesNotContain(FindDescendants<FrameworkElement>(surface!),
                        element => AutomationProperties.GetAutomationId(element).StartsWith("ChatQuestion", StringComparison.Ordinal));
                Assert.Empty(FindDescendants<RadioButton>(surface!));
            });
            if (unavailable)
            {
                await ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(
                    Control<ScrollViewer>(surface!, "ChatQuestionPanel"), $"Reactor-question-unavailable-{width}-{theme}"));
                var unavailableRequest = Assert.Single(coordinator.Snapshot().Prompts).Request;
                gateway.Request(unavailableRequest with
                {
                    Id = "newer-answered", Status = "answered", CreatedAtMs = unavailableRequest.CreatedAtMs + 1,
                });
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Equal("Question no longer available",
                        Control<TextBlock>(surface!, "ChatQuestionResult_proof-question").Text);
                    Assert.DoesNotContain(FindDescendants<TextBlock>(surface!),
                        text => AutomationProperties.GetAutomationId(text) == "ChatQuestionResult_newer-answered");
                });
                gateway.Request(unavailableRequest with
                {
                    Id = "newer-pending", Status = "pending", CreatedAtMs = unavailableRequest.CreatedAtMs + 2,
                });
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Equal("Question no longer available",
                        Control<TextBlock>(surface!, "ChatQuestionResult_proof-question").Text);
                    Assert.NotNull(Control<Border>(surface!, "ChatQuestion_newer-pending"));
                    Assert.DoesNotContain(FindDescendants<TextBlock>(surface!),
                        text => AutomationProperties.GetAutomationId(text) == "ChatQuestionResult_newer-answered");
                });
            }
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null) surface.Child = null;
                host?.Dispose();
                window?.Close();
                scope?.Dispose();
            });
        }
    }

    private async Task SettleAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(30);
            await ui.YieldToRenderAsync();
        }
    }

    private static T Control<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Assert.Single(FindDescendants<T>(root), control => AutomationProperties.GetAutomationId(control) == id);

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static global::Windows.UI.Color Composite(SolidColorBrush brush, global::Windows.UI.Color background)
    {
        var foreground = brush.Color;
        var alpha = foreground.A / 255d * brush.Opacity;
        byte Blend(byte front, byte back) => (byte)Math.Round(front * alpha + back * (1 - alpha));
        return global::Windows.UI.Color.FromArgb(255,
            Blend(foreground.R, background.R), Blend(foreground.G, background.G), Blend(foreground.B, background.B));
    }

    private static void AssertReadable(TextBlock text, global::Windows.UI.Color background)
    {
        var foreground = Composite(Assert.IsType<SolidColorBrush>(text.Foreground), background);
        var foregroundLight = ReactorChatLayoutProofTests.Luminance(foreground);
        var backgroundLight = ReactorChatLayoutProofTests.Luminance(background);
        var contrast = (Math.Max(foregroundLight, backgroundLight) + 0.05)
            / (Math.Min(foregroundLight, backgroundLight) + 0.05);
        Assert.True(contrast >= 4.5, $"Chat text contrast {contrast:F2}:1 must meet 4.5:1.");
    }

    private sealed class QuestionGateway : IChatQuestionGateway
    {
        public bool IsConnected => true;
        public event EventHandler<ConnectionStatus>? StatusChanged { add { } remove { } }
        public event EventHandler? QuestionProtocolError { add { } remove { } }
        public event EventHandler<GatewayQuestionEvent>? QuestionReceived;
        internal TaskCompletionSource<string> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyDictionary<string, string[]>? Answers { get; private set; }
        public Task<GatewayQuestionList> ListQuestionsAsync() => Task.FromResult(new GatewayQuestionList(true, []));
        public Task<GatewayQuestionRequest> GetQuestionAsync(string id) => throw new InvalidOperationException();
        public Task<string> ResolveQuestionAsync(string id, IReadOnlyDictionary<string, string[]>? answers)
        {
            Answers = answers;
            return Reply.Task;
        }
        internal void Request(GatewayQuestionRequest request) =>
            QuestionReceived?.Invoke(this, new(request.Id, request.Status, request));
    }
}
