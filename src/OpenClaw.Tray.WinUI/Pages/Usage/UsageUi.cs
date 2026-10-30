using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Shared.Usage;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation.Usage;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Pages.Usage;

/// <summary>Shared Fluent building blocks for the Reactor Usage page. Colors are ThemeResource references only.</summary>
internal static class UsageUi
{
    public const double CardRadius = 8;
    public const double CardPadding = 16;
    public const double SectionGap = 16;

    public static readonly ThemeRef CardFill = Theme.Ref("CardBackgroundFillColorDefaultBrush");
    public static readonly ThemeRef CardStroke = Theme.Ref("CardStrokeColorDefaultBrush");
    public static readonly ThemeRef SecondaryText = Theme.Ref("TextFillColorSecondaryBrush");
    public static readonly ThemeRef TertiaryText = Theme.Ref("TextFillColorTertiaryBrush");
    public static readonly ThemeRef Track = Theme.Ref("SubtleFillColorSecondaryBrush");
    // Data marks need the palette's HC highlight; a native button fill can match the plot background.
    public static readonly ThemeRef Accent = Theme.Ref("UsagePalette1Brush");
    public static readonly ThemeRef AccentText = Theme.Ref("AccentTextFillColorPrimaryBrush");
    public static readonly ThemeRef Success = Theme.Ref("SystemFillColorSuccessBrush");
    public static readonly ThemeRef Caution = Theme.Ref("SystemFillColorCautionBrush");
    public static readonly ThemeRef Critical = Theme.Ref("SystemFillColorCriticalBrush");
    public static readonly ThemeRef CautionBackground = Theme.Ref("SystemFillColorCautionBackgroundBrush");
    public static readonly ThemeRef CacheRead = Theme.Ref("UsageChartCacheReadBrush");
    public static readonly ThemeRef Other = Theme.Ref("ControlStrongStrokeColorDefaultBrush");

    public static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static string S(string key) => LocalizationHelper.GetString(key);

    public static string F(string key, params object?[] args) => LocalizationHelper.Format(key, args);

    /// <summary>The Gateway's error text, or a localized reason when the read failed without one.</summary>
    public static string ReadError<T>(UsageRead<T> read) where T : class =>
        read.Error is { Length: > 0 } error ? error
        : S(read.Failure is UsageReadStatus.Unsupported or UsageReadStatus.Disconnected
            ? "Usage_ReadError_" + read.Failure
            : "Usage_ReadError_Failed");

    /// <summary>Upstream marks partial counters with a lower-bound prefix.</summary>
    public static string AtLeast(string value, bool lowerBound) => lowerBound ? F("Usage_AtLeastFormat", value) : value;

    public static string Tokens(long tokens, bool lowerBound = false) => AtLeast(UsageFormat.Tokens(tokens, Culture), lowerBound);

    public static string Cost(double cost, bool lowerBound = false) => AtLeast(UsageFormat.Cost(cost, Culture), lowerBound);

    public static string AnalysisCost(double cost, bool lowerBound = false) => AtLeast(UsageFormat.AnalysisCost(cost, Culture), lowerBound);

    public static string Metric(UsageMetric metric, long tokens, double cost, bool lowerBound = false) =>
        metric == UsageMetric.Cost ? AnalysisCost(cost, lowerBound) : Tokens(tokens, lowerBound);

    public static string Count(long value) => UsageFormat.Count(value, Culture);

    /// <summary>Formats a single count with the key's singular "One" variant when the count is exactly 1.</summary>
    public static string FN(string key, long count) => F(count == 1 ? key + "One" : key, Count(count));

    public static string Dash => S("Usage_EmptyValue");

    // ── palette ──

    public static ThemeRef SeriesBrush(UsageSeries series) => series.Kind switch
    {
        UsageSeriesKind.Output or UsageSeriesKind.Total => Accent,
        UsageSeriesKind.Input => Success,
        UsageSeriesKind.CacheWrite => Caution,
        UsageSeriesKind.CacheRead => CacheRead,
        UsageSeriesKind.Other => Other,
        _ => ProviderBrush(series.PaletteIndex),
    };

