using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Controls.Validation;

/// <summary>
/// A virtual element that represents a cross-field validation rule.
/// Place anywhere in the element tree; errors bubble to the nearest visualizer.
/// Does not render any UI — it only produces validation messages.
/// </summary>
public sealed record ValidationRuleElement(
    string Field,
    Func<bool> Predicate,
    string Message,
    Severity Severity = Severity.Error) : Element
{
    /// <summary>
    /// Optional async predicate for rules that require I/O.
    /// When set, takes precedence over the sync Predicate.
    /// </summary>
    public Func<Task<bool>>? AsyncPredicate { get; init; }
}

/// <summary>
/// DSL factory methods for ValidationRule elements.
/// </summary>
public static class ValidationRuleDsl
{
    /// <summary>
    /// Creates a cross-field validation rule. When the predicate returns false,
    /// the message is added to the ValidationContext under the given field name.
    /// When the predicate returns true, any previous message for this rule is cleared.
    /// </summary>
    /// <param name="predicate">Returns true when the rule passes (valid).</param>
    /// <param name="message">Error message when the rule fails.</param>
    /// <param name="field">Field name to associate the error with.</param>
    /// <param name="severity">Severity level (default: Error).</param>
    public static ValidationRuleElement ValidationRule(
        Func<bool> predicate,
        string message,
        string field,
        Severity severity = Severity.Error) =>
        new(field, predicate, message, severity);

    /// <summary>
    /// Creates an async cross-field validation rule.
    /// </summary>
    public static ValidationRuleElement ValidationRuleAsync(
        Func<Task<bool>> asyncPredicate,
        string message,
        string field,
        Severity severity = Severity.Error) =>
        new(field, () => true, message, severity) { AsyncPredicate = asyncPredicate };

    /// <summary>
    /// Evaluates the validation rule against a ValidationContext.
    /// Adds or clears messages based on the predicate result.
    /// </summary>
    public static void Evaluate(this ValidationRuleElement rule, ValidationContext ctx)
    {
        // Clear any previous message from this rule (identified by field + message combo)
        ctx.ClearInternal(rule.Field);

        if (!rule.Predicate())
        {
            ctx.Add(new ValidationMessage(rule.Field, rule.Message, rule.Severity));
        }
    }

    /// <summary>
    /// Evaluates the async validation rule against a ValidationContext.
    /// </summary>
    public static async Task EvaluateAsync(this ValidationRuleElement rule, ValidationContext ctx,
        CancellationToken cancellationToken = default)
    {
        if (rule.AsyncPredicate is null)
        {
            rule.Evaluate(ctx);
            return;
        }

        ctx.ClearInternal(rule.Field);
        var result = await rule.AsyncPredicate();
        cancellationToken.ThrowIfCancellationRequested();

        if (!result)
        {
            ctx.Add(new ValidationMessage(rule.Field, rule.Message, rule.Severity));
        }
    }
}
