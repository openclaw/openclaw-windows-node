namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Maintains the current context values during the reconciler's tree traversal.
/// Pushed on entering an element with ContextValues, popped on leaving.
/// Most recent entry for a given context wins (shadowing).
/// </summary>
internal sealed class ContextScope
{
    private readonly List<(ContextBase Context, object? Value)> _stack = new();
    private long _version;

    internal void Push(IReadOnlyDictionary<ContextBase, object?> values)
    {
        foreach (var (ctx, val) in values)
            _stack.Add((ctx, val));
        _version++;
    }

    internal void Pop(int count)
    {
        _stack.RemoveRange(_stack.Count - count, count);
        _version++;
    }

    internal T Read<T>(Context<T> context)
    {
        // Walk backward (most recent first) for shadowing
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_stack[i].Context, context))
                return (T)_stack[i].Value!;
        }
        return context.DefaultValue;
    }

    /// <summary>
    /// Non-generic read for memo change detection. Returns the boxed value
    /// or the boxed default if no provider exists.
    /// </summary>
    internal object? Read(ContextBase context)
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_stack[i].Context, context))
                return _stack[i].Value;
        }
        return context.DefaultValueBoxed;
    }

    internal long Version => _version;
}
