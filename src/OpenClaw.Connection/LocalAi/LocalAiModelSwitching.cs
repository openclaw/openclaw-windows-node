using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Connection.LocalAi;

/// <summary>A model change was refused or failed; the message is user-facing.</summary>
public sealed class LocalAiModelSwitchException(string message) : InvalidOperationException(message);

public enum LocalAiModelSwitchPhase
{
    Resolving,
    Downloading,
    Verifying,
    InstallingRuntime,
    StoppingRuntime,
    StartingRuntime,
    RollingBack,
}

public sealed record LocalAiModelSwitchProgress(
    LocalAiModelSwitchPhase Phase,
    long CompletedBytes = 0,
    long TotalBytes = 0);

/// <summary>A qualified (or, with <see cref="RequiresForce"/>, user-overridable) switch target.</summary>
public sealed record LocalAiModelSwitchTarget(
    LocalInferencePlan Plan,
    string GpuStableId,
    string GpuName,
    long RequiredMemoryBytes,
    long? DetectedMemoryBytes,
    bool RequiresForce);

/// <summary>In-place model change for an existing managed Local AI installation.</summary>
public interface ILocalAiModelSwitcher
{
    Task<LocalInferencePlan?> LoadInstalledPlanAsync(CancellationToken ct);
    Task<LocalAiModelSwitchTarget> PrepareCatalogAsync(HostHardwareInfo hardware, string modelId, CancellationToken ct);
    Task<LocalAiModelSwitchTarget> PrepareCustomAsync(HostHardwareInfo hardware, string reference, CancellationToken ct);
    Task<LocalAiRuntimeSnapshot> SwitchAsync(
        LocalAiModelSwitchTarget target,
        bool force,
        IProgress<LocalAiModelSwitchProgress>? progress,
        CancellationToken ct);
}
