namespace OpenClaw.Connection;

public static class OperatorScopeHelper
{
    public static bool CanApproveDevices(IReadOnlyList<string> grantedScopes) =>
        grantedScopes.Any(s =>
            s.Equals("operator.admin", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("operator.pairing", StringComparison.OrdinalIgnoreCase));

    public static bool CanReadConfig(IReadOnlyList<string> grantedScopes) =>
        HasScope(grantedScopes, "operator.admin") ||
        HasScope(grantedScopes, "operator.read");

    public static bool CanWriteConfig(IReadOnlyList<string> grantedScopes) =>
        HasScope(grantedScopes, "operator.admin") ||
        HasScope(grantedScopes, "operator.write");

    private static bool HasScope(IReadOnlyList<string> grantedScopes, string scope) =>
        grantedScopes.Any(s => s.Equals(scope, StringComparison.OrdinalIgnoreCase));
}
