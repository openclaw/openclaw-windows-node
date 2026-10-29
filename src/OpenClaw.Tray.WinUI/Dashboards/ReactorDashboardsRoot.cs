using System.Collections.Immutable;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Dashboards;

internal sealed record DashboardsRootProps(DashboardsPageState State);

/// <summary>
/// Production Reactor root for the Dashboards hub page: the gallery of
/// session dashboards, or one open dashboard.
/// </summary>
internal sealed class ReactorDashboardsRoot : Component<DashboardsRootProps>
{
    public override Element Render()
    {
        var state = Props.State;
        var (_, setVersion) = UseState(0, threadSafe: true);
        var version = UseRef(0);
        UseEffect((Func<Action>)(() =>
        {
            void OnChanged() => state.Post(() => setVersion(++version.Current));
            state.Changed += OnChanged;
            return () => state.Changed -= OnChanged;
        }), state);

        if (state.Client is not { } client || !state.Connected)
            return DashboardsStates.Disconnected(state);

        return state.OpenTarget is { } target
            ? Component<ReactorBoardView, BoardViewProps>(new BoardViewProps(state, client, target, state.ConnectionRevision))
                .WithKey("board:" + target.AgentId + "\0" + target.SessionKey)
            : Component<ReactorDashboardGallery, DashboardGalleryProps>(
                    new DashboardGalleryProps(state, client, state.AgentId, state.ConnectionRevision))
                .WithKey("gallery");
    }
}

internal static class DashboardsGlyphs
{
    public const string Back = "\uE72B";
    public const string Refresh = "\uE72C";
    public const string Search = "\uE721";
    public const string Chat = "\uE8BD";
    public const string Dashboard = "\uECA5";
    public const string Shield = "\uEA18";
    public const string Disconnected = "\uF384";
    public const string Error = "\uEA39";
}

/// <summary>Shared visual building blocks: status panels, glyphs, icon buttons.</summary>
internal static class DashboardsStates
{
    public static TextBlockElement Glyph(string glyph, double size, string brush = "TextFillColorSecondaryBrush") =>
        TextBlock(glyph)
            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
            .FontSize(size)
            .Foreground(Theme.Ref(brush))
            .AccessibilityView(AccessibilityView.Raw);

    public static Element Status(string id, string glyph, string title, string? detail, Element? action = null, bool alert = false, Element? art = null, int hue = DashboardHues.Blue)
    {
        var children = new List<Element>
        {
            (art ?? (alert
                    ? DashboardsArt.GlyphBadge(glyph, "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", 72)
                    : DashboardsArt.HueBadge(glyph, hue, 72)))
                .HAlign(HorizontalAlignment.Center)
                .Margin(0, 0, 0, 4),
            TextBlock(title).FontSize(20).SemiBold()
                .TextWrapping(TextWrapping.Wrap).TextAlignment(TextAlignment.Center)
                .HeadingLevel(AutomationHeadingLevel.Level2)
                .AutomationId(id + "Title"),
        };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            children.Add(TextBlock(detail).FontSize(14)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextWrapping(TextWrapping.Wrap).TextAlignment(TextAlignment.Center)
                .MaxWidth(440)
                .AutomationId(id + "Detail"));
        }
        if (action is not null)
            children.Add(action.HAlign(HorizontalAlignment.Center).Margin(0, 8, 0, 0));
        return VStack(12, children.ToArray())
            .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center)
            .Margin(24, 48)
            .LiveRegion(alert ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite)
            .AutomationId(id);
    }

    public static Element Disconnected(DashboardsPageState state) =>
        Status(
            "DashboardsDisconnected",
            DashboardsGlyphs.Disconnected,
            LocalizationHelper.GetString("Dashboards_Disconnected_Title"),
            LocalizationHelper.GetString("Dashboards_Disconnected_Detail"),
            state.OpenConnection is { } open
                ? Button(LocalizationHelper.GetString("Dashboards_Disconnected_Action"), open).AccentButton()
                    .AutomationName(LocalizationHelper.GetString("Dashboards_Disconnected_Action"))
                    .AutomationId("DashboardsOpenConnection")
                : null,
            art: DashboardsArt.GlyphBadge(DashboardsGlyphs.Disconnected, "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", 72));

    public static Element Unavailable() =>
        Status(
            "DashboardsUnavailable",
            DashboardsGlyphsExtra.Lock,
            LocalizationHelper.GetString("Dashboards_Unavailable_Title"),
            LocalizationHelper.GetString("Dashboards_Unavailable_Detail"),
            hue: DashboardHues.Purple);

    public static ButtonElement IconButton(string glyph, string label, Action onClick, string automationId) =>
        Button(Glyph(glyph, 16, "TextFillColorPrimaryBrush"), onClick)
            .SubtleButton()
            .Size(36, 36)
            .ToolTip(label)
            .AutomationName(label)
            .AutomationId(automationId);
}

