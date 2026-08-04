using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Reproduces the exact shutdown sequencing of the former <c>App.ExitApplicationAsync</c>/
/// <c>SafeShutdownStep(Async)</c> pair: a first-wins shared task in place of the old
/// <c>_isExiting</c> bool guard, then each step's identical log/catch/continue behavior, then one
/// final <see cref="AppShutdownPlan.ExitApplication"/> call.
/// </summary>
internal sealed class AppShutdownCoordinator : IAppShutdownCoordinator
{
    private readonly object _gate = new();
    private Task? _shutdownTask;

    public bool IsShuttingDown => Volatile.Read(ref _shutdownTask) != null;

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
