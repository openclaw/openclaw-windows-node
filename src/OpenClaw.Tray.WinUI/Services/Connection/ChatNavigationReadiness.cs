namespace OpenClawTray.Services.Connection;

public static class ChatNavigationReadiness
{
    public static bool IsOperatorHandshakeReady(IGatewayConnectionManager? connectionManager) =>
        connectionManager == null ||
        connectionManager.CurrentSnapshot.OperatorState == RoleConnectionState.Connected;

    public static async Task<bool> WaitForOperatorHandshakeAsync(
        IGatewayConnectionManager? connectionManager,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (IsOperatorHandshakeReady(connectionManager))
            return true;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<GatewayConnectionSnapshot>? handler = null;
        handler = (_, snapshot) =>
        {
            if (snapshot.OperatorState == RoleConnectionState.Connected)
                tcs.TrySetResult(true);
        };

        connectionManager!.StateChanged += handler;
        try
        {
            if (IsOperatorHandshakeReady(connectionManager))
                return true;

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(true);
            if (completed == tcs.Task)
                return await tcs.Task.ConfigureAwait(true);

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            connectionManager.StateChanged -= handler;
        }
    }
}
