namespace OpenClawTray.Infrastructure.Data;

/// <summary>
/// Describes a sort operation on a field.
/// </summary>
public record SortDescriptor(string Field, SortDirection Direction = SortDirection.Ascending);

/// <summary>
/// Sort direction for data queries.
/// </summary>
public enum SortDirection
{
    Ascending,
    Descending,
}
