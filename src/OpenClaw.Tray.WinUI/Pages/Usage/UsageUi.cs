using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Shared.Usage;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Usage;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Pages.Usage;

/// <summary>Shared Fluent building blocks for the Reactor Usage page. Colors are ThemeResource references only.</summary>
internal static class UsageUi
{
    public const double CardRadius = 8;
    public const double CardPadding = 16;

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
    public static string ReportedTokens(UsageTotals totals, string field, long value) =>
        totals.HasValue(field) ? Tokens(value, totals.IsLowerBound) : Dash;

    public static string Cost(double cost, bool lowerBound = false) => AtLeast(UsageFormat.Cost(cost, Culture), lowerBound);

    public static string AnalysisCost(double cost, bool lowerBound = false) => AtLeast(UsageFormat.AnalysisCost(cost, Culture), lowerBound);

    public static string Metric(UsageMetric metric, long tokens, double cost, bool lowerBound = false) =>
        metric == UsageMetric.Cost ? AnalysisCost(cost, lowerBound) : Tokens(tokens, lowerBound);

    public static string Count(long value) => UsageFormat.Count(value, Culture);

    /// <summary>Formats a single count with the key's singular "One" variant when the count is exactly 1.</summary>
    public static string FN(string key, long count) => F(count == 1 ? key + "One" : key, Count(count));

    public static string Dash => S("Usage_EmptyValue");

    // ── personality: palette, badges and avatars ──

    public const int PaletteSize = 6;

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

    public static string Initials(string name)
    {
        var words = name.Split([' ', '-', '_', '/', ':', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0])).ToArray();
        if (words.Length == 0) return name.Length > 0 && char.IsLetterOrDigit(name[0]) ? name[..1].ToUpperInvariant() : "?";
        return words.Length == 1
            ? words[0][..1].ToUpperInvariant()
            : string.Concat(words[0][0], words[1][0]).ToUpperInvariant();
    }

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
        string title, string? subtitle, Element? actions, Element body, string automationId, bool framed = true, bool stackActions = false)
    {
        Element heading = VStack(2,
            TextBlock(title).FontSize(16).FontWeight(FontWeights.SemiBold)
                .TextWrapping(TextWrapping.WrapWholeWords).MaxLines(2).TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(title)
                .HeadingLevel(AutomationHeadingLevel.Level2).AutomationId(automationId + "Title"),
            subtitle is null ? null : TextBlock(subtitle).FontSize(12).Foreground(SecondaryText).TextWrapping(TextWrapping.WrapWholeWords));
        var header = actions is null
            ? heading
            : Grid(stackActions ? [GridSize.Star()] : [GridSize.Star(), GridSize.Auto],
                stackActions ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
                heading.VAlign(VerticalAlignment.Center).Grid(column: 0),
                actions.VAlign(VerticalAlignment.Center)
                    .Margin(stackActions ? 0 : PageResponsiveLayout.GapGroup, stackActions ? PageResponsiveLayout.GapInner : 0, 0, 0)
                    .Grid(row: stackActions ? 1 : 0, column: stackActions ? 0 : 1));
        return (framed
                ? Card(VStack(16, header, body), automationId + "Card")
                : VStack(12, header, body))
            .AutomationId(automationId);
    }

    public static Element Pair(bool sideBySide, Element left, Element right, string id)
    {
        return (Grid(sideBySide ? [GridSize.Star(), GridSize.Star()] : [GridSize.Star()],
                sideBySide ? [GridSize.Auto] : [GridSize.Auto, GridSize.Auto],
                left.VAlign(VerticalAlignment.Top).Grid(row: 0, column: 0).WithKey("left"),
                right.VAlign(VerticalAlignment.Top).Grid(row: sideBySide ? 0 : 1, column: sideBySide ? 1 : 0).WithKey("right"))
                with { ColumnSpacing = sideBySide ? PageResponsiveLayout.GapStack : 0, RowSpacing = sideBySide ? 0 : PageResponsiveLayout.GapStack })
            .AutomationId(id);
    }

    public static TextBlockElement Note(string text) =>
        TextBlock(text).FontSize(12).Foreground(SecondaryText).TextWrapping(TextWrapping.WrapWholeWords).MaxWidth(640)
            .HAlign(HorizontalAlignment.Left);

    public static Element EmptyText(string text, string automationId) =>
        TextBlock(text).FontSize(13).Foreground(SecondaryText).TextWrapping(TextWrapping.WrapWholeWords)
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

    public static readonly ThemeRef CautionTint = Theme.Ref("ConnectionCapabilityPillCautionBrush");
    public static readonly ThemeRef CriticalTint = Theme.Ref("ConnectionCapabilityPillCriticalBrush");

    /// <summary>Stacked proportion bar for parts of a whole.</summary>
    public static Element StackedBar(IReadOnlyList<(double Share, ThemeRef Fill)> parts, double height = 10)
    {
        var visible = parts.Where(p => p.Share > 0).ToList();
        if (visible.Count == 0)
            return Border(null).Height(height).CornerRadius(height / 2).Background(Track).AccessibilityView(AccessibilityView.Raw);
        return Border(Grid(Array.ConvertAll(visible.ToArray(), p => GridSize.Star(p.Share)), [GridSize.Px(height)],
                    visible.Select((p, i) => (Element)Border(null).Background(p.Fill).Grid(column: i).WithKey("part:" + i)).ToArray()))
            .CornerRadius(height / 2)
            .Height(height)
            .AccessibilityView(AccessibilityView.Raw);
    }

    public static Element LegendItem(string label, ThemeRef fill, string? value = null) =>
        HStack(6,
            Border(null).Size(10, 10).CornerRadius(2).Background(fill).VAlign(VerticalAlignment.Center),
            TextBlock(label).FontSize(12).Foreground(SecondaryText).VAlign(VerticalAlignment.Center),
            value is null ? null : TextBlock(value).FontSize(12).FontWeight(FontWeights.SemiBold).VAlign(VerticalAlignment.Center));

    /// <summary>Small rounded label.</summary>
    public static Element Pill(string text, string automationId, bool critical = false) =>
        Border(TextBlock(text).FontSize(12).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis).MaxWidth(220).ToolTip(text)
                .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center)
                .Foreground(critical ? Critical : Theme.Ref("TextFillColorPrimaryBrush")))
            .Padding(8, 2).MinWidth(24).MinHeight(24).CornerRadius(12)
            .Background(critical ? CriticalTint : Theme.Ref("UsageCountPillBrush"))
            .VAlign(VerticalAlignment.Center).AutomationId(automationId);

    public static string FriendlyDate(DateTimeOffset at, bool olderTime = false) =>
        UsageFormat.FriendlyDate(at.LocalDateTime, DateTime.Now, Culture, S("Usage_Preset_Today"), olderTime);

    public static Element SubtleButton(string text, Action onClick, string automationId, string? glyph = null)
    {
        Element content = glyph is null
            ? TextBlock(text).FontSize(13)
            : HStack(6, TextBlock(glyph).FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(12).VAlign(VerticalAlignment.Center),
                TextBlock(text).FontSize(13).VAlign(VerticalAlignment.Center));
        return Button(content, onClick)
            .SubtleButton()
            .AutomationId(automationId)
            .AutomationName(text);
    }

    public static Element IconButton(string glyph, string name, Action onClick, string automationId, bool enabled = true) =>
        Button(TextBlock(glyph).FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(14), onClick)
            .SubtleButton()
            .IsEnabled(enabled)
            .ToolTip(name)
            .AutomationName(name)
            .AutomationId(automationId);
}
