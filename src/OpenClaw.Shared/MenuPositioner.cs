namespace OpenClaw.Shared;

/// <summary>
/// Pure positioning math for tray popup menus.
/// </summary>
public static class MenuPositioner
{
    /// <summary>
    /// Calculates the top-left position for a popup menu given the cursor location,
    /// the menu dimensions, and the monitor work area (excluding taskbar).
    /// Prefers showing above the cursor (typical tray behaviour).
    /// </summary>
    public static (int x, int y) CalculatePosition(
        int cursorX, int cursorY,
        int menuWidth, int menuHeight,
        int workLeft, int workTop, int workRight, int workBottom,
        int margin = 8)
    {
        // Clamp X within work area
        int maxX = workRight - menuWidth;
        if (maxX < workLeft) maxX = workLeft;
        int x = Math.Clamp(cursorX, workLeft, maxX);

        // Clamp Y within work area
        int maxY = workBottom - menuHeight;
        if (maxY < workTop) maxY = workTop;

        int yAbove = cursorY - menuHeight - margin;
        int yBelow = cursorY + margin;

        bool canShowAbove = yAbove >= workTop;
        bool canShowBelow = yBelow <= maxY;

        int y;
        if (canShowAbove)
        {
            y = Math.Min(yAbove, maxY);
        }
        else if (canShowBelow)
        {
            y = Math.Max(yBelow, workTop);
        }
        else
        {
            // Worst case: clamp within the visible work area.
            y = Math.Clamp(yAbove, workTop, maxY);
        }

        return (x, y);
    }
}
