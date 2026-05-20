namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Decision returned by <see cref="IExecApprovalV2PromptHandler"/>.
/// Deny is the zero/default value so uninitialized instances fail-closed.
/// </summary>
public enum ExecApprovalPromptOutcome
{
    Deny = 0,
    Allow,
    AllowOnce,
    AllowAlways,
}
