// Read-only Systems Gateway transport. Usage: await SystemsGatewayClient.For(client).LoadInventoryAsync(token).
using System.Text.Json;

namespace OpenClaw.Shared.Systems;

public sealed record NodeHostStatsEvent(string NodeId, NodeHostStats Stats);

/// <summary>Read-only Gateway requests behind the Systems page. Node management uses the operator client.</summary>
public interface ISystemsGateway
{
    /// <summary>
    /// Reads environments, nodes and Gateway host info together. An inventory failure throws;
    /// node and host-info failures are reported as partial errors.
    /// </summary>
    Task<SystemsInventorySnapshot> LoadInventoryAsync(CancellationToken cancellationToken);
    Task<(GatewaySystemInfo Info, DateTimeOffset SampledAt)?> ReadSystemInfoAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<GatewayNodeInfo>> ReadNodesAsync(CancellationToken cancellationToken);
}

/// <summary>Request-delegate implementation so tests can drive it without a socket.</summary>
public sealed class SystemsGatewayClient : ISystemsGateway
{
    public const int RequestTimeoutMs = 15_000;
    private readonly Func<string, object?, int, Task<JsonElement>> _request;
    private readonly Func<DateTimeOffset> _clock;

    public SystemsGatewayClient(Func<string, object?, int, Task<JsonElement>> request, Func<DateTimeOffset>? clock = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static SystemsGatewayClient For(IOperatorGatewayClient client) =>
        new((method, parameters, timeoutMs) => client.SendWizardRequestAsync(method, parameters, timeoutMs));

    public async Task<SystemsInventorySnapshot> LoadInventoryAsync(CancellationToken cancellationToken)
    {
        var environmentsTask = RequestOptionalAsync("environments.list", new { }, cancellationToken);
        var nodesTask = ReadNodesAsync(cancellationToken);
        var infoTask = ReadSystemInfoAsync(cancellationToken);
        try
        {
            await Task.WhenAll(environmentsTask, nodesTask, infoTask).ConfigureAwait(false);
        }
        catch
        {
            // Each task is inspected below; the inventory read alone decides whether the refresh fails.
        }

        var environmentsPayload = await environmentsTask.ConfigureAwait(false);
        var environments = environmentsPayload is { } payload ? SystemsParser.ParseEnvironments(payload) : null;
        cancellationToken.ThrowIfCancellationRequested();

        return new SystemsInventorySnapshot
        {
            Environments = environments,
            Nodes = nodesTask.IsCompletedSuccessfully ? nodesTask.Result : Array.Empty<GatewayNodeInfo>(),
            SystemInfo = infoTask.IsCompletedSuccessfully ? infoTask.Result?.Info : null,
            SystemInfoSampledAt = infoTask.IsCompletedSuccessfully ? infoTask.Result?.SampledAt : null,
            Errors = new SystemsPartialErrors(
                Nodes: ErrorText(nodesTask),
                SystemInfo: ErrorText(infoTask)),
        };
    }

    public async Task<(GatewaySystemInfo Info, DateTimeOffset SampledAt)?> ReadSystemInfoAsync(CancellationToken cancellationToken)
    {
        var payload = await RequestOptionalAsync("system.info", new { }, cancellationToken).ConfigureAwait(false);
        if (payload is not { } value)
            return null;
        var info = SystemsParser.ParseSystemInfo(value)
            ?? throw new InvalidOperationException("The Gateway returned invalid system information.");
        return (info, _clock());
    }

    public async Task<IReadOnlyList<GatewayNodeInfo>> ReadNodesAsync(CancellationToken cancellationToken)
    {
        var payload = await RequestOptionalAsync("node.list", new { }, cancellationToken).ConfigureAwait(false);
        if (payload is not { } value)
            return Array.Empty<GatewayNodeInfo>();
        return OpenClawGatewayClient.ParseNodeListPayload(value)
            ?? throw new InvalidOperationException("The Gateway returned an invalid node list.");
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _request(method, parameters, RequestTimeoutMs).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Unknown methods mean an older Gateway, not a failure.</summary>
    private async Task<JsonElement?> RequestOptionalAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        try
        {
            return await RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (OpenClawGatewayClient.IsUnknownMethodError(ex.Message))
        {
            return null;
        }
    }

    private static string? ErrorText(Task task) =>
        task.IsFaulted ? task.Exception?.InnerException?.Message ?? "Request failed." : null;
}
