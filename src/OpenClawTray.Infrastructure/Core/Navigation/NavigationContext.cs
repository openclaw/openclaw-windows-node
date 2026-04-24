using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Navigation;

/// <summary>
/// Provides a static <see cref="Context{T}"/> instance per TRoute type
/// for sharing a <see cref="NavigationHandle{TRoute}"/> through the element tree.
/// Uses the static-generic-class pattern so each TRoute gets its own singleton context
/// without per-render allocation.
/// </summary>
internal static class NavigationContext<TRoute> where TRoute : notnull
{
    internal static readonly Context<NavigationHandle<TRoute>?> Instance =
        new(defaultValue: null, name: $"NavigationContext<{typeof(TRoute).Name}>");
}
