using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClawTray.Presentation;

namespace OpenClawTray.Controls;

/// <summary>
/// Shows a flyout even while its previous close is still running. WinUI ignores <c>ShowAt</c>
/// from <c>Closing</c> until after <c>Closed</c> has returned, so a shortcut pressed right after
/// Escape would otherwise be dropped. <see cref="DeferredFlyoutShowState"/> decides; a deferred
/// request is replayed through the owner's open path, which re-checks visibility, dialogs and the
/// anchor at replay time.
/// </summary>
internal sealed class FlyoutShowRequest
{
    private readonly FlyoutBase _flyout;
    private readonly Action _replay;
    private readonly DeferredFlyoutShowState _state = new();

    public FlyoutShowRequest(FlyoutBase flyout, Action replay)
    {
        _flyout = flyout;
        _replay = replay;
        flyout.Opened += (_, _) => _state.Opened();
        flyout.Closing += (_, args) => _state.Closing(args.Cancel);
        // ShowAt is still ignored inside Closed; the close counts as finished on the next turn.
        flyout.Closed += (_, _) =>
        {
            _state.Closed();
            if (!flyout.DispatcherQueue.TryEnqueue(Settle)) Settle();
        };
    }

    public void ShowAt(FrameworkElement anchor)
    {
        if (!DeferUntilClosed()) _flyout.ShowAt(anchor);
    }

    /// <summary>Queues this owner's replay when a caller is about to open another surface's flyout.</summary>
    public bool DeferUntilClosed() => _state.TryDefer();

    /// <summary>Drops a queued reopen, for example when its section is hidden or closed.</summary>
    public void Cancel() => _state.Cancel();

    private void Settle()
    {
        if (_state.Settle()) _replay();
    }
}
