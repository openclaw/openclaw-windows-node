namespace OpenClaw.Connection;

public static class OperatorScopeHelper
{
    public static bool CanApproveDevices(IReadOnlyList<string> grantedScopes) =>
        grantedScopes.Any(s =>
            s.Equals("operator.admin", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("operator.pairing", StringComparison.OrdinalIgnoreCase));
}
