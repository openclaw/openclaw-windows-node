// Animates a pane width once per drawn frame, so the pane and the content beside it move together.
// The Workspace sidebar uses it because the native SplitView slide snaps the content to its final
// width and then slides it, which clips the content while closing and leaves an empty strip while
// opening. The chat side panel uses it to ease its Grid column between its resting widths.
//
// Usage (the owner decides when it runs and what each width changes):
//   var animator = new WorkspacePaneAnimator(width => NavView.OpenPaneLength = width);
//   animator.Run(from: 256, to: 0, onCompleted: FinishPaneClose);
//   animator.Cancel(); // stops where it is without completing, e.g. when the window closes
using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Windows;

/// <summary>
/// Eases a pane width between two values on <see cref="CompositionTarget.Rendering"/>. A new
/// <see cref="Run"/> replaces the one in flight, so a reversed toggle continues from
/// <see cref="Current"/> instead of jumping.
/// </summary>
internal sealed class WorkspacePaneAnimator
{
    internal const int DurationMilliseconds = 220;

    private readonly Action<double> _apply;
    private readonly Stopwatch _clock = new();
    private double _from;
    private double _to;
    private Action? _completed;
    private bool _attached;

    internal WorkspacePaneAnimator(Action<double> apply) => _apply = apply;

    internal bool IsRunning => _attached;

    internal double Current { get; private set; }

    internal void Run(double from, double to, Action onCompleted)
    {
        _from = from;
        _to = to;
        _completed = onCompleted;
        Current = from;
        _apply(from);
        _clock.Restart();
        if (!_attached)
        {
            CompositionTarget.Rendering += OnRendering;
            _attached = true;
        }
    }

    internal void Cancel()
    {
        Detach();
        _completed = null;
    }

    /// <summary>Cubic ease in and out: the pane starts and lands gently, like the web sidebar.</summary>
    internal static double Ease(double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    private void OnRendering(object? sender, object args)
    {
        var progress = _clock.Elapsed.TotalMilliseconds / DurationMilliseconds;
        Current = _from + (_to - _from) * Ease(progress);
        _apply(Current);
        if (progress < 1)
            return;
        Detach();
        var completed = _completed;
        _completed = null;
        completed?.Invoke();
    }

    private void Detach()
    {
        if (!_attached)
            return;
        CompositionTarget.Rendering -= OnRendering;
        _attached = false;
        _clock.Stop();
    }
}
