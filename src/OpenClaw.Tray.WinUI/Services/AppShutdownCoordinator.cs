using System;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

internal sealed class AppShutdownCoordinator
{
    private readonly object _gate = new();
    private Task? _shutdownTask;

    public Task ShutdownAsync(AppShutdownPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        TaskCompletionSource completion;
        Task shutdownTask;
        lock (_gate)
        {
            if (_shutdownTask != null)
            {
                Logger.Info("Exit requested while shutdown already in progress");
                return _shutdownTask;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownTask = completion.Task;
            shutdownTask = _shutdownTask;
        }

        _ = CompleteShutdownAsync(plan, completion);
        return shutdownTask;
    }

    private static async Task CompleteShutdownAsync(
        AppShutdownPlan plan,
        TaskCompletionSource completion)
    {
        try
        {
            await RunAsync(plan);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private static async Task RunAsync(AppShutdownPlan plan)
    {
        plan.BeginShutdown();

        foreach (var step in plan.Steps)
        {
            try
            {
                Logger.Info($"Shutdown: disposing {step.Name}");
                await step.Execute();
                Logger.Info($"Shutdown: disposed {step.Name}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Shutdown: failed disposing {step.Name}: {ex.Message}");
            }
        }

        Logger.Info("Shutdown complete; calling Exit() now");
        plan.ExitApplication();
    }
}
