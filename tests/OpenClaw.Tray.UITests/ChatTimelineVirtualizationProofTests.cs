using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Hosting;
using Windows.Graphics;
using static OpenClawTray.FunctionalUI.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ChatTimelineVirtualizationProofTests
{
    private const int InitialRows = 240;

    private readonly UIThreadFixture _ui;

    public ChatTimelineVirtualizationProofTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task LargeNativeChatTimeline_VirtualizesRowsAndFollowsNewMessages()
    {
        await _ui.ResetContainerAsync();

        var props = BuildProps(InitialRows, scrollToBottomToken: 0);
        FunctionalHostControl? host = null;

        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            _ui.TestWindow.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 960, 720));
            _ui.Container.Width = 900;
            _ui.Container.Height = 640;

            host = new FunctionalHostControl
            {
                Width = 860,
                Height = 560,
                SuppressAutoDispose = true,
            };
            _ui.Container.Children.Add(host);
            host.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var repeater = FindLogical<ItemsRepeater>(host!).Single();
            var layout = Assert.IsType<StackLayout>(repeater.Layout);
            Assert.Equal(Orientation.Vertical, layout.Orientation);
            Assert.Equal(2, layout.Spacing);
            Assert.Equal(InitialRows, CountItems(repeater.ItemsSource));

            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            Assert.True(scrollViewer.ScrollableHeight > 0, "large chat timeline should overflow and become scrollable");

            scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight, null, disableAnimation: true);
            _ui.Container.UpdateLayout();
            Assert.True(scrollViewer.VerticalOffset > 0, "large chat timeline should scroll away from the top");
        });

        await DrainRenderQueueAsync();

        var appendedRows = InitialRows + 1;
        props = BuildProps(appendedRows, scrollToBottomToken: 1);
        await _ui.RunOnUIAsync(() =>
        {
            host!.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var repeater = FindLogical<ItemsRepeater>(host!).Single();
            Assert.Equal(appendedRows, CountItems(repeater.ItemsSource));

            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            Assert.True(
                scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset <= 4,
                $"chat follow should stay near the newest row; offset={scrollViewer.VerticalOffset:0.0}, height={scrollViewer.ScrollableHeight:0.0}");

            var visibleText = FindDescendants<TextBlock>(host!)
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
            Assert.Contains("User proof row 241", visibleText);

            Console.WriteLine(
                "CHAT_TIMELINE_VIRTUALIZATION_PROOF " +
                $"rows={appendedRows} " +
                $"itemsRepeater={repeater.GetType().Name} " +
                $"layout={((StackLayout)repeater.Layout).Orientation} " +
                $"scrollableHeight={scrollViewer.ScrollableHeight:0.0} " +
                $"verticalOffset={scrollViewer.VerticalOffset:0.0} " +
                "newestRowVisible=true");
        });

        await _ui.RunOnUIAsync(() => host!.Dispose());
    }

    private async Task DrainRenderQueueAsync()
    {
        await _ui.RunOnUIAsync(() => { });
        await Task.Delay(50);
        await _ui.RunOnUIAsync(() => _ui.Container.UpdateLayout());
        await _ui.RunOnUIAsync(() => { });
    }

    private static OpenClawChatTimelineProps BuildProps(int rows, int scrollToBottomToken) =>
        new(
            SessionId: "ui-proof-large-chat",
            Entries: BuildEntries(rows),
            HasMoreHistory: false,
            OnLoadMoreHistory: null,
            UserSenderLabel: "UI proof user",
            AssistantSenderLabel: "UI proof assistant",
            DefaultModel: "proof-model",
            ShowToolCalls: true,
            ScrollToBottomToken: scrollToBottomToken);

    private static IReadOnlyList<ChatTimelineItem> BuildEntries(int rows)
    {
        var entries = new List<ChatTimelineItem>(rows);
        for (var i = 1; i <= rows; i++)
        {
            var isUser = i % 2 == 1;
            entries.Add(new ChatTimelineItem(
                Id: $"proof-{i:000}",
                Kind: isUser ? ChatTimelineItemKind.User : ChatTimelineItemKind.Assistant,
                Text: isUser
                    ? $"User proof row {i}"
                    : $"Assistant proof row {i}"));
        }

        return entries;
    }

    private static int CountItems(object? itemsSource) =>
        itemsSource is System.Collections.IEnumerable enumerable
            ? enumerable.Cast<object>().Count()
            : 0;
}
