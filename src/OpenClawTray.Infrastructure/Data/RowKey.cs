namespace OpenClawTray.Infrastructure.Data;

/// <summary>
/// A strongly-typed key for identifying rows in a data source.
/// </summary>
public readonly record struct RowKey(string Value)
{
    public static implicit operator RowKey(string value) => new(value);
    public static implicit operator RowKey(int value) => new(value.ToString());
    public static implicit operator RowKey(Guid value) => new(value.ToString());

    public override string ToString() => Value;
}
