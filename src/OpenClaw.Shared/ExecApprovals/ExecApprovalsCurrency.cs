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
    private readonly HashSet<(string Pattern, string ArgPattern, string Source)> _allowlistPatterns;

    private ExecApprovalsCurrency(
        ExecSecurity security,
        ExecAsk ask,
        ExecSecurity askFallback,
        HashSet<(string Pattern, string ArgPattern, string Source)> allowlistPatterns)
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
    /// raised (higher <see cref="ExecAsk"/>), or any allowlist grant the snapshot carried
    /// now absent - where a grant is the (pattern, argPattern, source) triple, so tightening
    /// an entry counts as revoking it. Additive changes (new entries, looser policy) stay current.
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

    /// <summary>
    /// An allowlist grant is the triple (pattern, argPattern, source), not the pattern
    /// alone: <see cref="ExecAllowlistMatcher"/> narrows a match by <c>argPattern</c> and
    /// skips a generated entry whose <c>source</c> is set but whose <c>argPattern</c> is
    /// missing. Fingerprinting only the pattern would let the owner tighten the very entry
    /// an approval relied on - by adding or narrowing its argPattern, or by marking a
    /// hand-written path-only entry as generated - while the pattern string stayed present,
    /// so the guard would report the policy unchanged and the stale approval would run.
    /// </summary>
    private static HashSet<(string Pattern, string ArgPattern, string Source)> CollectPatterns(
        ExecApprovalsResolved resolved)
    {
        var patterns = new HashSet<(string, string, string)>(GrantIdentityComparer.Instance);
        foreach (var entry in resolved.Allowlist)
        {
            if (!string.IsNullOrWhiteSpace(entry.Pattern))
                patterns.Add((entry.Pattern!, entry.ArgPattern ?? string.Empty, entry.Source ?? string.Empty));
        }
        return patterns;
    }

    /// <summary>
    /// Patterns keep their historical case-insensitive comparison because they name Windows
    /// paths. <c>argPattern</c> is a regex and <c>source</c> is a protocol token, so both are
    /// compared ordinally - case is significant in a regex.
    /// </summary>
    private sealed class GrantIdentityComparer : IEqualityComparer<(string Pattern, string ArgPattern, string Source)>
    {
        internal static readonly GrantIdentityComparer Instance = new();

        public bool Equals(
            (string Pattern, string ArgPattern, string Source) x,
            (string Pattern, string ArgPattern, string Source) y)
            => string.Equals(x.Pattern, y.Pattern, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.ArgPattern, y.ArgPattern, StringComparison.Ordinal)
                && string.Equals(x.Source, y.Source, StringComparison.Ordinal);

        public int GetHashCode((string Pattern, string ArgPattern, string Source) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Pattern),
                StringComparer.Ordinal.GetHashCode(obj.ArgPattern),
                StringComparer.Ordinal.GetHashCode(obj.Source));
    }
}
