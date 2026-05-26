using Xunit;

namespace OpenClaw.E2ETests;

/// <summary>
/// E2E tests are intentionally opt-in for local development because they
/// provision WSL, launch the tray, and dominate local test runtime. CI sets
/// OPENCLAW_RUN_E2E=1 so these still run before merge.
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (!E2ETestGate.IsEnabled)
            Skip = $"E2E tests disabled. Set {E2ETestGate.EnvVar}=1 to enable.";
    }
}

internal static class E2ETestGate
{
    public const string EnvVar = "OPENCLAW_RUN_E2E";

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable(EnvVar) is { } value &&
        (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
}
