namespace OpenClaw.Connection;

internal enum ConnectionCredentialRole
{
    Operator,
    Node
}

internal static class CredentialResolutionFailureFormatter
{
    private const string MissingNodeCredentialMessage =
        "No node credential available. Re-pair this PC or add a shared/bootstrap gateway token.";

    internal static string Format(
        ConnectionCredentialRole role,
        GatewayCredentialResolution resolution)
    {
        var isNode = role == ConnectionCredentialRole.Node;
        var prefix = isNode
            ? "No node credential available"
            : "No operator credential available";
        return resolution.Status switch
        {
            GatewayCredentialResolutionStatus.Corrupt =>
                $"{prefix}: stored device token is corrupt. Re-pair this PC or add a shared/bootstrap gateway token.",
            GatewayCredentialResolutionStatus.Unreadable =>
                $"{prefix}: stored device token is unreadable. Check file permissions, re-pair this PC, or add a shared/bootstrap gateway token.",
            GatewayCredentialResolutionStatus.Missing => isNode
                ? MissingNodeCredentialMessage
                : $"{prefix}. Add a shared/bootstrap gateway token or re-pair this PC.",
            _ when !string.IsNullOrWhiteSpace(resolution.Detail) =>
                $"{prefix}. {resolution.Detail}",
            _ => isNode
                ? MissingNodeCredentialMessage
                : $"{prefix}. Add a shared/bootstrap gateway token or re-pair this PC."
        };
    }
}