internal sealed record DashboardGalleryProps(
    DashboardsPageState State,
    IBoardGatewayClient Client,
    string AgentId,
    int ConnectionRevision);

/// <summary>Gallery of every session dashboard for the selected agent.</summary>
internal sealed class ReactorDashboardGallery : Component<DashboardGalleryProps>
{
    private const double CardMinWidth = 300;
    private const double CardHeight = 316;

    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState(DashboardGallerySnapshot.Initial, threadSafe: true);
        var (query, setQuery) = UseState("");
        var (authorId, setAuthorId) = UseState("");
        var (sort, setSort) = UseState(DashboardGallerySort.Updated);
        var model = UseRef<DashboardGalleryModel?>(null);

        UseEffect((Func<Action>)(() =>
        {
            var gallery = new DashboardGalleryModel(props.Client, props.State.Post);
            model.Current = gallery;
            gallery.Changed += next => setSnapshot(next);
            _ = gallery.LoadAsync(props.AgentId);
            return () =>
            {
                model.Current = null;
                gallery.Dispose();
            };
        }), props.Client, props.AgentId, props.ConnectionRevision);

        var refresh = () => { _ = model.Current?.LoadAsync(props.AgentId); };
        var reducedMotion = UseReducedMotion();
        var header = Hero(props, snapshot, refresh, reducedMotion);

        Element body = snapshot.Status switch
        {
            DashboardLoadStatus.Unavailable => DashboardsStates.Unavailable(),
            DashboardLoadStatus.Disconnected => DashboardsStates.Disconnected(props.State),
            DashboardLoadStatus.Loading when !snapshot.HasRows => Skeleton(reducedMotion),
            DashboardLoadStatus.Error when !snapshot.HasRows => DashboardsStates.Status(
                "DashboardsLoadError",
                DashboardsGlyphs.Error,
                LocalizationHelper.GetString("Dashboards_LoadError_Title"),
                LocalizationHelper.Format("Dashboards_LoadErrorFormat", snapshot.Error ?? ""),
                Button(LocalizationHelper.GetString("Dashboards_Retry"), refresh).AutomationName(LocalizationHelper.GetString("Dashboards_Retry")).AutomationId("DashboardsRetry"),
                alert: true),
            _ when !snapshot.HasRows => DashboardsStates.Status(
                "DashboardsEmpty",
                DashboardsGlyphs.Dashboard,
                LocalizationHelper.GetString("Dashboards_Empty_Title"),
                LocalizationHelper.GetString("Dashboards_Empty_Detail"),
                art: DashboardsArt.Mosaic(1.2, reducedMotion)),
            _ => Gallery(props, snapshot, query, setQuery, authorId, setAuthorId, sort, setSort, reducedMotion),
        };

