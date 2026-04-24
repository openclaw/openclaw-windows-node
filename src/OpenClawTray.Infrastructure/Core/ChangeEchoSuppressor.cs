using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Per-control "suppress next change event" counter used by the reconciler.
///
/// Background — why this exists:
///   A Reactor Update handler that writes a value-bearing DP (<c>cp.Color = ...</c>,
///   <c>nb.Value = ...</c>, <c>ts.IsOn = ...</c>) synthesizes a ValueChanged /
///   ColorChanged / Toggled / etc. event. If the user wired an OnChanged callback
///   (via <c>ColorPicker(..., onChanged: ...)</c> and friends), that callback
///   gets invoked with the value WE just wrote — which is indistinguishable from
///   a real user interaction. If the owning component's state derives from
///   another source (e.g. a PropertyGrid bound to the selected row), the echo
///   writes the new value back into the PREVIOUS state — a silent cross-row
///   value swap. See spec-030 investigation notes.
///
/// Contract:
///   - Before a programmatic write that will raise the control's change event,
///     call <see cref="BeginSuppress(UIElement)"/>. Pair it 1:1 with the write.
///   - The registered event handler must call <see cref="ShouldSuppress(UIElement)"/>
///     as its first line and return early if it returns true. That consumes
///     one suppression token.
///   - Callers should guard with an equality check (only suppress when the new
///     value actually differs) so the token is always consumed by a real event.
///
/// Why <see cref="ConditionalWeakTable{TKey,TValue}"/>: controls returned to the
/// ElementPool or garbage-collected should not leak an entry. Weak keys keep
/// the map small without any explicit cleanup.
/// </summary>
internal static class ChangeEchoSuppressor
{
    // Box the counter so we can increment/decrement without repeatedly re-inserting.
    private sealed class Counter { public int Value; }

    private static readonly ConditionalWeakTable<UIElement, Counter> _counters = new();

    /// <summary>
    /// Increment the suppress counter before a programmatic property write that
    /// will raise a change event. Pair exactly one BeginSuppress with exactly
    /// one expected event.
    /// </summary>
    internal static void BeginSuppress(UIElement control)
    {
        var c = _counters.GetValue(control, static _ => new Counter());
        c.Value++;
    }

    /// <summary>
    /// Returns <c>true</c> if the current event fire should be suppressed (and
    /// decrements the counter). Returns <c>false</c> otherwise. Call at the top
    /// of a change-event handler before invoking the user's OnChanged.
    /// </summary>
    internal static bool ShouldSuppress(UIElement control)
    {
        if (_counters.TryGetValue(control, out var c) && c.Value > 0)
        {
            c.Value--;
            return true;
        }
        return false;
    }
}
