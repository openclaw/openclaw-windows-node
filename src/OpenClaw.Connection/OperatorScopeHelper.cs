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

    private static bool HasScope(IReadOnlyList<string> grantedScopes, string scope) =>
        grantedScopes.Any(s => s.Equals(scope, StringComparison.OrdinalIgnoreCase));
}
