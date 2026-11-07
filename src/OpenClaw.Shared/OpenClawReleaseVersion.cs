using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

public readonly record struct OpenClawReleaseVersion(
    int Year,
    int Month,
    int Patch,
    int Correction) : IComparable<OpenClawReleaseVersion>
{
    private static readonly Regex StableVersionPattern = new(
        @"^v?(?<year>0|[1-9]\d*)\.(?<month>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<correction>[1-9]\d*))?$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex AlphaVersionPattern = new(
        @"^(?<base>v?(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))-alpha\.(?<alpha>0|[1-9]\d*)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public int? Alpha { get; init; }

    public static bool TryParse(string? value, out OpenClawReleaseVersion version)
    {
        if (TryParseStable(value, out version))
            return true;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = AlphaVersionPattern.Match(value.Trim());
        if (!match.Success ||
            !TryParseStable(match.Groups["base"].Value, out var stable) ||
            !TryParseComponent(match.Groups["alpha"].Value, out var alpha))
        {
            return false;
        }

        version = stable with { Alpha = alpha };
        return true;
    }

    public static bool TryParseStable(string? value, out OpenClawReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = StableVersionPattern.Match(value.Trim());
        if (!match.Success ||
            !TryParseComponent(match.Groups["year"].Value, out var year) ||
            !TryParseComponent(match.Groups["month"].Value, out var month) ||
            !TryParseComponent(match.Groups["patch"].Value, out var patch))
        {
            return false;
        }

        var correctionGroup = match.Groups["correction"];
        var correction = 0;
        if (correctionGroup.Success &&
            !TryParseComponent(correctionGroup.Value, out correction))
        {
            return false;
        }

        version = new OpenClawReleaseVersion(year, month, patch, correction);
        return true;
    }

    public static bool IsNewerStableRelease(string? candidateTag, string? currentVersion)
        => TryParseStable(candidateTag, out var candidate) &&
           TryParseStable(currentVersion, out var current) &&
           candidate.CompareTo(current) > 0;

    public int CompareTo(OpenClawReleaseVersion other)
    {
        var year = Year.CompareTo(other.Year);
        if (year != 0)
            return year;

        var month = Month.CompareTo(other.Month);
        if (month != 0)
            return month;

        var patch = Patch.CompareTo(other.Patch);
        if (patch != 0)
            return patch;

        var correction = Correction.CompareTo(other.Correction);
        if (correction != 0)
            return correction;

        // The final release follows all alphas of the same base version.
        if (Alpha is null)
            return other.Alpha is null ? 0 : 1;
        return other.Alpha is null ? -1 : Alpha.Value.CompareTo(other.Alpha.Value);
    }

    private static bool TryParseComponent(string value, out int component)
        => int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out component);
}
