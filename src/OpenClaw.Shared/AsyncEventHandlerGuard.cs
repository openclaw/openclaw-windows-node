namespace OpenClaw.Shared;

/// <summary>
/// Runs async event-handler work behind a fault boundary so fire-and-forget UI
/// and transport events cannot crash the process through an async void escape.
/// </summary>
public static class AsyncEventHandlerGuard
{
    public static void Run(
        Func<Task> work,
        IOpenClawLogger? logger = null,
        string? operationName = null,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        _ = RunCoreAsync(work, logger, operationName, onError);
    }

    private static async Task RunCoreAsync(
        Func<Task> work,
        IOpenClawLogger? logger,
        string? operationName,
        Action<Exception>? onError)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException ex)
        {
            logger?.Debug($"{FormatOperation(operationName)} canceled: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger?.Error($"{FormatOperation(operationName)} failed", ex);
            onError?.Invoke(ex);
        }
    }

    private static string FormatOperation(string? operationName) =>
        string.IsNullOrWhiteSpace(operationName) ? "Async event handler" : operationName;
}
