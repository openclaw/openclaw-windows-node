using System;

namespace OpenClawTray.Presentation;

/// <summary>
/// Keeps the latest navigation intent requested while a modal dialog is open, plus a pending
/// re-render of history that was pruned meanwhile. When the dialog closes the pruned destination is
/// rendered first, so a deferred Back/Forward that no longer has an entry cannot leave a stale page.
/// </summary>
internal sealed class DeferredNavigation
{
    private Action? _intent;
    private bool _renderPending;

    public bool HasIntent => _intent is not null;

    /// <summary>Stores the latest intent; returns true when another intent was already waiting.</summary>
    public bool Defer(Action intent)
    {
        var hadIntent = _intent is not null;
        _intent = intent;
        return hadIntent;
    }

    /// <summary>Marks the current destination for rendering before the waiting intent runs.</summary>
    public void DeferRender() => _renderPending = true;

    public void Clear()
    {
        _intent = null;
        _renderPending = false;
    }

    /// <summary>
    /// Runs the pending render (unless another modal opened) and then the waiting intent once.
    /// An intent that finds a modal open defers itself again.
    /// </summary>
    public void Run(Func<bool> isModalOpen, Action render)
    {
        var intent = _intent;
        _intent = null;
        if (_renderPending && !isModalOpen())
        {
            _renderPending = false;
            render();
        }
        intent?.Invoke();
    }
}