    public static ThemeRef KindBrush(UsageSeriesKind kind) => SeriesBrush(new UsageSeries("", kind, null, 0));

    /// <summary>
    /// Brushes for one chart's series. Provider series keep their page-wide name color unless two would share a slot,
    /// in which case the later one moves to the next free slot so stacked segments stay distinct.
    /// </summary>
    public static IReadOnlyDictionary<string, ThemeRef> SeriesBrushes(IReadOnlyList<UsageSeries> series)
    {
        var result = new Dictionary<string, ThemeRef>(StringComparer.Ordinal);
        var used = new HashSet<int>();
        foreach (var s in series)
        {
            if (s.Kind != UsageSeriesKind.Provider)
            {
                result[s.Key] = SeriesBrush(s);
                continue;
            }
            var slot = PaletteIndex(s.Label ?? s.Key);
            for (var step = 0; step < PaletteSize && used.Contains(slot); step++) slot = (slot + 1) % PaletteSize;
            used.Add(slot);
            result[s.Key] = Palette(slot);
        }
        return result;
    }

    private static ThemeRef ProviderBrush(int index) => Palette(index);

    // ── personality: palette, badges, avatars and motion ──

    public const int PaletteSize = 6;

    /// <summary>
    /// Shared setup art (provider logos, mascot, party popper). The package pipeline scopes it under
    /// OpenClaw.SetupEngine.UI, the same URIs setup's ProviderArtwork uses; the root copy exists only in loose builds.
    /// </summary>
    public const string SetupAssets = "ms-appx:///OpenClaw.SetupEngine.UI/Assets/Setup/";
    private static readonly ThemeRef[] PaletteFills =
        Enumerable.Range(1, PaletteSize).Select(i => Theme.Ref($"UsagePalette{i}Brush")).ToArray();
    private static readonly ThemeRef[] PaletteTints =
        Enumerable.Range(1, PaletteSize).Select(i => Theme.Ref($"UsagePalette{i}TintBrush")).ToArray();

    /// <summary>Categorical fill (DESIGN.md usagePalette). Slot 0 follows the user's accent color.</summary>
    public static ThemeRef Palette(int index) => PaletteFills[((index % PaletteSize) + PaletteSize) % PaletteSize];

    public static ThemeRef PaletteTint(int index) => PaletteTints[((index % PaletteSize) + PaletteSize) % PaletteSize];

    /// <summary>Stable palette slot for a name, so an agent, model or provider keeps one color across the page.</summary>
    public static int PaletteIndex(string? key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        var hash = 2166136261u;
        foreach (var c in key.ToLowerInvariant()) hash = (hash ^ c) * 16777619u;
        return (int)(hash % PaletteSize);
    }

    /// <summary>Set by the page root from the system animation setting; motion helpers are no-ops when true.</summary>
    [ThreadStatic] private static bool t_reduceMotion;

    public static bool ReduceMotion
    {
        get => t_reduceMotion;
        set => t_reduceMotion = value;
    }

    /// <summary>Fades and rises an element in when it first appears.</summary>
    public static T Enter<T>(this T element) where T : Element =>
        ReduceMotion
            ? element
            : element.Transition(Microsoft.UI.Reactor.Animation.Transition.Fade
                + Microsoft.UI.Reactor.Animation.Transition.Slide(Microsoft.UI.Reactor.Animation.Edge.Bottom),
                Microsoft.UI.Reactor.Animation.Curve.Ease(260));

    /// <summary>Cascades the enter transitions of a container's children.</summary>
    public static T Cascade<T>(this T container, int delayMs = 35) where T : Element =>
        ReduceMotion ? container : container.Stagger(TimeSpan.FromMilliseconds(delayMs));

