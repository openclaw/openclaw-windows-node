using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed class GatewayFixtureUiTheoryAttribute : TheoryAttribute
{
    public GatewayFixtureUiTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_GATEWAY_FIXTURE_UI") != "1")
            Skip = "Opt in with scripts\\test-gateway-fixture.ps1. This test launches a real isolated Windows app.";
    }
}

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "GatewayFixture")]
    public async Task CancelledRunLateFinalCannotEndNewerNativeTurn(bool terminalBeforeNewRun)
    {
        await WithAppAsync(async run =>
        {
            await OpenCorrelationChatAsync(run);
            await SendCorrelationPromptAsync(run, "Fixture question A.");
            await PublishLifecycleAsync(run, "fixture-chat-1", "start");
            await run.Gateway.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, "fixture-chat-1",
                "assistant", new { delta = "Fixture A partial." });
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, "Fixture A partial."), "first stream");
            await WaitPrimaryActionAsync(run, "Stop");
            Invoke(FindById(run, "ChatComposerPrimaryAction")!);
            await run.Gateway.WaitForCompletedRequestAsync("chat.abort", GatewayScenario.MainSessionKey);
            await WaitPrimaryActionAsync(run, "Send");
            if (terminalBeforeNewRun)
                await PublishLifecycleAsync(run, "fixture-chat-1", "end");

            await SendCorrelationPromptAsync(run, "Fixture question B.");
            await PublishLifecycleAsync(run, "fixture-chat-2", "start");
            await run.Gateway.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, "fixture-chat-2",
                "assistant", new { delta = "Fixture B is streaming" });
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, "Fixture B is streaming"), "newer stream");
            if (!terminalBeforeNewRun)
                await PublishLifecycleAsync(run, "fixture-chat-1", "end");
            await run.Gateway.PublishChatFinalAsync(GatewayScenario.MainSessionKey, "fixture-chat-1",
                "STALE CANCELLED FINAL MUST NOT APPEAR");
            // The subsequent visible delta also acknowledges processing the preceding stale wire frames.
            await run.Gateway.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, "fixture-chat-2",
                "assistant", new { delta = " after stale A." });
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, "Fixture B is streaming after stale A."),
                "continued newer stream after cancelled final");
            await AssertStreamingAsync(run, "Fixture B is streaming after stale A.");
            var timeline = await CorrelationTimelineAsync(run);
            Assert.DoesNotContain("STALE CANCELLED FINAL", timeline.GetRawText(), StringComparison.Ordinal);
            Assert.Null(FindText(run, "STALE CANCELLED FINAL"));
            await CaptureIfRequestedAsync(run, "cancelled-a-newer-b-streaming.png");
            await CompleteCorrelationRunAsync(run, "fixture-chat-2", "Fixture B complete.");
            await CaptureIfRequestedAsync(run, "cancelled-a-newer-b-complete.png");
        }, scenario: GatewayScenario.CreateBrowse(allowChatSend: true));
    }

    [GatewayFixtureUiTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "GatewayFixture")]
    public async Task DeferredAbortFailureAfterTerminalRestoresNativeFinal(bool startNewerRun, bool finalWhileNewerRunActive)
    {
        await WithAppAsync(async run =>
        {
            await OpenCorrelationChatAsync(run);
            run.Gateway.HoldChatSends();
            run.Gateway.HoldChatAborts();
            var send = SendCorrelationPromptAsync(run, "Fixture question awaiting run identity.");
            await run.Gateway.WaitForRequestAsync("chat.send", GatewayScenario.MainSessionKey);
            await WaitPrimaryActionAsync(run, "Stop");
            Invoke(FindById(run, "ChatComposerPrimaryAction")!);
            await WaitPrimaryActionAsync(run, "Send");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "chat.abort");
            await PublishLifecycleAsync(run, "fixture-chat-1", "start");
            await run.Gateway.WaitForRequestAsync("chat.abort", GatewayScenario.MainSessionKey);
            await PublishLifecycleAsync(run, "fixture-chat-1", "end");
            // The send acknowledgement follows lifecycle.end on the same socket.
            run.Gateway.ReleaseChatSends();
            await send;
            if (startNewerRun)
            {
                await SendCorrelationPromptAsync(run, "Fixture newer question.");
                await PublishLifecycleAsync(run, "fixture-chat-2", "start");
                await run.Gateway.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, "fixture-chat-2",
                    "assistant", new { delta = "Fixture newer stream" });
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, "Fixture newer stream"), "newer stream");
            }

            run.Gateway.ReleaseChatAborts(fail: true);
            // The existing warning is emitted after guarded rollback, not merely after the RPC response.
            await run.WaitForAsync(async () =>
            {
                var path = Path.Combine(run.Profile.DataDirectory, "openclaw-tray.log");
                if (!File.Exists(path)) return false;
                using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(file);
                return (await reader.ReadToEndAsync()).Contains(
                    "[ABORT] Deferred chat.abort failed:", StringComparison.Ordinal);
            }, "deferred abort failure processed by provider");
            if (startNewerRun)
            {
                await AssertStreamingAsync(run, "Fixture newer stream");
                if (finalWhileNewerRunActive)
                {
                    await run.Gateway.PublishChatFinalAsync(GatewayScenario.MainSessionKey, "fixture-chat-1",
                        "Fixture legitimate trailing answer restored.");
                    await WaitUiAsync(run, () => IsVisibleInTimeline(run, "Fixture legitimate trailing answer restored."),
                        "restored final while newer turn is still streaming");
                    await AssertStreamingAsync(run, "Fixture newer stream");
                    await CaptureIfRequestedAsync(run, "restored-final-newer-still-streaming.png");
                    await PublishLifecycleAsync(run, "fixture-chat-1", "start");
                    await run.Gateway.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, "fixture-chat-1",
                        "assistant", new { delta = "STALE RESUMED OUTPUT MUST NOT APPEAR" });
                }
                await run.Gateway.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, "fixture-chat-2",
                    "assistant", new { delta = " after abort failure." });
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, "Fixture newer stream after abort failure."),
                    "newer stream survives failed abort");
                await AssertStreamingAsync(run, "Fixture newer stream after abort failure.");
                Assert.DoesNotContain("STALE RESUMED OUTPUT", (await CorrelationTimelineAsync(run)).GetRawText(),
                    StringComparison.Ordinal);
                await CaptureIfRequestedAsync(run, "failed-abort-newer-streaming.png");
                await CompleteCorrelationRunAsync(run, "fixture-chat-2", "Fixture newer answer complete.");
            }
            if (!finalWhileNewerRunActive)
                await CompleteCorrelationRunAsync(run, "fixture-chat-1", "Fixture legitimate trailing answer restored.");
            else
                Assert.Contains("Fixture legitimate trailing answer restored.", (await CorrelationTimelineAsync(run)).GetRawText(),
                    StringComparison.Ordinal);
            await CaptureIfRequestedAsync(run, "failed-abort-restored-final.png");
        }, scenario: GatewayScenario.CreateBrowse(allowChatSend: true));
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task BackgroundMediaRetransmitAndRetainedToolOutcomePreserveNewerNativeTurn()
    {
        await WithAppAsync(async run =>
        {
            await OpenCorrelationChatAsync(run);
            await SendCorrelationPromptAsync(run, "Fixture old question.");
            await PublishLifecycleAsync(run, "fixture-chat-1", "start");
            await run.Gateway.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, "fixture-chat-1",
                "tool", new { phase = "start", name = "system.run", toolCallId = "shared-tool" });
            await PublishLifecycleAsync(run, "fixture-chat-1", "end");
            await WaitPrimaryActionAsync(run, "Send");
            await SendCorrelationPromptAsync(run, "Fixture newer question.");
            await PublishLifecycleAsync(run, "fixture-chat-2", "start");
            await run.Gateway.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, "fixture-chat-2",
                "tool", new { phase = "start", name = "system.run", toolCallId = "shared-tool" });
            await run.Gateway.PublishChatContentAsync(GatewayScenario.MainSessionKey, "fixture-chat-2",
                [new { type = "text", text = "Fixture newer stream" },
                 new { type = "audio", artifactId = "fixture-b", fileName = "fixture-b.wav" }], isFinal: false);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, "Fixture newer stream")
                && IsVisibleInTimeline(run, "fixture-b.wav"), "newer stream with its own media");
            var before = await CorrelationTimelineAsync(run);
            var tools = before.GetProperty("entries").EnumerateArray()
                .Where(entry => entry.GetProperty("ToolCallId").GetString() == "shared-tool").ToArray();
            Assert.Equal(2, tools.Length);
            Assert.Equal("Interrupted", tools[0].GetProperty("toolResult").GetString());
            Assert.Equal("InProgress", tools[1].GetProperty("toolResult").GetString());
            var oldToolId = tools[0].GetProperty("Id").GetString();
            var newToolId = tools[1].GetProperty("Id").GetString();

            await run.Gateway.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, "fixture-chat-1",
                "tool", new { phase = "result", toolCallId = "shared-tool", result = "Fixture old result." });
            for (var repeat = 0; repeat < 2; repeat++)
            {
                await run.Gateway.PublishChatContentAsync(GatewayScenario.MainSessionKey, "fixture-chat-1",
                    [new { type = "text", text = "Fixture restored final." },
                     new { type = "audio", artifactId = "fixture-a", fileName = "fixture-a.wav" }], isFinal: true);
            }
            await run.Gateway.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, "fixture-chat-2",
                "assistant", new { delta = " continues." });
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, "Fixture newer stream continues.")
                && IsVisibleInTimeline(run, "Fixture restored final.")
                && IsVisibleInTimeline(run, "fixture-a.wav")
                && IsVisibleInTimeline(run, "fixture-b.wav"), "distinct background and streaming media");
            await AssertStreamingAsync(run, "Fixture newer stream continues.");
            var after = await CorrelationTimelineAsync(run);
            var entries = after.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Single(entries, entry => entry.GetProperty("Text").GetString() == "Fixture restored final.");
            Assert.Equal("Success", entries.Single(entry => entry.GetProperty("Id").GetString() == oldToolId)
                .GetProperty("toolResult").GetString());
            Assert.Equal("InProgress", entries.Single(entry => entry.GetProperty("Id").GetString() == newToolId)
                .GetProperty("toolResult").GetString());
            var textNodes = FindTimeline(run)!.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().ToArray();
            Assert.Single(textNodes, element => element.Current.Name == "fixture-a.wav");
            Assert.Single(textNodes, element => element.Current.Name == "fixture-b.wav");
            await CaptureIfRequestedAsync(run, "background-media-tool-newer-streaming.png");
            await CompleteCorrelationRunAsync(run, "fixture-chat-2", "Fixture newer answer complete.");
            await CaptureIfRequestedAsync(run, "background-media-tool-newer-complete.png");
        }, scenario: GatewayScenario.CreateBrowse(allowChatSend: true));
    }

    private static async Task OpenCorrelationChatAsync(GatewayFixtureRun run)
    {
        await run.InvokeAsync("app.navigate", new { page = "chat" });
        await SelectSessionAsync(run, GatewayScenario.MainTitle, GatewayScenario.MainSessionKey);
    }

    private static Task<JsonElement> SendCorrelationPromptAsync(GatewayFixtureRun run, string message) =>
        run.InvokeAsync("app.chat.send", new { threadId = GatewayScenario.MainSessionKey, message });

    private static Task PublishLifecycleAsync(GatewayFixtureRun run, string runId, string phase) =>
        run.Gateway.PublishChatRunEventAsync(GatewayScenario.MainSessionKey, runId, "lifecycle", new { phase });

    private static Task WaitPrimaryActionAsync(GatewayFixtureRun run, string name) =>
        WaitUiAsync(run, () => FindById(run, "ChatComposerPrimaryAction")?.Current.Name == name,
            $"composer {name} action");

    private static async Task<JsonElement> CorrelationTimelineAsync(GatewayFixtureRun run) =>
        (await run.InvokeAsync("app.chat.snapshot", new { threadId = GatewayScenario.MainSessionKey }))
            .GetProperty("selectedTimeline");

    private static async Task AssertStreamingAsync(GatewayFixtureRun run, string text)
    {
        var timeline = await CorrelationTimelineAsync(run);
        Assert.True(timeline.GetProperty("turnActive").GetBoolean());
        Assert.Contains(timeline.GetProperty("entries").EnumerateArray(),
            entry => entry.GetProperty("Text").GetString() == text && entry.GetProperty("IsStreaming").GetBoolean());
        await WaitPrimaryActionAsync(run, "Stop");
        await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "streaming-state.json"), timeline.GetRawText());
    }

    private static async Task CompleteCorrelationRunAsync(GatewayFixtureRun run, string runId, string text)
    {
        await run.Gateway.PublishChatFinalAsync(GatewayScenario.MainSessionKey, runId, text);
        await WaitUiAsync(run, () => IsVisibleInTimeline(run, text), "visible completed answer");
        await WaitPrimaryActionAsync(run, "Send");
        var timeline = await CorrelationTimelineAsync(run);
        Assert.False(timeline.GetProperty("turnActive").GetBoolean());
        var final = Assert.Single(timeline.GetProperty("entries").EnumerateArray(),
            entry => entry.GetProperty("Text").GetString() == text);
        Assert.False(final.GetProperty("IsStreaming").GetBoolean());
        await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "completed-state.json"), timeline.GetRawText());
    }
}
