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
    [InlineData(FocusNavigationDirection.Next)]
    [InlineData(FocusNavigationDirection.Previous)]
    public async Task DetailsSlot_CompactTabTraversalIncludesBackAndExcludesHiddenConversation(
        FocusNavigationDirection direction)
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            await PublishPanelToolAsync(provider, "Tool output");
            await OpenPanelToolAsync(surface);
            await ui.RunOnUIAsync(async () =>
            {
                var panel = FindControl<Grid>(surface, "ConversationPanel");
                var back = FindControl<Button>(surface, "ConversationPanelToggle");
                var conversation = FindControl<Border>(surface, "ChatConversation");
                Assert.Equal(Visibility.Collapsed, conversation.Visibility);
                var tab = Assert.IsType<TabViewItem>(FindControl<TabView>(panel, "ConversationPanelTabs").TabItems[0]);
                Assert.True(tab.Focus(FocusState.Keyboard));
                var reachedBack = false;
                for (var step = 0; step < 32; step++)
                {
                    var move = await FocusManager.TryMoveFocusAsync(direction, new FindNextElementOptions { SearchRoot = surface });
                    Assert.True(move.Succeeded);
                    var focused = Assert.IsAssignableFrom<DependencyObject>(FocusManager.GetFocusedElement(surface.XamlRoot));
                    Assert.DoesNotContain(focused, FindDescendants<DependencyObject>(conversation));
                    if (ReferenceEquals(back, focused))
                    {
                        reachedBack = true;
                        break;
                    }
                    Assert.Contains(focused, FindDescendants<DependencyObject>(panel));
                }
                Assert.True(reachedBack, "Tab traversal must leave the panel's inner focus scope to reach Back.");
                var next = await FocusManager.TryMoveFocusAsync(direction, new FindNextElementOptions { SearchRoot = surface });
                Assert.True(next.Succeeded);
                Assert.Contains(FocusManager.GetFocusedElement(surface.XamlRoot),
                    FindDescendants<DependencyObject>(panel));
            });
        });
    }

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
                ToolName: toolName, ToolCallId: "call-one", ToolRunId: "run", ToolOutput: "First tool output",
                ToolInputDetails: toolName == "powershell"
                    ? new("", "Write-Output '" + string.Concat(Enumerable.Repeat("literal output ", 12)) + "'\n# second line")
                    : null);
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
                var copy = FindDescendants<Button>(surface).First(button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatCopy_", StringComparison.Ordinal));
                Assert.Equal(14, Assert.IsType<TextBlock>(copy.Content).FontSize);
                Assert.Equal(Assert.IsType<TextBlock>(copy.Content).FontSize,
                    Assert.IsType<TextBlock>(open.Content).FontSize);
                Assert.Equal(AutomationProperties.GetName(open), ToolTipService.GetToolTip(open));
                var touch = new Windows.Devices.Input.TouchCapabilities().TouchPresent != 0;
                Assert.Equal(toolName == "powershell" && !touch ? 0d : 1d, open.Opacity);
                var expander = FindControl<Expander>(surface, "ChatToolCall_one");
                var card = FindControl<Border>(expander, "ChatToolDetailsCard_one");
                var cardBounds = Bounds(card, surface);
                var actionBounds = Bounds(open, surface);
                Assert.True(actionBounds.Left >= cardBounds.Left && actionBounds.Right <= cardBounds.Right);
                Assert.True(actionBounds.Top >= cardBounds.Top && actionBounds.Bottom <= cardBounds.Bottom);
                Assert.Equal(toolName == "powershell" || touch ? 40 : 28, open.ActualWidth);
                Assert.Equal(open.ActualWidth, open.ActualHeight);
                var input = FindDescendants<RichTextBlock>(expander).First();
                var inputBounds = Bounds(input, surface);
                if (toolName == "powershell")
                {
                    Assert.InRange(actionBounds.Top - cardBounds.Top, 8, 10);
                    Assert.InRange(cardBounds.Right - actionBounds.Right, 6, 8);
                    Assert.InRange(inputBounds.Top - cardBounds.Top, 8, 10);
                    Assert.True(inputBounds.Top < actionBounds.Bottom,
                        "Command input starts beside the overlay, not below an extra header row.");
                    Assert.True(inputBounds.Right <= actionBounds.Left - 10,
                        "Reserved input space keeps selectable command text clear of the overlay.");
                    Assert.DoesNotContain(FindDescendants<Grid>(card), grid =>
                        AutomationProperties.GetAutomationId(grid) == "ChatToolDetailsHeader");
                }
                else
                {
                    var header = FindControl<Grid>(card, "ChatToolDetailsHeader");
                    var title = FindDescendants<TextBlock>(header).Single(text => !ReferenceEquals(text, open.Content));
                    Assert.False(string.IsNullOrWhiteSpace(title.Text));
                    Assert.True(Bounds(title, surface).Right <= actionBounds.Left);
                    Assert.True(Bounds(header, surface).Top >= cardBounds.Top);
                    Assert.True(inputBounds.Top > Bounds(header, surface).Bottom);
                    Assert.Contains(FindDescendants<Border>(card), border => border.Height == 1 &&
                        Bounds(border, surface).Top >= Bounds(header, surface).Bottom &&
                        Bounds(border, surface).Bottom <= inputBounds.Top);
                }
                Assert.True(open.Focus(FocusState.Keyboard));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var open = FindControl<Button>(surface, "ChatToolDetails_one");
                Assert.Equal(1d, open.Opacity);
                Assert.Equal(new Thickness(0), open.BorderThickness);
                var foreground = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(open.Foreground);
                var pointerForeground = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(
                    open.Resources["ButtonForegroundPointerOver"]);
                Assert.Equal(pointerForeground.Color, foreground.Color);
                var background = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(open.Background);
                var pointerBackground = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(
                    open.Resources["ButtonBackgroundPointerOver"]);
                Assert.Equal(pointerBackground.Color, background.Color);
                Assert.Equal(pointerBackground.Opacity, background.Opacity);
            });
            await CaptureAsync(surface, $"ConversationDetailsAction-{toolName}-{width}");
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatToolDetails_one")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var tabs = FindControl<TabView>(surface, "ConversationPanelTabs");
                Assert.Single(tabs.TabItems);
                Assert.Equal(0, tabs.SelectedIndex);
                var panel = FindControl<Grid>(surface, "ConversationPanel");
                Assert.Equal(Visibility.Visible, panel.Visibility);
                var conversation = FindControl<Border>(surface, "ChatConversation");
                var canvas = Assert.IsType<Grid>(conversation.Child);
                var conversationFill = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(canvas.Background);
                var panelFill = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(panel.Background);
                Assert.Equal(conversationFill.Color, panelFill.Color);
                Assert.Equal(conversationFill.Opacity, panelFill.Opacity);
                if (Environment.GetEnvironmentVariable("OPENCLAW_PROOF_THEME") != "HighContrast")
                    Assert.Equal(0, panelFill.Color.A);
                var divider = FindControl<Border>(surface, "ConversationPanelDivider");
                Assert.False(divider.IsHitTestVisible);
                Assert.Equal(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw,
                    AutomationProperties.GetAccessibilityView(divider));
                Assert.Equal(width < 652 ? Visibility.Collapsed : Visibility.Visible, divider.Visibility);
                if (width >= 652)
                {
                    var separatorBounds = Bounds(divider, surface);
                    var panelBounds = Bounds(panel, surface);
                    Assert.InRange(separatorBounds.Width, 0.5, 1.5);
                    Assert.InRange(separatorBounds.Top - panelBounds.Top, -1, 1);
                    Assert.InRange(separatorBounds.Bottom - panelBounds.Bottom, -1, 1);
                    Assert.True(separatorBounds.Left >= Bounds(conversation, surface).Right);
                    Assert.True(separatorBounds.Right <= panelBounds.Left);
                    var resize = FindControl<Button>(surface, "ConversationPanelResize");
                    Assert.Equal(12, resize.Width);
                    Assert.Equal(
                        "Use Left and Right arrow keys to resize. Home selects the minimum width; End selects the maximum width.",
                        new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(resize).GetHelpText());
                }
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
                Assert.Equal(Visibility.Collapsed,
                    FindControl<Border>(surface, "ConversationPanelDivider").Visibility);
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
