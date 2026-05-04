using System.Collections.Generic;

namespace OpenClaw.Shared.ExecApprovals;

// Option grammar of the POSIX `env` command.
// Mirrors the constants in the windows-app reference (ExecEnvOptions.cs).
internal static class ExecEnvOptions
{
    // Options that consume the next argument as their value (or use inline = form).
    internal static readonly HashSet<string> WithValue = new(System.StringComparer.Ordinal)
    {
        "-u", "--unset",
        "-c", "--chdir",
        "-s", "--split-string",
        "--default-signal",
        "--ignore-signal",
        "--block-signal",
    };

    // Options that are standalone flags (take no value at all).
    internal static readonly HashSet<string> FlagOnly = new(System.StringComparer.Ordinal)
    {
        "-i", "--ignore-environment",
        "-0", "--null",
    };

    // Prefixes for the inline-value form (e.g. `-uFOO` or `--unset=FOO`).
    internal static readonly IReadOnlyList<string> InlineValuePrefixes =
    [
        "-u", "-c", "-s",
        "--unset=",
        "--chdir=",
        "--split-string=",
        "--default-signal=",
        "--ignore-signal=",
        "--block-signal=",
    ];
}