    /// <summary>Tinted rounded square holding a Fluent glyph, like Windows Settings cards.</summary>
    public static Element IconBadge(string glyph, int palette, double size = 32) =>
        Border(TextBlock(glyph).AccessibilityHidden().FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(Math.Round(size * 0.5))
                .Foreground(Palette(palette)).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
            .Size(size, size)
            .CornerRadius(Math.Round(size * 0.28))
            .Background(PaletteTint(palette))
            .AccessibilityView(AccessibilityView.Raw);

    /// <summary>Colored monogram for a name. Decorative: the label next to it carries the meaning.</summary>
    public static Element Avatar(string label, string? colorKey = null, double size = 28)
    {
        var index = PaletteIndex(colorKey ?? label);
        return Border(TextBlock(Initials(label)).AccessibilityHidden().FontSize(Math.Round(size * 0.4)).FontWeight(FontWeights.SemiBold)
                .Foreground(Palette(index)).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
            .Size(size, size)
            .CornerRadius(size / 2)
            .Background(PaletteTint(index))
            .AccessibilityView(AccessibilityView.Raw);
    }

    /// <summary>
    /// Bundled provider logo on the requested-Dark artwork tile used by setup (DESIGN.md providerArtwork*),
    /// or a colored monogram for providers without bundled art.
    /// </summary>
    public static Element ProviderMark(string? provider, double size = 24)
    {
        var file = OpenClaw.SetupEngine.GatewayAiSetupPresentation.GetBundledProviderIconFileName(provider);
        if (file is null || file.StartsWith("Pair", StringComparison.Ordinal))
            return Avatar(string.IsNullOrWhiteSpace(provider) ? "?" : provider!, provider, size);
        return Border(Image(UsageUi.SetupAssets + "ProviderIcons/" + file).AccessibilityHidden()
                    .Size(Math.Round(size * 0.6), Math.Round(size * 0.6))
                    .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
            .RequestedTheme(ElementTheme.Dark)
            .Size(size, size)
            .CornerRadius(Math.Round(size * 0.25))
            .Background(Theme.Ref("SolidBackgroundFillColorBaseBrush"))
            .WithBorder(CardStroke, 1)
            .AccessibilityView(AccessibilityView.Raw);
    }

    public static string Initials(string name)
    {
        var words = name.Split([' ', '-', '_', '/', ':', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0])).ToArray();
        if (words.Length == 0) return name.Length > 0 && char.IsLetterOrDigit(name[0]) ? name[..1].ToUpperInvariant() : "?";
        return words.Length == 1
            ? words[0][..1].ToUpperInvariant()
            : string.Concat(words[0][0], words[1][0]).ToUpperInvariant();
    }

    // Glyphs (Segoe Fluent Icons / MDL2 fallback through SymbolThemeFontFamily).
    public const string GlyphTokens = "\uE9D9";
    public const string GlyphCost = "\uE8C7";
    public const string GlyphSessions = "\uE8BD";
    public const string GlyphMessages = "\uE8F2";
    public const string GlyphSpeed = "\uEC4A";
    public const string GlyphTools = "\uE90F";
    public const string GlyphCache = "\uE8F7";
    public const string GlyphErrors = "\uE783";
    public const string GlyphCalendar = "\uE787";

    public static string SeriesLabel(UsageSeries series) => series.Kind switch
    {
        UsageSeriesKind.Output => S("Usage_Type_Output"),
        UsageSeriesKind.Input => S("Usage_Type_Input"),
        UsageSeriesKind.CacheWrite => S("Usage_Type_CacheWrite"),
        UsageSeriesKind.CacheRead => S("Usage_Type_CacheRead"),
        UsageSeriesKind.Other => S("Usage_Daily_Other"),
        UsageSeriesKind.Total => S("Usage_Daily_Total"),
        _ => series.Label ?? S("Usage_Unknown"),
    };

    // ── building blocks ──

    public static Element Card(Element content, string automationId) =>
        Border(content)
            .Padding(CardPadding)
            .CornerRadius(CardRadius)
            .Background(CardFill)
            .WithBorder(CardStroke, 1)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationId(automationId);

    /// <summary>
    /// A section in the web page's structure: a group header (title, optional note and actions) above its content,
    /// like Windows Settings groups. The header is an automation heading so screen readers can jump between sections.
    /// <paramref name="framed"/> puts the body in one card; unframed bodies supply their own cards (overview grid).
    /// </summary>
    public static Element Section(
        string title, string? subtitle, Element? actions, Element body, string automationId, bool framed = true)
    {
        Element heading = VStack(2,
            TextBlock(title).FontSize(14).FontWeight(FontWeights.SemiBold)
                .HeadingLevel(AutomationHeadingLevel.Level2).AutomationId(automationId + "Title"),
            subtitle is null ? null : TextBlock(subtitle).FontSize(12).Foreground(SecondaryText).TextWrapping(TextWrapping.Wrap));
        var header = actions is null
            ? heading
            : Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                heading.VAlign(VerticalAlignment.Bottom).Grid(column: 0),
                actions.VAlign(VerticalAlignment.Bottom).Margin(12, 0, 0, 0).Grid(column: 1));
        return VStack(8,
                header.Margin(2, 0, 2, 0),
                framed ? Card(body, automationId + "Card") : body)
            .AutomationId(automationId);
    }

    /// <summary>A compact card for one stat or one ranked list inside an unframed section grid.</summary>
    public static Element Tile(Element content) =>
        Border(content)
            .Padding(14, 12)
            .CornerRadius(CardRadius)
            .Background(CardFill)
            .WithBorder(CardStroke, 1)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch);

