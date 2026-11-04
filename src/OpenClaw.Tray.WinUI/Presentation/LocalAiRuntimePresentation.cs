// <summary>
// Projects Local AI runtime evidence into presentation-level ownership decisions shared by pages.
// A managed install requires a verified model or a non-missing companion-managed runtime.
// Usage:
//   bool managed = LocalAiRuntimePresentation.HasManagedInstall(runtime.Snapshot);
//   if (managed) ShowLocalAiControls();
// </summary>

using OpenClaw.Connection.LocalAi;

namespace OpenClawTray.Presentation;

/// <summary>Shared Local AI runtime facts used by the Local AI and Default model pages.</summary>
internal static class LocalAiRuntimePresentation
{
    /// <summary>Returns whether a verified model or companion-managed runtime proves Local AI ownership.</summary>
    public static bool HasManagedInstall(LocalAiRuntimeSnapshot snapshot) =>
        snapshot.ModelEvidence.State is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded ||
        (snapshot.Ownership == LocalAiOwnership.CompanionManaged &&
         snapshot.State != LocalAiRuntimeState.NotInstalled);
}
