using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Localization;

/// <summary>
/// Context-based locale context. The LocaleProvider component provides an IntlAccessor
/// to its subtree via this context. Components consume it via UseIntl() or UseContext(IntlContexts.Locale).
/// </summary>
public static class IntlContexts
{
    /// <summary>
    /// The Context for locale/intl. Default is null — UseIntl() falls back to OS locale.
    /// </summary>
    public static readonly Context<IntlAccessor?> Locale = new(defaultValue: null);
}

/// <summary>
/// Legacy ambient context for the current locale.
/// Kept for backward compatibility. New code should use IntlContexts.Locale via Context.
/// </summary>
internal sealed class LocaleContext
{
    /// <summary>
    /// The current (innermost) locale context. In a typical app there is
    /// one LocaleProvider at the root so this is effectively a singleton.
    /// </summary>
    internal static LocaleContext? Current { get; set; }

    private readonly List<Action> _subscribers = new();

    public IntlAccessor Accessor { get; private set; }

    internal LocaleContext(IntlAccessor accessor)
    {
        Accessor = accessor;
    }

    internal void UpdateAccessor(IntlAccessor accessor)
    {
        Accessor = accessor;
        NotifySubscribers();
    }

    internal void Subscribe(Action onLocaleChanged)
    {
        _subscribers.Add(onLocaleChanged);
    }

    internal void Unsubscribe(Action onLocaleChanged)
    {
        _subscribers.Remove(onLocaleChanged);
    }

    private void NotifySubscribers()
    {
        // Snapshot to avoid issues if subscriber list is modified during notification
        foreach (var subscriber in _subscribers.ToArray())
            subscriber();
    }
}
