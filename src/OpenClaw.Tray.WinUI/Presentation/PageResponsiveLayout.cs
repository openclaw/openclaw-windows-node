namespace OpenClawTray.Presentation;

/// <summary>
/// Cross-page responsive contract for the Systems, Automations, Usage, Dashboards and Sessions pages.
/// This file must stay byte-identical in every branch that adds it so the branches merge cleanly.
/// Width is the page content host's ActualWidth (not the window width).
/// </summary>
public enum PageWidthTier
{
    Narrow,
    Medium,
    Wide,
}

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

    /// <summary>
    /// WinUI layout can report values such as 999.99994 for an exact 1000 DIP host.
    /// Every comparison snaps widths to 0.01 DIP first so tier boundaries are exact.
    /// </summary>
    public const int SnapDecimals = 2;

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
        var tier = TierFor(w);
        var inner = Math.Max(0, w - (2 * HorizontalPadding(tier)));
        return Math.Min(inner, ContentMaxWidth);
    }

    /// <summary>Two-column bodies stay side by side only on Wide and when each column keeps its minimum width.</summary>
    public static bool UseTwoColumns(double contentWidth)
    {
        var w = Snap(contentWidth);
        return w >= WideMin - (2 * PaddingWide)
            && Snap((w - GapStack) / 2) >= TwoColumnMinColumnWidth;
    }

    /// <summary>Equal-width tile column count: floor((width + gap) / (minTile + gap)), at least 1, at most <paramref name="maxColumns"/>.</summary>
    public static int TileColumns(double contentWidth, double minTileWidth, int maxColumns = int.MaxValue, double gap = GapGroup)
    {
        if (minTileWidth <= 0)
        {
            return 1;
        }

        var columns = (int)Math.Floor(Snap((Snap(contentWidth) + gap) / (minTileWidth + gap)));
        return Math.Clamp(columns, 1, Math.Max(1, maxColumns));
    }

    /// <summary>Width of one tile for <paramref name="columns"/> equal columns.</summary>
    public static double TileWidth(double contentWidth, int columns, double gap = GapGroup) =>
        columns <= 1 ? contentWidth : (contentWidth - ((columns - 1) * gap)) / columns;
}
