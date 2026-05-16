using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Wraps <see cref="OpenClawGatewayClient"/> behind <see cref="IGatewayClientLifecycle"/>.
/// Creates a real WebSocket-connected client instance.
/// </summary>
public sealed class GatewayClientFactory : IGatewayClientFactory
{
    public IGatewayClientLifecycle Create(
        string gatewayUrl,
        GatewayCredential credential,
        string identityPath,
        IOpenClawLogger logger)
    {
        var client = new OpenClawGatewayClient(
            gatewayUrl,
            credential.Token,
            logger,
            tokenIsBootstrapToken: credential.IsBootstrapToken,
            identityPath: identityPath);

        return new GatewayClientLifecycleAdapter(client);
    }
}

/// <summary>
/// Adapts <see cref="OpenClawGatewayClient"/> (which inherits from
/// <see cref="WebSocketClientBase"/>) to the <see cref="IGatewayClientLifecycle"/> interface.
/// </summary>
internal sealed class GatewayClientLifecycleAdapter : IGatewayClientLifecycle
{
    private readonly OpenClawGatewayClient _client;

    public GatewayClientLifecycleAdapter(OpenClawGatewayClient client)
    {
        _client = client;
        // Forward events from WebSocketClientBase
        _client.StatusChanged += (s, e) => StatusChanged?.Invoke(this, e);
        _client.AuthenticationFailed += (s, e) => AuthenticationFailed?.Invoke(this, e);
    }

    public OpenClawGatewayClient DataClient => _client;

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<string>? AuthenticationFailed;

    public Task ConnectAsync(CancellationToken ct) => _client.ConnectAsync();

    public void Dispose() => _client.Dispose();
}
