using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Chat;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public async Task Subagents_CloseReturnsToTheParentsReadingPosition(int width)
    {
        await WithChatAsync(width, async (surface, _, _, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Threads = [snapshot.Threads[0],
                    new ChatThread { Id = "child", Title = "Child", ParentSessionKey = "proof", IsSubagent = true }],
                Timelines = new Dictionary<string, ChatTimelineState>
                {
                    ["proof"] = ChatTimelineState.Initial() with
                    {
                        HistoryLoaded = true,
                        Entries = Enumerable.Range(0, 40).Select(index => new ChatTimelineItem(
                            $"scroll-{index}", index % 2 == 0 ? ChatTimelineItemKind.User : ChatTimelineItemKind.Assistant,
                            $"Reading message {index}.")).ToImmutableList(),
                    },
                },
            }));
            await AwaitInitialTailAsync(surface, "proof", "scroll-39");
            ScrollView? scroll = null;
            await ui.RunOnUIAsync(() =>
            {
                var items = Assert.Single(FindDescendants<ItemsView>(surface));
                scroll = items.ScrollView;
                var repeater = Assert.Single(FindDescendants<ItemsRepeater>(scroll));
                var reader = Enumerable.Range(0, repeater.ItemsSourceView.Count)
                    .Select(index => repeater.TryGetElement(index)).OfType<ItemContainer>()
                    .SelectMany(row => FindDescendants<Button>(row))
                    .First(button => button.IsLoaded && button.IsEnabled && button.IsTabStop &&
                        AutomationProperties.GetAutomationId(button).StartsWith("ChatCopy_", StringComparison.Ordinal));
                Assert.True(FindControl<TextBox>(surface, "ChatComposerInput").Focus(FocusState.Programmatic));
                Assert.True(reader.Focus(FocusState.Keyboard));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(FindControl<TextBox>(surface, "ChatComposerInput").Focus(FocusState.Programmatic));
                scroll!.ScrollTo(0, 0,
                    new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => scroll!.VerticalOffset <= 1 && FindControl<Button>(surface, "ChatJumpLatest").Opacity == 1,
                "parent reading its earlier messages"));
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatSubagentsToggle")));
            await SettleAsync();
            await ui.RunOnUIAsync(() => CloseSubagentsTab(surface));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(scroll, Assert.Single(FindDescendants<ItemsView>(
                    FindControl<Border>(surface, "ChatConversation"))).ScrollView);
                Assert.True(scroll!.ScrollableHeight > 0);
                Assert.InRange(scroll.VerticalOffset, 0, 1);
                Assert.Equal(1, FindControl<Button>(surface, "ChatJumpLatest").Opacity);
            });
        }, interactiveKeyboard: true);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public async Task Subagents_ListDetailAndResizePreserveTheParentConversation(int width)
    {
        await WithChatAsync(width, async (surface, _, session, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            var child = new ChatThread
            {
                Id = "child", Title = "Checking the results", ParentSessionKey = "proof",
                IsSubagent = true, Activity = ChatActivity.Working, CurrentActivity = "Reading the report",
            };
            snapshot = snapshot with
            {
                Threads = [snapshot.Threads[0], child,
                    child with { Id = "unrelated", ParentSessionKey = "other" },
                    child with { Id = "archived", IsArchived = true },
                    child with { Id = "ordinary", IsSubagent = false }],
                Timelines = snapshot.Timelines.ToDictionary(pair => pair.Key, pair => pair.Value)
                    .Append(new KeyValuePair<string, ChatTimelineState>("child", ChatTimelineState.Initial() with
                    {
                        HistoryLoaded = true,
                        Entries = ImmutableList.Create(
                            new ChatTimelineItem("child-reply", ChatTimelineItemKind.Assistant, "The child report is ready.")),
                    })).ToDictionary(pair => pair.Key, pair => pair.Value),
            };
            TextBox? composer = null;
            ItemsView? parentTimeline = null;
            await ui.RunOnUIAsync(() =>
            {
                composer = FindControl<TextBox>(surface, "ChatComposerInput");
                composer.SelectionStart = 3;
                composer.SelectionLength = 4;
                parentTimeline = Assert.Single(FindDescendants<ItemsView>(surface));
                session.ViewModel.AddAttachments([new ChatAttachment
                {
                    FileName = "draft.txt", MimeType = "text/plain", Content = "ZGVtbw==",
                }]);
                provider.Publish(snapshot);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Collapsed, FindControl<Grid>(surface, "ConversationPanel").Visibility);
                Assert.DoesNotContain(FindDescendants<Expander>(surface),
                    element => AutomationProperties.GetAutomationId(element) == "ChatChildActivity");
                Invoke(FindControl<Button>(surface, "ChatSubagentsToggle"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var pane = FindControl<Border>(surface, "ChatSubagentsPane");
                Assert.Equal(Visibility.Visible, pane.Visibility);
                var rows = FindDescendants<Button>(pane).Where(button =>
                    AutomationProperties.GetAutomationId(button).StartsWith("ChatSubagent_", StringComparison.Ordinal));
                Assert.Equal("ChatSubagent_child", AutomationProperties.GetAutomationId(Assert.Single(rows)));
                Assert.Contains("Working", AutomationProperties.GetName(FindControl<Button>(pane, "ChatSubagent_child")));
                Assert.InRange(Bounds(pane, surface).Right, width - 1, width + 1);
                Assert.Equal(width < 652 ? Visibility.Collapsed : Visibility.Visible,
                    FindControl<Border>(surface, "ChatConversation").Visibility);
            });
            await CaptureAsync(surface, $"Subagents-list-{width}");
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatSubagent_child")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var detail = FindControl<Border>(surface, "ChatSubagentTranscript");
                Assert.NotEmpty(FindDescendants<ItemsView>(detail));
                Assert.DoesNotContain(FindDescendants<TextBox>(detail),
                    control => AutomationProperties.GetAutomationId(control) == "ChatComposerInput");
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Same(parentTimeline, Assert.Single(FindDescendants<ItemsView>(
                    FindControl<Border>(surface, "ChatConversation"))));
                Assert.Equal("proof", session.ViewModel.Inputs!.CurrentThread.Id);
                Assert.Equal(Draft, composer!.Text);
                Assert.Single(session.ViewModel.PendingAttachments);
                surface.Width = width < 652 ? 1100 : 480;
            });
            await SettleAsync();
            await CaptureAsync(surface, $"Subagents-detail-resized-{width}");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal(Draft, composer!.Text);
                Invoke(FindControl<Button>(surface, "ChatSubagentsBack"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Visible, FindControl<ScrollViewer>(surface, "ChatSubagentsList").Visibility);
                Assert.Equal(FocusState.Keyboard, FindControl<Button>(surface, "ChatSubagent_child").FocusState);
                CloseSubagentsTab(surface);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Collapsed, FindControl<Grid>(surface, "ConversationPanel").Visibility);
                Assert.Equal(Visibility.Visible, FindControl<Border>(surface, "ChatConversation").Visibility);
                Assert.Equal(Draft, composer!.Text);
                Assert.Single(session.ViewModel.PendingAttachments);
            });
            Assert.Empty(provider.SentMessages);
        });
    }

    [Fact]
    public async Task Subagents_RemovedChildrenAndDisconnectWithdrawTheDetail()
    {
        await WithChatAsync(1100, async (surface, _, session, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            var children = snapshot with
            {
                Threads = [snapshot.Threads[0],
                    new ChatThread { Id = "child", Title = "Child", ParentSessionKey = "proof", IsSubagent = true }],
            };
            await ui.RunOnUIAsync(() => provider.Publish(children));
            await SettleAsync();
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatSubagentsToggle")));
            await SettleAsync();
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatSubagent_child")));
            await SettleAsync();
            await ui.RunOnUIAsync(() => provider.Publish(snapshot));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Collapsed, FindControl<Border>(surface, "ChatSubagentTranscript").Visibility);
                Assert.Contains(FindDescendants<TextBlock>(surface),
                    text => text.Text == "No subagents in the current session list.");
                provider.Publish(children with { ConnectionStatus = "Disconnected" });
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Collapsed, FindControl<Grid>(surface, "ConversationPanel").Visibility);
                provider.Publish(children);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Collapsed, FindControl<Grid>(surface, "ConversationPanel").Visibility);
                Invoke(FindControl<Button>(surface, "ChatSubagentsToggle"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.True(session.Controller.TrySelectSession("child")));
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Equal(Visibility.Collapsed,
                FindControl<Grid>(surface, "ConversationPanel").Visibility));
        });
    }

    [Fact]
    public async Task Subagents_LoadsProviderHistoryOnSelectionWithoutSelectingOrWritingTheChild()
    {
        var parent = new ChatHistoryInfo
        {
            SessionKey = "proof", SessionId = "parent-instance",
            Messages = [new ChatMessageInfo { Role = "assistant", Text = "Parent history remains here.", SessionKey = "proof" }],
        };
        var child = new ChatHistoryInfo
        {
            SessionKey = "agent:main:subagent:proof", SessionId = "child-instance",
            Messages = [new ChatMessageInfo
            {
                Role = "assistant", Text = "CHILD HISTORY ARRIVED THROUGH THE PROVIDER.",
                SessionKey = "agent:main:subagent:proof",
            }],
        };
        using var directory = new TempDirectory();
        using var bridge = new ConversationParityBridge(parent,
            new SessionInfo { Key = "proof", IsMain = true, Label = "Parent" })
        {
            Children = [new SessionInfo { Key = child.SessionKey, ParentSessionKey = "proof", Label = "Research" }],
            ChildHistory = child,
        };
        await using var source = new OpenClawChatDataProvider(bridge, null,
            directory.Combine("tools.json"), directory.Combine("attachments.json"), directory.Combine("last-state.json"));
        await source.LoadHistoryAsync("proof");
        await WithChatAsync(1100, async (surface, _, session, _) =>
        {
            Assert.DoesNotContain(child.SessionKey, bridge.HistoryRequests);
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatSubagentsToggle")));
            await SettleAsync();
            Assert.DoesNotContain(child.SessionKey, bridge.HistoryRequests);
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, $"ChatSubagent_{child.SessionKey}")));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                RealizedTimelineElements<RichTextBlock>(
                    FindControl<Border>(surface, "ChatSubagentTranscript"))
                    .Any(text => CollectText(text).Contains("CHILD HISTORY ARRIVED", StringComparison.Ordinal)),
                "selected child history from the production provider"));
            Assert.Contains(child.SessionKey, bridge.HistoryRequests);
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("proof", session.ViewModel.Inputs!.CurrentThread.Id);
                Assert.Equal(Draft, FindControl<TextBox>(surface, "ChatComposerInput").Text);
            });
            await CaptureAsync(surface, "Subagents-provider-history");
        }, initialThreadId: "proof", renderProvider: source);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public async Task Subagents_SharesTabsWithToolDetailsAndRetainsChildSelection(int width)
    {
        await WithChatAsync(width, async (surface, _, session, provider) =>
        {
            await PublishPanelToolAsync(provider, "Retained tool result");
            var snapshot = await provider.LoadAsync();
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Threads = [snapshot.Threads[0],
                    new ChatThread { Id = "child", Title = "Research", ParentSessionKey = "proof", IsSubagent = true }],
                Timelines = new Dictionary<string, ChatTimelineState>(snapshot.Timelines)
                {
                    ["child"] = ChatTimelineState.Initial() with
                    {
                        HistoryLoaded = true,
                        Entries = ImmutableList.Create(
                            new ChatTimelineItem("child-answer", ChatTimelineItemKind.Assistant, "Retained child result")),
                    },
                },
            }));
            await SettleAsync();
            await OpenPanelToolAsync(surface);
            if (width < 652)
            {
                await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ConversationPanelToggle")));
                await SettleAsync();
            }
            TextBox? composer = null;
            await ui.RunOnUIAsync(() =>
            {
                composer = FindControl<TextBox>(surface, "ChatComposerInput");
                var opener = FindControl<Button>(surface, "ChatSubagentsToggle");
                Assert.True(opener.Focus(FocusState.Programmatic));
                Invoke(opener);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var tabs = FindControl<TabView>(surface, "ConversationPanelTabs");
                Assert.Equal(2, tabs.TabItems.Count);
                Assert.Equal(1, tabs.SelectedIndex);
                Assert.Equal("Subagents", Assert.IsType<TabViewItem>(tabs.TabItems[1]).Header);
                Assert.Same(tabs.SelectedItem, FocusManager.GetFocusedElement(surface.XamlRoot));
                Invoke(FindControl<Button>(surface, "ChatSubagent_child"));
            });
            await SettleAsync();
            ItemsView? childTranscript = null;
            await ui.RunOnUIAsync(() =>
            {
                childTranscript = Assert.Single(FindDescendants<ItemsView>(
                    FindControl<Border>(surface, "ChatSubagentTranscript")));
                FindControl<TabView>(surface, "ConversationPanelTabs").SelectedIndex = 0;
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Contains(FindDescendants<RichTextBlock>(
                    FindControl<ScrollViewer>(surface, "ConversationToolDetailsReader")),
                    text => CollectText(text).Contains("Retained tool result", StringComparison.Ordinal));
                FindControl<TabView>(surface, "ConversationPanelTabs").SelectedIndex = 1;
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(childTranscript, Assert.Single(FindDescendants<ItemsView>(
                    FindControl<Border>(surface, "ChatSubagentTranscript"))));
                Assert.Equal(Visibility.Visible, FindControl<Button>(surface, "ChatSubagentsBack").Visibility);
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal(Draft, composer!.Text);
                Assert.Equal("proof", session.ViewModel.Inputs!.CurrentThread.Id);
            });
            await CaptureAsync(surface, $"Subagents-shared-tabs-{width}");
            await ui.RunOnUIAsync(() =>
            {
                var tabs = FindControl<TabView>(surface, "ConversationPanelTabs");
                var details = (TabViewItem)tabs.TabItems[0];
                Invoke(FindDescendants<Button>(details).Single(button => button.Name == "CloseButton"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Single(FindControl<TabView>(surface, "ConversationPanelTabs").TabItems);
                CloseSubagentsTab(surface);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Empty(FindControl<TabView>(surface, "ConversationPanelTabs").TabItems);
                Assert.Equal(Visibility.Visible, FindControl<Border>(surface, "ChatConversation").Visibility);
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Same(FindControl<Button>(surface, "ChatSubagentsToggle"),
                    FocusManager.GetFocusedElement(surface.XamlRoot));
            });
        });
    }

    private static void CloseSubagentsTab(DependencyObject surface)
    {
        var tab = FindControl<TabView>(surface, "ConversationPanelTabs").TabItems
            .OfType<TabViewItem>().Single(item => Equals(item.Header, "Subagents"));
        Invoke(FindDescendants<Button>(tab).Single(button => button.Name == "CloseButton"));
    }
}