        var children = new List<Element> { header.Grid(row: 0) };
        if (snapshot.Status == DashboardLoadStatus.Error && snapshot.HasRows)
        {
            children.Add(InfoBar(
                    LocalizationHelper.GetString("Dashboards_LoadError_Title"),
                    LocalizationHelper.Format("Dashboards_LoadErrorFormat", snapshot.Error ?? ""))
                .Error().IsClosable(false)
                .Margin(0, 0, 0, 12)
                .AutomationId("DashboardsStaleError")
                .Grid(row: 1));
        }
        children.Add(body.Grid(row: 2));
        return Border(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star()], children.ToArray()))
            .Padding(32, 24, 32, 0)
            .AutomationId("DashboardsGallery");
    }

    private static Element Hero(DashboardGalleryProps props, DashboardGallerySnapshot snapshot, Action refresh, bool reducedMotion)
    {
        var titleStack = VStack(4,
            TextBlock(LocalizationHelper.GetString("Dashboards_Title")).FontSize(32).SemiBold()
                .HeadingLevel(AutomationHeadingLevel.Level1)
                .AutomationId("DashboardsTitle"),
            TextBlock(LocalizationHelper.GetString("Dashboards_Tagline")).FontSize(14)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextWrapping(TextWrapping.Wrap)
                .AutomationId("DashboardsSubtitle"));

        Element stats = Empty();
        if (snapshot.HasRows)
        {
            var live = snapshot.Rows.Count(row => row.IsLive);
            var authors = DashboardGalleryPolicy.Authors(snapshot.Rows, props.AgentId).Length;
            stats = HStack(32,
                    Stat(snapshot.Rows.Length, LocalizationHelper.GetString("Dashboards_StatTotal"), null, "DashboardsStatTotal"),
                    Stat(live, LocalizationHelper.GetString("Dashboards_StatLive"),
                        live > 0 ? DashboardsArt.LiveDot(reducedMotion) : null, "DashboardsStatLive"),
                    Stat(authors, LocalizationHelper.GetString("Dashboards_StatAuthors"), null, "DashboardsStatAuthors"))
                .Margin(0, 20, 0, 0);
        }

        var actions = HStack(8,
                AgentPicker(props.State),
                HyperlinkButton(LocalizationHelper.GetString("Dashboards_LearnMore"), new Uri("https://docs.openclaw.ai/web/dashboards"))
                    .AutomationId("DashboardsDocsLink"),
                DashboardsStates.IconButton(DashboardsGlyphs.Refresh, LocalizationHelper.GetString("Dashboards_Refresh"), refresh, "DashboardsRefresh"))
            .VAlign(VerticalAlignment.Top)
            .HAlign(HorizontalAlignment.Right);

        return Border(Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto, GridSize.Star()],
                    titleStack.Grid(row: 0, column: 0),
                    stats.Grid(row: 1, column: 0),
                    actions.Grid(row: 0, column: 1),
                    DashboardsArt.Mosaic(1.35, reducedMotion)
                        .HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Bottom)
                        .Margin(24, 8, 8, 0)
                        .Grid(row: 1, column: 1)))
            .Padding(28, 24, 28, 22)
            .CornerRadius(12)
            .Background(Theme.Ref(DashboardHues.Hero))
            .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
            .BorderThickness(1)
            .Margin(0, 0, 0, 20);
    }

    private static Element Stat(int value, string label, Element? marker, string automationId) =>
        VStack(0,
                TextBlock(value.ToString(System.Globalization.CultureInfo.CurrentCulture)).FontSize(28).SemiBold(),
                HStack(6,
                    (marker ?? Empty()).VAlign(VerticalAlignment.Center),
                    TextBlock(label).FontSize(12)
                        .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                        .VAlign(VerticalAlignment.Center)))
            .AutomationName($"{label}: {value}")
            .AutomationId(automationId);

    private static Element AgentPicker(DashboardsPageState state)
    {
        if (state.AgentIds.Length <= 1)
            return Empty();
        var index = Math.Max(0, state.AgentIds.IndexOf(state.AgentId));
        return ComboBox(state.AgentIds.ToArray(), index, selected =>
            {
                if (selected >= 0 && selected < state.AgentIds.Length)
                    state.SelectAgent(state.AgentIds[selected]);
            })
            .MinWidth(140)
            .AutomationName(LocalizationHelper.GetString("Dashboards_AgentLabel"))
            .AutomationId("DashboardsAgentPicker");
    }

    private static Element Gallery(
        DashboardGalleryProps props,
        DashboardGallerySnapshot snapshot,
        string query,
        Action<string> setQuery,
        string authorId,
        Action<string> setAuthorId,
        DashboardGallerySort sort,
        Action<DashboardGallerySort> setSort,
        bool reducedMotion)
    {
        var fallbackAgentId = props.AgentId;
        var authors = DashboardGalleryPolicy.Authors(snapshot.Rows, fallbackAgentId);
        var filters = new DashboardGalleryFilters(query, authorId, sort);
        var visible = DashboardGalleryPolicy.Apply(snapshot.Rows, filters, fallbackAgentId, DashboardsPresentation.RowTitle);

        var authorLabels = new[] { LocalizationHelper.GetString("Dashboards_AllAuthors") }
            .Concat(authors.Select(author => author.Label)).ToArray();
        var authorIndex = authorId.Length == 0 ? 0 : authors.Select(a => a.Id).ToList().IndexOf(authorId) + 1;
        var sortLabels = new[]
        {
            LocalizationHelper.GetString("Dashboards_SortUpdated"),
            LocalizationHelper.GetString("Dashboards_SortTitle"),
        };

        var toolbar = Grid(
            [GridSize.Star(), GridSize.Star(0.5), GridSize.Auto, GridSize.Auto],
            [GridSize.Auto],
            TextBox(query, setQuery, LocalizationHelper.GetString("Dashboards_SearchPlaceholder"))
                .SearchInput()
                .AutomationName(LocalizationHelper.GetString("Dashboards_SearchLabel"))
                .AutomationId("DashboardsSearch")
                .HAlign(HorizontalAlignment.Stretch).VAlign(VerticalAlignment.Bottom)
                .Grid(column: 0),
            Empty().Grid(column: 1),
            ComboBox(authorLabels, Math.Max(0, authorIndex), selected =>
                    setAuthorId(selected <= 0 || selected > authors.Length ? "" : authors[selected - 1].Id))
                .Header(LocalizationHelper.GetString("Dashboards_AuthorLabel"))
                .MinWidth(160)
                .Margin(12, 0, 0, 0)
                .AutomationId("DashboardsAuthorFilter")
                .Grid(column: 2),
            ComboBox(sortLabels, sort == DashboardGallerySort.Title ? 1 : 0, selected =>
                    setSort(selected == 1 ? DashboardGallerySort.Title : DashboardGallerySort.Updated))
                .Header(LocalizationHelper.GetString("Dashboards_SortLabel"))
                .MinWidth(160)
                .Margin(12, 0, 0, 0)
                .AutomationId("DashboardsSort")
                .Grid(column: 3))
            .VAlign(VerticalAlignment.Bottom);

        var count = TextBlock(LocalizationHelper.Format("Dashboards_ResultCountFormat", visible.Length))
            .FontSize(12)
            .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
            .LiveRegion()
            .Margin(0, 12, 0, 8)
            .AutomationId("DashboardsResultCount");

        Element results = visible.IsEmpty
            ? DashboardsStates.Status(
                "DashboardsNoResults",
                DashboardsGlyphs.Search,
                LocalizationHelper.GetString("Dashboards_NoResults_Title"),
                LocalizationHelper.GetString("Dashboards_NoResults_Detail"),
                hue: DashboardHues.Teal)
            : Cards(props, visible, fallbackAgentId, reducedMotion);

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            toolbar.Grid(row: 0),
            count.Grid(row: 1),
            results.Grid(row: 2));
    }

    private static Element Cards(DashboardGalleryProps props, ImmutableArray<DashboardSessionRow> rows, string fallbackAgentId, bool reducedMotion)
    {
        var now = DateTimeOffset.UtcNow;
        var items = ItemsView(
            rows,
            static row => row.AgentId + "\0" + row.Key,
            (row, _) => ItemContainer(Card(props, row, fallbackAgentId, now, reducedMotion))
                .IsTabStop(false)
                .WithKey(row.AgentId + "\0" + row.Key)) with
        {
            LayoutKind = ItemsViewLayoutKind.UniformGridLayout,
            SelectionMode = ItemsViewSelectionMode.None,
            IsItemInvokedEnabled = false,
        };
        return items
            .Set(view =>
            {
                if (view.Layout is not UniformGridLayout { MinItemWidth: CardMinWidth })
                {
                    view.Layout = new UniformGridLayout
                    {
                        MinItemWidth = CardMinWidth,
                        MinItemHeight = CardHeight,
                        MinColumnSpacing = 20,
                        MinRowSpacing = 20,
                        ItemsStretch = UniformGridLayoutItemsStretch.Fill,
                    };
                }
            })
            .Margin(0, 0, 0, 24)
            .AutomationName(LocalizationHelper.GetString("Dashboards_GalleryLabel"))
            .AutomationId("DashboardsCards");
    }

    private static Element Card(DashboardGalleryProps props, DashboardSessionRow row, string fallbackAgentId, DateTimeOffset now, bool reducedMotion)
    {
        var title = DashboardsPresentation.RowTitle(row);
        var author = DashboardGalleryPolicy.ResolveAuthor(row, fallbackAgentId);
        var updated = DashboardsPresentation.UpdatedText(row, now);
        var hue = DashboardHues.For(row.Key);

        // A tinted wash in the dashboard's identity color frames the live miniature.
        var preview = Grid([GridSize.Star()], [GridSize.Star()],
                Border(Empty()).Background(Theme.Ref(DashboardHues.Wash(hue))).Grid(row: 0),
                InertPreviewHostElement.InertPreviewHost()
                    .Set(host => host.Update(new DashboardPreviewProps(
                        props.Client, new BoardSessionTarget(row.Key, row.AgentId), props.State, props.ConnectionRevision, hue, title)))
                    .Margin(12, 12, 12, 0)
                    .Grid(row: 0))
            .CornerRadius(12, 12, 0, 0);

        var heading = Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            DashboardsArt.Monogram(title, hue, 40).VAlign(VerticalAlignment.Center).Grid(column: 0),
            VStack(4,
                    TextBlock(title).FontSize(16).SemiBold()
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .ToolTip(title),
                    HStack(6,
                        DashboardsArt.Avatar(author.Initial, DashboardHues.For(author.Id), 18).VAlign(VerticalAlignment.Center),
                        TextBlock(LocalizationHelper.Format("Dashboards_ByAuthorFormat", author.Label)).FontSize(12)
                            .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .VAlign(VerticalAlignment.Center)))
                .Margin(12, 0, 0, 0)
                .VAlign(VerticalAlignment.Center)
                .Grid(column: 1),
            (row.IsLive ? DashboardsArt.LiveBadge(reducedMotion) : Empty())
                .Margin(8, 0, 0, 0).VAlign(VerticalAlignment.Top)
                .Grid(column: 2));

        var footer = Border(Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                HStack(6,
                    DashboardsStates.Glyph(DashboardsGlyphsExtra.Clock, 12, "TextFillColorTertiaryBrush").VAlign(VerticalAlignment.Center),
                    TextBlock(updated).FontSize(12)
                        .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .VAlign(VerticalAlignment.Center))
                    .Grid(column: 0),
                HStack(6,
                    TextBlock(LocalizationHelper.GetString("Dashboards_OpenAction")).FontSize(12).SemiBold()
                        .Foreground(Theme.Ref("AccentTextFillColorPrimaryBrush"))
                        .VAlign(VerticalAlignment.Center),
                    DashboardsStates.Glyph(DashboardsGlyphsExtra.ArrowRight, 12, "AccentTextFillColorPrimaryBrush").VAlign(VerticalAlignment.Center))
                    .Grid(column: 1)))
            .Padding(16, 10)
            .BorderBrush(Theme.Ref("DividerStrokeColorDefaultBrush"))
            .BorderThickness(0, 1, 0, 0);

        var content = Grid(
            [GridSize.Star()],
            [GridSize.Star(), GridSize.Auto, GridSize.Auto],
            preview.Grid(row: 0),
            heading.Margin(16, 14, 16, 14).Grid(row: 1),
            footer.Grid(row: 2));
        var label = string.Join(", ", new[]
        {
            title,
            row.IsLive ? LocalizationHelper.GetString("Dashboards_Live") : null,
            LocalizationHelper.Format("Dashboards_ByAuthorFormat", author.Label),
            updated,
        }.Where(part => !string.IsNullOrEmpty(part)));

        // The card surface is one native subtle button (click, Enter, and Space open the
        // dashboard); the outer frame owns the backplate, stroke, and hover lift.
        var button = Button(content, () => props.State.Open(new BoardSessionTarget(row.Key, row.AgentId), title, row))
            .SubtleButton()
            .Set(b =>
            {
                b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                b.VerticalContentAlignment = VerticalAlignment.Stretch;
                b.Padding = new Thickness(0);
            })
            .CornerRadius(12)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            .AutomationName(label)
            .AutomationId("DashboardCard_" + row.Key);

        Element card = Border(button)
            .Height(CardHeight)
            .CornerRadius(12)
            .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
            .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
            .BorderThickness(1);
        if (!reducedMotion)
        {
            card = card.InteractionStates(states => states
                    .PointerOver(translation: new System.Numerics.Vector3(0, -4, 0))
                    .Pressed(translation: new System.Numerics.Vector3(0, -1, 0)),
                Microsoft.UI.Reactor.Animation.Curve.Ease(160, Microsoft.UI.Reactor.Animation.Easing.Decelerate));
        }
        return DashboardsArt.Enter(card, reducedMotion);
    }

    private static Element Skeleton(bool reducedMotion)
    {
        var placeholders = Enumerable.Range(0, 6)
            .Select(index =>
            {
                Element tile = Border(Empty())
                    .Height(CardHeight)
                    .CornerRadius(12)
                    .Background(Theme.Ref(DashboardHues.Wash(index)))
                    .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
                    .BorderThickness(1);
                if (!reducedMotion)
                {
                    tile = tile.Keyframes("shimmer", 0, kf => kf
                        .Duration(1400 + index * 120).Loop()
                        .At(0f, opacity: 1f)
                        .At(0.5f, opacity: 0.45f)
                        .At(1f, opacity: 1f));
                }
                return tile.WrapGridColumnSpan(1).WithKey("skeleton" + index);
            })
            .ToArray();
        return VStack(12,
                ProgressRing().IsActive(true).Size(20, 20).HAlign(HorizontalAlignment.Left)
                    .AutomationName(LocalizationHelper.GetString("Dashboards_Loading")),
                UniformGrid(Orientation.Horizontal, placeholders)
                    .Set(grid =>
                    {
                        grid.ColumnSpacing = 20;
                        grid.RowSpacing = 20;
                    }))
            .AutomationId("DashboardsLoading");
    }
}
