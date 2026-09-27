using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;

namespace OpenClawTray.Presentation;

internal sealed record SetupNativeNavigationRequest(SetupNativeCompletion Completion)
{
    public string PageTag => HubPageRegistry.GetNativeSetupPage(Completion.Target.Destination);
    public string? ChannelId => Completion.Target.ChannelId;

    public IOperatorGatewayClient GetConnectedClient(GatewayRegistry? registry, IGatewayConnectionManager? manager)
    {
        var active = registry?.GetActive();
        var client = manager?.OperatorClient;
        var snapshot = manager?.CurrentSnapshot;
        if (manager is null || client is null || snapshot is null)
            throw new InvalidOperationException("The verified Gateway is not connected.");
        RequireCurrent(active, snapshot.GatewayId, client.MainSessionKey,
            client.IsConnectedToGateway && snapshot.OperatorState == RoleConnectionState.Connected);
        if (registry is null || string.IsNullOrWhiteSpace(client.AuthenticatedSigningDeviceId) ||
            SetupCompletionAuthority.CaptureIdentity(registry.GetIdentityDirectory(Completion.Verification.GatewayId),
                client.AuthenticatedSigningDeviceId) != Completion.Verification.IdentityBinding)
            throw new SetupNativeOwnershipException();
        SetupCompletionAuthority.RequirePersistedIdentity(
            registry.GetIdentityDirectory(Completion.Verification.GatewayId), Completion.Verification.IdentityBinding);
        if (!ReferenceEquals(client, manager.OperatorClient) ||
            manager.CurrentSnapshot.GatewayId != snapshot.GatewayId ||
            manager.CurrentSnapshot.OperatorState != RoleConnectionState.Connected ||
            client.MainSessionKey != Completion.Target.SessionKey)
            throw new SetupNativeOwnershipException();
        return client;
    }

    public void RequireCurrent(GatewayRecord? active, string? connectedGatewayId, string? sessionKey, bool connected)
    {
        if (active is null || active.Id != Completion.Verification.GatewayId ||
            GatewayDashboardBinding.Capture(active) != Completion.Verification.EndpointBinding ||
            !Completion.Target.Matches(Completion.Verification))
            throw new SetupNativeOwnershipException();
        if (!connected) throw new InvalidOperationException("The verified Gateway is not connected.");
        if (connectedGatewayId != Completion.Verification.GatewayId || sessionKey != Completion.Target.SessionKey)
            throw new SetupNativeOwnershipException();
    }
}

internal enum SetupChannelAvailability { Offered, NotOffered, Unconfirmed }

internal static class SetupChannelFocusPolicy
{
    public static SetupChannelAvailability GetAvailability(ChannelsStatusSnapshot snapshot, string channelId)
    {
        bool Offered(string id) => string.Equals(id, channelId, StringComparison.OrdinalIgnoreCase);
        if (snapshot.ChannelOrder.Any(Offered) || snapshot.Channels.Keys.Any(Offered) || snapshot.ChannelAccounts.Keys.Any(Offered) ||
            snapshot.ChannelMeta?.Any(meta => Offered(meta.Id)) == true)
            return SetupChannelAvailability.Offered;
        return snapshot.ChannelOrder.Count > 0 || snapshot.ChannelMeta?.Count > 0
            ? SetupChannelAvailability.NotOffered : SetupChannelAvailability.Unconfirmed;
    }
}
