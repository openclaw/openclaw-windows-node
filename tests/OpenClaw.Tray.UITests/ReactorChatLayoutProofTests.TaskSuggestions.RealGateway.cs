// Opt-in combined proof: compiled Gateway/model fixture -> signed native client -> mounted task card.
// Run with the task-owned Gateway receipt and the exclusive native-keyboard proof runner.
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Chat;
using OpenClawTray.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed class RealGatewayTaskTheoryAttribute : TheoryAttribute
{
    public RealGatewayTaskTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_COMBINED_TASK_PROOF") != "1" ||
            Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF") != "1")
            Skip = "Requires the isolated compiled Gateway and exclusive native keyboard proof runner.";
    }
}

public sealed partial class ReactorChatLayoutProofTests
{
    [RealGatewayTaskTheory]
    [InlineData(480)]
    [Trait("Category", "CombinedGatewayTaskProof")]
    public async Task CombinedGatewayTask_ProducesNativeCardAndRetainsDraft(int width)
    {
        var receiptPath = Environment.GetEnvironmentVariable("OPENCLAW_COMBINED_GATEWAY_RECEIPT")
            ?? throw new InvalidOperationException("A task-owned compiled Gateway receipt is required.");
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
        var endpoint = new Uri(receipt.RootElement.GetProperty("endpoint").GetString()!);
        Assert.True(endpoint.IsLoopback && endpoint.Scheme == "ws");
        var nonce = receipt.RootElement.GetProperty("nonce").GetString()!;
        var title = "Inspect the combined native follow-up";
        var prompt = "Inspect the synthetic combined follow-up. Keep <instructions> and **literal text** unchanged.";
        var sessionKey = "agent:main:dashboard:combined-native-" + nonce;
        using var temp = new TempDirectory();
        using var client = new OpenClawGatewayClient(endpoint.AbsoluteUri,
            receipt.RootElement.GetProperty("token").GetString()!, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true,
            persistHandshakeDeviceTokens: false) { ClientIdentity = OperatorClientIdentity.Interactive };
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, status) => { if (status == ConnectionStatus.Connected) connected.TrySetResult(); };
        var terminal = new TaskCompletionSource<ChatMessageInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ChatMessageReceived += (_, message) =>
        {
            if (message.SessionKey == sessionKey && message.Role == "assistant" && message.IsTerminal) terminal.TrySetResult(message);
        };
        var taskEvents = 0;
        client.TaskSuggestionsInvalidated += (_, _) => Interlocked.Increment(ref taskEvents);
        await client.ConnectAsync();
        await connected.Task.WaitAsync(TaskProofStepTimeout);
        Assert.True(client.IsConnectedToGateway);
        Assert.Contains("operator.admin", client.GrantedOperatorScopes);
        using (var identityBridge = new GatewayClientChatBridge(client))
        {
            var identity = await identityBridge.ReadTaskOperatorIdentityAsync(client.SessionMutationConnectionEpoch!.Value);
            Assert.False(string.IsNullOrWhiteSpace(identity));
        }
        var created = await client.CreateSessionAsync(new() { Key = sessionKey });
        Assert.True(created.Ok, created.Error);
        Assert.Equal(sessionKey, created.Key);
        using var coordinator = new OpenClawChatCoordinator(new SettingsManager(temp.Combine("settings")),
            () => null, NullLogger.Instance, null, () => null);
        coordinator.SetOperatorClient(client, "combined-gateway-" + nonce);
        var source = coordinator.Provider!;
        await source.LoadAsync();
        await source.LoadHistoryAsync(sessionKey);
        try
        {
            await WithChatAsync(width, async (surface, _, session, _) =>
            {
                nint handle = 0;
                TextBox? editor = null;
                await ui.RunOnUIAsync(() =>
                {
                    var scale = surface.XamlRoot.RasterizationScale;
                    _captureWindow!.AppWindow.ResizeClient(new((int)Math.Ceiling(width * scale), (int)Math.Ceiling(surface.Height * scale)));
                    handle = WinRT.Interop.WindowNative.GetWindowHandle(_captureWindow);
                    editor = FindControl<TextBox>(surface, "ChatComposerInput");
                    editor.Text = "Retain this exact combined Gateway unsent draft.";
                });
                await NativeKeyboardProof.ActivateAsync(handle);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    session.ViewModel.Draft == "Retain this exact combined Gateway unsent draft.", "combined draft accepted"));
                var sent = await client.SendChatMessageForRunAsync("Flag the separate combined follow-up.",
                    sessionKey, created.SessionId, idempotencyKey: "combined-" + nonce);
                Assert.False(sent.IsTerminalFailure, sent.Error);
                Assert.False(string.IsNullOrWhiteSpace(sent.RunId));
                var completed = await terminal.Task.WaitAsync(TaskProofStepTimeout);
                Assert.Equal(sent.RunId, completed.RunId);
                Assert.True(completed.IsFinal, completed.ErrorMessage);
                string? taskId = null;
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                {
                    taskId = source.TaskSuggestions!.Snapshot().Cards.SingleOrDefault(card => card.Suggestion.SessionKey == sessionKey)?.Suggestion.Id;
                    return taskId is not null && FindOptional<Border>(surface, "ChatTaskSuggestion_" + taskId) is { IsLoaded: true, ActualWidth: > 0 };
                }, "real Gateway task card mounted", timeout: TaskProofStepTimeout));
                Assert.True(Volatile.Read(ref taskEvents) > 0);
                Assert.False(source.TaskSuggestions!.Snapshot().IdentityUnavailable);
                var card = Assert.Single(source.TaskSuggestions.Snapshot().Cards, card => card.Suggestion.Id == taskId);
                Assert.Equal(title, card.Suggestion.Title);
                Assert.Equal(prompt, card.Suggestion.Prompt);
                await ui.RunOnUIAsync(() =>
                {
                    var rendered = FindControl<Border>(surface, "ChatTaskSuggestion_" + taskId);
                    Assert.Contains(FindDescendants<TextBlock>(rendered), text => text.Text == title && text.Visibility == Visibility.Visible);
                    Assert.Contains(FindDescendants<Button>(rendered), button => Equals(button.Content, "Start in a new session") && button.IsEnabled);
                });
                await CombinedTaskButtonAsync(surface, taskId!, "Show instructions", handle, VirtualKey.Enter);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    FindDescendants<TextBlock>(FindControl<Border>(surface, "ChatTaskSuggestion_" + taskId))
                        .Any(text => text.Text == prompt && text.Visibility == Visibility.Visible && text.IsTextSelectionEnabled), "real instructions shown literally"));
                if (Environment.GetEnvironmentVariable("OPENCLAW_TASK_PROOF_ARTIFACTS") is { Length: > 0 } captures)
                    await SaveTaskWindowAsync(handle, captures, "combined-native-instructions");
                await CombinedTaskButtonAsync(surface, taskId!, "Copy instructions", handle, VirtualKey.Space);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    FindDescendants<Button>(surface).Any(button => Equals(button.Content, "Instructions copied")), "real instruction copy acknowledged"));
                await ui.RunOnUIAsync(async () => Assert.Equal(prompt, await Clipboard.GetContent().GetTextAsync()));
                await CombinedTaskButtonAsync(surface, taskId!, "Hide instructions", handle, VirtualKey.Enter);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    FindDescendants<TextBlock>(FindControl<Border>(surface, "ChatTaskSuggestion_" + taskId))
                        .Any(text => text.Text == prompt && text.Visibility == Visibility.Collapsed), "instructions collapsed before dismissal focus"));
                await CombinedTaskButtonAsync(surface, taskId!, "Dismiss task", handle, VirtualKey.Enter);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    FindOptional<Border>(surface, "ChatTaskSuggestion_" + taskId) is null, "real dismissed card removed", timeout: TaskProofStepTimeout));
                var pending = await client.ListTaskSuggestionsAsync(client.SessionMutationConnectionEpoch!.Value);
                Assert.DoesNotContain(pending.Suggestions, task => task.Id == taskId);
                var history = await client.RequestChatHistoryAsync(sessionKey);
                Assert.DoesNotContain(history.Messages, message => message.Role == "user" && message.Text == "Retain this exact combined Gateway unsent draft.");
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Same(editor, FindControl<TextBox>(surface, "ChatComposerInput"));
                    Assert.Equal("Retain this exact combined Gateway unsent draft.", editor!.Text);
                    Assert.Equal(editor.Text, session.ViewModel.Draft);
                    Assert.Equal(sessionKey, session.ViewModel.Inputs!.CurrentThread.Id);
                });
                if (Environment.GetEnvironmentVariable("OPENCLAW_TASK_PROOF_ARTIFACTS") is { Length: > 0 } results)
                    await SaveTaskWindowAsync(handle, results, "combined-native-dismissed-draft");
            }, renderProvider: source, initialThreadId: sessionKey, interactiveKeyboard: true);
        }
        finally { await client.DisconnectAsync(); }
    }

    private async Task CombinedTaskButtonAsync(Border surface, string taskId, string label, nint handle, VirtualKey key)
    {
        ButtonBase? target = null;
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            var card = FindOptional<Border>(surface, "ChatTaskSuggestion_" + taskId);
            if (card is null) return false;
            target = FindDescendants<ButtonBase>(card).SingleOrDefault(button =>
                (button is Button && Equals(button.Content, label)) ||
                (button is ToggleButton && AutomationProperties.GetName(button) == label));
            return target is { IsLoaded: true, IsEnabled: true, IsTabStop: true, ActualWidth: > 0 } && target.Focus(FocusState.Keyboard);
        }, label + " real Gateway action focusable"));
        await ui.RunOnUIAsync(() => target!.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }));
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(surface.XamlRoot), target), label + " focused"));
        await AssertTaskGeometryAsync(surface);
        await ui.RunOnUIAsync(() =>
        {
            var panel = Bounds(FindControl<ScrollViewer>(surface, "ChatTaskSuggestions"), surface);
            var bounds = Bounds(target!, surface);
            Assert.True(bounds.Top >= panel.Top - 1 && bounds.Bottom <= panel.Bottom + 1);
            Assert.Same(target, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(surface.XamlRoot));
            Assert.Equal(label, FrameworkElementAutomationPeer.CreatePeerForElement(target!).GetName());
            NativeKeyboardProof.Press(handle, key);
        });
    }
}
