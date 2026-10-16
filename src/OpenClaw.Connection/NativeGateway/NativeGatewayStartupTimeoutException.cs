namespace OpenClaw.Connection.NativeGateway;

public sealed class NativeGatewayStartupTimeoutException(Exception? innerException = null) : TimeoutException(
    "The package-managed Gateway startup or listener confirmation timed out. Check clawctl gateway-service status before retrying.",
    innerException);
