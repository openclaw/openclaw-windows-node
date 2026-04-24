namespace OpenClawTray.Infrastructure.Controls.Validation;

/// <summary>
/// A synchronous validator that checks a value and returns a validation message on failure.
/// </summary>
public interface IValidator
{
    /// <summary>
    /// Validates the given value. Returns a ValidationMessage on failure, or null on success.
    /// The returned message's Field property may be empty — the caller sets the correct field name.
    /// </summary>
    ValidationMessage? Validate(object? value, string field);
}

/// <summary>
/// An asynchronous validator for operations that require I/O (e.g., server-side uniqueness checks).
/// </summary>
public interface IAsyncValidator
{
    /// <summary>
    /// Validates the given value asynchronously.
    /// Returns a ValidationMessage on failure, or null on success.
    /// </summary>
    Task<ValidationMessage?> ValidateAsync(object? value, string field, CancellationToken cancellationToken = default);
}
