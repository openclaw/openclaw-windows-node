namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    public async Task<GatewayHeartbeatEvent?> GetLastHeartbeatAsync(
        long connectionEpoch, CancellationToken cancellationToken = default)
    {
        const string method = "last-heartbeat";
        cancellationToken.ThrowIfCancellationRequested();
        if (SessionMutationConnectionEpoch != connectionEpoch)
            throw new InvalidOperationException("The authenticated Gateway connection changed.");
        if (!AdvertisedServerMethods.Contains(method, StringComparer.Ordinal))
            throw new NotSupportedException("The Gateway does not advertise last-heartbeat.");
        // Pinned operator-scope-compat.ts: general write and admin imply general read.
        if (!GrantedOperatorScopes.Any(scope => scope is "operator.read" or "operator.write" or "operator.admin"))
            throw new UnauthorizedAccessException("last-heartbeat requires operator.read.");
        var payload = await SendResponseRequestAsync(
            method, new { }, DefaultHeartbeatTimeoutMs, connectionEpoch, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (SessionMutationConnectionEpoch != connectionEpoch)
            throw new InvalidOperationException("The authenticated Gateway connection changed.");
        return GatewayHeartbeatEvent.Parse(payload);
    }

    private const int DefaultHeartbeatTimeoutMs = 15000;
}
