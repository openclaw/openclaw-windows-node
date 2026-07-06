using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
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
        var initialCachedVirtualControls = 0;

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
            var realizedRows = Enumerable.Range(0, InitialRows)
                .Count(index => repeater.TryGetElement(index) is not null);
            Assert.InRange(realizedRows, 1, InitialRows - 1);
            initialCachedVirtualControls = host!.CachedVirtualStackControlCount;
            Assert.True(initialCachedVirtualControls > 0);

            var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
            _ui.Container.UpdateLayout();
            Assert.True(scrollViewer.ScrollableHeight > 0, "large chat timeline should overflow and become scrollable");
        });

        foreach (var fraction in new[] { 0.25, 0.5, 0.75, 1.0 })
        {
            await _ui.RunOnUIAsync(() =>
            {
                var scrollViewer = FindLogical<ScrollViewer>(host!).Single();
                scrollViewer.ChangeView(
                    null,
                    scrollViewer.ScrollableHeight * fraction,
                    null,
                    disableAnimation: true);
                _ui.Container.UpdateLayout();
            });
            await DrainRenderQueueAsync();
        }

        object? stableItemsSource = null;
        object? stableItemTemplate = null;
        props = BuildProps(InitialRows, scrollToBottomToken: 0, textRevision: 1);
        await _ui.RunOnUIAsync(() =>
        {
            var repeater = FindLogical<ItemsRepeater>(host!).Single();
            Assert.Null(repeater.TryGetElement(0));
            Assert.NotNull(repeater.TryGetElement(InitialRows - 1));
            Assert.InRange(
                host!.CachedVirtualStackControlCount,
                1,
                initialCachedVirtualControls * 2);
            stableItemsSource = repeater.ItemsSource;
            stableItemTemplate = repeater.ItemTemplate;

            host!.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var repeater = FindLogical<ItemsRepeater>(host!).Single();
            Assert.NotNull(stableItemsSource);
            Assert.NotNull(stableItemTemplate);
            Assert.Same(stableItemsSource, repeater.ItemsSource);
            Assert.Same(stableItemTemplate, repeater.ItemTemplate);

            var visibleText = FindDescendants<TextBlock>(host!)
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
            Assert.Contains(visibleText, text => text.Contains("revision 1", StringComparison.Ordinal));
        });

        var appendedRows = InitialRows + 1;
        props = BuildProps(appendedRows, scrollToBottomToken: 1, textRevision: 1);
        await _ui.RunOnUIAsync(() =>
        {
            host!.Mount(_ => Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(props));
        });

        await DrainRenderQueueAsync();
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
            Assert.Contains(visibleText, text => text.Contains("User proof row 241", StringComparison.Ordinal));

            Console.WriteLine(
                "CHAT_TIMELINE_VIRTUALIZATION_PROOF " +
                $"rows={appendedRows} " +
                $"itemsRepeater={repeater.GetType().Name} " +
                $"layout={((StackLayout)repeater.Layout).Orientation} " +
                $"scrollableHeight={scrollViewer.ScrollableHeight:0.0} " +
                $"verticalOffset={scrollViewer.VerticalOffset:0.0} " +
                "variableHeight=true " +
                "renderChurnStable=true " +
                "newestRowVisible=true");
        });

        await _ui.RunOnUIAsync(() => host!.Dispose());
    }

    [Fact]
    public async Task RealizedComponentRow_ReplacesRootAndPrunesRemovedEffects()
    {
        await _ui.ResetContainerAsync();
        DisposableVirtualRow.CleanupCount = 0;

        FunctionalHostControl? host = null;
        ItemsRepeater? repeater = null;
        UIElement? stableContainer = null;
        var expandedCacheCount = 0;
        var cleanupCountBeforeCollapse = 0;

        await _ui.RunOnUIAsync(() =>
        {
            host = new FunctionalHostControl { Width = 600, Height = 400, SuppressAutoDispose = true };
            _ui.Container.Children.Add(host);
            host.Mount(_ => VirtualVStack(
                0,
                Component<SwappingVirtualRow, SwappingVirtualRowProps>(new SwappingVirtualRowProps(true))));
        });
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            repeater = FindLogical<ItemsRepeater>(host!).Single();
            stableContainer = repeater.TryGetElement(0);
            Assert.IsType<Border>(stableContainer);
            Assert.Contains(FindDescendants<TextBlock>(host!), text => text.Text == "expanded row");
            Assert.Contains(FindDescendants<TextBlock>(host!), text => text.Text == "disposable child");
            expandedCacheCount = host!.CachedVirtualStackControlCount;
            Assert.True(expandedCacheCount > 1);
            cleanupCountBeforeCollapse = DisposableVirtualRow.CleanupCount;

            host.Mount(_ => VirtualVStack(
                0,
                Component<SwappingVirtualRow, SwappingVirtualRowProps>(new SwappingVirtualRowProps(false))));
        });
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            Assert.Same(stableContainer, repeater!.TryGetElement(0));
            Assert.Contains(FindDescendants<TextBlock>(host!), text => text.Text == "collapsed row");
            Assert.DoesNotContain(FindDescendants<TextBlock>(host!), text => text.Text == "disposable child");
            Assert.Equal(cleanupCountBeforeCollapse + 1, DisposableVirtualRow.CleanupCount);
            Assert.InRange(host!.CachedVirtualStackControlCount, 1, expandedCacheCount - 1);
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

    private static OpenClawChatTimelineProps BuildProps(int rows, int scrollToBottomToken, int textRevision = 0) =>
        new(
            SessionId: "ui-proof-large-chat",
            Entries: BuildEntries(rows, textRevision),
            HasMoreHistory: false,
            OnLoadMoreHistory: null,
            UserSenderLabel: "UI proof user",
            AssistantSenderLabel: "UI proof assistant",
            DefaultModel: "proof-model",
            ShowToolCalls: true,
            ScrollToBottomToken: scrollToBottomToken);

    private static IReadOnlyList<ChatTimelineItem> BuildEntries(int rows, int textRevision)
    {
        var entries = new List<ChatTimelineItem>(rows);
        for (var i = 1; i <= rows; i++)
        {
            var isUser = i % 2 == 1;
            entries.Add(new ChatTimelineItem(
                Id: $"proof-{i:000}",
                Kind: isUser ? ChatTimelineItemKind.User : ChatTimelineItemKind.Assistant,
                Text: FormatVariableHeightText(isUser ? "User" : "Assistant", i, textRevision)));
        }

        return entries;
    }

    private static string FormatVariableHeightText(string role, int row, int textRevision)
    {
        var detailLines = row % 4;
        var details = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, detailLines).Select(i => $"detail {row}-{i} revision {textRevision}"));
        var heading = $"{role} proof row {row}";
        return string.IsNullOrEmpty(details)
            ? heading
            : heading + Environment.NewLine + details;
    }

    private static int CountItems(object? itemsSource) =>
        itemsSource is System.Collections.IEnumerable enumerable
            ? enumerable.Cast<object>().Count()
            : 0;

    private sealed record SwappingVirtualRowProps(bool Expanded);

    private sealed class SwappingVirtualRow : Component<SwappingVirtualRowProps>
    {
        public override Element Render() => Props.Expanded
            ? VStack(
                0,
                TextBlock("expanded row"),
                Component<DisposableVirtualRow>())
            : TextBlock("collapsed row");
    }

    private sealed class DisposableVirtualRow : Component
    {
        internal static int CleanupCount { get; set; }

        public override Element Render()
        {
            UseEffect((Func<Action>)(() => () => CleanupCount++), Array.Empty<object>());
            return TextBlock("disposable child");
        }
    }
}
