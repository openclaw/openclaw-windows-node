using System.Diagnostics;

namespace OpenClaw.Shared.Telemetry;

/// <summary>
/// Stable ActivitySource names used by OpenClaw instrumentation.
/// </summary>
public static class OpenClawActivitySources
{
    public const string OpenClaw = "openclaw";

    public static ActivitySource OpenClawSource { get; } = new(OpenClaw);

    public static IReadOnlyList<string> ExportedNames { get; } = Array.AsReadOnly([OpenClaw]);
}
