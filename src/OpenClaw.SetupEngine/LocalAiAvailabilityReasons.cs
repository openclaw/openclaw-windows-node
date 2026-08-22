namespace OpenClaw.SetupEngine;

internal static class LocalAiAvailabilityReasons
{
    public static string? Build(
        string? hardwareReason,
        WslViabilityResult wslViability,
        string? wslNetworkingReason)
    {
        ArgumentNullException.ThrowIfNull(wslViability);
        var reasons = new List<string>(capacity: 3);

        if (!string.IsNullOrWhiteSpace(hardwareReason))
            reasons.Add($"Hardware: {hardwareReason.Trim()}");
        if (wslViability.BlocksSetup)
            reasons.Add($"WSL: {wslViability.Description}");
        if (!string.IsNullOrWhiteSpace(wslNetworkingReason))
            reasons.Add($"WSL networking: {wslNetworkingReason.Trim()}");

        return reasons.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, reasons);
    }
}
