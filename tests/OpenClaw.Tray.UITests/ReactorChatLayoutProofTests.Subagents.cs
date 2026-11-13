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
    [InlineData(480, false)]
    [InlineData(1100, false)]
    [InlineData(1100, true)]
    public async Task Subagents_CloseReturnsToTheParentsReadingPosition(int width, bool withDetails)
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
                        Entries = (withDetails
                                ? [new ChatTimelineItem("one", ChatTimelineItemKind.ToolCall, "read",
                                    ToolName: "read_file", ToolCallId: "call-one", ToolRunId: "run", ToolOutput: "Panel result")]
                                : Array.Empty<ChatTimelineItem>())
                            .Concat(Enumerable.Range(0, 40).Select(index => new ChatTimelineItem(
                                $"scroll-{index}", index % 2 == 0 ? ChatTimelineItemKind.User : ChatTimelineItemKind.Assistant,
                                $"Reading message {index}."))).ToImmutableList(),
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
            if (withDetails)
            {
                // Two open tabs: closing the selected Subagents tab must not drop focus into the transcript.
                await ui.RunOnUIAsync(() => scroll!.ScrollTo(0, 0,
                    new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore)));
                await SettleAsync();
                await OpenPanelToolAsync(surface);
            }
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(FindControl<TextBox>(surface, "ChatComposerInput").Focus(FocusState.Programmatic));
                scroll!.ScrollTo(0, 0,
                    new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => scroll!.VerticalOffset <= 1 && FindControl<Button>(surface, "ChatJumpLatest").Opacity == 1,
                "parent reading its earlier messages"));
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatSubagentsPill")));
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
                if (withDetails)
                {
                    var tabs = FindControl<TabView>(surface, "ConversationPanelTabs");
                    Assert.Same(Assert.Single(tabs.TabItems), FocusManager.GetFocusedElement(surface.XamlRoot));
                }
            });
        }, interactiveKeyboard: true);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public async Task Subagents_PillOpensTheStatusListAndPreservesTheParentConversation(int width)
    {
        await WithChatAsync(width, async (surface, _, session, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            var child = new ChatThread
            {
                Id = "child", Title = "Checking the results", ParentSessionKey = "proof", IsSubagent = true,
                Activity = ChatActivity.Working, CurrentActivity = "Reading the report",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            };
            var failed = child with
            {
                Id = "failed", Title = "Linting the change", Activity = ChatActivity.Idle, CurrentActivity = null,
                RunStatus = "failed", RuntimeMs = 58_000,
            };
            snapshot = snapshot with
            {
                Threads = [snapshot.Threads[0], child, failed,
                    child with { Id = "unrelated", ParentSessionKey = "other" },
                    child with { Id = "archived", IsArchived = true },
                    child with { Id = "ordinary", IsSubagent = false }],
            };
            TextBox? composer = null;
            ItemsView? parentTimeline = null;
            await ui.RunOnUIAsync(() =>
            {
                Assert.DoesNotContain(FindDescendants<Button>(surface),
                    button => AutomationProperties.GetAutomationId(button) == "ChatSubagentsPill");
                composer = FindControl<TextBox>(surface, "ChatComposerInput");
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
                var pill = FindControl<Button>(surface, "ChatSubagentsPill");
                Assert.Equal("Subagents: 1 running, 1 finished", AutomationProperties.GetName(pill));
                Assert.Equal("2", FindControl<TextBlock>(pill, "ChatSubagentsPillCount").Text);
                Assert.Single(FindDescendants<ProgressRing>(pill));
                Assert.True(Bounds(pill, surface).Bottom <= Bounds(composer!, surface).Top);
                Invoke(pill);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var pane = FindControl<Border>(surface, "ChatSubagentsPane");
                Assert.Equal(Visibility.Visible, pane.Visibility);
                var rows = FindDescendants<Grid>(pane).Where(row =>
                    AutomationProperties.GetAutomationId(row).StartsWith("ChatSubagent_", StringComparison.Ordinal))
                    .Select(AutomationProperties.GetAutomationId).ToArray();
                Assert.Equal(["ChatSubagent_child", "ChatSubagent_failed"], rows);
                Assert.Empty(FindDescendants<Button>(pane));
                Assert.Equal("Running: 1", AutomationProperties.GetName(
                    FindControl<StackPanel>(pane, "ChatSubagentsGroup_Running")));
                Assert.Equal("Finished: 1", AutomationProperties.GetName(
                    FindControl<StackPanel>(pane, "ChatSubagentsGroup_Finished")));
                var working = FindControl<Grid>(pane, "ChatSubagent_child");
                Assert.StartsWith("Checking the results, Working · Reading the report, 2m ",
                    AutomationProperties.GetName(working));
                Assert.Equal("Working", AutomationProperties.GetItemStatus(working));
                Assert.Equal("Linting the change, Failed, 58s",
                    AutomationProperties.GetName(FindControl<Grid>(pane, "ChatSubagent_failed")));
                Assert.InRange(Bounds(pane, surface).Right, width - 1, width + 1);
                Assert.Equal(width < 652 ? Visibility.Collapsed : Visibility.Visible,
                    FindControl<Border>(surface, "ChatConversation").Visibility);
            });
            await CaptureAsync(surface, $"Subagents-list-{width}");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Same(parentTimeline, Assert.Single(FindDescendants<ItemsView>(
                    FindControl<Border>(surface, "ChatConversation"))));
                Assert.Equal("proof", session.ViewModel.Inputs!.CurrentThread.Id);
                Assert.Equal(Draft, composer!.Text);
                Assert.Single(session.ViewModel.PendingAttachments);
                provider.Publish(snapshot with
                {
                    Threads = snapshot.Threads.Select(thread => thread.Id == "child"
                        ? thread with { Activity = ChatActivity.Idle, RunStatus = "completed", RuntimeMs = 90_000 }
                        : thread).ToArray(),
                });
                surface.Width = width < 652 ? 1100 : 480;
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var pane = FindControl<Border>(surface, "ChatSubagentsPane");
                Assert.DoesNotContain(FindDescendants<StackPanel>(pane),
                    group => AutomationProperties.GetAutomationId(group) == "ChatSubagentsGroup_Running");
                Assert.Equal("Checking the results, Done, 1m 30s",
                    AutomationProperties.GetName(FindControl<Grid>(pane, "ChatSubagent_child")));
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal(Draft, composer!.Text);
                CloseSubagentsTab(surface);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Collapsed, FindControl<Grid>(surface, "ConversationPanel").Visibility);
                Assert.Equal(Visibility.Visible, FindControl<Border>(surface, "ChatConversation").Visibility);
                var settledPill = FindControl<Button>(surface, "ChatSubagentsPill");
                Assert.Equal("2", FindControl<TextBlock>(settledPill, "ChatSubagentsPillCount").Text);
                Assert.Empty(FindDescendants<ProgressRing>(settledPill));
                Assert.Equal("Subagents: 0 running, 2 finished", AutomationProperties.GetName(settledPill));
                Assert.Equal(Draft, composer!.Text);
                Assert.Single(session.ViewModel.PendingAttachments);
            });
            Assert.Empty(provider.SentMessages);
        });
    }

    [Fact]
    public async Task Subagents_RemovedChildrenAndDisconnectWithdrawTheList()
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
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatSubagentsPill")));
            await SettleAsync();
            await ui.RunOnUIAsync(() => provider.Publish(snapshot));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Contains(FindDescendants<TextBlock>(FindControl<Border>(surface, "ChatSubagentsPane")),
                    text => text.Text == "No subagents in the current session list.");
                Assert.DoesNotContain(FindDescendants<Button>(surface),
                    button => AutomationProperties.GetAutomationId(button) == "ChatSubagentsPill");
                provider.Publish(children with
                {
                    Threads = [snapshot.Threads[0], new ChatThread
                    {
                        Id = "late", Title = "Late start", ParentSessionKey = "proof", IsSubagent = true,
                        Activity = ChatActivity.Working, CreatedAt = DateTimeOffset.UtcNow,
                    }],
                });
            });
            // A run that starts after the open list mounted still counts up.
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    FindDescendants<Grid>(surface).SingleOrDefault(row =>
                        AutomationProperties.GetAutomationId(row) == "ChatSubagent_late") is { } row &&
                    AutomationProperties.GetName(row).StartsWith("Late start, Working, ", StringComparison.Ordinal),
                "elapsed time for a subagent started while the list is open"));
            await ui.RunOnUIAsync(() =>
            {
                provider.Publish(children with { ConnectionStatus = "Disconnected" });
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Collapsed, FindControl<Grid>(surface, "ConversationPanel").Visibility);
                Assert.DoesNotContain(FindDescendants<Button>(surface),
                    button => AutomationProperties.GetAutomationId(button) == "ChatSubagentsPill");
                provider.Publish(children);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Collapsed, FindControl<Grid>(surface, "ConversationPanel").Visibility);
                Invoke(FindControl<Button>(surface, "ChatSubagentsPill"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.True(session.Controller.TrySelectSession("child")));
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Equal(Visibility.Collapsed,
                FindControl<Grid>(surface, "ConversationPanel").Visibility));
        });
    }

    [Fact]
    public async Task Subagents_ListsProviderChildrenWithoutLoadingOrSelectingThem()
    {
        var parent = new ChatHistoryInfo
        {
            SessionKey = "proof", SessionId = "parent-instance",
            Messages = [new ChatMessageInfo { Role = "assistant", Text = "Parent history remains here.", SessionKey = "proof" }],
        };
        const string childKey = "agent:main:subagent:proof";
        using var directory = new TempDirectory();
        using var bridge = new ConversationParityBridge(parent,
            new SessionInfo { Key = "proof", IsMain = true, Label = "Parent" })
        {
            Children = [new SessionInfo
            {
                Key = childKey, ParentSessionKey = "proof", Label = "Research",
                HasActiveRun = true, Status = "running", CurrentActivity = "Searching the docs",
            }],
        };
        await using var source = new OpenClawChatDataProvider(bridge, null,
            directory.Combine("tools.json"), directory.Combine("attachments.json"), directory.Combine("last-state.json"));
        await source.LoadHistoryAsync("proof");
        await WithChatAsync(1100, async (surface, _, session, _) =>
        {
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindDescendants<Button>(surface)
                    .Any(button => AutomationProperties.GetAutomationId(button) == "ChatSubagentsPill"),
                "subagents pill from the production provider"));
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatSubagentsPill")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var row = FindControl<Grid>(surface, $"ChatSubagent_{childKey}");
                Assert.StartsWith("Research, Working · Searching the docs", AutomationProperties.GetName(row));
                Assert.Equal("proof", session.ViewModel.Inputs!.CurrentThread.Id);
                Assert.Equal(Draft, FindControl<TextBox>(surface, "ChatComposerInput").Text);
            });
            Assert.DoesNotContain(childKey, bridge.HistoryRequests);
            await CaptureAsync(surface, "Subagents-provider-list");
        }, initialThreadId: "proof", renderProvider: source);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public async Task Subagents_SharesTabsWithToolDetailsAndReturnsFocusToThePill(int width)
    {
        await WithChatAsync(width, async (surface, _, session, provider) =>
        {
            await PublishPanelToolAsync(provider, "Retained tool result");
            var snapshot = await provider.LoadAsync();
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Threads = [snapshot.Threads[0],
                    new ChatThread { Id = "child", Title = "Research", ParentSessionKey = "proof", IsSubagent = true,
                        Activity = ChatActivity.Working }],
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
                var opener = FindControl<Button>(surface, "ChatSubagentsPill");
                Assert.True(opener.Focus(FocusState.Programmatic));
                Invoke(opener);
            });
            await SettleAsync();
            ScrollViewer? list = null;
            await ui.RunOnUIAsync(() =>
            {
                var tabs = FindControl<TabView>(surface, "ConversationPanelTabs");
                Assert.Equal(2, tabs.TabItems.Count);
                Assert.Equal(1, tabs.SelectedIndex);
                Assert.Equal("Subagents", Assert.IsType<TabViewItem>(tabs.TabItems[1]).Header);
                Assert.Same(tabs.SelectedItem, FocusManager.GetFocusedElement(surface.XamlRoot));
                list = FindControl<ScrollViewer>(surface, "ChatSubagentsList");
                tabs.SelectedIndex = 0;
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
                Assert.Same(list, FindControl<ScrollViewer>(surface, "ChatSubagentsList"));
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
                Assert.Same(FindControl<Button>(surface, "ChatSubagentsPill"),
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
