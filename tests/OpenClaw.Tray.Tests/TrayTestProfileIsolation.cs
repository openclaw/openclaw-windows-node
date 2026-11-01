using System.Runtime.CompilerServices;
using OpenClawTray;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Keeps a plain <c>dotnet test</c> out of the developer's real Companion profile. Production code
/// falls back to %APPDATA% and %LOCALAPPDATA% when these overrides are unset, so without this a run
/// writes fixture logs, chat state and a device key beside a real installation. Values the caller
/// already set, as CI does, are kept.
/// </summary>
internal static class TrayTestProfileIsolation
{
    internal static readonly string[] OverrideNames =
    [
        "OPENCLAW_TRAY_DATA_DIR",
        "OPENCLAW_TRAY_APPDATA_DIR",
        "OPENCLAW_TRAY_LOCAL_DATA_DIR",
        "OPENCLAW_TRAY_LOCALAPPDATA_DIR",
    ];

    [ModuleInitializer]
    public static void Initialize()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenClawTrayTests-" + Guid.NewGuid().ToString("N"));
        foreach (var name in OverrideNames)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                Environment.SetEnvironmentVariable(name, Path.Combine(root, name));
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); }
            // slopwatch-ignore: SW003 Temp cleanup is best-effort, and the folder may not exist.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }
}

public sealed class TrayTestProfileIsolationTests
{
    [Fact]
    public void ProfileOverridesAreSetBeforeAnyTestRuns()
    {
        foreach (var name in TrayTestProfileIsolation.OverrideNames)
            Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)), name);
    }

    [Fact]
    public void TrayDataDirectoriesResolveOutsideTheUserProfile()
    {
        var realRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClawTray"),
        };

        foreach (var directory in new[]
                 {
                     AppIdentity.ResolveRoamingDataDirectory(),
                     AppIdentity.ResolveLocalDataDirectory(),
                 })
        {
            foreach (var root in realRoots)
                Assert.False(directory.StartsWith(root, StringComparison.OrdinalIgnoreCase), directory);
        }
    }
}
