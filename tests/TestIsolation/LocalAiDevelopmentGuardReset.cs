using System.Runtime.CompilerServices;

namespace OpenClaw.Tests.Isolation;

/// <summary>
/// Forces the Local AI low-memory models opt-out before any test runs, so a developer's
/// user-level setting cannot flip guard-off assertions. The low-memory models are
/// offered by default (see LocalModelCatalog.DevelopmentLowMemoryModelsEnabled), so the
/// deterministic guard-off state is "0", not unset. Must match
/// LocalModelCatalog.DevelopmentLowMemoryModelsEnvironmentVariable. Guard-on tests set
/// it with EnvironmentScope inside a non-parallel collection.
/// </summary>
internal static class LocalAiDevelopmentGuardReset
{
    [ModuleInitializer]
    internal static void Reset() =>
        Environment.SetEnvironmentVariable("OPENCLAW_LOCAL_AI_DEV_LOW_MEMORY_MODELS", "0");
}
