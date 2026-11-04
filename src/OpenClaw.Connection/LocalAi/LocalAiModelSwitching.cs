// <summary>
// Defines the model-switching contract for an existing companion-managed Local AI installation.
// A switcher qualifies a requested target, serializes the destructive replacement, and reports progress.
// Callers must prepare a target against current hardware before switching and may force an overridable fit warning.
// Usage:
//   LocalAiModelSwitchTarget target = await switcher.PrepareCatalogAsync(hardware, modelId, cancellationToken);
//   LocalAiRuntimeSnapshot snapshot = await switcher.SwitchAsync(target, force: false, progress, cancellationToken);
//   // A LocalAiModelSwitchException exposes a user-facing refusal or recovery failure.
// </summary>
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Connection.LocalAi;

/// <summary>Represents a user-facing refusal or failure while changing a managed Local AI model.</summary>
public sealed class LocalAiModelSwitchException(string message) : InvalidOperationException(message);

/// <summary>Identifies the durable stages reported while a Local AI model is replaced.</summary>
public enum LocalAiModelSwitchPhase
{
    Resolving,
    Downloading,
    Verifying,
    InstallingRuntime,
    StoppingRuntime,
    StartingRuntime,
    LoadingModel,
    RollingBack,
}

/// <summary>Reports the current model-switch phase and optional byte progress.</summary>
public sealed record LocalAiModelSwitchProgress(
    LocalAiModelSwitchPhase Phase,
    long CompletedBytes = 0,
    long TotalBytes = 0);

/// <summary>Describes a qualified model replacement target and any hardware-fit override required to apply it.</summary>
public sealed record LocalAiModelSwitchTarget(
    LocalInferencePlan Plan,
    string GpuStableId,
    string GpuName,
    long RequiredMemoryBytes,
    long? DetectedMemoryBytes,
    bool RequiresForce);

/// <summary>Qualifies and applies in-place model changes for an existing managed Local AI installation.</summary>
public interface ILocalAiModelSwitcher
{
    /// <summary>Loads the qualified plan for the installed model, or null when Local AI is not installed.</summary>
    Task<LocalInferencePlan?> LoadInstalledPlanAsync(CancellationToken ct);
    /// <summary>Qualifies a catalog model against the selected host hardware.</summary>
    Task<LocalAiModelSwitchTarget> PrepareCatalogAsync(HostHardwareInfo hardware, string modelId, CancellationToken ct);
    /// <summary>Qualifies a custom model reference against the selected host hardware.</summary>
    Task<LocalAiModelSwitchTarget> PrepareCustomAsync(HostHardwareInfo hardware, string reference, CancellationToken ct);
    /// <summary>Replaces the installed model with a prepared target and returns the resulting runtime state.</summary>
    Task<LocalAiRuntimeSnapshot> SwitchAsync(
        LocalAiModelSwitchTarget target,
        bool force,
        IProgress<LocalAiModelSwitchProgress>? progress,
        CancellationToken ct);

    /// <summary>True while a <see cref="SwitchAsync"/> call is running, for any caller.</summary>
    bool IsSwitching { get; }

    /// <summary>Raised on the switching thread after <see cref="IsSwitching"/> changes.</summary>
    event EventHandler? IsSwitchingChanged;
}
