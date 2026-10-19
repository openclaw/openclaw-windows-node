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
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ReactorQuestionProofTests(UIThreadFixture ui)
{
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
                false, false, false, false, false, [], null, false)]));
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
                    Component<ReactorQuestionPanel, ReactorQuestionPanelProps>(new(coordinator, "proof", "main"))));
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
                Assert.Contains("Waiting for your answer", Control<TextBlock>(surface!, "ChatRunProgressText").Text);
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
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(unavailable ? "Question no longer available" : "Question answered",
                    Control<TextBlock>(surface!, "ChatQuestionResult_proof-question").Text);
                Assert.Empty(FindDescendants<RadioButton>(surface!));
            });
            if (unavailable)
                await ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(
                    Control<ScrollViewer>(surface!, "ChatQuestionPanel"), $"Reactor-question-unavailable-{width}-{theme}"));
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
