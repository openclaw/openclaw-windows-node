namespace OpenClawTray.Infrastructure.Localization;

/// <summary>
/// Options for IntlAccessor.FormatNumber(). Mirrors ICU/Intl.NumberFormat options.
/// </summary>
public enum NumberStyle
{
    Default,
    Currency,
    Percent
}

public sealed class NumberFormatOptions
{
    public NumberStyle Style { get; init; }
    public int? MinimumFractionDigits { get; init; }
    public int? MaximumFractionDigits { get; init; }
}
