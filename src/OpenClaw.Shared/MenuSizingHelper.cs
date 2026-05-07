namespace OpenClaw.Shared;

/// <summary>
/// Pure helper methods for constraining popup menu size to the visible work area.
/// </summary>
public static class MenuSizingHelper
{
    private const double ScaleTolerance = 0.001;

    public static int ConvertPixelsToViewUnits(int pixels, uint dpi)
    {
        if (pixels <= 0) return 0;
        if (dpi == 0) dpi = 96;

        return Math.Max(1, (int)Math.Floor(pixels * 96.0 / dpi));
    }

    public static bool HasDpiOrScaleChanged(uint previousDpi, double previousRasterizationScale, uint currentDpi, double currentRasterizationScale)
    {
        previousDpi = NormalizeDpi(previousDpi);
        currentDpi = NormalizeDpi(currentDpi);

        if (previousDpi != currentDpi)
            return true;

        var previousScale = NormalizeScale(previousRasterizationScale);
        var currentScale = NormalizeScale(currentRasterizationScale);
        return Math.Abs(previousScale - currentScale) > ScaleTolerance;
    }

    public static int CalculateWindowHeight(int contentHeight, int workAreaHeight, int minimumHeight = 100)
    {
        if (contentHeight < 0) contentHeight = 0;
        if (minimumHeight < 1) minimumHeight = 1;

        if (workAreaHeight <= 0)
            return Math.Max(contentHeight, minimumHeight);

        var minimumVisibleHeight = Math.Min(minimumHeight, workAreaHeight);
        var desiredHeight = Math.Max(contentHeight, minimumVisibleHeight);
        return Math.Min(desiredHeight, workAreaHeight);
    }

    private static uint NormalizeDpi(uint dpi) => dpi == 0 ? 96u : dpi;

    private static double NormalizeScale(double scale) =>
        double.IsFinite(scale) && scale > 0 ? scale : 1.0;
}
