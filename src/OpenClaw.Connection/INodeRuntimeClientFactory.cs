using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Creates the node runtime used by <see cref="NodeConnector"/>.
/// </summary>
public interface INodeRuntimeClientFactory
{
    INodeRuntimeClient Create(
        string gatewayUrl,
        GatewayCredential credential,
        string identityPath,
        IOpenClawLogger logger);
}

/// <summary>
/// Preserves the existing in-process C# node runtime as the default.
/// </summary>
public sealed class WindowsNodeRuntimeClientFactory : INodeRuntimeClientFactory
{
    public INodeRuntimeClient Create(
        string gatewayUrl,
        GatewayCredential credential,
        string identityPath,
        IOpenClawLogger logger) =>
        new WindowsNodeClient(
            gatewayUrl,
            credential.IsBootstrapToken ? "" : credential.Token,
            identityPath,
            logger,
            bootstrapToken: credential.IsBootstrapToken ? credential.Token : null);
}
