using OpenClaw.SetupEngine;

namespace OpenClawTray.Services;

internal sealed class NativeGatewayLifecycleCoordinator
{
    private readonly Func<string, NativeGatewayControlAction, CancellationToken, Task<NativeGatewayControlResult>> _run;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NativeGatewayLifecycleCoordinator(ManagedNativeGatewayController controller)
        : this(controller.RunAsync)
    {
    }

    internal NativeGatewayLifecycleCoordinator(
        Func<string, NativeGatewayControlAction, CancellationToken, Task<NativeGatewayControlResult>> run)
    {
        _run = run;
    }

    public async Task<NativeGatewayControlResult> RunAsync(
        string taskName,
        NativeGatewayControlAction action,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _run(taskName, action, ct).ConfigureAwait(false);
            if (!result.Success)
                return result;

            if (action == NativeGatewayControlAction.Stop)
                NativeGatewayKeepAliveService.RecordUserStopped(taskName);
            else if (action is NativeGatewayControlAction.Start or NativeGatewayControlAction.Restart)
                NativeGatewayKeepAliveService.ClearUserStopped();

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
