using Xunit;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.E2ETests;

/// <summary>E2E fixtures provision WSL and launch apps. They require a separately authorized opt-in.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (!E2ETestGate.IsEnabled)
            Skip = $"E2E tests disabled. Set {E2ETestGate.EnvVar}=1 to enable.";
    }
}

public sealed class OllamaGatewayE2EFactAttribute : FactAttribute
{
    public const string EnableEnvVar = "OPENCLAW_RUN_OLLAMA_E2E";
    public OllamaGatewayE2EFactAttribute()
    {
        if (!E2ETestGate.IsEnabled)
            Skip = $"E2E tests disabled. Set {E2ETestGate.EnvVar}=1 to enable.";
        else if (!E2ETestGate.IsSet("GITHUB_ACTIONS") && !E2ETestGate.IsSet(EnableEnvVar))
            Skip = $"Ollama E2E proof disabled. Set {EnableEnvVar}=1 to enable.";
    }
}

public sealed class MxcE2EFactAttribute : FactAttribute
{
    public MxcE2EFactAttribute() => Skip = MxcE2ETestGate.SkipReason;
}

internal static class MxcE2ETestGate
{
    private static readonly Lazy<string?> Skip = new(GetSkipReason);
    public static string? SkipReason => Skip.Value;
    private static string? GetSkipReason()
    {
        if (!E2ETestGate.IsEnabled)
            return $"E2E tests disabled. Set {E2ETestGate.EnvVar}=1 to enable.";
        if (E2ETestGate.IsSet("GITHUB_ACTIONS") && !E2ETestGate.IsSet("OPENCLAW_RUN_MXC_E2E"))
            return "MXC E2E proof requires an explicitly enabled MXC-capable runner.";
        var availability = MxcAvailability.Probe();
        return availability.CanRunSystemRunSandbox ? null :
            $"MXC E2E proof not exercised: {string.Join(" ", availability.SystemRunSandboxUnsupportedReasons)}";
    }
}

internal static class E2ETestGate
{
    public const string EnvVar = "OPENCLAW_RUN_E2E";
    public static bool IsEnabled => IsSet(EnvVar);
    internal static bool IsSet(string name) => Environment.GetEnvironmentVariable(name) is { } value &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}
