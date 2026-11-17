// Retains shell allocation independently of Automations data and component mounts.
// Usage: CronPage applies the inset; the mounted root subscribes and samples TopInset.
using System;

namespace OpenClawTray.Automations;

/// <summary>UI-thread-owned presentation state shared by the page and its retained root.</summary>
internal sealed class AutomationsViewportState
{
    public double TopInset { get; private set; }
    public event EventHandler<double>? Changed;

    /// <summary>Retains allocation before mounting and notifies inset-only layout changes.</summary>
    public void ApplyTopInset(double topInset)
    {
        if (!double.IsFinite(topInset) || topInset < 0)
            throw new ArgumentOutOfRangeException(nameof(topInset));
        if (TopInset == topInset) return;
        TopInset = topInset;
        Changed?.Invoke(this, topInset);
    }
}
