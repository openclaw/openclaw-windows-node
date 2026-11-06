extern alias TrayRuntime;

using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Chat;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetailsSlot_CompactVisibilityCancelsSyntheticCapture(bool resizeIntoCompact)
    {
        var capture = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captureToken = default;
        var captureCalls = 0;
        await WithChatAsync(resizeIntoCompact ? 1100 : 480, async (surface, _, session, provider) =>
        {
            await PublishPanelToolAsync(provider, "Tool output");
            if (resizeIntoCompact) await OpenPanelToolAsync(surface);
            await ui.RunOnUIAsync(() =>
            {
                session.Controller.StartVoiceRecording();
                Assert.True(session.ViewModel.IsRecording);
            });
            if (resizeIntoCompact)
                await ui.RunOnUIAsync(() => surface.Width = 480);
            else
                await OpenPanelToolAsync(surface);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Collapsed, FindControl<Border>(surface, "ChatConversation").Visibility);
                Assert.True(captureToken.IsCancellationRequested);
                Assert.False(session.ViewModel.IsRecording);
                session.Controller.StartVoiceRecording();
                Assert.Equal(1, captureCalls);
                Assert.Equal(Draft, session.ViewModel.Draft);
                var item = (TabViewItem)FindControl<TabView>(surface, "ConversationPanelTabs").TabItems[0];
                Invoke(FindDescendants<Button>(item).Single(button => button.Name == "CloseButton"));
            });
            await SettleAsync();
            capture.SetResult("Late transcript");
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Visible, FindControl<Border>(surface, "ChatConversation").Visibility);
                Assert.Equal(1, captureCalls);
                Assert.Equal(Draft, session.ViewModel.Draft);
            });
        }, voiceRequest: (token, _) =>
        {
            captureCalls++;
            captureToken = token;
            return capture.Task;
        });
    }

    [Fact]
    public async Task DetailsSlot_LongOutputUsesPanelViewportAndHiddenOpenerFallsBack()
    {
        await WithChatAsync(1100, async (surface, _, _, provider) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var scale = surface.XamlRoot.RasterizationScale;
                _captureWindow!.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)Math.Ceiling(surface.Width * scale) + 32,
                    (int)Math.Ceiling(surface.Height * scale) + 64));
            });
            await PublishPanelToolAsync(provider,
                string.Join("\n", Enumerable.Range(1, 100).Select(index => $"Result line {index}")));
            await OpenPanelToolAsync(surface);
            Button? opener = null;
            await ui.RunOnUIAsync(() =>
            {
                var reader = FindControl<ScrollViewer>(surface, "ConversationToolDetailsReader");
                Assert.True(reader.ActualHeight > 240);
                Assert.True(reader.ScrollableHeight > 0);
                Assert.Contains(FindDescendants<RichTextBlock>(reader), text => text.ActualHeight > 240);
                Assert.Empty(FindDescendants<ScrollViewer>((DependencyObject)reader.Content));
                opener = FindControl<Button>(surface, "ChatToolDetails_one");
            });
            await CaptureAsync(surface, "ConversationDetails-LongOutput");
            await ui.RunOnUIAsync(() => FindControl<Expander>(surface, "ChatToolCall_one").IsExpanded = false);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var item = (TabViewItem)FindControl<TabView>(surface, "ConversationPanelTabs").TabItems[0];
                Assert.True(item.Focus(FocusState.Programmatic));
                Invoke(FindDescendants<Button>(item).Single(button => button.Name == "CloseButton"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var focused = Assert.IsAssignableFrom<Control>(FocusManager.GetFocusedElement(surface.XamlRoot));
                Assert.NotSame(opener, focused);
                Assert.Contains(focused, FindDescendants<Control>(FindControl<Border>(surface, "ChatConversation")));
            });
        });
    }

    private async Task PublishPanelToolAsync(ProofProvider provider, string output)
    {
        var snapshot = await provider.LoadAsync();
        provider.Publish(snapshot with
        {
            Timelines = new Dictionary<string, ChatTimelineState>
            {
                ["proof"] = ChatTimelineState.Initial() with
                {
                    HistoryLoaded = true,
                    Entries = ImmutableList.Create(new ChatTimelineItem("one", ChatTimelineItemKind.ToolCall, "read",
                        ToolName: "read_file", ToolCallId: "call-one", ToolRunId: "run", ToolOutput: output))
                }
            }
        });
        await SettleAsync();
    }

    private async Task OpenPanelToolAsync(Border surface)
    {
        await ExpandPanelToolAsync(surface, "one");
        await ui.RunOnUIAsync(() =>
        {
            var open = FindControl<Button>(surface, "ChatToolDetails_one");
            Assert.True(open.Focus(FocusState.Keyboard));
            Invoke(open);
        });
        await SettleAsync();
    }

    private async Task ExpandPanelToolAsync(Border surface, string entryId)
    {
        await ui.RunOnUIAsync(() => FindControl<Expander>(surface, "ChatToolActivity_" + entryId).IsExpanded = true);
        await SettleAsync();
        await ui.RunOnUIAsync(() => FindControl<Expander>(surface, "ChatToolCall_" + entryId).IsExpanded = true);
        await SettleAsync();
    }

    [Theory]
    [InlineData(1100, "read_file")]
    [InlineData(480, "read_file")]
    [InlineData(1100, "powershell")]
    [InlineData(480, "powershell")]
    public async Task DetailsSlot_ReusesContentClosesAndPreservesComposer(int width, string toolName)
    {
        await WithChatAsync(width, async (surface, _, _, provider) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                var scale = surface.XamlRoot.RasterizationScale;
                _captureWindow!.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)Math.Ceiling(surface.Width * scale) + 32,
                    (int)Math.Ceiling(surface.Height * scale) + 64));
                var isolatedRoot = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
                if (!string.IsNullOrWhiteSpace(isolatedRoot))
                    Assert.Equal(Path.Combine(Path.GetFullPath(isolatedRoot), "openclaw-tray.log"),
                        TrayRuntime::OpenClawTray.Services.Logger.LogFilePath, ignoreCase: true);
            });
            var snapshot = await provider.LoadAsync();
            var first = new ChatTimelineItem("one", ChatTimelineItemKind.ToolCall, "read first",
                ToolName: toolName, ToolCallId: "call-one", ToolRunId: "run", ToolOutput: "First tool output");
            var second = first with { Id = "two", Text = "read second", ToolCallId = "call-two", ToolOutput = "Second tool output" };
            provider.Publish(snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState>
                {
                    ["proof"] = ChatTimelineState.Initial() with
                    {
                        HistoryLoaded = true,
                        Entries = ImmutableList.Create(first,
                            new ChatTimelineItem("between", ChatTimelineItemKind.Assistant, "Next file"), second)
                    }
                }
            });
            await SettleAsync();
            TextBox? composer = null;
            await ui.RunOnUIAsync(() =>
            {
                composer = FindControl<TextBox>(surface, "ChatComposerInput");
            });
            await ExpandPanelToolAsync(surface, "one");
            await ui.RunOnUIAsync(() =>
            {
                var open = FindControl<Button>(surface, "ChatToolDetails_one");
                Assert.Equal("\uE8A0", Assert.IsType<TextBlock>(open.Content).Text);
                Assert.Equal(AutomationProperties.GetName(open), ToolTipService.GetToolTip(open));
                var touch = new Windows.Devices.Input.TouchCapabilities().TouchPresent != 0;
                Assert.Equal(toolName == "powershell" && !touch ? 0d : 1d, open.Opacity);
                var expander = FindControl<Expander>(surface, "ChatToolCall_one");
                Assert.InRange(Bounds(expander, surface).Right - Bounds(open, surface).Right, 0, 48);
                var input = FindDescendants<RichTextBlock>(expander).First();
                Assert.True(Bounds(open, surface).Bottom <= Bounds(input, surface).Top + 1,
                    "The action must sit above the tool text without covering it.");
                Assert.True(open.Focus(FocusState.Keyboard));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var open = FindControl<Button>(surface, "ChatToolDetails_one");
                Assert.Equal(1d, open.Opacity);
            });
            await CaptureAsync(surface, $"ConversationDetailsAction-{toolName}-{width}");
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatToolDetails_one")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var tabs = FindControl<TabView>(surface, "ConversationPanelTabs");
                Assert.Single(tabs.TabItems);
                Assert.Equal(0, tabs.SelectedIndex);
                Assert.Equal(Visibility.Visible, FindControl<Grid>(surface, "ConversationPanel").Visibility);
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal(Draft, composer!.Text);
                if (width < 652) Invoke(FindControl<Button>(surface, "ConversationPanelToggle"));
            });
            await SettleAsync();
            await ExpandPanelToolAsync(surface, "two");
            await ui.RunOnUIAsync(() =>
            {
                var open = FindControl<Button>(surface, "ChatToolDetails_two");
                open.Focus(FocusState.Programmatic);
                Invoke(open);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var tabs = FindControl<TabView>(surface, "ConversationPanelTabs");
                Assert.Single(tabs.TabItems);
                var panel = FindControl<Grid>(surface, "ConversationPanel");
                Assert.Equal(Visibility.Visible, panel.Visibility);
                var text = string.Join("\n", FindDescendants<RichTextBlock>(panel)
                    .SelectMany(block => block.Blocks.OfType<Microsoft.UI.Xaml.Documents.Paragraph>())
                    .SelectMany(paragraph => paragraph.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>())
                    .Select(run => run.Text));
                Assert.Contains("Second tool output", text);
                Assert.DoesNotContain("First tool output", text);
            });
            await CaptureAsync(surface, $"ConversationDetails-{toolName}-{width}");
            await ui.RunOnUIAsync(() =>
            {
                var item = (TabViewItem)FindControl<TabView>(surface, "ConversationPanelTabs").TabItems[0];
                Invoke(FindDescendants<Button>(item).Single(button => button.Name == "CloseButton"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Empty(FindControl<TabView>(surface, "ConversationPanelTabs").TabItems);
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal(Draft, composer!.Text);
                Assert.Same(FindControl<Button>(surface, "ChatToolDetails_two"),
                    FocusManager.GetFocusedElement(surface.XamlRoot));
                Invoke(FindControl<Button>(surface, "ChatToolDetails_two"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Single(FindControl<TabView>(surface, "ConversationPanelTabs").TabItems));
            provider.Publish(snapshot with { ConnectionStatus = "Disconnected" });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var panel = FindControl<Grid>(surface, "ConversationPanel");
                Assert.Empty(FindDescendants<RichTextBlock>(panel));
                Assert.Contains(FindDescendants<TextBlock>(panel),
                    text => text.Text.Contains("no longer available", StringComparison.Ordinal));
            });
        });
    }
}
