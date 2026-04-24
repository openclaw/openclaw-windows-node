using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure;

public static partial class Factories
{
    /// <summary>
    /// Renders a minimal, icon-only trigger (a lightning bolt "⚡" by default)
    /// whose click opens a flyout containing the menu items returned by
    /// <paramref name="items"/>. Deliberately distinct from normal chrome —
    /// an amber glyph with no drop-down indicator — so "this session is in
    /// dev mode" reads at a glance.
    ///
    /// When the in-app devtools UI is disabled for the session
    /// (<see cref="ReactorApp.DevtoolsEnabled"/> is false), this returns
    /// <see cref="Empty"/> and the <paramref name="items"/> lambda is not
    /// invoked — so any element construction inside the lambda is skipped
    /// at retail cost of one bool check.
    ///
    /// A built-in "Highlight reconcile changes" toggle is always appended
    /// (separated from user items) to flip
    /// <see cref="ReactorFeatureFlags.HighlightReconcileChanges"/>.
    ///
    /// Typical placement is a titlebar:
    /// <code>
    /// HStack(
    ///     Text("My App"), Spacer(),
    ///     DevtoolsMenu(() => new MenuFlyoutItemBase[]
    ///     {
    ///         ToggleMenuItem("Debug UI", AppFlags.DebugUI.Value,
    ///             v => AppFlags.DebugUI.Value = v),
    ///         MenuSeparator(),
    ///         MenuItem("Clear cache", () => CacheService.Clear()),
    ///     })
    /// )
    /// </code>
    ///
    /// Pass a different <paramref name="glyph"/> to customize — e.g. the
    /// radioactive sign "☢" (U+2622), a bug "🐛", or any Unicode/Segoe Fluent
    /// glyph you prefer. For toggle items to reflect fresh state when a
    /// backing <see cref="Observable{T}"/> changes, subscribe the enclosing
    /// component via <c>ctx.UseObservable(flag)</c>.
    /// </summary>
    public static Element DevtoolsMenu(
        Func<IEnumerable<MenuFlyoutItemBase>> items,
        string glyph = "⚡",
        string toolTip = "Devtools",
        string? automationId = null)
    {
        if (!ReactorApp.DevtoolsEnabled) return Empty();

        var userItems = items?.Invoke()?.ToArray() ?? Array.Empty<MenuFlyoutItemBase>();

        var builtInToggle = ToggleMenuItem("Highlight reconcile changes",
            ReactorFeatureFlags.HighlightReconcileChanges,
            v => ReactorFeatureFlags.HighlightReconcileChanges = v);

        // Separator only makes sense when there are user items to separate from.
        var builtInItems = userItems.Length > 0
            ? new MenuFlyoutItemBase[] { MenuSeparator(), builtInToggle }
            : new MenuFlyoutItemBase[] { builtInToggle };

        var materialized = userItems.Concat(builtInItems).ToArray();

        var trigger = Button(glyph)
            .Foreground("#F59E0B")
            .Background("#00000000")
            .WithBorder("#00000000", 0)
            .Padding(8, 4)
            .FontSize(16)
            .ToolTip(toolTip)
            .AutomationName(toolTip);

        if (automationId is not null)
            trigger = trigger.AutomationId(automationId);

        return MenuFlyout(trigger, materialized);
    }
}
