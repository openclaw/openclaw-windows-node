namespace OpenClawTray.Services.Voice;

public sealed class DispatcherQueueAdapter : IUiDispatcher
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    public DispatcherQueueAdapter(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public bool TryEnqueue(Action callback)
    {
        return _dispatcherQueue.TryEnqueue(() => callback());
    }
}
