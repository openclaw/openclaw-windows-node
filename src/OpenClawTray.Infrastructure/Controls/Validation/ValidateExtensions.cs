using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Controls.Validation;

namespace OpenClawTray.Infrastructure.Controls.Validation;

/// <summary>
/// Attached validation metadata for an element, stored in Element.Attached.
/// </summary>
public sealed record ValidationAttached(
    string FieldName,
    IValidator[] Validators,
    IAsyncValidator[] AsyncValidators)
{
    /// <summary>
    /// The current field value, used for automatic validation when the element is
    /// mounted inside a FormFieldElement or ValidationVisualizerElement.
    /// </summary>
    public object? Value { get; init; }

    public static readonly ValidationAttached Empty = new("", [], []);
}

/// <summary>
/// Fluent extension methods for attaching validation to Reactor elements.
/// </summary>
public static class ValidateExtensions
{
    /// <summary>
    /// Attaches validators to this element. The element's value will be validated
    /// against the provided validators, with results pushed to the nearest ValidationContext.
    /// </summary>
    /// <param name="el">The element to validate.</param>
    /// <param name="fieldName">Field name for validation messages.</param>
    /// <param name="validators">One or more validators to apply.</param>
    public static T Validate<T>(this T el, string fieldName, params IValidator[] validators) where T : Element
    {
        var existing = el.GetAttached<ValidationAttached>();
        var merged = existing is not null
            ? existing with
            {
                FieldName = fieldName,
                Validators = [.. existing.Validators, .. validators]
            }
            : new ValidationAttached(fieldName, validators, []);
        return (T)el.SetAttached(merged);
    }

    /// <summary>
    /// Attaches validators to this element along with the current field value.
    /// When placed inside a FormFieldElement, validators run automatically — no manual
    /// ValidationReconciler.ValidateField() call needed.
    /// </summary>
    public static T Validate<T>(this T el, string fieldName, object? value, params IValidator[] validators) where T : Element
    {
        var existing = el.GetAttached<ValidationAttached>();
        var merged = existing is not null
            ? existing with
            {
                FieldName = fieldName,
                Value = value,
                Validators = [.. existing.Validators, .. validators]
            }
            : new ValidationAttached(fieldName, validators, []) { Value = value };
        return (T)el.SetAttached(merged);
    }

    /// <summary>
    /// Attaches async validators to this element (in addition to any sync validators).
    /// </summary>
    public static T ValidateAsync<T>(this T el, string fieldName, params IAsyncValidator[] asyncValidators) where T : Element
    {
        var existing = el.GetAttached<ValidationAttached>();
        var merged = existing is not null
            ? existing with
            {
                FieldName = fieldName,
                AsyncValidators = [.. existing.AsyncValidators, .. asyncValidators]
            }
            : new ValidationAttached(fieldName, [], asyncValidators);
        return (T)el.SetAttached(merged);
    }

    /// <summary>
    /// Attaches async validators with the current field value for automatic validation.
    /// </summary>
    public static T ValidateAsync<T>(this T el, string fieldName, object? value, params IAsyncValidator[] asyncValidators) where T : Element
    {
        var existing = el.GetAttached<ValidationAttached>();
        var merged = existing is not null
            ? existing with
            {
                FieldName = fieldName,
                Value = value,
                AsyncValidators = [.. existing.AsyncValidators, .. asyncValidators]
            }
            : new ValidationAttached(fieldName, [], asyncValidators) { Value = value };
        return (T)el.SetAttached(merged);
    }

    /// <summary>
    /// Runs all synchronous validators attached to an element against a value.
    /// Returns the list of validation messages (empty if all pass).
    /// </summary>
    public static IReadOnlyList<ValidationMessage> RunValidators(
        this ValidationAttached attached, object? value)
    {
        var messages = new List<ValidationMessage>();
        foreach (var validator in attached.Validators)
        {
            var result = validator.Validate(value, attached.FieldName);
            if (result is not null)
                messages.Add(result);
        }
        return messages;
    }

    /// <summary>
    /// Runs all async validators attached to an element against a value.
    /// </summary>
    public static async Task<IReadOnlyList<ValidationMessage>> RunAsyncValidators(
        this ValidationAttached attached, object? value, CancellationToken cancellationToken = default)
    {
        var messages = new List<ValidationMessage>();
        foreach (var asyncValidator in attached.AsyncValidators)
        {
            var result = await asyncValidator.ValidateAsync(value, attached.FieldName, cancellationToken);
            if (result is not null)
                messages.Add(result);
        }
        return messages;
    }

    /// <summary>
    /// Gets the ValidationAttached metadata from an element, if any.
    /// </summary>
    public static ValidationAttached? GetValidation<T>(this T el) where T : Element =>
        el.GetAttached<ValidationAttached>();
}
