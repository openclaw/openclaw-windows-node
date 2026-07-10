using System.Diagnostics;

namespace OpenClaw.Shared.Telemetry;

public enum OpenClawActivitySourceName
{
    OpenClaw
}

/// <summary>
/// Stable ActivitySource names used by OpenClaw instrumentation.
/// </summary>
public static class OpenClawActivitySources
{
    public static ActivitySource OpenClawSource { get; } = new(OpenClawActivitySourceName.OpenClaw.ToTelemetryName());

    public static string ToTelemetryName(this OpenClawActivitySourceName source) =>
        source switch
        {
            OpenClawActivitySourceName.OpenClaw => "openclaw",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown OpenClaw activity source.")
        };

    internal static ActivitySource ToActivitySource(this OpenClawActivitySourceName source) =>
        source switch
        {
            OpenClawActivitySourceName.OpenClaw => OpenClawSource,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown OpenClaw activity source.")
        };
}
