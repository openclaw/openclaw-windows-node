namespace OpenClaw.Connection;

public enum GatewayProxyCredentialFailure
{
    Origin,
    InvalidToken,
    SavedCredential,
    SshCombination,
}

/// <summary>Finite proxy failures for native presentation without parsing diagnostic text.</summary>
public sealed class GatewayProxyCredentialException(
    GatewayProxyCredentialFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public GatewayProxyCredentialFailure Failure { get; } = failure;
}
