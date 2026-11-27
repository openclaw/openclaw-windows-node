// Defines the persisted mascot-only scale contract. Use Normalize before saving a percentage.
namespace OpenClaw.Shared;

/// <summary>Supported desktop lobster sizes, independent of popup text and Windows display scaling.</summary>
public static class DesktopCompanionScale
{
    public const int MinimumPercent = 50;
    public const int MaximumPercent = 200;
    public const int StepPercent = 25;
    public const int DefaultPercent = 100;

    /// <summary>Clamps a stored percentage and rounds it to the nearest supported size.</summary>
    public static int Normalize(int percent)
    {
        var bounded = Math.Clamp(percent, MinimumPercent, MaximumPercent);
        return (bounded + StepPercent / 2) / StepPercent * StepPercent;
    }
}
