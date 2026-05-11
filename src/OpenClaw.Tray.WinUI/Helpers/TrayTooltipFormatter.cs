namespace OpenClawTray.Helpers;

public static class TrayTooltipFormatter
{
    public const int MaxShellTooltipLength = 127;

    public static string FitShellTooltip(string tooltip)
    {
        var normalized = string.Join(
            " ",
            (tooltip ?? string.Empty).Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (normalized.Length <= MaxShellTooltipLength)
            return normalized;

        return normalized[..(MaxShellTooltipLength - 3)].TrimEnd() + "...";
    }
}
