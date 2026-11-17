namespace OpenClaw.Connection;

public static class OperatorScopeHelper
{
    private const string AdminScope = "operator.admin";
    private const string PairingScope = "operator.pairing";
    private const string ReadScope = "operator.read";
    private const string WriteScope = "operator.write";

    public static bool CanApproveDevices(IReadOnlyList<string> grantedScopes) =>
        HasAdminScope(grantedScopes) ||
        HasScope(grantedScopes, PairingScope);

    public static bool CanReadConfig(IReadOnlyList<string> grantedScopes) =>
        HasAdminScope(grantedScopes) ||
        HasScope(grantedScopes, ReadScope);

    public static bool CanWriteConfig(IReadOnlyList<string> grantedScopes) =>
        HasAdminScope(grantedScopes) ||
        HasScope(grantedScopes, WriteScope);

    public static bool HasAdminScope(IReadOnlyList<string> grantedScopes) =>
        HasScope(grantedScopes, AdminScope);

    /// <summary>
    /// Mirrors the Gateway's <c>operatorScopeSatisfied</c>: an exact grant, admin, or write for
    /// read-level scopes. Matching is ordinal, as on the Gateway.
    /// </summary>
    public static bool Satisfies(IReadOnlyList<string> grantedScopes, string requestedScope)
    {
        var granted = grantedScopes.Select(scope => scope.Trim()).ToArray();
        return granted.Contains(requestedScope, StringComparer.Ordinal) ||
            granted.Contains(AdminScope, StringComparer.Ordinal) ||
            (requestedScope is ReadScope or "operator.talk" or "operator.sessions.read" or "operator.sessions.write" &&
                granted.Contains(WriteScope, StringComparer.Ordinal)) ||
            (requestedScope == "operator.sessions.read" &&
                (granted.Contains(ReadScope, StringComparer.Ordinal) || granted.Contains("operator.sessions.write", StringComparer.Ordinal)));
    }

    private static bool HasScope(IReadOnlyList<string> grantedScopes, string scope) =>
        grantedScopes.Any(s => s.Equals(scope, StringComparison.OrdinalIgnoreCase));
}
