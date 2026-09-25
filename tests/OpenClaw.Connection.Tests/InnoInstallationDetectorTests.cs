using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class InnoInstallationDetectorTests
{
    [Fact]
    public void ProductionRegistryKey_MatchesInstallerAppIdExactly()
    {
        Assert.Equal(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{M0LTB0T-TRAY-4PP1-D3N7}_is1",
            InnoInstallationDetector.UninstallKey);
    }

    [Fact]
    public void AbsentExactRegistration_DoesNotInspectFiles()
    {
        using var fixture = new Fixture();
        fixture.Source.Registrations.Clear();
        fixture.Source.FileError = new InvalidOperationException("No filesystem inspection expected.");

        Assert.Equal(InnoInstallationStatus.NotInstalled, fixture.Detect().Status);
        Assert.Equal(4, fixture.Source.RegistryReads.Count);
        Assert.Empty(fixture.Logger.Warnings);
    }

    /// <summary>
    /// A machine-wide payload never lives in the canonical per-user directory, so falling back to
    /// it would answer for a different installation. An absent answer must read as "no payload".
    /// </summary>
    [Fact]
    public void AMachineRegistrationNamingNoPath_IsNotAnsweredByThePerUserDirectory()
    {
        using var fixture = new Fixture();
        var registration = fixture.Registration;
        fixture.Source.Registrations.Clear();
        fixture.Source.Registrations[(RegistryHive.LocalMachine, RegistryView.Registry64)] =
            registration with { InstallLocation = null };

        var result = fixture.Detect();

        Assert.Equal(InnoInstallationStatus.Unsupported, result.Status);
        Assert.False(result.SourcePayloadPresent);
    }

    [Fact]
    public void AMachineRegistrationWithItsOwnPayload_ReportsItPresent()
    {
        using var fixture = new Fixture();
        var registration = fixture.Registration;
        var machineDirectory = fixture.Temp.Combine("Machine");
        Directory.CreateDirectory(machineDirectory);
        File.WriteAllText(Path.Combine(machineDirectory, "OpenClaw.Tray.WinUI.exe"), "fixture only");
        fixture.Source.Registrations.Clear();
        fixture.Source.Registrations[(RegistryHive.LocalMachine, RegistryView.Registry64)] =
            registration with { InstallLocation = machineDirectory };

        Assert.True(fixture.Detect().SourcePayloadPresent);
    }

    /// <summary>
    /// Presence is a question about the machine, not about one registry view. An orphan beside a
    /// live registration must not answer for both, or the Store app starts next to a real client.
    /// </summary>
    [Fact]
    public void AnOrphanBesideALiveMachineRegistration_StillReportsThePayloadPresent()
    {
        using var fixture = new Fixture();
        var registration = fixture.Registration;
        var liveDirectory = fixture.Temp.Combine("Machine32");
        Directory.CreateDirectory(liveDirectory);
        File.WriteAllText(Path.Combine(liveDirectory, "OpenClaw.Tray.WinUI.exe"), "fixture only");
        fixture.Source.Registrations.Clear();
        fixture.Source.Registrations[(RegistryHive.LocalMachine, RegistryView.Registry64)] =
            registration with { InstallLocation = fixture.Temp.Combine("GoneForever") };
        fixture.Source.Registrations[(RegistryHive.LocalMachine, RegistryView.Registry32)] =
            registration with { InstallLocation = liveDirectory };

        Assert.True(fixture.Detect().SourcePayloadPresent);
    }

    [Fact]
    public void AnOrphanBesideALiveUserRegistration_StillReportsThePayloadPresent()
    {
        using var fixture = new Fixture();
        var registration = fixture.Registration;
        fixture.Source.Registrations[(RegistryHive.CurrentUser, RegistryView.Registry64)] =
            registration with { InstallLocation = fixture.Temp.Combine("GoneForever") };
        fixture.Source.Registrations[(RegistryHive.CurrentUser, RegistryView.Registry32)] =
            registration;

        var result = fixture.Detect();

        Assert.Equal(InnoInstallationStatus.Unsupported, result.Status);
        Assert.True(result.SourcePayloadPresent);
    }

    /// <summary>
    /// A registration is ordinary user-writable data, and a fully qualified path may still be
    /// malformed. Probing it must read as "no payload" rather than taking down startup admission.
    /// </summary>
    [Fact]
    public void AMalformedInstallLocation_ReadsAsNoPayloadRatherThanThrowing()
    {
        using var fixture = new Fixture();
        var registration = fixture.Registration;
        fixture.Source.Registrations.Clear();
        fixture.Source.Registrations[(RegistryHive.LocalMachine, RegistryView.Registry64)] =
            registration with { InstallLocation = "C:\\unusable\0location" };

        var result = fixture.Detect();

        Assert.Equal(InnoInstallationStatus.Unsupported, result.Status);
        Assert.False(result.SourcePayloadPresent);
    }

    /// <summary>
    /// An installed app whose identity marker is malformed still has to block startup. The
    /// validation failure says the installation is unsupportable, not that it is absent, and a
    /// user can cause this by appending bytes to a file in the previous install directory.
    /// </summary>
    [Fact]
    public void AnInstalledSourceThatFailsValidation_StillReportsItsPayloadPresent()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Payload("app-identity.txt"), new string('r', 64));

        var result = fixture.Detect();

        Assert.Equal(InnoInstallationStatus.Unsupported, result.Status);
        Assert.True(result.SourcePayloadPresent);
    }

    /// <summary>
    /// An unreadable registration must not hide a live sibling. Probing is per registration, so a
    /// failure on one contributes no evidence instead of abandoning the whole inspection.
    /// </summary>
    [Fact]
    public void AnUnreadableRegistration_DoesNotMaskALiveSibling()
    {
        using var fixture = new Fixture();
        var registration = fixture.Registration;
        var unreadable = Path.Combine(fixture.Temp.Path, "unreadable");
        // user64 is probed first, so the unreadable registration has to sit there: were it second
        // the live sibling would short-circuit the OR and the masking would go unobserved.
        fixture.Source.Registrations[(RegistryHive.CurrentUser, RegistryView.Registry32)] = registration;
        fixture.Registration = registration with { InstallLocation = unreadable + "\\" };
        fixture.Source.FileError = new UnauthorizedAccessException("denied");
        fixture.Source.FileErrorPath = unreadable;

        var result = fixture.Detect();

        Assert.Equal(InnoInstallationStatus.Unsupported, result.Status);
        Assert.True(result.SourcePayloadPresent);
    }

    [Theory]
    [InlineData(Machine.Amd64, "x64")]
    [InlineData(Machine.Arm64, "arm64")]
    public void DefaultProductionInstallation_IsDetected(Machine machine, string architecture)    {
        using var fixture = new Fixture();
        fixture.Source.Binary = new(machine, "2026.9.17.0");

        var result = fixture.Detect();

        Assert.Equal(InnoInstallationStatus.Detected, result.Status);
        Assert.Equal(new InnoInstallation(fixture.InstallDirectory,
            fixture.Payload("OpenClaw.Tray.WinUI.exe"), fixture.Payload("unins000.exe"),
            architecture, new Version(2026, 9, 17, 0)), result.Installation);
        Assert.Null(result.Reason);
        Assert.Equal(4, fixture.Source.RegistryReads.Count);
    }

    [Fact]
    public void ActualProductionInnoMetadata_IsDetected()
    {
        using var fixture = new Fixture();
        using var registry = new RegistryFixture();
        // VM-produced metadata, with only the user-specific install root relocated.
        var registration = new InnoInstallationRegistration(
            fixture.InstallDirectory + "\\", "2026.9.5.0", "OpenClaw Foundation",
            "OpenClaw Companion version 2026.9.5.0", $"\"{fixture.Payload("unins000.exe")}\"");
        using (var production = registry.Root.CreateSubKey(InnoInstallationDetector.UninstallKey))
            WriteRegistration(production, registration);
        fixture.Source.Binary = new(Machine.Amd64, "2026.9.5.0");
        var detector = new InnoInstallationDetector(fixture.Temp.Path, fixture.Logger, registry.Source(fixture.Source));

        var result = detector.Detect();

        Assert.Equal(InnoInstallationStatus.Detected, result.Status);
        Assert.Equal(new InnoInstallation(fixture.InstallDirectory,
            fixture.Payload("OpenClaw.Tray.WinUI.exe"), fixture.Payload("unins000.exe"),
            "x64", new Version(2026, 9, 5, 0)), result.Installation);
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Fact]
    public void IdenticalSharedViewAliases_AreDeduplicated()
    {
        using var fixture = new Fixture();
        fixture.Source.Registrations[(RegistryHive.CurrentUser, RegistryView.Registry32)] = fixture.Registration;
        Assert.Equal(InnoInstallationStatus.Detected, fixture.Detect().Status);
    }

    [Fact]
    public void CurrentUser32ViewAlone_IsDetected()
    {
        using var fixture = new Fixture();
        var registration = fixture.Registration;
        fixture.Source.Registrations.Clear();
        fixture.Source.Registrations[(RegistryHive.CurrentUser, RegistryView.Registry32)] = registration;
        Assert.Equal(InnoInstallationStatus.Detected, fixture.Detect().Status);
    }

    [Fact]
    public void ConflictingViews_AreUnsupported()
    {
        using var fixture = new Fixture();
        fixture.Source.Registrations[(RegistryHive.CurrentUser, RegistryView.Registry32)] =
            fixture.Registration with { DisplayVersion = "2026.9.18" };
        fixture.AssertUnsupported();
    }

    [Theory]
    [InlineData(RegistryView.Registry32, false)]
    [InlineData(RegistryView.Registry64, false)]
    [InlineData(RegistryView.Registry32, true)]
    [InlineData(RegistryView.Registry64, true)]
    public void MachineWideRegistration_BlocksEvenWithValidCurrentUser(RegistryView view, bool includeUser)
    {
        using var fixture = new Fixture();
        var registration = fixture.Registration;
        if (!includeUser)
            fixture.Source.Registrations.Clear();
        fixture.Source.Registrations[(RegistryHive.LocalMachine, view)] = registration;
        fixture.AssertUnsupported();
    }

    [Theory]
    [InlineData("OpenClaw.Tray.WinUI.exe")]
    [InlineData("unins000.exe")]
    [InlineData("app-identity.txt")]
    [InlineData("Test-InnoMigration.ps1")]
    [InlineData("MigrationRecordCodec.cs")]
    [InlineData("Uninstall-LocalGateway.ps1")]
    public void StaleRegistrationWithMissingPayload_IsUnsupported(string name)
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Payload(name));
        fixture.AssertUnsupported();
    }

    [Fact]
    public void MissingPayload_CarriesTheRegisteredVersionSoAdmissionCanAskForAnUpdate()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Payload("MigrationRecordCodec.cs"));

        var result = fixture.Detect();

        Assert.Equal(InnoInstallationStatus.Unsupported, result.Status);
        Assert.Equal(Version.Parse("2026.9.17.0"), result.RegisteredVersion);
    }

    /// <summary>
    /// The payload loop cannot tell an old install from an orphan on its own, so admission needs
    /// the executable itself as the discriminator. A canonical install that merely predates the
    /// migration payload is still a real, runnable client.
    /// </summary>
    [Theory]
    [InlineData("MigrationRecordCodec.cs")]
    [InlineData("Test-InnoMigration.ps1")]
    [InlineData("Uninstall-LocalGateway.ps1")]
    public void AnInstallMissingOnlyTheMigrationPayload_StillReportsItsSourcePresent(string name)
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Payload(name));

        var result = fixture.Detect();

        Assert.Equal(InnoInstallationStatus.Unsupported, result.Status);
        Assert.True(result.SourcePayloadPresent);
    }

    /// <summary>
    /// The opposite case, and the reason the signal exists: an interrupted uninstall can leave the
    /// registration behind with the app gone. Reporting a source here would let startup policy
    /// block a user who has no working app left.
    /// </summary>
    [Fact]
    public void ARegistrationWhoseExecutableIsGone_ReportsNoSourcePresent()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Payload("OpenClaw.Tray.WinUI.exe"));

        var result = fixture.Detect();

        Assert.Equal(InnoInstallationStatus.Unsupported, result.Status);
        Assert.False(result.SourcePayloadPresent);
    }

    [Fact]
    public void ADetectedInstallation_ReportsItsSourcePresent()
    {
        using var fixture = new Fixture();

        var result = fixture.Detect();

        Assert.Equal(InnoInstallationStatus.Detected, result.Status);
        Assert.True(result.SourcePayloadPresent);
    }

    [Fact]
    public void AnAbsentRegistration_ReportsNoSourcePresent()
    {
        using var fixture = new Fixture();
        fixture.Source.Registrations.Clear();

        var result = fixture.Detect();

        Assert.Equal(InnoInstallationStatus.NotInstalled, result.Status);
        Assert.False(result.SourcePayloadPresent);
    }

    [Fact]
    public void UnsupportableShape_CarriesNoRegisteredVersion()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Payload("app-identity.txt"), "dev");

        Assert.Null(fixture.Detect().RegisteredVersion);
    }

    [Fact]
    public void DirectoryInsteadOfPayload_IsUnsupported()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Payload("unins000.exe"));
        Directory.CreateDirectory(fixture.Payload("unins000.exe"));
        fixture.AssertUnsupported();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3-beta")]
    [InlineData("1.2.3+release")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3.4.5")]
    [InlineData("999999999999.1.1")]
    public void MalformedRegisteredVersion_IsUnsupported(string? version)
    {
        using var fixture = new Fixture();
        fixture.Registration = fixture.Registration with { DisplayVersion = version };
        fixture.AssertUnsupported();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2026.9.18")]
    [InlineData("2026.9.17-beta")]
    [InlineData("2026.9.17.1")]
    public void BinaryVersionMustMatchNormalizedRegistryVersion(string? version)
    {
        using var fixture = new Fixture();
        fixture.Source.Binary = fixture.Source.Binary with { Version = version };
        fixture.AssertUnsupported();
    }

    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.ArmThumb2)]
    [InlineData(Machine.Unknown)]
    public void UnsupportedPeMachine_IsUnsupported(Machine machine)
    {
        using var fixture = new Fixture();
        fixture.Source.Binary = fixture.Source.Binary with { Machine = machine };
        fixture.AssertUnsupported();
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("custom")]
    [InlineData("dot")]
    [InlineData("expanded")]
    [InlineData("missing")]
    public void NoncanonicalInstallLocation_IsUnsupported(string kind)
    {
        using var fixture = new Fixture();
        fixture.Registration = fixture.Registration with
        {
            InstallLocation = kind switch
            {
                "relative" => "OpenClawTray",
                "custom" => fixture.Temp.Combine("Custom"),
                "dot" => fixture.Temp.Combine(".", "OpenClawTray"),
                "expanded" => @"%LOCALAPPDATA%\OpenClawTray",
                _ => null
            }
        };
        fixture.AssertUnsupported();
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("unquoted")]
    [InlineData("outside")]
    [InlineData("arguments")]
    [InlineData("other-uninstaller")]
    [InlineData("missing")]
    public void UninstallCommandMustBeExactQuotedCanonicalPath(string kind)
    {
        using var fixture = new Fixture();
        fixture.Registration = fixture.Registration with
        {
            UninstallString = kind switch
            {
                "relative" => "\"unins000.exe\"",
                "unquoted" => fixture.Payload("unins000.exe"),
                "outside" => $"\"{fixture.Temp.Combine("unins000.exe")}\"",
                "arguments" => $"\"{fixture.Payload("unins000.exe")}\" /SILENT",
                "other-uninstaller" => $"\"{fixture.Payload("unins001.exe")}\"",
                _ => null
            }
        };
        fixture.AssertUnsupported();
    }

    [Theory]
    [InlineData("OpenClaw Companion (Dev)", "OpenClaw Foundation")]
    [InlineData("OpenClaw Companion (Dev) version 2026.9.17", "OpenClaw Foundation")]
    [InlineData("OpenClaw Companion", "OpenClaw Foundation")]
    [InlineData("OpenClaw Companion version 2026.9.17.0", "OpenClaw Foundation")]
    [InlineData("OpenClaw Companion version 2026.9.18", "OpenClaw Foundation")]
    [InlineData("OpenClaw Companion version 2026.9.17 ", "OpenClaw Foundation")]
    [InlineData("OpenClaw Companion version 2026.9.17", "Unknown")]
    [InlineData(null, "OpenClaw Foundation")]
    [InlineData("OpenClaw Companion version 2026.9.17", null)]
    public void ProductionNameAndPublisherAreRequired(string? name, string? publisher)
    {
        using var fixture = new Fixture();
        fixture.Registration = fixture.Registration with { DisplayName = name, Publisher = publisher };
        fixture.AssertUnsupported();
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("")]
    [InlineData("release-dev")]
    [InlineData(" release ")]
    [InlineData("release\nextra")]
    [InlineData("this marker exceeds the bounded identity marker length")]
    public void NonReleaseOrMalformedIdentity_IsUnsupported(string identity)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Payload("app-identity.txt"), identity);
        fixture.AssertUnsupported();
    }

    [Theory]
    [InlineData("release")]
    [InlineData("release\n")]
    [InlineData("release\r\n")]
    public void BuildMarkerLineEndings_AreAccepted(string identity)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Payload("app-identity.txt"), identity);
        Assert.Equal(InnoInstallationStatus.Detected, fixture.Detect().Status);
    }

    [Theory]
    [InlineData("io", false)]
    [InlineData("access", false)]
    [InlineData("security", false)]
    [InlineData("io", true)]
    [InlineData("access", true)]
    [InlineData("security", true)]
    public void OperatingSystemErrors_AreLoggedAndFailInspection(string kind, bool files)
    {
        using var fixture = new Fixture();
        Exception error = kind switch
        {
            "io" => new IOException("read failed"),
            "access" => new UnauthorizedAccessException("denied"),
            _ => new SecurityException("denied")
        };
        if (files)
            fixture.Source.FileError = error;
        else
            fixture.Source.RegistryError = error;
        var result = fixture.Detect();
        Assert.Equal(InnoInstallationStatus.InspectionFailed, result.Status);
        Assert.Null(result.Installation);
        Assert.False(string.IsNullOrEmpty(result.Reason));
        Assert.Same(error, Assert.Single(fixture.Logger.Errors));
    }

    [Fact]
    public void InaccessibleMachineView_DoesNotBecomeNotInstalled()
    {
        using var fixture = new Fixture();
        fixture.Source.Registrations.Clear();
        fixture.Source.RegistryError = new UnauthorizedAccessException();
        fixture.Source.ErrorHive = RegistryHive.LocalMachine;
        Assert.Equal(InnoInstallationStatus.InspectionFailed, fixture.Detect().Status);
        Assert.Single(fixture.Logger.Errors);
    }

    [Fact]
    public void ProgrammingErrors_AreNotSwallowed()
    {
        using var fixture = new Fixture();
        fixture.Source.RegistryError = new InvalidOperationException("bug");
        Assert.Throws<InvalidOperationException>(() => fixture.Detect());
    }

    [Fact]
    public void InvalidPePayload_IsUnsupported()
    {
        using var fixture = new Fixture();
        fixture.Source.InspectRealBinary = true;
        fixture.AssertUnsupported();
    }

    [Fact]
    public void LockedIdentityFile_FailsInspectionRatherThanReportingMissing()
    {
        using var fixture = new Fixture();
        using var locked = new FileStream(fixture.Payload("app-identity.txt"),
            FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(InnoInstallationStatus.InspectionFailed, fixture.Detect().Status);
        Assert.IsAssignableFrom<IOException>(Assert.Single(fixture.Logger.Errors));
    }

    [Theory]
    [InlineData(Machine.Amd64)]
    [InlineData(Machine.Arm64)]
    public void NativeAdapter_ReadsActualPeMachineAndFileVersion(Machine machine)
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("native.exe");
        var bytes = File.ReadAllBytes(Environment.ProcessPath!);
        var peOffset = BitConverter.ToInt32(bytes, 0x3c);
        BitConverter.GetBytes((ushort)machine).CopyTo(bytes, peOffset + 4);
        File.WriteAllBytes(path, bytes);

        var evidence = new InnoInstallationReadSource().ReadExecutable(path);
        Assert.Equal(machine, evidence.Machine);
        Assert.Equal(FileVersionInfo.GetVersionInfo(path).FileVersion, evidence.Version);
    }

    [Fact]
    public void ManagedAssembly_IsNotNativeApplicationEvidence()
    {
        var source = new InnoInstallationReadSource();
        Assert.Throws<InvalidDataException>(() => source.ReadExecutable(typeof(InnoInstallationDetector).Assembly.Location));
    }

    [Fact]
    public void ReparsePointAncestor_IsUnsupported()
    {
        using var fixture = new Fixture();
        var target = fixture.Temp.Combine("payload-target");
        Directory.Move(fixture.InstallDirectory, target);
        // A junction needs no developer-mode or symbolic-link privilege.
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"/d /c mklink /J \"{fixture.InstallDirectory}\" \"{target}\""
        };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var result = fixture.Detect();

            Assert.Equal(InnoInstallationStatus.Unsupported, result.Status);
            Assert.Null(result.Installation);
            // The probe and the payload loop each reject the junction, so the reason is logged
            // alongside the probe's own warning rather than on its own.
            Assert.Contains("Migration paths must not contain reparse points.", fixture.Logger.Warnings);
            // A path the migration refuses to traverse yields no usable evidence, so it cannot
            // assert the source is installed.
            Assert.False(result.SourcePayloadPresent);
        }
        finally { Directory.Delete(fixture.InstallDirectory); }
    }

    [Fact]
    public void DetectionDoesNotModifyInstallationOrReadUserState()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Payload("settings.json"), "must not be read or modified");
        var before = Snapshot(fixture.InstallDirectory);

        Assert.Equal(InnoInstallationStatus.Detected, fixture.Detect().Status);

        Assert.Equal(before, Snapshot(fixture.InstallDirectory));
        Assert.DoesNotContain(fixture.Source.FileReads, path => path.EndsWith("settings.json"));
        Assert.All(fixture.Source.FileReads, path => Assert.Equal(fixture.InstallDirectory, Path.GetDirectoryName(path)));
    }

    [Fact]
    public void NativeRegistryAdapter_IgnoresDevIdAndDeduplicatesReadOnlyProductionAlias()
    {
        using var fixture = new Fixture();
        using var registry = new RegistryFixture();
        using (var dev = registry.Root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{M0LTB0T-TRAY-4PP1-DEV}_is1"))
            WriteRegistration(dev, fixture.Registration);
        var detector = new InnoInstallationDetector(fixture.Temp.Path, NullLogger.Instance,
            registry.Source(fixture.Source));
        Assert.Equal(InnoInstallationStatus.NotInstalled, detector.Detect().Status);
        using (var production = registry.Root.CreateSubKey(InnoInstallationDetector.UninstallKey))
            WriteRegistration(production, fixture.Registration);

        Assert.Equal(InnoInstallationStatus.Detected, detector.Detect().Status);
        using var after = registry.Root.OpenSubKey(InnoInstallationDetector.UninstallKey)!;
        Assert.Equal(5, after.ValueCount);
        Assert.Equal(fixture.Registration.DisplayVersion, after.GetValue("DisplayVersion"));
        Assert.Equal(fixture.Registration.UninstallString, after.GetValue("UninstallString"));
    }

    [Theory]
    [InlineData(RegistryValueKind.ExpandString)]
    [InlineData(RegistryValueKind.DWord)]
    [InlineData(RegistryValueKind.MultiString)]
    [InlineData(RegistryValueKind.Binary)]
    public void NativeRegistryAdapter_RejectsNonStringKindsWithoutExpansion(RegistryValueKind kind)
    {
        using var fixture = new Fixture();
        using var registry = new RegistryFixture();
        foreach (var name in new[] { "InstallLocation", "DisplayVersion", "Publisher", "DisplayName", "UninstallString" })
        {
            using (var production = registry.Root.CreateSubKey(InnoInstallationDetector.UninstallKey))
            {
                WriteRegistration(production, fixture.Registration);
                object value = kind switch
                {
                    RegistryValueKind.ExpandString => "%LOCALAPPDATA%\\OpenClawTray",
                    RegistryValueKind.DWord => 1,
                    RegistryValueKind.MultiString => new[] { fixture.InstallDirectory },
                    _ => new byte[] { 1, 2 }
                };
                production.SetValue(name, value, kind);
            }
            var detector = new InnoInstallationDetector(fixture.Temp.Path, fixture.Logger, registry.Source(fixture.Source));
            Assert.Equal(InnoInstallationStatus.Unsupported, detector.Detect().Status);
            using var after = registry.Root.OpenSubKey(InnoInstallationDetector.UninstallKey)!;
            Assert.Equal(kind, after.GetValueKind(name));
        }
        Assert.Equal(5, fixture.Logger.Warnings.Count);
    }

    private static string[] Snapshot(string directory) => Directory.GetFiles(directory)
        .Order(StringComparer.Ordinal).Select(path =>
            $"{Path.GetFileName(path)}:{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
        .ToArray();

    private static void WriteRegistration(RegistryKey key, InnoInstallationRegistration registration)
    {
        key.SetValue("InstallLocation", registration.InstallLocation!);
        key.SetValue("DisplayVersion", registration.DisplayVersion!);
        key.SetValue("DisplayName", registration.DisplayName!);
        key.SetValue("Publisher", registration.Publisher!);
        key.SetValue("UninstallString", registration.UninstallString!);
    }

    private sealed class RegistryFixture : IDisposable
    {
        private readonly string _path = @"Software\OpenClawDetectorTests\" + Guid.NewGuid().ToString("N");
        public RegistryKey Root { get; }

        public RegistryFixture() => Root = Registry.CurrentUser.CreateSubKey(_path);

        public FakeSource Source(FakeSource source)
        {
            source.RegistrySource = new InnoInstallationReadSource((hive, _) =>
                hive == RegistryHive.CurrentUser ? Root.OpenSubKey(InnoInstallationDetector.UninstallKey, false) : null);
            return source;
        }

        public void Dispose()
        {
            Root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_path);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public TempDirectory Temp { get; } = new();
        public FakeSource Source { get; } = new();
        public RecordingLogger Logger { get; } = new();
        public string InstallDirectory => Temp.Combine("OpenClawTray");
        public string Payload(string name) => Path.Combine(InstallDirectory, name);
        public InnoInstallationRegistration Registration
        {
            get => Source.Registrations[(RegistryHive.CurrentUser, RegistryView.Registry64)];
            set => Source.Registrations[(RegistryHive.CurrentUser, RegistryView.Registry64)] = value;
        }

        public Fixture()
        {
            Directory.CreateDirectory(InstallDirectory);
            foreach (var name in new[] { "OpenClaw.Tray.WinUI.exe", "unins000.exe", "Test-InnoMigration.ps1",
                "MigrationRecordCodec.cs", "Uninstall-LocalGateway.ps1" })
                File.WriteAllText(Payload(name), "fixture only, never execute");
            File.WriteAllText(Payload("app-identity.txt"), "release\r\n");
            Registration = new(InstallDirectory + "\\", "2026.9.17", "OpenClaw Foundation",
                "OpenClaw Companion version 2026.9.17", $"\"{Payload("unins000.exe")}\"");
        }

        public InnoInstallationDetection Detect() => new InnoInstallationDetector(Temp.Path, Logger, Source).Detect();
        public void AssertUnsupported()
        {
            var result = Detect();
            Assert.Equal(InnoInstallationStatus.Unsupported, result.Status);
            Assert.Null(result.Installation);
            Assert.False(string.IsNullOrEmpty(result.Reason));
            Assert.Equal(result.Reason, Assert.Single(Logger.Warnings));
        }
        public void Dispose() => Temp.Dispose();
    }

    private sealed class FakeSource : IInnoInstallationReadSource
    {
        private readonly InnoInstallationReadSource _files = new();
        public Dictionary<(RegistryHive, RegistryView), InnoInstallationRegistration> Registrations { get; } = new();
        public List<(RegistryHive, RegistryView)> RegistryReads { get; } = [];
        public List<string> FileReads { get; } = [];
        public InnoInstallationExecutable Binary { get; set; } = new(Machine.Amd64, "2026.9.17.0");
        public Exception? RegistryError { get; set; }
        public RegistryHive? ErrorHive { get; set; }
        public Exception? FileError { get; set; }
        public string? FileErrorPath { get; set; }
        public bool InspectRealBinary { get; set; }
        public InnoInstallationReadSource? RegistrySource { get; set; }

        public InnoInstallationRegistration? ReadRegistration(RegistryHive hive, RegistryView view)
        {
            RegistryReads.Add((hive, view));
            if (RegistryError is not null && (ErrorHive is null || ErrorHive == hive))
                throw RegistryError;
            return RegistrySource is not null ? RegistrySource.ReadRegistration(hive, view) :
                Registrations.GetValueOrDefault((hive, view));
        }

        public bool IsOrdinaryFile(string path)
        {
            FileReads.Add(path);
            if (FileError is not null &&
                (FileErrorPath is null || path.StartsWith(FileErrorPath, StringComparison.OrdinalIgnoreCase)))
                throw FileError;
            return _files.IsOrdinaryFile(path);
        }

        public string ReadIdentity(string path)
        {
            FileReads.Add(path);
            return _files.ReadIdentity(path);
        }

        public InnoInstallationExecutable ReadExecutable(string path)
        {
            FileReads.Add(path);
            return InspectRealBinary ? _files.ReadExecutable(path) : Binary;
        }
    }

    private sealed class RecordingLogger : IOpenClawLogger
    {
        public List<string> Warnings { get; } = [];
        public List<Exception?> Errors { get; } = [];
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) => Errors.Add(ex);
    }
}