    public static Element Note(string text) =>
        TextBlock(text).FontSize(12).Foreground(SecondaryText).TextWrapping(TextWrapping.Wrap);

    public static Element EmptyText(string text, string automationId) =>
        TextBlock(text).FontSize(13).Foreground(SecondaryText).TextWrapping(TextWrapping.Wrap)
            .Margin(0, 4, 0, 4).AutomationId(automationId);

    /// <summary>Non-dismissible page banner with an optional action button.</summary>
    public static Element Banner(
        string title, string message, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity, string automationId,
        string? actionText = null, Action? action = null) =>
        (InfoBar(title, message) with
        {
            Severity = severity,
            IsOpen = true,
            IsClosable = false,
            ActionButtonContent = actionText,
            OnActionButtonClick = action,
        })
        .HAlign(HorizontalAlignment.Stretch)
        .AutomationId(automationId);

    public static readonly ThemeRef SuccessTint = Theme.Ref("ConnectionCapabilityPillSuccessBrush");
    public static readonly ThemeRef CautionTint = Theme.Ref("ConnectionCapabilityPillCautionBrush");
    public static readonly ThemeRef CriticalTint = Theme.Ref("ConnectionCapabilityPillCriticalBrush");

    private static ThemeRef ToneTint(ThemeRef tone) =>
        tone.Equals(Success) ? SuccessTint
        : tone.Equals(Critical) ? CriticalTint
        : tone.Equals(Caution) ? CautionTint
        : PaletteTint(0);

    /// <summary>
    /// KPI tile: glyph badge, label, value and a secondary line. A tone (success, caution or critical) recolors the
    /// badge to grade the value; text always carries the meaning.
    /// </summary>
    public static Element Stat(
        string label, string value, string? sub, string automationId, ThemeRef? tone = null, string? tooltip = null,
        string? glyph = null, int palette = 0)
    {
        var body = VStack(2,
            TextBlock(label).FontSize(12).Foreground(SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis),
            TextBlock(value).FontSize(22).FontWeight(FontWeights.SemiBold).TextTrimming(TextTrimming.CharacterEllipsis)
                .AutomationId(automationId + "Value"),
            sub is null ? null : TextBlock(sub).FontSize(12).Foreground(TertiaryText).TextWrapping(TextWrapping.Wrap));
        Element marker = glyph is null
            ? Border(null).Width(3).CornerRadius(1.5).Background(tone ?? Track).VAlign(VerticalAlignment.Stretch)
            : tone is { } graded
                ? Border(TextBlock(glyph).AccessibilityHidden().FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(16).Foreground(graded)
                        .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                    .Size(32, 32).CornerRadius(9).Background(ToneTint(graded)).AccessibilityView(AccessibilityView.Raw)
                    .VAlign(VerticalAlignment.Top)
                : IconBadge(glyph, palette, 32).VAlign(VerticalAlignment.Top);
        Element tile = Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                marker.Grid(column: 0),
                body.Margin(12, 0, 0, 0).Grid(column: 1))
            .MinWidth(150)
            .AutomationName($"{label}: {value}{(sub is null ? "" : ". " + sub)}")
            .AutomationId(automationId);
        return tooltip is null ? tile : tile.ToolTip(tooltip);
    }

