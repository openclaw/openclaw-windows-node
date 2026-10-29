using System.Collections.Immutable;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Dashboards;

[GenerateReactorWrapper(typeof(BoardWidgetWebHost), AutoDiscover = false, RegisterAssembly = false)]
internal partial record BoardWidgetWebHostElement;

[GenerateReactorWrapper(typeof(BoardWebsiteHost), AutoDiscover = false, RegisterAssembly = false)]
internal partial record BoardWebsiteHostElement;

[GenerateReactorWrapper(typeof(InertPreviewHost), AutoDiscover = false, RegisterAssembly = false)]
internal partial record InertPreviewHostElement;

internal sealed record DashboardPreviewProps(
    IBoardGatewayClient Client,
    BoardSessionTarget Target,
    DashboardsPageState State,
    int ConnectionRevision,
    int Hue = DashboardHues.Blue,
    string Title = "");

/// <summary>
/// Passive live miniature of a dashboard's first tab for a gallery card. It
/// is mounted only while its card is realized near the viewport, shows only
/// saved HTML and data-only widgets without host bridge authority, and runs
/// live HTML only while it holds a <see cref="BoardPreviewSlots"/> slot.
/// </summary>
internal sealed class ReactorDashboardPreview : Component<DashboardPreviewProps>
{
    /// <summary>Board width the miniature represents.</summary>
    private const double ReferenceWidth = 1200;

    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState(BoardDocumentSnapshot.Initial, threadSafe: true);
        var (width, setWidth) = UseState(0d);
        var (slots, setSlots) = UseState(ImmutableHashSet<string>.Empty, threadSafe: true);

        UseEffect((Func<Action>)(() =>
        {
            var model = new BoardDocumentModel(props.Client, props.Target, props.State.Post, props.State.Schedule);
            model.Changed += next => setSnapshot(next);
            _ = model.LoadAsync();
            return model.Dispose;
        }), props.Client, props.Target, props.ConnectionRevision);

        var tiles = PreviewTiles(snapshot);
        var htmlKeys = tiles.Where(tile => tile.Widget.ContentKind == BoardWidgetContentKind.Html)
            .Select(tile => tile.Widget.Name).ToImmutableArray();
        var slotKey = string.Join('\0', htmlKeys);

        UseEffect((Func<Action>)(() =>
        {
            var lease = new BoardPreviewSlotLease(BoardPreviewSlots.Shared, htmlKeys, props.State.Post, granted => setSlots(granted));
            return lease.Dispose;
        }), slotKey);

        Element content = snapshot.Status switch
        {
            DashboardLoadStatus.Ready when snapshot.Board is not null && tiles.IsEmpty =>
                VStack(8,
                        DashboardsArt.Monogram(props.Title, props.Hue, 52).HAlign(HorizontalAlignment.Center),
                        TextBlock(LocalizationHelper.GetString("Dashboards_PreviewEmpty")).FontSize(12)
                            .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                            .HAlign(HorizontalAlignment.Center))
                    .Center(),
            DashboardLoadStatus.Ready when snapshot.Board is not null && width > 0 =>
                Miniature(snapshot, tiles, slots, width),
            DashboardLoadStatus.Error => TextBlock(LocalizationHelper.Format("Dashboards_PreviewErrorFormat", snapshot.Error ?? ""))
                .FontSize(12).Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextWrapping(TextWrapping.Wrap).MaxLines(3).Margin(12).VAlign(VerticalAlignment.Center),
            _ => Empty(),
        };

        return Border(content)
            .AccessibilityView(AccessibilityView.Raw)
            .OnSizeChanged((_, args) =>
            {
                if (Math.Abs(args.NewSize.Width - width) > 1)
                    setWidth(args.NewSize.Width);
            })
            .Set(border => border.IsHitTestVisible = false);
    }

    private sealed record PreviewTile(BoardWidget Widget, BoardGridRect Rect);

    private static ImmutableArray<PreviewTile> PreviewTiles(BoardDocumentSnapshot snapshot)
    {
        if (snapshot.Board is not { } board || snapshot.ActiveTabId.Length == 0)
            return [];
        var widgets = board.WidgetsForTab(snapshot.ActiveTabId)
            .Where(widget => BoardWidgetBodyPolicy.IsPreviewSafe(widget, snapshot.PluginWidgetKinds) &&
                (widget.ContentKind != BoardWidgetContentKind.Html ||
                 BoardWidgetBodyPolicy.Resolve(widget, snapshot.PluginWidgetKinds) == BoardWidgetBodyKind.Frame))
            .ToImmutableArray();
        var rects = BoardGridLayout.Layout(widgets.Select(widget =>
            new BoardGridItem(widget.Name, widget.SizeW, widget.SizeH, widget.Position)));
        var byName = widgets.ToDictionary(widget => widget.Name, StringComparer.Ordinal);
        return [.. rects.Select(rect => new PreviewTile(byName[rect.Name], rect))];
    }

    private static Element Miniature(
        BoardDocumentSnapshot snapshot,
        ImmutableArray<PreviewTile> tiles,
        ImmutableHashSet<string> slots,
        double width)
    {
        var scale = Math.Min(1, width / ReferenceWidth);
        var rowCount = Math.Max(1, BoardGridLayout.RowCount(tiles.Select(tile => tile.Rect)));
        var children = tiles.Select(tile =>
        {
            Element body = tile.Widget.ContentKind == BoardWidgetContentKind.Html
                ? slots.Contains(tile.Widget.Name) && snapshot.GatewayOrigin is { } origin
                    ? BoardWidgetWebHostElement.BoardWidgetWebHost()
                        .Set(host => host.Update(new BoardWidgetWebHostRequest(tile.Widget, origin, scale)))
                    : Empty()
                : Viewbox(ReactorBoardWidgets.Report(tile.Widget).Width(Math.Max(1, tile.Rect.W * (ReferenceWidth / BoardGridLayout.Columns))))
                    .Set(viewbox => viewbox.Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform)
                    .VAlign(VerticalAlignment.Top);
            return Border(body)
                .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
                .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
                .BorderThickness(1)
                .CornerRadius(Math.Max(2, 8 * scale))
                .Grid(row: tile.Rect.Y, column: tile.Rect.X, rowSpan: tile.Rect.H, columnSpan: tile.Rect.W)
                .WithKey(tile.Widget.Name);
        }).ToArray();

        return Grid(
                Enumerable.Repeat(GridSize.Star(), BoardGridLayout.Columns).ToArray(),
                Enumerable.Repeat(GridSize.Px(BoardGridLayout.RowHeight * scale), rowCount).ToArray(),
                children)
            .Set(grid =>
            {
                grid.ColumnSpacing = BoardGridLayout.Gap * scale;
                grid.RowSpacing = BoardGridLayout.Gap * scale;
            })
            .Margin(0)
            .VAlign(VerticalAlignment.Top);
    }
}
