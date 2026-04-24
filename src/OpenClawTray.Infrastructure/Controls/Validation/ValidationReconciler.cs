using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Controls.Validation;

namespace OpenClawTray.Infrastructure.Controls.Validation;

/// <summary>
/// Helpers for running validation within a component's render cycle.
/// Validation runs after state updates and before the returned element tree is reconciled.
/// Results are available to visualizers in the same render pass.
/// </summary>
public static class ValidationReconciler
{
    /// <summary>
    /// Runs all synchronous validators for a field and pushes results to the context.
    /// Call this from a component's Render() method after state is finalized.
    /// </summary>
    public static void ValidateField(
        ValidationContext ctx,
        string fieldName,
        object? value,
        params IValidator[] validators)
    {
        ctx.RegisterField(fieldName);
        ctx.ClearInternal(fieldName);
        ctx.NotifyValueChanged(fieldName, value);

        foreach (var validator in validators)
        {
            var result = validator.Validate(value, fieldName);
            if (result is not null)
                ctx.Add(result);
        }
    }

    /// <summary>
    /// Runs validators from a ValidationAttached record and pushes results to the context.
    /// </summary>
    public static void ValidateAttached(
        ValidationContext ctx,
        ValidationAttached attached,
        object? value)
    {
        ctx.RegisterField(attached.FieldName);
        ctx.ClearInternal(attached.FieldName);
        ctx.NotifyValueChanged(attached.FieldName, value);

        foreach (var validator in attached.Validators)
        {
            var result = validator.Validate(value, attached.FieldName);
            if (result is not null)
                ctx.Add(result);
        }
    }

    /// <summary>
    /// Runs all async validators for a field. Typically called from a UseEffect hook.
    /// </summary>
    public static async Task ValidateFieldAsync(
        ValidationContext ctx,
        string fieldName,
        object? value,
        IAsyncValidator[] asyncValidators,
        CancellationToken cancellationToken = default)
    {
        foreach (var validator in asyncValidators)
        {
            var result = await validator.ValidateAsync(value, fieldName, cancellationToken);
            if (result is not null)
                ctx.Add(result);
        }
    }

    /// <summary>
    /// Evaluates all ValidationRuleElements in a list and pushes results to the context.
    /// </summary>
    public static void EvaluateRules(
        ValidationContext ctx,
        params ValidationRuleElement[] rules)
    {
        foreach (var rule in rules)
        {
            rule.Evaluate(ctx);
        }
    }
}
