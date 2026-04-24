using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Controls.Validation;

/// <summary>
/// Provides the Context definition for propagating ValidationContext through the tree.
/// </summary>
public static class ValidationContexts
{
    /// <summary>
    /// The Context definition used to propagate ValidationContext through the tree.
    /// Default value is null (no validation context provided by any ancestor).
    /// </summary>
    public static readonly Context<ValidationContext?> Current = new(null, "ValidationContext");
}

/// <summary>
/// Extension methods for the UseValidationContext hook on RenderContext.
/// </summary>
public static class ValidationContextHookExtensions
{
    /// <summary>
    /// Returns the nearest ancestor's ValidationContext, or creates a new one
    /// scoped to this component. The created context persists across re-renders.
    /// To provide it to a subtree, use .Provide(ValidationContexts.Current, ctx).
    /// </summary>
    public static ValidationContext UseValidationContext(this RenderContext ctx)
    {
        var parent = ctx.UseContext(ValidationContexts.Current);
        // UseState captures initial value only on first render; subsequent renders reuse stored value.
        var (local, _) = ctx.UseState(new ValidationContext());

        return parent ?? local;
    }

    /// <summary>
    /// Creates a child validation context independent from any parent.
    /// Returns both the child context and the parent (if one exists).
    /// The child collects its own messages; use the parent reference to bubble manually.
    /// </summary>
    public static (ValidationContext Child, ValidationContext? Parent) UseChildValidationContext(
        this RenderContext ctx)
    {
        var parent = ctx.UseContext(ValidationContexts.Current);
        var (child, _) = ctx.UseState(new ValidationContext());
        return (child, parent);
    }
}

/// <summary>
/// Convenience hook wrappers on Component base class.
/// </summary>
public static class ValidationContextComponentExtensions
{
    /// <summary>
    /// Returns the nearest ancestor's ValidationContext, or creates a new one.
    /// </summary>
    public static ValidationContext UseValidationContext(this Component component)
        => component.Context.UseValidationContext();

    /// <summary>
    /// Creates a child validation context independent from any parent.
    /// </summary>
    public static (ValidationContext Child, ValidationContext? Parent) UseChildValidationContext(
        this Component component)
        => component.Context.UseChildValidationContext();
}
