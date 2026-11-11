// Shared page sizing in DIPs. Example: ContentWidth(host.ActualWidth), then UseTwoColumns(contentWidth).
namespace OpenClawTray.Presentation;

public enum PageWidthTier
{
    Narrow,
    Medium,
    Wide,
}

/// <summary>Responsive policy measured from the page content host, not the window.</summary>
public static class PageResponsiveLayout
{
    public const double WideMin = 1000;
    public const double MediumMin = 720;
    public const double MinWidth = 480;
    public const double ContentMaxWidth = 1200;
    public const double PaddingWide = 32;
    public const double PaddingNarrow = 20;
    public const double GapInner = 8;
    public const double GapGroup = 12;
    public const double GapStack = 16;
    public const double TwoColumnMinColumnWidth = 320;
    public const int SnapDecimals = 2;

    /// <summary>Normalizes subpixel layout noise at exact breakpoints, such as 999.99994 DIPs.</summary>
    public static double Snap(double width) => Math.Round(width, SnapDecimals, MidpointRounding.AwayFromZero);

    public static PageWidthTier TierFor(double width)
    {
        var w = Snap(width);
        return w >= WideMin ? PageWidthTier.Wide
            : w >= MediumMin ? PageWidthTier.Medium
            : PageWidthTier.Narrow;
    }

    public static double HorizontalPadding(PageWidthTier tier) =>
        tier == PageWidthTier.Narrow ? PaddingNarrow : PaddingWide;

    /// <summary>Usable content width after padding, capped at <see cref="ContentMaxWidth"/>.</summary>
    public static double ContentWidth(double width)
    {
        var w = Snap(width);
        var inner = Math.Max(0, w - (2 * HorizontalPadding(TierFor(w))));
        return Math.Min(inner, ContentMaxWidth);
    }

    /// <summary>Two-column bodies stay side by side only on Wide with minimum column width retained.</summary>
    public static bool UseTwoColumns(double contentWidth)
    {
        var w = Snap(contentWidth);
        return w >= WideMin - (2 * PaddingWide)
            && Snap((w - GapStack) / 2) >= TwoColumnMinColumnWidth;
    }

    /// <summary>Returns the number of equal-width tiles fitting the available width.</summary>
    public static int TileColumns(double contentWidth, double minTileWidth, int maxColumns = int.MaxValue, double gap = GapGroup)
    {
        if (minTileWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(minTileWidth));
        var columns = (int)Math.Floor((Snap(contentWidth) + gap) / (minTileWidth + gap));
        return Math.Clamp(columns, 1, Math.Max(1, maxColumns));
    }

    public static double TileWidth(double contentWidth, int columns, double gap = GapGroup) =>
        columns <= 1 ? contentWidth : (contentWidth - ((columns - 1) * gap)) / columns;
}
