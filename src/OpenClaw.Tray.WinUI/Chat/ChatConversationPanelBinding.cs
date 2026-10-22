using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

/// <summary>Observes only the manager-owned transport. No UI state is read by completion fences.</summary>
internal static class ChatConversationPanelBinding
{
    internal static ChatConversationPanelState Create(
        IGatewayConnectionManager manager, IOperatorGatewayClient client, Func<SessionInfo[]> sessions,
        ChatConversationPanelLayouts? layouts = null, Func<bool>? identityAdvertised = null)
    {
        var initial = manager.CurrentSnapshot;
        var gatewayId = ReferenceEquals(manager.OperatorClient, client) &&
            ReferenceEquals(initial, manager.CurrentSnapshot) ? initial.GatewayId : null;
        bool IsCurrentClient() => gatewayId is not null &&
            manager.CurrentSnapshot.GatewayId == gatewayId &&
            ReferenceEquals(manager.OperatorClient, client);
        var account = new ChatConversationAccountIdentity(client, IsCurrentClient,
            identityAdvertised ?? (() => client is OpenClawGatewayClient gateway &&
                gateway.AdvertisedServerMethods.Contains("users.self", StringComparer.Ordinal)));
        var state = new ChatConversationPanelState(key =>
        {
            var before = manager.CurrentSnapshot;
            if (gatewayId is null || before.GatewayId != gatewayId ||
                before.OperatorState != RoleConnectionState.Connected ||
                !ReferenceEquals(manager.OperatorClient, client))
                return null;
            var identity = account.Current;
            var context = identity is null ? null : ChatConversationContext.Capture(gatewayId, client,
                sessions().FirstOrDefault(session => string.Equals(session.Key, key, StringComparison.Ordinal)),
                identity.UserId, identity.Revision);
            return ReferenceEquals(before, manager.CurrentSnapshot) &&
                ReferenceEquals(manager.OperatorClient, client) &&
                context?.IsTransportCurrent == true ? context : null;
        }, layouts, () => account.Status);
        void RefreshAccount(bool invalidate = false)
        {
            var pending = account.RefreshAsync(invalidate);
            // Unexpected faults have already been reported by the account owner.
            _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            state.Refresh();
        }
        void OnStateChanged(object? sender, GatewayConnectionSnapshot snapshot) => RefreshAccount();
        void OnStatusChanged(object? sender, ConnectionStatus status) => RefreshAccount();
        void OnHandshake(object? sender, EventArgs args) => RefreshAccount();
        void OnProfileChanged(object? sender, EventArgs args) => RefreshAccount(invalidate: true);
        void OnSessions(object? sender, SessionInfo[] rows) => state.Refresh();
        void OnClientChanged(object? sender, OperatorClientChangedEventArgs args) => RefreshAccount();
        account.Changed += state.Refresh;
        manager.StateChanged += OnStateChanged;
        manager.OperatorClientChanged += OnClientChanged;
        client.StatusChanged += OnStatusChanged;
        client.HandshakeSucceeded += OnHandshake;
        client.SessionsUpdated += OnSessions;
        client.SelfProfileChanged += OnProfileChanged;
        state.Disposed += () =>
        {
            manager.StateChanged -= OnStateChanged;
            manager.OperatorClientChanged -= OnClientChanged;
            client.StatusChanged -= OnStatusChanged;
            client.HandshakeSucceeded -= OnHandshake;
            client.SessionsUpdated -= OnSessions;
            client.SelfProfileChanged -= OnProfileChanged;
            account.Changed -= state.Refresh;
            account.Dispose();
        };
        RefreshAccount();
        return state;
    }
}
