using System.Text.RegularExpressions;

namespace OpenClawTray.Infrastructure.Controls.Validation;

/// <summary>
/// Factory methods for built-in validators.
/// </summary>
public static class Validate
{
    /// <summary>
    /// Value must be non-null, non-empty string, and non-default.
    /// </summary>
    public static IValidator Required(string? message = null) =>
        new RequiredValidator(message ?? "This field is required.");

    /// <summary>
    /// String length must be >= n.
    /// </summary>
    public static IValidator MinLength(int n, string? message = null) =>
        new MinLengthValidator(n, message ?? $"Must be at least {n} characters.");

    /// <summary>
    /// String length must be <= n.
    /// </summary>
    public static IValidator MaxLength(int n, string? message = null) =>
        new MaxLengthValidator(n, message ?? $"Must be at most {n} characters.");

    /// <summary>
    /// Numeric value must be within [min, max].
    /// </summary>
    public static IValidator Range(double min, double max, string? message = null) =>
        new RangeValidator(min, max, message ?? $"Must be between {min} and {max}.");

    /// <summary>
    /// String must match the given regex pattern.
    /// </summary>
    public static IValidator Match(string regex, string? message = null) =>
        new MatchValidator(regex, message ?? "Invalid format.");

    /// <summary>
    /// String must be a valid email address.
    /// </summary>
    public static IValidator Email(string? message = null) =>
        new MatchValidator(
            @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$",
            message ?? "Must be a valid email address.",
            "EMAIL");

    /// <summary>
    /// String must be a valid URL.
    /// </summary>
    public static IValidator Url(string? message = null) =>
        new UrlValidator(message ?? "Must be a valid URL.");

    /// <summary>
    /// Custom synchronous predicate validator.
    /// </summary>
    public static IValidator Must<T>(Func<T, bool> predicate, string message) =>
        new MustValidator<T>(predicate, message);

    /// <summary>
    /// Custom asynchronous predicate validator.
    /// </summary>
    public static IAsyncValidator MustAsync<T>(Func<T, Task<bool>> predicate, string message) =>
        new MustAsyncValidator<T>(predicate, message);

    /// <summary>
    /// Boolean value must be true (e.g., for checkboxes).
    /// </summary>
    public static IValidator MustBeTrue(string? message = null) =>
        new MustBeTrueValidator(message ?? "Must be checked.");

    /// <summary>
    /// Value must equal another value (for cross-field equality like password confirmation).
    /// </summary>
    public static IValidator EqualTo<T>(T otherValue, string? message = null) =>
        new EqualToValidator<T>(otherValue, message ?? "Values must match.");
}

// ════════════════════════════════════════════════════════════════
//  Validator implementations
// ════════════════════════════════════════════════════════════════

internal sealed class RequiredValidator(string message) : IValidator
{
    public ValidationMessage? Validate(object? value, string field)
    {
        var invalid = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            _ => false,
        };
        return invalid ? new ValidationMessage(field, message, Severity.Error, "REQUIRED") : null;
    }
}

internal sealed class MinLengthValidator(int minLength, string message) : IValidator
{
    public ValidationMessage? Validate(object? value, string field)
    {
        if (value is not string s) return null; // non-string skips
        return s.Length < minLength
            ? new ValidationMessage(field, message, Severity.Error, "MIN_LENGTH")
            : null;
    }
}

internal sealed class MaxLengthValidator(int maxLength, string message) : IValidator
{
    public ValidationMessage? Validate(object? value, string field)
    {
        if (value is not string s) return null;
        return s.Length > maxLength
            ? new ValidationMessage(field, message, Severity.Error, "MAX_LENGTH")
            : null;
    }
}

internal sealed class RangeValidator(double min, double max, string message) : IValidator
{
    public ValidationMessage? Validate(object? value, string field)
    {
        double? num = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            short s => s,
            byte b => b,
            _ => null,
        };
        if (num is null) return null; // non-numeric skips
        return num < min || num > max
            ? new ValidationMessage(field, message, Severity.Error, "RANGE")
            : null;
    }
}

internal sealed class MatchValidator : IValidator
{
    private readonly Regex _regex;
    private readonly string _message;
    private readonly string _code;

    public MatchValidator(string pattern, string message, string code = "MATCH")
    {
        _regex = new Regex(pattern, RegexOptions.Compiled);
        _message = message;
        _code = code;
    }

    public ValidationMessage? Validate(object? value, string field)
    {
        if (value is not string s || string.IsNullOrEmpty(s)) return null; // empty handled by Required
        return !_regex.IsMatch(s)
            ? new ValidationMessage(field, _message, Severity.Error, _code)
            : null;
    }
}

internal sealed class UrlValidator(string message) : IValidator
{
    public ValidationMessage? Validate(object? value, string field)
    {
        if (value is not string s || string.IsNullOrEmpty(s)) return null;
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")
            ? null
            : new ValidationMessage(field, message, Severity.Error, "URL");
    }
}

internal sealed class MustValidator<T>(Func<T, bool> predicate, string message) : IValidator
{
    public ValidationMessage? Validate(object? value, string field)
    {
        if (value is not T typed) return null;
        return !predicate(typed)
            ? new ValidationMessage(field, message, Severity.Error, "MUST")
            : null;
    }
}

internal sealed class MustAsyncValidator<T>(Func<T, Task<bool>> predicate, string message) : IAsyncValidator
{
    public async Task<ValidationMessage?> ValidateAsync(object? value, string field, CancellationToken cancellationToken = default)
    {
        if (value is not T typed) return null;
        var result = await predicate(typed);
        cancellationToken.ThrowIfCancellationRequested();
        return !result
            ? new ValidationMessage(field, message, Severity.Error, "MUST_ASYNC")
            : null;
    }
}

internal sealed class MustBeTrueValidator(string message) : IValidator
{
    public ValidationMessage? Validate(object? value, string field)
    {
        if (value is bool b && b) return null;
        return new ValidationMessage(field, message, Severity.Error, "MUST_BE_TRUE");
    }
}

internal sealed class EqualToValidator<T>(T otherValue, string message) : IValidator
{
    public ValidationMessage? Validate(object? value, string field)
    {
        if (value is not T typed) return null;
        return !EqualityComparer<T>.Default.Equals(typed, otherValue)
            ? new ValidationMessage(field, message, Severity.Error, "EQUAL_TO")
            : null;
    }
}
