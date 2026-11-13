using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Chat;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    // Covers the TabView item entrance fade before a proof capture; RenderTargetBitmap would otherwise record an empty header.
    private static readonly TimeSpan TabEntranceCaptureDelay = TimeSpan.FromMilliseconds(600);

    [Theory]
    [InlineData(480, SubagentsOnly)]
    [InlineData(1100, SubagentsOnly)]
    [InlineData(1100, DetailsThenSubagents)]
    [InlineData(1100, SubagentsThenDetails)]
    public async Task Subagents_CloseReturnsToTheParentsReadingPosition(int width, string tabs)
    {
        await WithChatAsync(width, async (surface, _, _, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Threads = [snapshot.Threads[0],
                    new ChatThread { Id = "child", Title = "Child", ParentSessionKey = "proof", IsSubagent = true,
                        Activity = ChatActivity.Working }],
                Timelines = new Dictionary<string, ChatTimelineState>
                {
                    ["proof"] = ChatTimelineState.Initial() with
                    {
                        HistoryLoaded = true,
                        Entries = (tabs != SubagentsOnly
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
            ItemsView? timeline = null;
            Button? reader = null;
            await ui.RunOnUIAsync(() =>
            {
                timeline = Assert.Single(FindDescendants<ItemsView>(surface));
                scroll = timeline.ScrollView;
                var repeater = Assert.Single(FindDescendants<ItemsRepeater>(scroll));
                reader = Enumerable.Range(0, repeater.ItemsSourceView.Count)
                    .Select(index => repeater.TryGetElement(index)).OfType<ItemContainer>()
                    .SelectMany(row => FindDescendants<Button>(row))
                    .First(button => button.IsLoaded && button.IsEnabled && button.IsTabStop &&
                        AutomationProperties.GetAutomationId(button).StartsWith("ChatCopy_", StringComparison.Ordinal));
                Assert.True(FindControl<TextBox>(surface, "ChatComposerInput").Focus(FocusState.Programmatic));
                Assert.True(reader.Focus(FocusState.Keyboard));
            });
            // The keyboard-focused row becomes ItemsView's current item, which it may later re-reveal.
            // Its focus scroll must also finish before the reader moves elsewhere.
            await UntilAsync(Steady(() => timeline!.CurrentItemIndex >= 0 && reader!.FocusState == FocusState.Keyboard, scroll!),
                "reader row focused, current and its focus scroll settled");
            if (tabs == DetailsThenSubagents)
            {
                // Two open tabs: closing the selected Subagents tab must not drop focus into the transcript.
                await ui.RunOnUIAsync(() => scroll!.ScrollTo(0, 0,
                    new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore)));
                await UntilAsync(Steady(() => scroll!.VerticalOffset <= 1, scroll!), "reader at the first message");
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
            var subagentsIndex = tabs == DetailsThenSubagents ? 1 : 0;
            // Closing from the tab strip: keyboard focus is on the tab being closed.
            await UntilAsync(() => FocusTab(surface, subagentsIndex + 1, subagentsIndex), "Subagents tab selected and focused");
            if (tabs == SubagentsThenDetails)
            {
                // Closing the selected first tab must leave focus on the remaining tab. Details opens
                // without keyboard focus, which ItemsView would redirect to the reader's earlier row.
                await ExpandPanelToolAsync(surface, "one");
                await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatToolDetails_one")));
                await UntilAsync(() => TabsSelected(surface, 2, 1), "Tool details tab opened");
                await ui.RunOnUIAsync(() => FindControl<TabView>(surface, "ConversationPanelTabs").SelectedIndex = 0);
                await UntilAsync(() => FocusTab(surface, 2, 0), "Subagents tab reselected and focused");
                await ui.RunOnUIAsync(() =>
                {
                    var subagentsTab = Assert.IsType<TabViewItem>(
                        FindControl<TabView>(surface, "ConversationPanelTabs").TabItems[0]);
                    Assert.Equal("Subagents", subagentsTab.Header);
                    Assert.InRange(scroll!.VerticalOffset, 0, 1);
                });
            }
            await ui.RunOnUIAsync(() => CloseSubagentsTab(surface));
            // Focus returns to the composer (the pill's opener) or to the remaining tab.
            await UntilAsync(() => tabs == SubagentsOnly
                    ? PanelCollapsed(surface) && FocusManager.GetFocusedElement(surface.XamlRoot) is TextBox
                    : SelectedTabFocused(surface, 1, 0),
                "focus restored after the close");
            // test-timing-exception: negative-progress probe. ItemsView's stale reveal lands frames after the close.
            await SettleAsync();
            // The panel close also reflows the transcript width; assert once it has stopped moving.
            await UntilAsync(Steady(() => true, scroll!), "transcript settled after the close");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(scroll, Assert.Single(FindDescendants<ItemsView>(
                    FindControl<Border>(surface, "ChatConversation"))).ScrollView);
                Assert.True(scroll!.ScrollableHeight > 0);
                // Widening the transcript reflows text above the reader by a few pixels; the regression is a
                // jump of many rows back to ItemsView's last current item.
                Assert.InRange(scroll.VerticalOffset, 0, CloseReflowTolerance);
                Assert.Equal(1, FindControl<Button>(surface, "ChatJumpLatest").Opacity);
                if (tabs != SubagentsOnly)
                {
                    var view = FindControl<TabView>(surface, "ConversationPanelTabs");
                    Assert.Same(Assert.Single(view.TabItems), FocusManager.GetFocusedElement(surface.XamlRoot));
                }
            });
        }, interactiveKeyboard: true);
    }

    private const string SubagentsOnly = "subagents";
    private const double CloseReflowTolerance = 16;
    private const string DetailsThenSubagents = "details-then-subagents";
    private const string SubagentsThenDetails = "subagents-then-details";

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
            global::Windows.UI.Color pillBorder = default;
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
            await UntilAsync(() => PillReady(surface), "Subagents pill");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Collapsed, FindControl<Grid>(surface, "ConversationPanel").Visibility);
                var pill = FindControl<Button>(surface, "ChatSubagentsPill");
                Assert.Equal("Subagents: 1 running, 1 finished", AutomationProperties.GetName(pill));
                Assert.Equal("2", FindControl<TextBlock>(pill, "ChatSubagentsPillCount").Text);
                Assert.Single(FindDescendants<ProgressRing>(pill));
                Assert.True(Bounds(pill, surface).Bottom <= Bounds(composer!, surface).Top);
                pillBorder = Assert.IsType<SolidColorBrush>(pill.BorderBrush).Color;
                Invoke(pill);
            });
            await UntilAsync(() => FindOptional<Grid>(surface, "ConversationPanel") is { Visibility: Visibility.Visible } &&
                    FindOptional<Border>(surface, "ChatSubagentsPane") is { } pane &&
                    FindDescendants<ProgressRing>(pane).Any(ring => ring.IsLoaded),
                "Subagents list with its running spinner");
            await ui.RunOnUIAsync(() =>
            {
                // Selecting the tab keeps the pill's neutral border.
                var pill = FindControl<Button>(surface, "ChatSubagentsPill");
                Assert.Equal(pillBorder, Assert.IsType<SolidColorBrush>(pill.BorderBrush).Color);
                var pane = FindControl<Border>(surface, "ChatSubagentsPane");
                var badgeRing = Assert.Single(FindDescendants<ProgressRing>(pane));
                // ProgressRing's default 16-DIP minimum would crop the badge spinner.
                Assert.Equal(ChatVisuals.SubagentBadgeSpinnerSize, badgeRing.ActualWidth, 0.5);
                var avatar = Bounds(Assert.IsType<Grid>(VisualTreeHelper.GetParent(
                    VisualTreeHelper.GetParent(badgeRing))), surface);
                var ring = Bounds(badgeRing, surface);
                Assert.True(ring.Right <= avatar.Right + 0.5 && ring.Bottom <= avatar.Bottom + 0.5);
                var spinners = new List<ProgressRing> { badgeRing };
                if (width >= 652)
                {
                    // The compact layout hides the conversation, and the pill with it, while the tab shows.
                    var pillRing = Assert.Single(FindDescendants<ProgressRing>(pill));
                    Assert.Equal(ChatVisuals.SubagentPillIconSize, pillRing.ActualWidth, 0.5);
                    spinners.Add(pillRing);
                }
                foreach (var spinner in spinners)
                    Assert.InRange(Assert.Single(FindDescendants<AnimatedVisualPlayer>(spinner)).PlaybackRate, 0.1, 0.9);
                // Working status uses the same secondary text as the group heading, not accent text.
                var heading = FindDescendants<TextBlock>(FindControl<StackPanel>(pane, "ChatSubagentsGroup_Running"))
                    .First(text => text.Text == "Running");
                var status = FindDescendants<TextBlock>(FindControl<Grid>(pane, "ChatSubagent_child"))
                    .Single(text => text.Text.StartsWith("Working", StringComparison.Ordinal));
                Assert.Equal(Assert.IsType<SolidColorBrush>(heading.Foreground).Color,
                    Assert.IsType<SolidColorBrush>(status.Foreground).Color);
            });
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
            await UntilAsync(() => FindOptional<Grid>(surface, "ChatSubagent_child") is { } row &&
                    AutomationProperties.GetName(row).Contains(", Done, ", StringComparison.Ordinal) &&
                    FindOptional<Border>(surface, "ChatConversation") is { } conversation &&
                    conversation.Visibility == (width < 652 ? Visibility.Visible : Visibility.Collapsed),
                "settled subagent at the new width");
            await ui.RunOnUIAsync(() =>
            {
                var pane = FindControl<Border>(surface, "ChatSubagentsPane");
                Assert.DoesNotContain(FindDescendants<StackPanel>(pane),
                    group => AutomationProperties.GetAutomationId(group) == "ChatSubagentsGroup_Running");
                Assert.Equal("Checking the results, Done, 1m 30s",
                    AutomationProperties.GetName(FindControl<Grid>(pane, "ChatSubagent_child")));
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal(Draft, composer!.Text);
                // While its tab stays open, the pill remains and shows the settled state.
                var settledPill = FindControl<Button>(surface, "ChatSubagentsPill");
                Assert.Equal("2", FindControl<TextBlock>(settledPill, "ChatSubagentsPillCount").Text);
                Assert.Empty(FindDescendants<ProgressRing>(settledPill));
                Assert.Equal("Subagents: 0 running, 2 finished", AutomationProperties.GetName(settledPill));
                CloseSubagentsTab(surface);
            });
            await UntilAsync(() => PanelCollapsed(surface) &&
                    FindOptional<Button>(surface, "ChatSubagentsPill") is null,
                "closed tab with no running subagents");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Collapsed, FindControl<Grid>(surface, "ConversationPanel").Visibility);
                Assert.Equal(Visibility.Visible, FindControl<Border>(surface, "ChatConversation").Visibility);
                // With nothing running and the tab closed, finished subagents leave no pill behind.
                Assert.DoesNotContain(FindDescendants<Button>(surface),
                    button => AutomationProperties.GetAutomationId(button) == "ChatSubagentsPill");
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
                    new ChatThread { Id = "child", Title = "Child", ParentSessionKey = "proof", IsSubagent = true,
                        Activity = ChatActivity.Working }],
            };
            await ui.RunOnUIAsync(() => provider.Publish(children));
            await UntilAsync(() => PillReady(surface), "Subagents pill");
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatSubagentsPill")));
            await UntilAsync(() => FindOptional<Grid>(surface, "ConversationPanel") is { Visibility: Visibility.Visible },
                "Subagents tab");
            await ui.RunOnUIAsync(() => provider.Publish(snapshot));
            await UntilAsync(() => FindOptional<Border>(surface, "ChatSubagentsPane") is { } pane &&
                    FindDescendants<TextBlock>(pane).Any(text => text.Text == "No subagents in the current session list.") &&
                    FindOptional<Button>(surface, "ChatSubagentsPill") is null,
                "empty list after the children were removed");
            await ui.RunOnUIAsync(() =>
            {
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
            await UntilAsync(() => PanelCollapsed(surface) && FindOptional<Button>(surface, "ChatSubagentsPill") is null,
                "disconnect withdrawing the tab and pill");
            await ui.RunOnUIAsync(() => provider.Publish(children));
            await UntilAsync(() => PillReady(surface), "pill after reconnect");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Collapsed, FindControl<Grid>(surface, "ConversationPanel").Visibility);
                Invoke(FindControl<Button>(surface, "ChatSubagentsPill"));
            });
            await UntilAsync(() => FindOptional<Grid>(surface, "ConversationPanel") is { Visibility: Visibility.Visible },
                "Subagents tab after reconnect");
            await ui.RunOnUIAsync(() => Assert.True(session.Controller.TrySelectSession("child")));
            await UntilAsync(() => PanelCollapsed(surface), "parent selection change closing the tab");
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
            await UntilAsync(() => FindOptional<Grid>(surface, $"ChatSubagent_{childKey}") is not null,
                "provider child row");
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

    [Fact]
    public async Task Files_PillOpensTheSessionFileListBesideSubagents()
    {
        var parent = new ChatHistoryInfo
        {
            SessionKey = "proof", SessionId = "parent-instance",
            Messages = [new ChatMessageInfo { Role = "assistant", Text = "Updated the parser and added tests.", SessionKey = "proof" }],
        };
        using var directory = new TempDirectory();
        using var bridge = new ConversationParityBridge(parent,
            new SessionInfo { Key = "proof", IsMain = true, Label = "Parent" })
        {
            Children = [new SessionInfo
            {
                Key = "agent:main:subagent:proof", ParentSessionKey = "proof", Label = "Research",
                HasActiveRun = true, Status = "running", CurrentActivity = "Searching the docs",
            }],
            Files = new SessionFileList
            {
                Key = "proof", Root = @"C:\repo\openclaw",
                Files =
                [
                    new SessionFileEntry { Path = "src/Parser.cs", Kind = "modified", Size = 18_432 },
                    new SessionFileEntry { Path = "tests/ParserTests.cs", Kind = "modified", Size = 6_120 },
                    new SessionFileEntry { Path = "README.md", Kind = "read", Size = 2_048 },
                    new SessionFileEntry { Path = "docs/old-notes.md", Kind = "read", Missing = true },
                ],
                // The Gateway also lists the workspace root; untouched entries are not session files.
                Browser = new SessionFileBrowser
                {
                    Path = "",
                    Entries =
                    [
                        new SessionFileBrowserEntry { Path = "package.json", Name = "package.json", Kind = "file" },
                        new SessionFileBrowserEntry { Path = "src", Name = "src", Kind = "directory" },
                    ],
                },
            },
        };
        await using var source = new OpenClawChatDataProvider(bridge, null,
            directory.Combine("tools.json"), directory.Combine("attachments.json"), directory.Combine("last-state.json"));
        await source.LoadHistoryAsync("proof");
        await WithChatAsync(1100, async (surface, _, _, _) =>
        {
            await UntilAsync(() => FindOptional<Button>(surface, "ChatFilesPill") is { IsLoaded: true } &&
                    FindOptional<Button>(surface, "ChatSubagentsPill") is { IsLoaded: true },
                "Files and Subagents pills");
            await ui.RunOnUIAsync(() =>
            {
                var files = FindControl<Button>(surface, "ChatFilesPill");
                var subagents = FindControl<Button>(surface, "ChatSubagentsPill");
                Assert.Equal("Session files: 4", AutomationProperties.GetName(files));
                Assert.Equal("4", FindControl<TextBlock>(files, "ChatFilesPillCount").Text);
                Assert.Equal(Bounds(subagents, surface).Top, Bounds(files, surface).Top, 0.5);
                Assert.True(Bounds(subagents, surface).Right <= Bounds(files, surface).Left);
            });
            await CaptureAsync(surface, "Files-pills");
            await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatFilesPill")));
            await UntilAsync(() => FindOptional<Grid>(surface, "ConversationPanel") is { Visibility: Visibility.Visible } &&
                    FindOptional<Border>(surface, "ChatFilesPane") is { IsLoaded: true } pane &&
                    FindOptional<TextBlock>(pane, "ChatFilesRoot") is { IsLoaded: true } &&
                    FindDescendants<Grid>(pane).Count(row => AutomationProperties.GetName(row).Contains(", ")) == 4 &&
                    PanelTabs(surface) is { TabItems: [TabViewItem { IsLoaded: true, Header: "Files" }] },
                "Files tab with its rows");
            await ui.RunOnUIAsync(() =>
            {
                var pane = FindControl<Border>(surface, "ChatFilesPane");
                Assert.Equal(@"C:\repo\openclaw", FindControl<TextBlock>(pane, "ChatFilesRoot").Text);
                Assert.StartsWith("Parser.cs, Edited, src", AutomationProperties.GetName(
                    FindDescendants<Grid>(pane).First(row => AutomationProperties.GetName(row).StartsWith("Parser.cs"))));
                Assert.Contains(FindDescendants<Grid>(pane),
                    row => AutomationProperties.GetName(row).StartsWith("old-notes.md, Missing"));
                Assert.DoesNotContain(FindDescendants<Grid>(pane),
                    row => AutomationProperties.GetName(row).StartsWith("package.json"));
            });
            if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") == "1")
                await Task.Delay(TabEntranceCaptureDelay); // test-timing-exception: capture-only; the TabView item fade is a composition animation with no observable completion
            await CaptureAsync(surface, "Files-tab");
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
            await UntilAsync(() => PillReady(surface), "Subagents pill");
            await OpenPanelToolAsync(surface);
            if (width < 652)
            {
                await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ConversationPanelToggle")));
                await UntilAsync(() => FindOptional<Border>(surface, "ChatConversation") is { Visibility: Visibility.Visible },
                    "conversation shown again in the compact layout");
            }
            TextBox? composer = null;
            await ui.RunOnUIAsync(() =>
            {
                composer = FindControl<TextBox>(surface, "ChatComposerInput");
                var opener = FindControl<Button>(surface, "ChatSubagentsPill");
                Assert.True(opener.Focus(FocusState.Programmatic));
                Invoke(opener);
            });
            await UntilAsync(() => SelectedTabFocused(surface, 2, 1), "Subagents tab beside Tool details");
            ScrollViewer? list = null;
            await ui.RunOnUIAsync(() =>
            {
                var tabs = FindControl<TabView>(surface, "ConversationPanelTabs");
                Assert.Equal("Subagents", Assert.IsType<TabViewItem>(tabs.TabItems[1]).Header);
                list = FindControl<ScrollViewer>(surface, "ChatSubagentsList");
                tabs.SelectedIndex = 0;
            });
            await UntilAsync(() => IsShown(FindOptional<ScrollViewer>(surface, "ConversationToolDetailsReader")),
                "Tool details tab body");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Contains(FindDescendants<RichTextBlock>(
                    FindControl<ScrollViewer>(surface, "ConversationToolDetailsReader")),
                    text => CollectText(text).Contains("Retained tool result", StringComparison.Ordinal));
                FindControl<TabView>(surface, "ConversationPanelTabs").SelectedIndex = 1;
            });
            await UntilAsync(() => IsShown(FindOptional<ScrollViewer>(surface, "ChatSubagentsList")),
                "Subagents tab body");
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
            await UntilAsync(() => PanelTabs(surface) is { TabItems.Count: 1 }, "Tool details tab closed");
            await ui.RunOnUIAsync(() => CloseSubagentsTab(surface));
            await UntilAsync(() => PanelTabs(surface) is { TabItems.Count: 0 } &&
                    FindOptional<Button>(surface, "ChatSubagentsPill") is { } pill &&
                    ReferenceEquals(pill, FocusManager.GetFocusedElement(surface.XamlRoot)),
                "focus returned to the pill after the last tab closed");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(Visibility.Visible, FindControl<Border>(surface, "ChatConversation").Visibility);
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
            });
        });
    }

    private static void CloseSubagentsTab(DependencyObject surface)
    {
        var tab = FindControl<TabView>(surface, "ConversationPanelTabs").TabItems
            .OfType<TabViewItem>().Single(item => Equals(item.Header, "Subagents"));
        Invoke(FindDescendants<Button>(tab).Single(button => button.Name == "CloseButton"));
    }

    /// <summary>Waits on the UI thread until <paramref name="condition"/> holds after a rendered frame.</summary>
    private Task UntilAsync(Func<bool> condition, string operation) =>
        ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(condition, operation));

    private static T? FindOptional<T>(DependencyObject root, string id) where T : FrameworkElement =>
        FindDescendants<T>(root).SingleOrDefault(control => AutomationProperties.GetAutomationId(control) == id);

    private static TabView? PanelTabs(DependencyObject root) => FindOptional<TabView>(root, "ConversationPanelTabs");

    private static bool PanelCollapsed(DependencyObject root) =>
        FindOptional<Grid>(root, "ConversationPanel") is { Visibility: Visibility.Collapsed };

    /// <summary>
    /// Holds once <paramref name="condition"/> is true and the transcript has been idle at the same
    /// offset for several consecutive checks, so a queued focus or tail scroll cannot still move it.
    /// </summary>
    private static Func<bool> Steady(Func<bool> condition, ScrollView scroll)
    {
        const int RequiredSamples = 3;
        double? previous = null;
        var samples = 0;
        return () =>
        {
            var offset = scroll.VerticalOffset;
            samples = condition() && scroll.State == ScrollingInteractionState.Idle && previous == offset
                ? samples + 1 : 0;
            previous = offset;
            return samples >= RequiredSamples;
        };
    }

    private static bool PillReady(FrameworkElement surface) =>
        FindOptional<Button>(surface, "ChatSubagentsPill") is { IsLoaded: true } pill &&
        FindDescendants<TextBlock>(pill).Any(text => AutomationProperties.GetAutomationId(text) == "ChatSubagentsPillCount");

    private static bool IsShown(UIElement? element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
        return element is FrameworkElement { IsLoaded: true };
    }

    private static bool TabsSelected(FrameworkElement surface, int count, int selected) =>
        PanelTabs(surface) is { } view && view.TabItems.Count == count && view.SelectedIndex == selected;

    /// <summary>Once the selected tab item has loaded, gives it keyboard focus; holds when it has focus.</summary>
    private static bool FocusTab(FrameworkElement surface, int count, int selected) =>
        TabsSelected(surface, count, selected) && PanelTabs(surface)!.TabItems[selected] is TabViewItem { IsLoaded: true } tab &&
        (ReferenceEquals(tab, FocusManager.GetFocusedElement(surface.XamlRoot)) || tab.Focus(FocusState.Keyboard));

    /// <summary>The tab strip shows <paramref name="count"/> tabs and keyboard focus is on the selected one.</summary>
    private static bool SelectedTabFocused(FrameworkElement surface, int count, int selected) =>
        TabsSelected(surface, count, selected) && PanelTabs(surface) is { } view &&
        ReferenceEquals(view.TabItems[selected], FocusManager.GetFocusedElement(surface.XamlRoot));
}
