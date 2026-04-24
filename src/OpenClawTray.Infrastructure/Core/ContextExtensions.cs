namespace OpenClawTray.Infrastructure.Core;

public static class ContextExtensions
{
    /// <summary>
    /// Provides a context value to this element's subtree.
    /// Any descendant can consume it via UseContext().
    /// Multiple .Provide() calls on the same element merge into a single dictionary.
    /// Providing the same context twice on the same element is last-write-wins.
    /// </summary>
    public static T Provide<T, TValue>(this T element, Context<TValue> context, TValue value)
        where T : Element
    {
        var existing = element.ContextValues;
        var dict = existing is not null
            ? new Dictionary<ContextBase, object?>(existing) { [context] = value }
            : new Dictionary<ContextBase, object?> { [context] = value };
        return element with { ContextValues = dict };
    }
}
