namespace OpenClaw.SetupEngine;

public enum GatewayAiDiscoveryFailure { Unavailable, InvalidResponse }

/// <summary>Single-transfer authenticated discovery, owned by the preparation surface until Take.</summary>
public sealed class GatewayAiPreparation : IAsyncDisposable
{
    private readonly IGatewayAiSetupTransport _transport;
    private readonly IAsyncDisposable _owner;
    private readonly GatewayAiSetupRoute _route;
    private readonly long _generation;
    private bool _released;
    public GatewayAiSetupClient Client { get; }
    public GatewayAiDiscoveryFailure? DiscoveryFailure { get; private set; }

    private GatewayAiPreparation(IGatewayAiSetupTransport transport, IAsyncDisposable owner, GatewayAiSetupClient client)
    {
        _transport = transport;
        _owner = owner;
        Client = client;
        _route = transport.Route;
        _generation = transport.Generation;
    }

    public static async Task<GatewayAiPreparation> PrepareNativeAsync(NativeGatewaySetupSession session, CancellationToken ct,
        IProgress<SetupLoadingStep>? progress = null)
    {
        var connection = await NativeGatewaySetupConnection.ConnectAsync(session, ct, progress);
        return await PrepareAsync(connection, connection, ct, progress);
    }

    public static async Task<GatewayAiPreparation> PrepareAsync(
        IGatewayAiSetupTransport transport, IAsyncDisposable owner, CancellationToken ct,
        IProgress<SetupLoadingStep>? progress = null)
    {
        try
        {
            var client = new GatewayAiSetupClient(transport);
            var preparation = new GatewayAiPreparation(transport, owner, client);
            try
            {
                progress?.Report(SetupLoadingStep.DiscoverChoices);
                await client.DetectAsync(ct);
            }
            catch (Exception error) when (error is TimeoutException or IOException or
                InvalidDataException or System.Text.Json.JsonException)
            {
                // Discovery is read-only. Retain only this explicitly classified
                // failure, and only while the original authenticated authority is intact.
                ct.ThrowIfCancellationRequested();
                preparation.RequireCurrent();
                preparation.DiscoveryFailure = error is InvalidDataException or System.Text.Json.JsonException
                    ? GatewayAiDiscoveryFailure.InvalidResponse : GatewayAiDiscoveryFailure.Unavailable;
            }
            ct.ThrowIfCancellationRequested();
            preparation.RequireCurrent();
            return preparation;
        }
        catch
        {
            await owner.DisposeAsync();
            throw;
        }
    }

    public (IGatewayAiSetupTransport Transport, IAsyncDisposable Owner, GatewayAiSetupClient Client,
        GatewayAiDiscoveryFailure? DiscoveryFailure) Take()
    {
        RequireCurrent();
        _released = true;
        return (_transport, _owner, Client, DiscoveryFailure);
    }

    private void RequireCurrent()
    {
        if (_released || !_transport.IsConnected || !_transport.OperatorScopes.Contains("operator.admin", StringComparer.Ordinal) ||
            _generation <= 0 ||
            _transport.Generation != _generation || _transport.Route != _route)
            throw new SetupNativeOwnershipException();
    }

    public async ValueTask DisposeAsync()
    {
        if (_released) return;
        _released = true;
        await _owner.DisposeAsync();
    }
}
