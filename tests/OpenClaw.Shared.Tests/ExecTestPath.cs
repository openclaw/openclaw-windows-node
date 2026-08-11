using System;
using System.Collections.Generic;
using System.IO;

namespace OpenClaw.Shared.Tests;

// Exec-approval tests that bind a bare payload name deliberately exercise PATH
// resolution, so they must not inherit the developer's PATH. On a machine with
// coreutils, git-bash, or similar installed ahead of System32, "hostname.exe"
// resolves to something like "C:\Program Files\coreutils\bin\hostname.exe". That
// path contains a space, which the binder correctly refuses to pin, so the test
// would fail for a reason that has nothing to do with what it is asserting.
//
// Pinning PATH to the system directory keeps the behavior under test intact (a
// bare name still has to be resolved through PATH) while removing the dependence
// on what happens to be installed. Do not replace this with the process PATH.
internal static class ExecTestPath
{
    internal static readonly string SystemDirectory = Environment.GetFolderPath(
        Environment.SpecialFolder.System);

    // A bare-name payload used by carrier binding tests. It lives in the system
    // directory on every supported Windows install and takes no arguments.
    internal const string BarePayload = "hostname.exe";

    internal static string ResolvedPayload => Path.Combine(SystemDirectory, BarePayload);

    // Passed as the `env` argument so ExecCommandResolver searches only the system
    // directory. GetSearchPaths reads PATH case-insensitively and falls back to the
    // process PATH only when this is absent or empty.
    internal static IReadOnlyDictionary<string, string> SystemOnly { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = SystemDirectory,
        };

    // Same, with extra directories appended for tests that need a controlled
    // shadow or a second candidate ahead of or behind the system directory.
    internal static IReadOnlyDictionary<string, string> SystemPlus(params string[] directories)
    {
        var parts = new List<string>(directories.Length + 1);
        parts.AddRange(directories);
        parts.Add(SystemDirectory);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = string.Join(Path.PathSeparator, parts),
        };
    }
}
