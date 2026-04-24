namespace OpenClawTray.Infrastructure.Localization;

/// <summary>
/// Options for IntlAccessor.FormatDate(). Maps to .NET DateTimeFormatInfo styles.
/// </summary>
public enum DateStyle
{
    Default,
    Short,
    Long,
    Full
}

public sealed class DateFormatOptions
{
    public DateStyle Style { get; init; }
}
