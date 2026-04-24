namespace OpenClawTray.Infrastructure.Data;

/// <summary>
/// Describes a filter operation on a field.
/// </summary>
public record FilterDescriptor(
    string Field,
    FilterOperator Operator,
    object? Value = null,
    object? ValueTo = null);

/// <summary>
/// Filter operators for data queries.
/// </summary>
public enum FilterOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    EndsWith,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Between,
    In,
    IsNull,
    IsNotNull,
}