    /// <summary>Horizontal proportion bar. <paramref name="share"/> is clamped to 0..1.</summary>
    public static Element ShareBar(double share, ThemeRef fill, double height = 6)
    {
        var value = Math.Clamp(double.IsNaN(share) ? 0 : share, 0, 1);
        return Grid([GridSize.Star(Math.Max(value, 0.0001)), GridSize.Star(Math.Max(1 - value, 0.0001))], [GridSize.Px(height)],
                Border(null).Background(fill).CornerRadius(height / 2).Grid(column: 0),
                Border(null).Background(Track).CornerRadius(height / 2).Grid(column: 1))
            .Height(height)
            .AccessibilityView(AccessibilityView.Raw);
    }

    /// <summary>Stacked proportion bar for parts of a whole.</summary>
    public static Element StackedBar(IReadOnlyList<(double Share, ThemeRef Fill)> parts, double height = 10)
    {
        var visible = parts.Where(p => p.Share > 0).ToList();
        if (visible.Count == 0)
            return Border(null).Height(height).CornerRadius(height / 2).Background(Track).AccessibilityView(AccessibilityView.Raw);
        return Border(Grid(visible.Select(p => GridSize.Star(p.Share)).ToArray(), [GridSize.Px(height)],
                    visible.Select((p, i) => (Element)Border(null).Background(p.Fill).Grid(column: i).WithKey("part:" + i)).ToArray()))
            .CornerRadius(height / 2)
            .Height(height)
            .AccessibilityView(AccessibilityView.Raw);
    }

    /// <summary>
    /// Left-to-right wrapping row sized to each child. Unlike WrapGrid (VariableSizedWrapGrid), cells are not
    /// uniform, so wide controls and short labels can share a row without clipping.
    /// </summary>
    public static FlexElement Flow(params Element?[] children) =>
        Flex(children) with { Wrap = Microsoft.UI.Reactor.Layout.FlexWrap.Wrap, AlignItems = Microsoft.UI.Reactor.Layout.FlexAlign.FlexStart };

    public static Element LegendItem(string label, ThemeRef fill, string? value = null) =>
        HStack(6,
            Border(null).Size(10, 10).CornerRadius(2).Background(fill).VAlign(VerticalAlignment.Center),
            TextBlock(label).FontSize(12).Foreground(SecondaryText).VAlign(VerticalAlignment.Center),
            value is null ? null : TextBlock(value).FontSize(12).FontWeight(FontWeights.SemiBold).VAlign(VerticalAlignment.Center));

    /// <summary>Small rounded label.</summary>
    public static Element Pill(string text, string automationId) =>
        Border(TextBlock(text).FontSize(12).TextTrimming(TextTrimming.CharacterEllipsis).MaxWidth(220))
            .Padding(8, 2).CornerRadius(10).Background(Track).AutomationId(automationId);

    public static Element SubtleButton(string text, Action onClick, string automationId, string? glyph = null)
    {
        Element content = glyph is null
            ? TextBlock(text).FontSize(13)
            : HStack(6, TextBlock(glyph).FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(12).VAlign(VerticalAlignment.Center),
                TextBlock(text).FontSize(13).VAlign(VerticalAlignment.Center));
        return Button(content, onClick)
            .Resources(ChatVisuals.ToolbarButtonResources)
            .AutomationId(automationId)
            .AutomationName(text);
    }

    public static Element IconButton(string glyph, string name, Action onClick, string automationId, bool enabled = true) =>
        Button(TextBlock(glyph).FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(14), onClick)
            .Padding(8)
            .IsEnabled(enabled)
            .Resources(ChatVisuals.ToolbarButtonResources)
            .ToolTip(name)
            .AutomationName(name)
            .AutomationId(automationId);

    /// <summary>True when Ctrl is held, used for additive selection like the web page's modifier clicks.</summary>
    public static bool ControlPressed() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
}
