using OpenClaw.Connection.LocalAi;

namespace OpenClawTray.Presentation;

/// <summary>Shared Local AI runtime facts used by the Local AI and Default model pages.</summary>
internal static class LocalAiRuntimePresentation
{
    /// <summary>A managed receipt exists: the model was verified, or the companion owns a non-missing runtime.</summary>
    public static bool HasManagedInstall(LocalAiRuntimeSnapshot snapshot) =>
        snapshot.ModelEvidence.State is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded ||
        (snapshot.Ownership == LocalAiOwnership.CompanionManaged &&
         snapshot.State != LocalAiRuntimeState.NotInstalled);
}
