namespace OpenClaw.Connection.Tests;

public sealed class CredentialResolutionFailureFormatterTests
{
    public static TheoryData<
        string,
        GatewayCredentialResolutionStatus,
        string?,
        string> ExactMessages => new()
    {
        {
            "operator",
            GatewayCredentialResolutionStatus.Missing,
            null,
            "No operator credential available. Add a shared/bootstrap gateway token or re-pair this PC."
        },
        {
            "node",
            GatewayCredentialResolutionStatus.Missing,
            null,
            "No node credential available. Re-pair this PC or add a shared/bootstrap gateway token."
        },
        {
            "operator",
            GatewayCredentialResolutionStatus.Corrupt,
            null,
            "No operator credential available: stored device token is corrupt. Re-pair this PC or add a shared/bootstrap gateway token."
        },
        {
            "node",
            GatewayCredentialResolutionStatus.Corrupt,
            null,
            "No node credential available: stored device token is corrupt. Re-pair this PC or add a shared/bootstrap gateway token."
        },
        {
            "operator",
            GatewayCredentialResolutionStatus.Unreadable,
            null,
            "No operator credential available: stored device token is unreadable. Check file permissions, re-pair this PC, or add a shared/bootstrap gateway token."
        },
        {
            "node",
            GatewayCredentialResolutionStatus.Unreadable,
            null,
            "No node credential available: stored device token is unreadable. Check file permissions, re-pair this PC, or add a shared/bootstrap gateway token."
        },
        {
            "operator",
            GatewayCredentialResolutionStatus.Resolved,
            "Resolution detail.",
            "No operator credential available. Resolution detail."
        },
        {
            "node",
            GatewayCredentialResolutionStatus.Resolved,
            "Resolution detail.",
            "No node credential available. Resolution detail."
        },
        {
            "operator",
            GatewayCredentialResolutionStatus.FallbackUsed,
            "Fallback detail.",
            "No operator credential available. Fallback detail."
        },
        {
            "node",
            GatewayCredentialResolutionStatus.FallbackUsed,
            "Fallback detail.",
            "No node credential available. Fallback detail."
        },
        {
            "operator",
            GatewayCredentialResolutionStatus.FallbackUsed,
            null,
            "No operator credential available. Add a shared/bootstrap gateway token or re-pair this PC."
        },
        {
            "node",
            GatewayCredentialResolutionStatus.FallbackUsed,
            null,
            "No node credential available. Re-pair this PC or add a shared/bootstrap gateway token."
        }
    };

    [Theory]
    [MemberData(nameof(ExactMessages))]
    public void Format_ReturnsExactRoleSpecificMessage(
        string role,
        GatewayCredentialResolutionStatus status,
        string? detail,
        string expected)
    {
        var resolution = new GatewayCredentialResolution(
            Credential: null,
            Status: status,
            FallbackUsed: status == GatewayCredentialResolutionStatus.FallbackUsed,
            Detail: detail);

        var message = CredentialResolutionFailureFormatter.Format(
            role == "node"
                ? ConnectionCredentialRole.Node
                : ConnectionCredentialRole.Operator,
            resolution);

        Assert.Equal(expected, message);
    }
}
