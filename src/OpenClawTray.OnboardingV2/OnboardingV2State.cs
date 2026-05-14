namespace OpenClawTray.Onboarding.V2;

/// <summary>
/// Mutable state shared across the V2 onboarding flow. Lightweight on
/// purpose — pages only read the bits they need and write back through
/// explicit setter methods so the preview's debug overlay can reset state
/// at will.
///
/// During the inner-loop (preview-project + per-page todos) every field
/// here is set by either the F1 debug overlay or by env vars in headless
/// capture mode. Real services (LocalGatewaySetup, PermissionChecker,
/// GatewayHealthCheck) only get bound at cutover — see plan.md.
/// </summary>
public sealed class OnboardingV2State
{
    public V2Route CurrentRoute { get; set; } = V2Route.Welcome;

    /// <summary>
    /// True when the All Set page should display the amber "Node Mode Active"
    /// warning bar (matches Dialog-4). Driven by the OPENCLAW_PREVIEW_NODE_MODE
    /// env var in capture mode and by the F1 overlay otherwise.
    /// </summary>
    public bool NodeModeActive { get; set; }

    /// <summary>
    /// Stages in the local-setup checklist (Dialog-1 / Dialog-6). Mirrors
    /// the seven rows the designer specified. The FakeLocalSetupEngine
    /// (added in fake-services todo) drives the per-stage status; real
    /// LocalGatewaySetupEngine maps onto the same enum at cutover.
    /// </summary>
    public enum LocalSetupStage
    {
        CheckSystem = 0,
        InstallingUbuntu = 1,
        ConfiguringInstance = 2,
        InstallingOpenClaw = 3,
        PreparingGateway = 4,
        StartingGateway = 5,
        GeneratingSetupCode = 6,
    }

    public enum LocalSetupRowState
    {
        Idle = 0,
        Running = 1,
        Done = 2,
        Failed = 3,
    }

    /// <summary>
    /// Per-stage row state for the LocalSetupProgress page. Default: all idle.
    /// Replaced wholesale on each progress event so consumers re-render.
    /// </summary>
    public IReadOnlyDictionary<LocalSetupStage, LocalSetupRowState> LocalSetupRows { get; set; }
        = Enum.GetValues<LocalSetupStage>().ToDictionary(s => s, _ => LocalSetupRowState.Idle);

    /// <summary>
    /// When non-null, the LocalSetupProgress page renders the inline error
    /// card (Dialog-6) with this message and a "Try again" button.
    /// </summary>
    public string? LocalSetupErrorMessage { get; set; }
}
