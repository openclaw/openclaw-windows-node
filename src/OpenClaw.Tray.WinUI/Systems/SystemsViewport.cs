// Retains shell content allocation without touching Systems data or its mounted tree.
// Usage: the page applies TopInset before or after mounting; the view observes Changed.
using System;

namespace OpenClawTray.Systems;

/// <summary>Page-owned presentation input, independent of Gateway reads and layout breakpoints.</summary>
internal sealed class SystemsViewport
{
    public double TopInset { get; private set; }
    public event EventHandler? Changed;

    /// <summary>Publishes inset-only changes, including the zero reset, without remounting.</summary>
    public void ApplyTopInset(double topInset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(topInset);
        if (!double.IsFinite(topInset))
            throw new ArgumentOutOfRangeException(nameof(topInset));
        if (TopInset == topInset)
            return;
        TopInset = topInset;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
