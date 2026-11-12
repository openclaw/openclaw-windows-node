using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InteractiveWork_DoesNotFadeCompletedMessagePixels()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.MainSentinel), "visible initial answer");
            var window = FindHub(run);
            var baselineInk = new Dictionary<string, int>(StringComparer.Ordinal);
            async Task AssertStableInkAsync(string stage, string message)
            {
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, message), $"{stage} message visible");
                for (var frame = 0; frame < 12; frame++)
                {
                    var target = FindText(run, message)!;
                    var file = $"{stage}-ink-{frame:00}.png";
                    OwnedWindowCapture.Save(run, window, file, target);
                    var path = Path.Combine(run.ArtifactsDirectory, file);
                    using var metadata = System.Text.Json.JsonDocument.Parse(
                        File.ReadAllText(Path.ChangeExtension(path, ".json")));
                    var bounds = metadata.RootElement.GetProperty("assertions")[0];
                    using var image = new System.Drawing.Bitmap(path);
                    var left = (int)Math.Ceiling(bounds.GetProperty("left").GetDouble());
                    var top = (int)Math.Ceiling(bounds.GetProperty("top").GetDouble());
                    var right = Math.Min(image.Width, left + (int)bounds.GetProperty("Width").GetDouble());
                    var bottom = Math.Min(image.Height, top + (int)bounds.GetProperty("Height").GetDouble());
                    var readablePixels = CountReadableInk(image,
                        System.Drawing.Rectangle.FromLTRB(left, top, right, bottom));
                    Assert.True(readablePixels >= 40,
                        $"{stage} frame {frame}: completed text lost its readable ink ({readablePixels} pixels). Capture: {path}");
                    if (baselineInk.TryGetValue(message, out var baseline))
                        Assert.True(readablePixels >= baseline * 0.9,
                            $"{stage} frame {frame}: completed ink faded from {baseline} to {readablePixels} pixels. Capture: {path}");
                    else
                        baselineInk.Add(message, readablePixels);
                    // Cover the complete two-second skeleton and 2.4-second shine cycles.
                    await Task.Delay(250);
                }
            }

            await AssertStableInkAsync("initial", GatewayScenario.MainSentinel);
            var playback = run.Gateway.Interactive!.Playback;
            playback.Select("tools");
            playback.Pause();
            await run.InvokeAsync("app.chat.send", new { message = "Inspect the synthetic file while earlier text stays readable." });
            for (var step = 0; step < 3; step++)
                playback.Step();
            await WaitUiAsync(run, () => FindById(run, "ChatRunProgress") is not null &&
                FindText(run, "fixture.txt") is not null, "running tool and clock");
            await AssertStableInkAsync("running", GatewayScenario.MainSentinel);
            playback.Resume();
            await WaitUiAsync(run, () => FindById(run, "ChatRunProgress") is null &&
                IsVisibleInTimeline(run, "FIXTURE INTERACTIVE COMPLETE"), "settled answer");
            await AssertStableInkAsync("settled", "FIXTURE INTERACTIVE COMPLETE");
        }, scenario: GatewayScenario.CreateInteractive());
    }

    [Theory]
    [InlineData(249, 26, 40)]
    [InlineData(249, 95, 40)]
    [InlineData(249, 160, 0)]
    [InlineData(32, 211, 40)]
    [InlineData(32, 90, 0)]
    [InlineData(249, 249, 0)]
    public void ReadableInk_AcceptsPrimaryAndSecondaryTextButRejectsFadedOrMissingText(
        int background, int foreground, int expected)
    {
        using var image = new System.Drawing.Bitmap(40, 21);
        using (var graphics = System.Drawing.Graphics.FromImage(image))
            graphics.Clear(System.Drawing.Color.FromArgb(background, background, background));
        for (var x = 0; x < 40; x++)
            image.SetPixel(x, 10, System.Drawing.Color.FromArgb(foreground, foreground, foreground));
        Assert.Equal(expected, CountReadableInk(image, new(0, 0, image.Width, image.Height)));
    }

    private static int CountReadableInk(System.Drawing.Bitmap image, System.Drawing.Rectangle bounds)
    {
        var colors = new Dictionary<int, int>();
        for (var y = bounds.Top; y < bounds.Bottom; y++)
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var color = image.GetPixel(x, y).ToArgb();
                colors[color] = colors.GetValueOrDefault(color) + 1;
            }
        Assert.NotEmpty(colors);
        static double Luminance(int argb)
        {
            var color = System.Drawing.Color.FromArgb(argb);
            return ReactorChatLayoutProofTests.Luminance(
                global::Windows.UI.Color.FromArgb(255, color.R, color.G, color.B));
        }
        // The text crop's dominant color is its actual background, independent of the app palette.
        var background = Luminance(colors.MaxBy(pair => pair.Value).Key);
        return colors.Sum(pair =>
        {
            var ink = Luminance(pair.Key);
            var contrast = (Math.Max(ink, background) + 0.05) / (Math.Min(ink, background) + 0.05);
            return contrast >= 4.5 ? pair.Value : 0;
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InteractiveQuestionKeepsComposerAndAcknowledgesNativeAnswer()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.EmptyTitle, GatewayScenario.EmptySessionKey);
            await WaitUiAsync(run, () =>
            {
                var input = FindById(run, "ChatComposerInput");
                if (input is null || string.IsNullOrEmpty(input.Current.ItemStatus)
                    || FindById(run, "ChatWelcomeAgent") is null)
                    return false;
                using var render = System.Text.Json.JsonDocument.Parse(input.Current.ItemStatus);
                return render.RootElement.GetProperty("selectedThreadId").GetString() == GatewayScenario.EmptySessionKey
                    && RenderConsumedHistory(run, GatewayScenario.EmptySessionKey);
            }, "mounted empty-session composer after navigation and history delivery");
            var composerId = FindById(run, "ChatComposerInput")!.GetRuntimeId();
            ((System.Windows.Automation.ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(
                System.Windows.Automation.ValuePattern.Pattern)).SetValue("Keep this unsent draft.");
            await WaitUiAsync(run, () =>
            {
                var input = FindById(run, "ChatComposerInput");
                var send = FindById(run, "ChatComposerPrimaryAction");
                return input is not null && send is { Current.IsEnabled: true }
                    && send.Current.Name == "Send"
                    && ((System.Windows.Automation.ValuePattern)input.GetCurrentPattern(
                        System.Windows.Automation.ValuePattern.Pattern)).Current.Value == "Keep this unsent draft.";
            }, "unsent draft accepted by the mounted composer before question delivery");
            run.Gateway.Interactive!.Playback.Select("question-choice");
            await run.InvokeAsync("app.chat.send", new
            {
                threadId = GatewayScenario.EmptySessionKey,
                message = "Synthetic native question proof.",
            });
            await WaitUiAsync(run, () => FindById(run, "ChatQuestionOption_fixture_choice_0") is not null,
                "native question option");
            await WaitUiAsync(run, () => FindById(run, "ChatRunProgress")?.Current.Name
                .Contains("Waiting for your answer", StringComparison.Ordinal) == true,
                "visible waiting-for-answer progress");
            var option = FindById(run, "ChatQuestionOption_fixture_choice_0")!;
            Assert.False(option.Current.IsOffscreen);
            Assert.True(FindById(run, "ChatComposerInput") is null or { Current.IsOffscreen: true });
            Invoke(FindButton(run, "Collapse question")!);
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false },
                "retained composer revealed by collapsing the question");
            Assert.Equal(composerId, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Equal("Keep this unsent draft.", ((System.Windows.Automation.ValuePattern)FindById(run, "ChatComposerInput")!
                .GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).Current.Value);
            Invoke(FindButton(run, "Expand question")!);
            await WaitUiAsync(run, () => FindById(run, "ChatQuestionOption_fixture_choice_0") is { Current.IsOffscreen: false },
                "expanded native question");
            option = FindById(run, "ChatQuestionOption_fixture_choice_0")!;
            Invoke(option);
            await WaitUiAsync(run, () => FindButton(run, "Submit answer")?.Current.IsEnabled == true,
                "enabled native answer submission");
            await CaptureIfRequestedAsync(run, "interactive-native-question.png");
            Invoke(FindButton(run, "Submit answer")!);
            await run.Gateway.WaitForCompletedRequestAsync("question.resolve");
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, "Your answer was accepted."),
                "continued answer after native submission");
            await WaitUiAsync(run, () => FindById(run, "ChatRunProgress") is null,
                "completed native run indicator dismissed");
            await WaitUiAsync(run, () => FindById(run, "ChatQuestionPanel") is null
                && FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false },
                "answered question dismissed and retained composer revealed");
            Assert.Null(FindText(run, "Question answered"));
            Assert.Equal(composerId, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Equal("Keep this unsent draft.", ((System.Windows.Automation.ValuePattern)FindById(run, "ChatComposerInput")!
                .GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).Current.Value);
            await CaptureIfRequestedAsync(run, "interactive-native-question-answered.png");
        }, scenario: GatewayScenario.CreateInteractive());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InteractiveActiveSessionForkShowsOnlyCompletedHistory()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            var key = GatewayScenario.OtherSessionKey;
            await SelectSessionAsync(run, GatewayScenario.OtherTitle, key);
            run.Gateway.Interactive!.Playback.Pause();
            const string pending = "Unfinished interactive fork proof";
            await run.InvokeAsync("app.chat.send", new { threadId = key, message = pending });
            var nextList = run.Gateway.Requests.Count(request => request.Method == "sessions.list") + 1;
            run.Gateway.Interactive.Playback.Step();
            await run.Gateway.WaitForCompletedRequestAsync("sessions.list", occurrence: nextList);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, pending), "active parent turn");
            await OpenSessionMenuAsync(run, key);
            Invoke(FindById(run, "WorkspaceSessionMenuFork")!);
            await run.Gateway.WaitForCompletedRequestAsync("sessions.create");
            await WaitUiAsync(run, () => SessionSelected(run, "Fixture: new conversation"), "selected fork");
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.OtherSentinel), "completed history in fork");
            Assert.Null(FindText(run, pending));
            await CaptureIfRequestedAsync(run, "interactive-active-fork.png");
        }, scenario: GatewayScenario.CreateInteractive());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InteractiveChatShowsStreamedAnswerAndGatewayApproval()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.EmptyTitle, GatewayScenario.EmptySessionKey);
            await run.InvokeAsync("app.chat.send", new { threadId = GatewayScenario.EmptySessionKey, message = "Synthetic interactive UI proof." });
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, "FIXTURE INTERACTIVE COMPLETE"), "interactive streamed final answer");
            await CaptureIfRequestedAsync(run, "interactive-chat-final.png");
            run.Gateway.Interactive!.Playback.Select("approval");
            await run.InvokeAsync("app.chat.send", new { threadId = GatewayScenario.EmptySessionKey, message = "Synthetic approval UI proof." });
            await WaitUiAsync(run, () => FindButton(run, "Allow once") is not null, "interactive approval prompt");
            await CaptureIfRequestedAsync(run, "interactive-chat-approval.png");
            Invoke(FindButton(run, "Allow once")!);
            await run.Gateway.WaitForCompletedRequestAsync("exec.approval.resolve");
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, "Approval decision: allow-once."), "answer after approval");
            await CaptureIfRequestedAsync(run, "interactive-chat-after-approval.png");
        }, scenario: GatewayScenario.CreateInteractive());
    }
}
