namespace OpenClaw.SetupEngine;

/// <summary>Setup window dimensions in DIPs, projected to native window pixels.</summary>
public static class SetupWindowSizing
{
    public const int MinimumWidth = 720;
    public const int MinimumHeight = 560;
    public const int InitialWidth = 720;
    public const int InitialHeight = 820;

    public static (int Width, int Height) MinimumPixels(uint dpi) =>
        (Pixels(MinimumWidth, dpi), Pixels(MinimumHeight, dpi));

    public static (int Width, int Height) InitialPixels(uint dpi) =>
        (Pixels(InitialWidth, dpi), Pixels(InitialHeight, dpi));

    private static int Pixels(int dips, uint dpi)
    {
        ArgumentOutOfRangeException.ThrowIfZero(dpi);
        return checked((int)Math.Ceiling(dips * (dpi / 96d)));
    }
}
