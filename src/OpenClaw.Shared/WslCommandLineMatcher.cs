using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

/// <summary>Matches the exact WSL distribution argument in app-owned keepalive commands.</summary>
public static class WslCommandLineMatcher
{
    private static readonly Regex DistroArgument = new(
        """(?:^|\s)(?:-d|--distribution)\s+(?:"(?<quoted>[^"]+)"|(?<bare>[^\s"]+))(?=\s|$)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SleepInfinity = new(
        """(?:^|\s)sleep\s+infinity(?=\s|$)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool IsKeepaliveForDistro(string? commandLine, string? distroName)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(distroName))
            return false;

        var match = DistroArgument.Match(commandLine);
        if (!match.Success)
            return false;

        var actualDistro = match.Groups["quoted"].Success
            ? match.Groups["quoted"].Value
            : match.Groups["bare"].Value;

        return string.Equals(actualDistro, distroName, StringComparison.OrdinalIgnoreCase)
            && SleepInfinity.IsMatch(commandLine);
    }
}
