using System.Buffers;

namespace OpenClaw.Shared;

/// <summary>
/// Shell-aware argument quoting for cmd.exe and PowerShell.
/// Used by LocalCommandRunner (actual execution) and SystemCapability
/// (system.run.prepare display formatting).
/// </summary>
internal static class ShellQuoting
{
    // SearchValues<char> builds an optimized SIMD lookup structure once at startup,
    // allowing IndexOfAny to scan multiple characters simultaneously (SSE2/AVX2 on x64).
    // Equivalent set to the former IsShellMetachar switch — 25 shell metacharacters.
    private static readonly SearchValues<char> s_shellMetachars =
        SearchValues.Create(" \t\"'&|;<>()^%!$`*?[]{}~\n\r");

    /// <summary>
    /// Returns true when the argument contains characters that require quoting
    /// to prevent shell splitting or metacharacter interpretation.
    /// </summary>
    internal static bool NeedsQuoting(string arg)
    {
        return arg.AsSpan().IndexOfAny(s_shellMetachars) >= 0;
    }

    /// <summary>
    /// Wraps an argument to prevent shell splitting and metacharacter interpretation.
    /// Uses shell-appropriate quoting: double quotes for cmd.exe, single quotes for
    /// PowerShell. PowerShell requires single quotes because double quotes in
    /// ProcessStartInfo.Arguments are stripped by the Windows CRT argv parser
    /// before PowerShell receives the -Command string.
    /// </summary>
    internal static string QuoteForShell(string arg, bool isCmd)
    {
        if (string.IsNullOrEmpty(arg))
            return isCmd ? "\"\"" : "''";

        if (!NeedsQuoting(arg))
            return arg;

        if (isCmd)
        {
            // cmd.exe: wrap in double quotes, escape inner double quotes by doubling
            return "\"" + arg.Replace("\"", "\"\"") + "\"";
        }
        else
        {
            // PowerShell -Command: wrap in single quotes, escape inner single quotes
            // by doubling them (PowerShell's single-quote escape convention)
            return "'" + arg.Replace("'", "''") + "'";
        }
    }

    /// <summary>
    /// Formats argv into a display command string (for logging / gateway consistency).
    /// Uses double-quote convention with backslash escaping, matching the gateway's
    /// formatExecCommand behavior.
    /// </summary>
    internal static string FormatExecCommand(string[] argv)
    {
        return string.Join(" ", argv.Select(FormatSingleArg));

        static string FormatSingleArg(string arg)
        {
            if (arg.Length == 0) return "\"\"";
            if (!NeedsQuoting(arg)) return arg;
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }
    }

}
