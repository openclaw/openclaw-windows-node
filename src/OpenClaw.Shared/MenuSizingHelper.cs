namespace OpenClaw.Shared;

/// <summary>
/// Pure helper methods for constraining popup menu size to the visible work area.
/// </summary>
public static class MenuSizingHelper
{
    public static int ConvertPixelsToViewUnits(int pixels, uint dpi)
    {
        if (pixels <= 0) return 0;
        if (dpi == 0) dpi = 96;

        return Math.Max(1, (int)Math.Floor(pixels * 96.0 / dpi));
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
}
