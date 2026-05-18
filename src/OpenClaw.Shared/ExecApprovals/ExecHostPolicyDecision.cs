namespace OpenClaw.Shared.ExecApprovals;

// Outcome of the stateless evaluator. Exactly three cases: deny, requiresPrompt, allow.
// Sealed class hierarchy is the idiomatic C# discriminated-union representation.
// Research doc 06 OQ-SM3: closed for implementation in this PR.
public abstract class ExecHostPolicyDecision
{
    private ExecHostPolicyDecision() { }

    public sealed class DenyOutcome : ExecHostPolicyDecision
    {
        public ExecApprovalV2Result Error { get; }
        internal DenyOutcome(ExecApprovalV2Result error) => Error = error;
    }

    public sealed class RequiresPromptOutcome : ExecHostPolicyDecision
    {
        internal RequiresPromptOutcome() { }
    }

    public sealed class AllowOutcome : ExecHostPolicyDecision
    {
        public bool ApprovedByAsk { get; }
        internal AllowOutcome(bool approvedByAsk) => ApprovedByAsk = approvedByAsk;
    }

    public static ExecHostPolicyDecision Deny(ExecApprovalV2Result error) => new DenyOutcome(error);
    public static readonly ExecHostPolicyDecision RequiresPrompt = new RequiresPromptOutcome();
    public static ExecHostPolicyDecision Allow(bool approvedByAsk) => new AllowOutcome(approvedByAsk);
}
