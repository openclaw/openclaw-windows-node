namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    private const int ChannelPairingTimeoutMs = 15000;

    public IChannelPairingGateway CreateChannelPairingGateway(int timeoutMs = ChannelPairingTimeoutMs) =>
        new GatewayChannelPairingClient(
            () => SessionMutationConnectionEpoch,
            (method, parameters, epoch) =>
            {
                if (!AdvertisedServerMethods.Contains(method, StringComparer.Ordinal))
                    throw new NotSupportedException("The Gateway does not advertise this channel pairing method.");
                if (!GrantedOperatorScopes.Contains("operator.pairing", StringComparer.Ordinal) &&
                    !GrantedOperatorScopes.Contains("operator.admin", StringComparer.Ordinal))
                    throw new UnauthorizedAccessException("Channel pairing requires operator.pairing.");
                return SendResponseRequestAsync(method, parameters, timeoutMs, epoch);
            });
}
