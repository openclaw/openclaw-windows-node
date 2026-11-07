using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using OpenClaw.Connection.Migration;
using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

[SupportedOSPlatform("windows")]
public sealed class InnoInstallationMetadataTests
{
    private const string Executable = @"C:\OpenClaw Test Fixture\Companion\OpenClaw.Tray.WinUI.exe";

    [WindowsFact]
    public void RunningReleaseRefreshesOnlyDisplayFieldsAndRemainsIdempotent()
    {
        foreach (var version in new[] { "2026.9.8-2", "2026.9.5-alpha.117", "2026.9.8-10" })
        {
            using var fixture = new RegistrationFixture();
            var before = fixture.Snapshot();

            Assert.Null(fixture.Owner.Refresh(Executable, version, false, false, false));

            using var registration = fixture.Open();
            Assert.Equal(version, registration.GetValue("DisplayVersion"));
            Assert.Equal("OpenClaw Companion version " + version, registration.GetValue("DisplayName"));
            Assert.Equal(RegistryValueKind.String, registration.GetValueKind("DisplayVersion"));
            Assert.Equal(RegistryValueKind.String, registration.GetValueKind("DisplayName"));
            var after = fixture.Snapshot();
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var name in before.Keys.Except(["DisplayVersion", "DisplayName"]))
                Assert.Equal(before[name], after[name]);

            Assert.Null(fixture.Owner.Refresh(Executable, version, false, false, false));
            Assert.Equal(after.OrderBy(pair => pair.Key), fixture.Snapshot().OrderBy(pair => pair.Key));
        }
    }

    [WindowsFact]
    public void InterruptedDisplayRefreshIsRepairedOnNextStartup()
    {
        foreach (var changedField in new[] { "DisplayVersion", "DisplayName" })
        {
            using var fixture = new RegistrationFixture();
            using (var registration = fixture.Open())
                registration.SetValue(changedField, changedField == "DisplayVersion"
                    ? "2026.9.8-2" : "OpenClaw Companion version 2026.9.8-2");

            Assert.Null(fixture.Owner.Refresh(Executable, "2026.9.8-2", false, false, false));
            using var after = fixture.Open();
            Assert.Equal("2026.9.8-2", after.GetValue("DisplayVersion"));
            Assert.Equal("OpenClaw Companion version 2026.9.8-2", after.GetValue("DisplayName"));
        }
    }

    [WindowsFact]
    public void ForeignOrMalformedRegistrationRemainsUnchanged()
    {
        foreach (var field in new[] { "InstallLocation", "Publisher", "UninstallString", "DisplayName", "DisplayVersion" })
        {
            using var fixture = new RegistrationFixture();
            using (var registration = fixture.Open())
            {
                if (field == "DisplayVersion")
                    registration.SetValue(field, 42, RegistryValueKind.DWord);
                else
                    registration.SetValue(field, "different installation", RegistryValueKind.String);
            }
            var before = fixture.Snapshot();
            Assert.Null(fixture.Owner.Refresh(Executable, "2026.9.8-2", false, false, false));
            Assert.Equal(before.OrderBy(pair => pair.Key), fixture.Snapshot().OrderBy(pair => pair.Key));
        }
    }

    [WindowsFact]
    public void PortableCopyCannotRefreshAnotherInstalledCopy()
    {
        using var fixture = new RegistrationFixture();
        var before = fixture.Snapshot();
        Assert.Null(fixture.Owner.Refresh(@"C:\Portable\OpenClaw.Tray.WinUI.exe", "2026.9.8-2", false, false, false));
        Assert.Equal(before.OrderBy(pair => pair.Key), fixture.Snapshot().OrderBy(pair => pair.Key));
    }

    [Fact]
    public void PackagedDevAndIsolatedProcessesNeverOpenRegistration()
    {
        var owner = new InnoInstallationMetadata(() => throw new InvalidOperationException("Registry must not be opened"));
        Assert.Null(owner.Refresh(Executable, "2026.9.8-2", true, false, false));
        Assert.Null(owner.Refresh(Executable, "2026.9.8-2", false, true, false));
        Assert.Null(owner.Refresh(Executable, "2026.9.8-2", false, false, true));
    }

    [WindowsFact]
    public void MissingRegistrationIsNotCreated()
    {
        var owner = new InnoInstallationMetadata(() => null);
        Assert.Null(owner.Refresh(Executable, "2026.9.8-2", false, false, false));
    }

    [WindowsFact]
    public void RegistryFailureReturnsRecoveryWarningWithoutBlockingStartup()
    {
        foreach (var failure in new Exception[]
        {
            new UnauthorizedAccessException(), new SecurityException(), new IOException()
        })
        {
            var owner = new InnoInstallationMetadata(() => throw failure);
            var warning = owner.Refresh(Executable, "2026.9.8-2", false, false, false);
            Assert.NotNull(warning);
            Assert.Contains(failure.GetType().Name, warning);
            Assert.Contains("repair the installation with Setup", warning);
        }
    }

    [Fact]
    public void StartupRefreshFollowsAdmissionAndCompletesBeforeStartupMarker()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        var launch = source[source.IndexOf("private async Task OnLaunchedAsync", StringComparison.Ordinal)..];
        var refresh = launch.IndexOf("new InnoInstallationMetadata().Refresh(", StringComparison.Ordinal);
        Assert.InRange(launch.IndexOf("InnoMigrationStartupGuard.ShouldStopLaunch", StringComparison.Ordinal), 0, refresh - 1);
        Assert.InRange(launch.IndexOf("ForwardLaunchToPrimaryAsync", StringComparison.Ordinal), 0, refresh - 1);
        Assert.InRange(launch.IndexOf("await _activationRouter.DispatchPlanAsync", StringComparison.Ordinal), 0, refresh - 1);
        Assert.True(refresh < launch.IndexOf("Logger.Info(\"Application started (WinUI 3)\")", StringComparison.Ordinal));
        Assert.Equal(InnoInstallationDetector.UninstallKey, InnoInstallationMetadata.UninstallKey);
    }

    private sealed class RegistrationFixture : IDisposable
    {
        private readonly string _path = @"Software\OpenClaw.Tests\Issue1676-" + Guid.NewGuid().ToString("N");
        public InnoInstallationMetadata Owner => new(() => Open());

        public RegistrationFixture()
        {
            using var key = Registry.CurrentUser.CreateSubKey(_path);
            key.SetValue("InstallLocation", @"C:\OpenClaw Test Fixture\Companion\");
            key.SetValue("Publisher", "OpenClaw Foundation");
            key.SetValue("UninstallString", @"""C:\OpenClaw Test Fixture\Companion\unins000.exe""");
            key.SetValue("DisplayName", "OpenClaw Companion version 2026.9.5-alpha.93");
            key.SetValue("DisplayVersion", "2026.9.5-alpha.93");
            key.SetValue("InstallDate", "20261003");
            key.SetValue("MajorVersion", 2026, RegistryValueKind.DWord);
            key.SetValue("QuietUninstallString", @"""C:\OpenClaw Test Fixture\Companion\unins000.exe"" /SILENT");
            key.SetValue("UnknownValue", new byte[] { 3, 1, 4 }, RegistryValueKind.Binary);
        }

        public RegistryKey Open() => Registry.CurrentUser.OpenSubKey(_path, writable: true)!;

        public Dictionary<string, (RegistryValueKind Kind, string Value)> Snapshot()
        {
            using var key = Open();
            return key.GetValueNames().ToDictionary(name => name, name =>
                (key.GetValueKind(name), JsonSerializer.Serialize(key.GetValue(name, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames))));
        }

        public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_path);
    }
}
