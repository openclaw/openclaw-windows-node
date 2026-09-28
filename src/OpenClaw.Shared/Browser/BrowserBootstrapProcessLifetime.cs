using System.Diagnostics;

namespace OpenClaw.Shared.Browser;

/// <summary>Cleanup failure does not authorize retirement of work that is still alive.</summary>
public static class BrowserBootstrapProcessLifetime
{
    public static Task JoinAsync(Process process, CancellationToken deadline) =>
        AwaitOwnedAsync(process.WaitForExitAsync(CancellationToken.None), deadline);

    public static async Task AwaitOwnedAsync(Task work, CancellationToken deadline)
    {
        try { await work.WaitAsync(deadline).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Preserve the expired-budget failure, but not at the cost of allowing
            // retirement while this owned process/task is still active. An unjoined
            // operation keeps the activation scope held and management busy.
            try { await work.ConfigureAwait(false); }
            catch { /* Work has settled; retain the original cleanup failure. */ }
            throw;
        }
    }
}
