using Microsoft.UI.Dispatching;

namespace OpenClawTray.Presentation.Adapters;

/// <summary>
/// WinUI adapter that implements <see cref="IUiDispatcher"/> over the real
/// <see cref="DispatcherQueue"/>. WinUI-bound; not compiled into the pure test
/// project. The contract itself is covered by <c>IUiDispatcher</c> unit tests via a
/// test double, and this adapter is covered by source-contract tests.
/// </summary>
internal sealed class WinUIDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public WinUIDispatcher(DispatcherQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public bool TryEnqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _queue.TryEnqueue(() => action());
    }
}
