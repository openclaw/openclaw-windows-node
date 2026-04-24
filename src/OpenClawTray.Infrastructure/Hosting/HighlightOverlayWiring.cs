using System.Diagnostics;
using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Hosting;

/// <summary>
/// Shared wiring for the reconcile-highlight overlay used by both
/// <see cref="ReactorHost"/> and <see cref="ReactorHostControl"/>.
/// Encapsulates the wrapper Grid (content slot + overlay Canvas),
/// snapshot-based scheduling, and the post-layout flush callback.
/// Includes throttling and caps to stay responsive under high update cadence.
/// </summary>
internal sealed class HighlightOverlayWiring
{
    /// <summary>Max elements to buffer per list between flushes.</summary>
    private const int MaxPendingElements = 200;

    /// <summary>Minimum interval between flush dispatches.</summary>
    private const int MinFlushIntervalMs = 80;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Stopwatch _flushCooldown = new();
    private Grid? _wrapperRoot;
    private Canvas? _overlayCanvas;
    private ReconcileHighlightOverlay? _overlay;
    private bool _flushPending;
    private List<UIElement>? _pendingMounted;
    private List<UIElement>? _pendingModified;

    public HighlightOverlayWiring(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    /// <summary>
    /// The wrapper Grid that holds both the content slot and the overlay Canvas.
    /// Created lazily by <see cref="SetContentViaWrapper"/>.
    /// </summary>
    public Grid? WrapperRoot => _wrapperRoot;

    /// <summary>
    /// Installs <paramref name="newControl"/> into a wrapper Grid that overlays
    /// a hit-test-invisible Canvas on top. The wrapper is created once; subsequent
    /// calls only swap the content slot. Returns the wrapper root (for the host
    /// to set as its Content / window.Content).
    /// </summary>
    public Grid SetContentViaWrapper(UIElement? newControl)
    {
        if (_wrapperRoot is null)
        {
            _overlayCanvas = new Canvas
            {
                IsHitTestVisible = false,
            };
            _wrapperRoot = new Grid();
            _wrapperRoot.Children.Add(new ContentControl
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            });
            _wrapperRoot.Children.Add(_overlayCanvas);
        }

        var slot = (ContentControl)_wrapperRoot.Children[0];
        slot.Content = newControl;
        return _wrapperRoot;
    }

    /// <summary>
    /// Snapshots the reconciler's highlight lists and schedules a low-priority
    /// flush so the overlay renders after layout completes. Caps pending lists
    /// and throttles flush frequency to stay responsive under high cadence.
    /// </summary>
    public void ScheduleHighlightFlush(Reconciler reconciler)
    {
        if (!ReactorFeatureFlags.HighlightReconcileChanges) return;
        if (reconciler.LastMountedElements.Count == 0 && reconciler.LastModifiedElements.Count == 0) return;

        // Cap pending lists — drop excess elements (best-effort display)
        if (reconciler.LastMountedElements.Count > 0)
        {
            _pendingMounted ??= new(Math.Min(reconciler.LastMountedElements.Count, MaxPendingElements));
            int room = MaxPendingElements - _pendingMounted.Count;
            if (room > 0)
            {
                int take = Math.Min(reconciler.LastMountedElements.Count, room);
                for (int i = 0; i < take; i++)
                    _pendingMounted.Add(reconciler.LastMountedElements[i]);
            }
        }
        if (reconciler.LastModifiedElements.Count > 0)
        {
            _pendingModified ??= new(Math.Min(reconciler.LastModifiedElements.Count, MaxPendingElements));
            int room = MaxPendingElements - _pendingModified.Count;
            if (room > 0)
            {
                int take = Math.Min(reconciler.LastModifiedElements.Count, room);
                for (int i = 0; i < take; i++)
                    _pendingModified.Add(reconciler.LastModifiedElements[i]);
            }
        }

        if (!_flushPending)
        {
            // Throttle: skip scheduling if we flushed very recently
            if (_flushCooldown.IsRunning && _flushCooldown.ElapsedMilliseconds < MinFlushIntervalMs)
                return;

            _flushPending = true;
            if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Flush))
            {
                // Queue shutting down — drop the pending batch and reset so we don't
                // deadlock future flushes or accumulate indefinitely.
                _flushPending = false;
                _pendingMounted = null;
                _pendingModified = null;
            }
        }
    }

    /// <summary>
    /// Swaps the content slot of the wrapper to show an error panel.
    /// Returns true if the wrapper was active and handled it; false if the
    /// host should fall back to its normal error path.
    /// </summary>
    public bool TryShowErrorInWrapper(UIElement errorPanel)
    {
        if (_wrapperRoot is null) return false;
        ((ContentControl)_wrapperRoot.Children[0]).Content = errorPanel;
        return true;
    }

    public void Dispose()
    {
        _overlay = null;
        _overlayCanvas = null;
        _wrapperRoot = null;
        _pendingMounted = null;
        _pendingModified = null;
    }

    private void Flush()
    {
        _flushPending = false;
        _flushCooldown.Restart();

        if (_overlayCanvas is null) return;
        if (_pendingMounted is null && _pendingModified is null) return;

        _overlay ??= new ReconcileHighlightOverlay(_overlayCanvas);

        var mounted = _pendingMounted;
        var modified = _pendingModified;
        _pendingMounted = null;
        _pendingModified = null;

        if ((mounted is null || mounted.Count == 0) && (modified is null || modified.Count == 0)) return;

        _overlay.Show(
            _overlayCanvas,
            mounted ?? (IReadOnlyList<UIElement>)Array.Empty<UIElement>(),
            modified ?? (IReadOnlyList<UIElement>)Array.Empty<UIElement>());
    }
}
