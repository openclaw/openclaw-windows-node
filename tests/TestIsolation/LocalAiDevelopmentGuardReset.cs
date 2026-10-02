using System.Runtime.CompilerServices;

namespace OpenClaw.Tests.Isolation;

/// <summary>
/// Clears the Local AI development guard before any test runs, so a developer's user-level
/// OPENCLAW_LOCAL_AI_DEV_LOW_MEMORY_MODELS=1 cannot flip guard-off assertions. Must match
/// LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable. Guard-on tests set it with
/// EnvironmentScope inside a non-parallel collection.
/// </summary>
internal static class LocalAiDevelopmentGuardReset
{
    [ModuleInitializer]
    internal static void Reset() =>
        Environment.SetEnvironmentVariable("OPENCLAW_LOCAL_AI_DEV_LOW_MEMORY_MODELS", null);
}
