using System.Runtime.CompilerServices;
using OpenClawTray;
using OpenClaw.TestSupport;

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
        SetMissingOverrides(root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); }
            // slopwatch-ignore: SW003 Temp cleanup is best-effort, and the folder may not exist.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }

    internal static void SetMissingOverrides(string root)
    {
        SetIfMissing("OPENCLAW_TRAY_DATA_DIR", Path.Combine(root, "OPENCLAW_TRAY_DATA_DIR"));
        SetIfMissing("OPENCLAW_TRAY_APPDATA_DIR", Path.Combine(root, "OPENCLAW_TRAY_APPDATA_DIR"));

        // Setup prefers the modern LOCALAPPDATA root, but registry migration prefers the legacy
        // direct directory and then looks only there. A caller-selected legacy directory therefore
        // must not be shadowed by a generated root, and a generated legacy directory must be the
        // root with the data folder appended, as GatewayFixtureIsolation requires.
        if (IsSet("OPENCLAW_TRAY_LOCAL_DATA_DIR") && !IsSet("OPENCLAW_TRAY_LOCALAPPDATA_DIR"))
            return;

        SetIfMissing("OPENCLAW_TRAY_LOCALAPPDATA_DIR", Path.Combine(root, "OPENCLAW_TRAY_LOCALAPPDATA_DIR"));
        SetIfMissing("OPENCLAW_TRAY_LOCAL_DATA_DIR", Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")!, AppIdentity.DataDirectoryName));
    }

    private static bool IsSet(string name) => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));

    private static void SetIfMissing(string name, string value)
    {
        if (!IsSet(name))
            Environment.SetEnvironmentVariable(name, value);
    }
}

[Collection(OpenClawTrayDataDirEnvironmentCollection.Name)]
public sealed class TrayTestProfileIsolationTests
{
    [Fact]
    public void ProfileOverridesAreSetBeforeAnyTestRuns()
    {
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")));
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR")));
        Assert.True(
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR")));
    }

    [Fact]
    public void MissingOverridesUseTheGeneratedProfile()
    {
        using var environment = new EnvironmentScope();
        foreach (var name in TrayTestProfileIsolation.OverrideNames)
            environment.Set(name, null);
        var root = Path.Combine(Path.GetTempPath(), "generated-test-profile");

        TrayTestProfileIsolation.SetMissingOverrides(root);

        var setupDirectory = Path.Combine(root, "OPENCLAW_TRAY_LOCALAPPDATA_DIR", AppIdentity.DataDirectoryName);
        Assert.Equal(Path.Combine(root, "OPENCLAW_TRAY_DATA_DIR"), Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR"));
        Assert.Equal(Path.Combine(root, "OPENCLAW_TRAY_APPDATA_DIR"), Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR"));
        Assert.Equal(setupDirectory, Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR"));
        Assert.Equal(setupDirectory, AppIdentity.ResolveSetupLocalDataDirectory());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerSelectedSetupDirectoryKeepsItsEffectivePrecedence(bool modernOverrideIsSet)
    {
        using var environment = new EnvironmentScope();
        foreach (var name in TrayTestProfileIsolation.OverrideNames)
            environment.Set(name, null);

        var legacyDirectory = Path.Combine(Path.GetTempPath(), "caller-owned-setup");
        var modernRoot = Path.Combine(Path.GetTempPath(), "caller-owned-local-appdata");
        environment.Set("OPENCLAW_TRAY_LOCAL_DATA_DIR", legacyDirectory);
        if (modernOverrideIsSet)
            environment.Set("OPENCLAW_TRAY_LOCALAPPDATA_DIR", modernRoot);

        TrayTestProfileIsolation.SetMissingOverrides(Path.Combine(Path.GetTempPath(), "generated-test-profile"));

        Assert.Equal(legacyDirectory, Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR"));
        Assert.Equal(modernOverrideIsSet ? modernRoot : null,
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR"));
        Assert.Equal(modernOverrideIsSet ? Path.Combine(modernRoot, AppIdentity.DataDirectoryName) : legacyDirectory,
            AppIdentity.ResolveSetupLocalDataDirectory());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetupStateWrittenAtTheEffectiveSetupPathIsFoundByRegistryMigration(bool callerSetModernRootOnly)
    {
        using var environment = new EnvironmentScope();
        foreach (var name in TrayTestProfileIsolation.OverrideNames)
            environment.Set(name, null);
        var root = Path.Combine(Path.GetTempPath(), "OpenClawTrayTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (callerSetModernRootOnly)
                environment.Set("OPENCLAW_TRAY_LOCALAPPDATA_DIR", Path.Combine(root, "caller-local-root"));

            TrayTestProfileIsolation.SetMissingOverrides(root);

            // Setup writes its state where AppIdentity resolves it; registry migration must read the same file.
            var setupDirectory = AppIdentity.ResolveSetupLocalDataDirectory();
            Directory.CreateDirectory(setupDirectory);
            File.WriteAllText(
                Path.Combine(setupDirectory, "setup-state.json"),
                """{"DistroName":"OpenClawGateway","GatewayUrl":"ws://127.0.0.1:18789"}""");
            var settingsDirectory = Path.Combine(root, "settings");
            Directory.CreateDirectory(settingsDirectory);
            var registry = new OpenClaw.Connection.GatewayRegistry(settingsDirectory);

            Assert.True(registry.MigrateFromSettings(
                "ws://localhost:18789", "shared-token", null, false, null, null, 0, 0, settingsDirectory));
            Assert.Equal("OpenClawGateway", registry.GetActive()!.SetupManagedDistroName);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            // slopwatch-ignore: SW003 Temp cleanup is best-effort and must not hide the test outcome.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void CallerSelectedProfileUnderRoamingAppDataIsPreserved()
    {
        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTrayTests", Guid.NewGuid().ToString("N"));
        using var environment = new EnvironmentScope("OPENCLAW_TRAY_DATA_DIR", profile);

        TrayTestProfileIsolation.SetMissingOverrides(Path.Combine(Path.GetTempPath(), "generated-test-profile"));

        Assert.Equal(profile, AppIdentity.ResolveRoamingDataDirectory());
        Assert.Equal(profile, AppIdentity.ResolveLocalDataDirectory());
        TrayDataDirectoriesResolveOutsideTheUserProfile();
    }

    [Fact]
    public void TrayDataDirectoriesResolveOutsideTheUserProfile()
    {
        var appDataRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        foreach (var directory in new[]
                 {
                     AppIdentity.ResolveRoamingDataDirectory(),
                     AppIdentity.ResolveLocalDataDirectory(),
                 })
        {
            foreach (var root in appDataRoots)
            foreach (var profile in new[] { "OpenClawTray", "OpenClawTray-Dev" })
                Assert.False(IsSameOrDescendant(directory, Path.Combine(root, profile)), directory);
        }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("/", true)]
    [InlineData("/chat-state", true)]
    [InlineData("/../OpenClawTray/chat-state", true)]
    [InlineData("/../OpenClawTrayTests", false)]
    [InlineData("Tests", false)]
    public void ProfileBoundaryIsNormalizedAndDoesNotRejectSiblingNames(string suffix, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "Roaming", "OpenClawTray");
        var candidate = root + suffix.Replace('/', Path.DirectorySeparatorChar);

        Assert.Equal(expected, IsSameOrDescendant(candidate, root + Path.DirectorySeparatorChar));
    }

    private static bool IsSameOrDescendant(string directory, string root)
    {
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return fullDirectory.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullDirectory.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
