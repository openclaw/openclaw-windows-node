using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Cron;

public enum CronDurationUnit { Seconds, Minutes, Hours, Days }

/// <summary>
/// Exact decimal duration parsing and lossless display. Mirrors the Gateway form
/// rules: decimal text is scaled without floating point so saved values round-trip.
/// </summary>
public static class CronDurations
{
    public const long MaxTimestampMs = 8_640_000_000_000_000;
    // ASCII digits only: \d also matches full-width and other Unicode digits that BigInteger cannot parse.
    private static readonly Regex PositiveDecimal = new(@"^(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)$", RegexOptions.CultureInvariant);

    public static long UnitMs(CronDurationUnit unit) => unit switch
    {
        CronDurationUnit.Seconds => 1_000,
        CronDurationUnit.Minutes => 60_000,
        CronDurationUnit.Hours => 3_600_000,
        _ => 86_400_000,
    };

    /// <summary>Returns a positive whole number of milliseconds, or null when the text is not exact.</summary>
    public static long? ParseMs(string? value, CronDurationUnit unit)
    {
        var trimmed = (value ?? "").Trim();
        if (!PositiveDecimal.IsMatch(trimmed))
            return null;
        var parts = trimmed.Split('.');
        var whole = parts[0].TrimStart('0');
        if (whole.Length == 0) whole = "0";
        var fraction = parts.Length > 1 ? parts[1].TrimEnd('0') : "";
        if (whole.Length > MaxTimestampMs.ToString(CultureInfo.InvariantCulture).Length || fraction.Length > 10)
            return null;
        var scale = BigInteger.Pow(10, fraction.Length);
        var decimalValue = BigInteger.Parse(whole, CultureInfo.InvariantCulture) * scale
            + (fraction.Length == 0 ? BigInteger.Zero : BigInteger.Parse(fraction, CultureInfo.InvariantCulture));
        var scaled = decimalValue * UnitMs(unit);
        if (scaled % scale != 0)
            return null;
        var ms = scaled / scale;
        return ms > 0 && ms <= MaxTimestampMs ? (long)ms : null;
    }

    /// <summary>Largest unit that divides <paramref name="everyMs"/> exactly, else decimal seconds.</summary>
    public static (string Amount, CronDurationUnit Unit) Split(long everyMs)
    {
        if (everyMs % 86_400_000 == 0) return ((everyMs / 86_400_000).ToString(CultureInfo.InvariantCulture), CronDurationUnit.Days);
        if (everyMs % 3_600_000 == 0) return ((everyMs / 3_600_000).ToString(CultureInfo.InvariantCulture), CronDurationUnit.Hours);
        if (everyMs % 60_000 == 0) return ((everyMs / 60_000).ToString(CultureInfo.InvariantCulture), CronDurationUnit.Minutes);
        return (MsToSecondsText(everyMs), CronDurationUnit.Seconds);
    }

    public static string MsToSecondsText(long ms)
    {
        var whole = ms / 1_000;
        var remainder = ms % 1_000;
        if (remainder == 0)
            return whole.ToString(CultureInfo.InvariantCulture);
        return $"{whole.ToString(CultureInfo.InvariantCulture)}.{remainder.ToString("000", CultureInfo.InvariantCulture).TrimEnd('0')}";
    }

    /// <summary>Stagger display: zero is exact timing; whole minutes stay minutes; otherwise seconds.</summary>
    public static (bool Exact, string Amount, CronDurationUnit Unit) SplitStagger(long? staggerMs)
    {
        if (staggerMs is not { } ms || ms <= 0)
            return (staggerMs == 0, "", CronDurationUnit.Seconds);
        return ms % 60_000 == 0
            ? (false, (ms / 60_000).ToString(CultureInfo.InvariantCulture), CronDurationUnit.Minutes)
            : (false, MsToSecondsText(ms), CronDurationUnit.Seconds);
    }

    /// <summary>Failure-alert cooldown in seconds (decimals allowed, zero allowed) to whole milliseconds.</summary>
    public static long? ParseCooldownMs(string? seconds)
    {
        var raw = (seconds ?? "").Trim();
        if (raw.Length == 0 || !decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            return null;
        try
        {
            return (long)decimal.Floor(value * 1000m);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>Default anti-thundering-herd window for recurring top-of-hour expressions.</summary>
    public static long? DefaultStaggerMs(string expr)
    {
        var fields = expr.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length switch
        {
            5 when fields[0] == "0" && HasRecurringWildcardHour(fields[1]) => CronProtocol.DefaultTopOfHourStaggerMs,
            6 when fields[0] == "0" && fields[1] == "0" && HasRecurringWildcardHour(fields[2]) => CronProtocol.DefaultTopOfHourStaggerMs,
            _ => null,
        };
    }

    private static readonly Regex HourListPart = new(@"^(?:[0-9]+|[0-9]+-[0-9]+)(?:/[0-9]+)?$|^[*?](?:/[0-9]+)?$", RegexOptions.CultureInvariant);

    private static bool HasRecurringWildcardHour(string field)
    {
        var parts = field.Split(',');
        return parts.All(p => HourListPart.IsMatch(p)) && parts.Any(p => p.StartsWith('*') || p.StartsWith('?'));
    }
}
