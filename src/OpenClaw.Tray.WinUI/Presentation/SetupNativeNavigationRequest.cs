using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;

namespace OpenClawTray.Presentation;

internal sealed record SetupNativeNavigationRequest(SetupNativeCompletion Completion)
{
    public string PageTag => HubPageRegistry.GetNativeSetupPage(Completion.Target.Destination);
    public WorkspaceDestination? WorkspaceDestination =>
        WorkspaceNavigation.TryResolveWorkspace(PageTag, out var destination)
            ? destination with { SessionKey = Completion.Target.SessionKey }
            : null;

    public void RequireWorkspaceDestination(WorkspaceDestination destination)
    {
        if (WorkspaceDestination is not { } expected || destination != expected)
            throw new SetupNativeOwnershipException();
    }

    public IOperatorGatewayClient GetConnectedClient(GatewayRegistry? registry, IGatewayConnectionManager? manager)
    {
        var active = registry?.GetActive();
        var client = manager?.OperatorClient;
        var snapshot = manager?.CurrentSnapshot;
        if (manager is null || client is null || snapshot is null)
            throw new SetupNativeConnectionUnavailableException();
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
        if (!connected) throw new SetupNativeConnectionUnavailableException();
        if (connectedGatewayId != Completion.Verification.GatewayId || sessionKey != Completion.Target.SessionKey)
            throw new SetupNativeOwnershipException();
    }
}

internal sealed class SetupNativeConnectionUnavailableException()
    : InvalidOperationException("The verified Gateway is not connected.");

internal sealed class SetupNativeChatBinding
{
    public SetupNativeNavigationRequest? Request { get; private set; }

    public void Bind(SetupNativeNavigationRequest? request) => Request = request;

    public void Invalidate() => Request = null;

    public void RetainForDestination(WorkspaceDestination destination)
    {
        if (Request is { } request && request.WorkspaceDestination != destination)
            Invalidate();
    }

    public void RequireCurrent(SetupNativeNavigationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(Request, request))
            throw new SetupNativeOwnershipException();
    }
}
