namespace OpenClaw.SetupEngine;

/// <summary>Setup window dimensions in DIPs, projected to native window pixels.</summary>
/// <remarks>
/// The design reference is 1120x720 with a 472-DIP companion pane and a 648-DIP page column.
/// Narrower windows keep the pane proportion; wider windows split the extra width evenly
/// around the composition so a maximized setup window stays balanced.
/// </remarks>
public static class SetupWindowSizing
{
    public const int MinimumWidth = 960;
    public const int MinimumHeight = 640;
    public const int InitialWidth = 1120;
    public const int InitialHeight = 720;
    public const double ReferenceCompanionWidth = 472;
    public const double MinimumCompanionWidth = 380;

    public static (int Width, int Height) MinimumPixels(uint dpi) =>
        (Pixels(MinimumWidth, dpi), Pixels(MinimumHeight, dpi));

    public static (int Width, int Height) InitialPixels(uint dpi) =>
        (Pixels(InitialWidth, dpi), Pixels(InitialHeight, dpi));

    /// <summary>
    /// Shrinks the initial size to the monitor work area (for example 1080p at 150%) without
    /// going below the native minimum, which the presenter enforces separately.
    /// </summary>
    public static (int Width, int Height) FitInitialPixels(
        (int Width, int Height) initial, (int Width, int Height) workArea, (int Width, int Height) minimum) =>
        (Fit(initial.Width, workArea.Width, minimum.Width), Fit(initial.Height, workArea.Height, minimum.Height));

    /// <summary>
    /// Caps the enforced minimum at the monitor work area so small or highly scaled displays
    /// (1366x768 at 125%, 1080p at 175%) can still fit the whole window, including its actions.
    /// An unknown work area keeps the design minimum.
    /// </summary>
    public static (int Width, int Height) FitMinimumPixels(
        (int Width, int Height) minimum, (int Width, int Height) workArea) =>
        (workArea.Width > 0 ? Math.Min(minimum.Width, workArea.Width) : minimum.Width,
         workArea.Height > 0 ? Math.Min(minimum.Height, workArea.Height) : minimum.Height);

    public static double CompanionPaneWidth(double windowWidthDips) => windowWidthDips > InitialWidth
        ? Math.Round(ReferenceCompanionWidth + (windowWidthDips - InitialWidth) / 2)
        : Math.Max(MinimumCompanionWidth, Math.Round(windowWidthDips * ReferenceCompanionWidth / InitialWidth));

    private static int Fit(int initial, int available, int minimum) =>
        available <= 0 ? initial : Math.Max(Math.Min(initial, available), Math.Min(minimum, initial));

    private static int Pixels(int dips, uint dpi)
    {
        ArgumentOutOfRangeException.ThrowIfZero(dpi);
        return checked((int)Math.Ceiling(dips * (dpi / 96d)));
    }
}
