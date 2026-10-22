namespace OpenClaw.Connection;

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

            // Settle the outcome in the callbacks, not in their asynchronously scheduled continuations.
            using var cancellationRegistration = cancellationToken.Register(
                static (state, token) => ((TaskCompletionSource<bool>)state!).TrySetCanceled(token),
                tcs);
            using var deadline = new CancellationTokenSource(timeout);
            using var deadlineRegistration = deadline.Token.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false),
                tcs);
            if ((long)timeout.TotalMilliseconds == 0)
                deadline.Cancel();

            var ready = await tcs.Task.ConfigureAwait(true);
            if (!ready)
                cancellationToken.ThrowIfCancellationRequested();
            return ready;
        }
        finally
        {
            connectionManager.StateChanged -= handler;
        }
    }
}
