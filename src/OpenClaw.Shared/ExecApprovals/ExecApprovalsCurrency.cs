using System;
using System.Collections.Generic;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Snapshot of the effective exec-approval policy taken when a request is authorized, used
/// to detect a mid-approval policy change before execution. Mirrors the macOS
/// policy-snapshot currency guard: additive/looser changes stay current,
/// but tightening (more restrictive security, a higher ask mode) or revoking an allowlist
/// entry the approval relied on must fail closed. This closes the window between reading
/// the policy and executing a human-approved command, during which the node owner could
/// tighten the policy while the prompt is open.
/// </summary>
internal sealed class ExecApprovalsCurrency
{
    private readonly ExecSecurity _security;
    private readonly ExecAsk _ask;
    private readonly ExecSecurity _askFallback;
    private readonly HashSet<string> _allowlistPatterns;

    private ExecApprovalsCurrency(
        ExecSecurity security,
        ExecAsk ask,
        ExecSecurity askFallback,
        HashSet<string> allowlistPatterns)
    {
        _security = security;
        _ask = ask;
        _askFallback = askFallback;
        _allowlistPatterns = allowlistPatterns;
    }

    public static ExecApprovalsCurrency Capture(ExecApprovalsResolved resolved)
        => new(
            resolved.Defaults.Security,
            resolved.Defaults.Ask,
            resolved.Defaults.AskFallback,
            CollectPatterns(resolved));

    /// <summary>
    /// True when <paramref name="fresh"/> has not tightened relative to the snapshot.
    /// Fails on: security made more restrictive (lower <see cref="ExecSecurity"/>), ask
    /// raised (higher <see cref="ExecAsk"/>), or any allowlist pattern the snapshot carried
    /// now absent. Additive changes (new entries, looser policy) stay current.
    /// </summary>
    public bool IsStillCurrent(ExecApprovalsResolved fresh)
    {
        // ExecSecurity: Deny(0) < Allowlist(1) < Full(2). A lower value is more restrictive.
        if (fresh.Defaults.Security < _security)
            return false;

        // ExecAsk: Off(0) < OnMiss(1) < Always(2) < Deny(3). A higher value denies more.
        if (fresh.Defaults.Ask > _ask)
            return false;

        // AskFallback uses ExecSecurity ordering. A lower value is more restrictive.
        if (fresh.Defaults.AskFallback < _askFallback)
            return false;

        if (_allowlistPatterns.Count > 0)
        {
            var freshPatterns = CollectPatterns(fresh);
            foreach (var pattern in _allowlistPatterns)
            {
                if (!freshPatterns.Contains(pattern))
                    return false;
            }
        }

        return true;
    }

    private static HashSet<string> CollectPatterns(ExecApprovalsResolved resolved)
    {
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in resolved.Allowlist)
        {
            if (!string.IsNullOrWhiteSpace(entry.Pattern))
                patterns.Add(entry.Pattern!);
        }
        return patterns;
    }
}
