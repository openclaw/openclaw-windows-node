namespace OpenClaw.Shared.ExecApprovals;

// Stateless evaluation function.
// Mirrors macOS ExecHostRequestEvaluator.evaluate(context:approvalDecision:).
// Research doc 06: five-step precedence table - order is fixed and must not be changed.
// The evaluator does not read the store, call the matcher, show UI, or execute commands.
internal static class ExecApprovalEvaluator
{
    // Pure decision function: same inputs always produce the same output, no side effects.
    //
    // Two-pass mechanics (coordinator's responsibility):
    //   pass 1: Evaluate(context, null)           → typically RequiresPrompt
    //   pass 2: Evaluate(context, userDecision)   → Allow or Deny
    // The context is constructed once and reused; only approvalDecision varies between passes.
    internal static ExecHostPolicyDecision Evaluate(
        ExecApprovalEvaluation context,
        ExecApprovalDecision? approvalDecision)
    {
        // Step 1: security=deny is an absolute override. No user approval can bypass it.
        if (context.Security == ExecSecurity.Deny)
            return ExecHostPolicyDecision.Deny(ExecApprovalV2Result.SecurityDeny("security=deny"));

        // Step 2: explicit user denial.
        if (approvalDecision == ExecApprovalDecision.Deny)
            return ExecHostPolicyDecision.Deny(ExecApprovalV2Result.UserDenied("user-denied"));

        // Step 3: prompt required - give the user a chance before checking for an allowlist miss.
        if (RequiresAsk(context.Ask, context.Security, context.AllowlistMatch, context.SkillAllow)
            && approvalDecision is null)
            return ExecHostPolicyDecision.RequiresPrompt;

        // Step 4: allowlist miss - security=allowlist, ask=off, no match, no user approval.
        var approvedByAsk = approvalDecision is not null;
        if (context.Security == ExecSecurity.Allowlist
            && !context.AllowlistSatisfied
            && !context.SkillAllow
            && !approvedByAsk)
            return ExecHostPolicyDecision.Deny(ExecApprovalV2Result.AllowlistMiss("allowlist-miss"));

        // Step 4.5: ask=deny - non-interactive fallback (AskFallback=Deny is the store default).
        // Fires only when no pre-authorization is in place (AllowlistSatisfied=false).
        // When AllowlistSatisfied=true the allowlist already answered the question without
        // any prompt, so AskFallback is irrelevant and execution proceeds to Allow.
        if (context.Ask == ExecAsk.Deny && approvalDecision is null && !context.AllowlistSatisfied)
            return ExecHostPolicyDecision.Deny(ExecApprovalV2Result.AskDeny("ask=deny"));

        // Step 5: allow.
        return ExecHostPolicyDecision.Allow(approvedByAsk);
    }

    // Pure helper for the prompt condition.
    // Research doc 06: four-input boolean function, no I/O, no matching, no store access.
    internal static bool RequiresAsk(
        ExecAsk ask,
        ExecSecurity security,
        ExecAllowlistEntry? allowlistMatch,
        bool skillAllow)
    {
        if (ask == ExecAsk.Always) return true;
        if (ask == ExecAsk.OnMiss
            && security == ExecSecurity.Allowlist
            && allowlistMatch is null
            && !skillAllow) return true;
        return false;
    }
}
