// Adapts the shared page layout to Systems sections. Usage: SystemsLayout.For(hostWidth, hostHeight).
using System;
using OpenClawTray.Presentation;

namespace OpenClawTray.Systems;

/// <summary>
/// Size-driven layout for the Systems page. Tiers govern detail stacking; measured content width
/// governs the shared hero tiles. Sizes are the page root's, including padding.
/// </summary>
internal readonly record struct SystemsLayout(bool Stacked, int StatColumns)
{
    public const double StatTileMinWidth = 160;
    public const double HeaderMetadataWidth = 180;
    /// <summary>Fixed width of the Capabilities column in the two-column inline details.</summary>
    public const double CapabilitiesWidth = 340;
    /// <summary>Gap between the Capabilities column and the statistics, desktop and sessions column (DESIGN spacing token 6).</summary>
    public const double DetailsColumnGap = 32;
    private const double InlineDetailsChrome = 34;
    public static readonly SystemsLayout Default = For(0, 0);
    public bool CompactHeader { get; init; }
    public PageWidthTier Tier { get; init; }
    public double PagePadding => PageResponsiveLayout.HorizontalPadding(Tier);
    public double ContentWidth { get; init; }
    public double HostWidth { get; init; }

    public static SystemsLayout For(double width, double height)
    {
        var tier = PageResponsiveLayout.TierFor(width);
        // Inline details have 16px padding and a 1px card stroke on each side.
        var available = PageResponsiveLayout.ContentWidth(width);
        var content = Math.Max(0, available - InlineDetailsChrome);
        var stacked = !PageResponsiveLayout.UseTwoColumns(available);
        var details = stacked ? content : content - CapabilitiesWidth - DetailsColumnGap;
        return new SystemsLayout(stacked, PageResponsiveLayout.TileColumns(details, StatTileMinWidth))
        {
            Tier = tier,
            CompactHeader = tier == PageWidthTier.Narrow,
            ContentWidth = available,
            HostWidth = width,
        };
    }
}
