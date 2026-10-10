namespace OpenClaw.TestSupport;

/// <summary>WinUI layout rounds physical pixels, then stores its view size as a single-precision value.</summary>
public static class NativeProofLayout
{
    // CWindowChrome reserves one physical pixel, independent of DPI, above restored
    // custom-titlebar content. Maximized windows have no such border.
    public static int ContentTopInset(bool extendsContentIntoTitleBar, bool maximized) =>
        extendsContentIntoTitleBar && !maximized ? 1 : 0;

    public static double RoundedViewSize(double configuredSize, double rasterizationScale)
    {
        Validate(configuredSize, rasterizationScale);
        return (float)(Math.Round((float)configuredSize * rasterizationScale, MidpointRounding.AwayFromZero) /
            rasterizationScale);
    }

    public static int PhysicalPixels(double viewSize, double rasterizationScale)
    {
        Validate(viewSize, rasterizationScale);
        return checked((int)Math.Round(viewSize * rasterizationScale, MidpointRounding.AwayFromZero));
    }

    public static int PhysicalEdge(double viewCoordinate, double rasterizationScale)
    {
        if (!double.IsFinite(viewCoordinate)) throw new ArgumentOutOfRangeException(nameof(viewCoordinate));
        Validate(0, rasterizationScale);
        return checked((int)Math.Round(viewCoordinate * rasterizationScale, MidpointRounding.AwayFromZero));
    }

    public static (int Left, int Right) CenteredPhysicalEdges(
        double parentOrigin, double slotLeft, double slotWidth, double renderedWidth, double scale)
    {
        Validate(slotWidth, scale);
        Validate(renderedWidth, scale);
        // microsoft-ui-xaml 948461c2, framework.cpp: ComputeAlignmentOffset then
        // LayoutRound(VisualOffset). Round the parent-relative offset, not two centers.
        var offset = (float)(((float)slotWidth - (float)renderedWidth) * 0.5f + (float)slotLeft);
        var roundedOffset = (float)(PhysicalEdge(offset, scale) / scale);
        return (PhysicalEdge(parentOrigin + roundedOffset, scale),
            PhysicalEdge(parentOrigin + roundedOffset + renderedWidth, scale));
    }

    public static bool ContainsPhysicalEdges(
        (double Left, double Top, double Right, double Bottom) viewport,
        (double Left, double Top, double Right, double Bottom) content, double scale) =>
        PhysicalEdge(content.Left, scale) >= PhysicalEdge(viewport.Left, scale) &&
        PhysicalEdge(content.Top, scale) >= PhysicalEdge(viewport.Top, scale) &&
        PhysicalEdge(content.Right, scale) <= PhysicalEdge(viewport.Right, scale) &&
        PhysicalEdge(content.Bottom, scale) <= PhysicalEdge(viewport.Bottom, scale);

    private static void Validate(double size, double scale)
    {
        if (!double.IsFinite(size) || size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
    }
}
