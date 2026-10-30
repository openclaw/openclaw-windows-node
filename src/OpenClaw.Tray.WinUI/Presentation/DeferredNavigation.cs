using System;

namespace OpenClawTray.Presentation;

/// <summary>
/// Keeps the latest replaceable navigation intent requested while a modal dialog is open, and,
/// separately, a required re-render of history that was pruned meanwhile. The required render is
/// never replaced by a later intent. When the dialog closes the pruned destination is rendered
/// first, so a deferred Back/Forward that no longer has an entry cannot leave a stale page.
/// Nothing is consumed while another modal is open; the caller re-arms on that dialog.
/// </summary>
internal sealed class DeferredNavigation
{
    private Action? _intent;
    private bool _renderPending;
    private bool _armed;

    public bool HasIntent => _intent is not null;

    /// <summary>
    /// True while the logical destination may be ahead of what is rendered (a waiting intent or a
    /// pruned render), so even a request for the current destination must wait for the dialog.
    /// </summary>
    public bool HasPending => _intent is not null || _renderPending;

    /// <summary>A navigation waits behind a modal when it changes destination or anything is already pending.</summary>
    public bool ShouldDefer(bool destinationChanged) => destinationChanged || HasPending;

    /// <summary>Stores the latest intent, replacing any earlier one.</summary>
    public void Defer(Action intent) => _intent = intent;

    /// <summary>Marks the current destination for rendering before any waiting intent runs.</summary>
    public void DeferRender() => _renderPending = true;

    /// <summary>Returns true once per drain cycle: the caller must then hook the open dialog's close.</summary>
    public bool TryArm()
    {
        if (_armed) return false;
        _armed = true;
        return true;
    }

    public void Clear()
    {
        _intent = null;
        _renderPending = false;
    }

    /// <summary>
    /// Drains after a dialog closed. Returns false, consuming nothing, when another modal is open
    /// so the caller can re-arm on it. Otherwise renders first when required, then runs the intent once.
    /// </summary>
    public bool Run(Func<bool> isModalOpen, Action render)
    {
        _armed = false;
        if (!HasPending) return true;
        if (isModalOpen()) return false;
        var intent = _intent;
        _intent = null;
        if (_renderPending)
        {
            _renderPending = false;
            render();
        }
        intent?.Invoke();
        return true;
    }
}
