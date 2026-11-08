using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>Binds a menu operation to the original client and authenticated connection, across dialogs and responses.</summary>
internal sealed class WorkspaceSessionOperation
{
    private readonly Func<IOperatorGatewayClient?> _currentClient;
    private readonly long? _epoch;

    public WorkspaceSessionOperation(IOperatorGatewayClient client, Func<IOperatorGatewayClient?> currentClient)
    {
        Client = client;
        _currentClient = currentClient;
        _epoch = client.SessionMutationConnectionEpoch;
    }

    public IOperatorGatewayClient Client { get; }

    public bool IsCurrent => _epoch.HasValue &&
        ReferenceEquals(Client, _currentClient()) &&
        Client.IsConnectedToGateway &&
        Client.SessionMutationConnectionEpoch == _epoch;

    public void RequireCurrent()
    {
        if (!IsCurrent)
            throw new WorkspaceSessionConnectionChangedException();
    }

    public async Task PatchAsync(string key, SessionPatch patch)
    {
        RequireCurrent();
        await Client.PatchSessionConfirmedAsync(key, patch, _epoch!.Value);
        RequireCurrent();
    }

    public async Task DeleteAsync(string key)
    {
        RequireCurrent();
        await Client.DeleteSessionConfirmedAsync(key, _epoch!.Value);
        RequireCurrent();
    }
}

internal sealed class WorkspaceSessionConnectionChangedException : InvalidOperationException
{
    public WorkspaceSessionConnectionChangedException()
        : base("The Gateway connection changed. Reopen the session menu and try again.") { }
}
