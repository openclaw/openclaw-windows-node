namespace OpenClaw.SetupEngine;

internal static class LocalAiAvailabilityReasons
{
    public static string? Build(
        string? hardwareReason,
        string? wslNetworkingReason)
    {
        var reasons = new List<string>(capacity: 2);

        if (!string.IsNullOrWhiteSpace(hardwareReason))
            reasons.Add($"Hardware: {hardwareReason.Trim()}");
        if (!string.IsNullOrWhiteSpace(wslNetworkingReason))
            reasons.Add($"WSL networking: {wslNetworkingReason.Trim()}");

        return reasons.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, reasons);
    }
}
