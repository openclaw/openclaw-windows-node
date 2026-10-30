using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Connection.Tests;

public class ManagedLocalGatewayPortProvenanceServiceTests
{
    private static GatewayRecord ManagedRecord() => new()
    {
        Id = "gw-local",
        Url = "ws://localhost:18789",
        IsLocal = true,
        SetupManagedDistroName = "OpenClawGateway",
    };

    [Fact]
    public async Task NativeRecord_NeverUsesWslProvenanceOrCachedCredentialExemption()
    {
        var platform = new FakePlatform();
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);
        var record = ManagedRecord() with
        {
            NativePackageFamilyName = "OpenClaw.Gateway_test",
            IsLocal = false,
            SetupManagedDistroName = null,
            Url = "wss://example.test",
        };

        var result = await service.InspectAsync(record);

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
        Assert.False(service.IsStrongCredentialAllowed(record,
            new GatewayCredential("shared", false, CredentialResolver.SourceSharedGatewayToken)));
        Assert.Equal(ManagedLocalPortConflictRepairOutcome.BlockedUnknownOwner,
            (await service.RepairConflictAsync(record, CancellationToken.None)).Outcome);
        Assert.Empty(platform.Actions);
        Assert.Equal(0, platform.ExpectedDistroChecks);
    }

    [Fact]
    public void EvaluateWslRelayBinary_NonCanonicalPathSkipsSignatureVerification()
    {
        var signatureChecked = false;

        var result = WindowsManagedLocalGatewayPortPlatform.EvaluateWslRelayBinary(
            @"C:\Temp\WSL\wslrelay.exe",
            _ =>
            {
                signatureChecked = true;
                return AuthenticodeTrustResult.Trusted();
            });

        Assert.False(result.IsTrusted);
        Assert.Contains("path is not canonical", result.Detail);
        Assert.False(signatureChecked);
    }

    [Fact]
    public void VerifyMicrosoftSignedFile_AcceptsWindowsWslBinary()
    {
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var wslPath = Path.Combine(windowsDir, "System32", "wsl.exe");

        var result = WindowsAuthenticodeVerifier.VerifyMicrosoftSignedFile(wslPath);

        Assert.True(result.IsTrusted, result.Detail);
    }

    [Fact]
    public void VerifyMicrosoftSignedFile_RejectsUnsignedAssembly()
    {
        var result = WindowsAuthenticodeVerifier.VerifyMicrosoftSignedFile(
            typeof(ManagedLocalGatewayPortProvenanceServiceTests).Assembly.Location);

        Assert.False(result.IsTrusted);
        Assert.Contains("Authenticode verification failed", result.Detail);
    }

    private const string WslPackageFamilyName =
        "MicrosoftCorporationII.WindowsSubsystemForLinux_8wekyb3d8bbwe";
    private const string WslPackageInstallLocation =
        @"C:\Program Files\WindowsApps\MicrosoftCorporationII.WindowsSubsystemForLinux_2.9.4.0_x64__8wekyb3d8bbwe";
    private const string ExternalWslRelayPath = @"C:\Program Files\WSL\wslrelay.exe";
    private const string PackageVersion = "2.9.4.0";

    [Fact]
    public void VerifyMicrosoftSignedFile_AuthenticodeFailsButProtectedExternalRelayMatchesPackage_IsTrusted()
    {
        var result = VerifyPackageFallback(ExternalWslRelayPath, ValidWslPackage());

        Assert.True(result.IsTrusted, result.Detail);
        Assert.False(result.IsWarning);
    }

    [Fact]
    public void VerifyMicrosoftSignedFile_AuthenticodeFailsButPackageOwnsWindowsAppsRelay_IsTrusted()
    {
        var packageRelayPath = Path.Combine(WslPackageInstallLocation, "wslrelay.exe");

        var result = VerifyPackageFallback(
            packageRelayPath,
            ValidWslPackage(),
            WslRelayPathInspection.Protected());

        Assert.True(result.IsTrusted, result.Detail);
        Assert.False(result.IsWarning);
    }

    [Fact]
    public void VerifyMicrosoftSignedFile_WindowsAppsRelayOutsideWslPackageFamily_IsRejected()
    {
        var siblingRelayPath = Path.Combine(
            @"C:\Program Files\WindowsApps\SomeOther.Package_1.0.0.0_x64__deadbeefcafe",
            "wslrelay.exe");

        var result = VerifyPackageFallback(siblingRelayPath, ValidWslPackage());

        Assert.False(result.IsTrusted);
        Assert.Contains("not owned by the WSL package family", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerifyMicrosoftSignedFile_WslNamedDirectoryNestedInsideAnotherWindowsAppsPackage_IsRejected(
        bool pathProtected)
    {
        var windowsAppsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        var nestedRelayPath = Path.Combine(
            windowsAppsRoot,
            "Other.App_1.0.0.0_x64__deadbeefcafe",
            "MicrosoftCorporationII.WindowsSubsystemForLinux_9__8wekyb3d8bbwe",
            "wslrelay.exe");

        var result = VerifyPackageFallback(
            nestedRelayPath,
            ValidWslPackage(),
            pathProtected
                ? WslRelayPathInspection.Protected()
                : WslRelayPathInspection.Unverifiable("Injected unverifiable path."));

        Assert.False(result.IsTrusted);
        Assert.False(result.IsWarning);
        Assert.Contains("not owned by the WSL package family", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyMicrosoftSignedFile_PackageIdentityMismatches_IsWarnedNotRejected()
    {
        var package = ValidWslPackage() with
        {
            PackageFamilyName = "SomeImpostor.WindowsSubsystemForLinux_deadbeefcafe",
        };

        var result = VerifyPackageFallback(ExternalWslRelayPath, package);

        Assert.True(result.IsTrusted, result.Detail);
        Assert.True(result.IsWarning);
        Assert.Contains("package identity could not be corroborated", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyMicrosoftSignedFile_UnsignedPackage_IsWarnedNotRejected()
    {
        var package = ValidWslPackage() with { SignatureKind = "None" };

        var result = VerifyPackageFallback(ExternalWslRelayPath, package);

        Assert.True(result.IsTrusted, result.Detail);
        Assert.True(result.IsWarning);
        Assert.Contains("unsigned signature kind", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyMicrosoftSignedFile_NonMicrosoftPackagePublisher_IsWarnedNotRejected()
    {
        var package = ValidWslPackage() with { Publisher = "CN=Evil Corp, O=Evil Corp, C=US" };

        var result = VerifyPackageFallback(ExternalWslRelayPath, package);

        Assert.True(result.IsTrusted, result.Detail);
        Assert.True(result.IsWarning);
    }

    [Fact]
    public void VerifyMicrosoftSignedFile_NoWslPackageFound_IsWarnedNotRejected()
    {
        var result = VerifyPackageFallback(ExternalWslRelayPath, null);

        Assert.True(result.IsTrusted, result.Detail);
        Assert.True(result.IsWarning);
        Assert.Contains("no installed WSL package could corroborate", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyMicrosoftSignedFile_UnprotectedRelayPath_IsRejected()
    {
        var result = VerifyPackageFallback(
            ExternalWslRelayPath,
            ValidWslPackage(),
            WslRelayPathInspection.Unprotected("Injected unprotected path."));

        Assert.False(result.IsTrusted);
        Assert.Contains("unprotected", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyMicrosoftSignedFile_UnverifiableRelayPath_IsWarned()
    {
        var result = VerifyPackageFallback(
            ExternalWslRelayPath,
            ValidWslPackage(),
            WslRelayPathInspection.Unverifiable("Injected unverifiable path."));

        Assert.True(result.IsTrusted, result.Detail);
        Assert.True(result.IsWarning);
        Assert.Contains("Injected unverifiable path", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyMicrosoftSignedFile_AuthenticodeSucceeds_NeverConsultsWslPackageFallback()
    {
        var fallbackInvoked = false;

        var result = WindowsAuthenticodeVerifier.VerifyMicrosoftSignedFile(
            ExternalWslRelayPath,
            _ => AuthenticodeTrustResult.Trusted(),
            () =>
            {
                fallbackInvoked = true;
                throw new InvalidOperationException("Fallback should not be consulted.");
            },
            (_, _) => throw new InvalidOperationException("Path should not be inspected."));

        Assert.True(result.IsTrusted, result.Detail);
        Assert.False(fallbackInvoked);
    }

    [Fact]
    public void VerifyMicrosoftSignedFile_NonRelayFailure_NeverConsultsWslPackageFallback()
    {
        var fallbackInvoked = false;

        var result = WindowsAuthenticodeVerifier.VerifyMicrosoftSignedFile(
            @"C:\Program Files\WSL\wsl.exe",
            _ => AuthenticodeTrustResult.Rejected("Injected primary failure."),
            () =>
            {
                fallbackInvoked = true;
                return ValidWslPackage();
            },
            (_, _) => throw new InvalidOperationException("Path should not be inspected."));

        Assert.False(result.IsTrusted);
        Assert.Contains("Injected primary failure", result.Detail, StringComparison.Ordinal);
        Assert.False(fallbackInvoked);
    }

    private static AppxPackageInfo ValidWslPackage() => new(
        WslPackageFamilyName,
        "CN=Microsoft Windows, O=Microsoft Corporation, C=US",
        "Developer");

    private static AuthenticodeTrustResult VerifyPackageFallback(
        string path,
        AppxPackageInfo? package,
        WslRelayPathInspection? pathInspection = null) =>
        WindowsAuthenticodeVerifier.VerifyMicrosoftSignedFile(
            path,
            _ => AuthenticodeTrustResult.Rejected("Injected primary failure."),
            () => package,
            (_, _) => pathInspection ?? WslRelayPathInspection.Protected());

    [Fact]
    [SupportedOSPlatform("windows")]
    public void HasProtectedOwnershipAndWriteAcl_AllowsReadOnlyUsersButRejectsUserWrite()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        FileSystemAccessRule[] protectedRules =
        [
            new(system, FileSystemRights.FullControl, AccessControlType.Allow),
            new(administrators, FileSystemRights.Modify, AccessControlType.Allow),
            new(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow),
            new(
                new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.InheritOnly,
                AccessControlType.Allow),
        ];

        Assert.True(
            WindowsAuthenticodeVerifier.HasProtectedOwnershipAndWriteAcl(
                system,
                protectedRules,
                hasDiscretionaryAcl: true));

        var writableByUsers = protectedRules.Append(
            new FileSystemAccessRule(
                users,
                FileSystemRights.WriteData,
                AccessControlType.Allow));
        Assert.False(
            WindowsAuthenticodeVerifier.HasProtectedOwnershipAndWriteAcl(
                system,
                writableByUsers,
                hasDiscretionaryAcl: true));
        Assert.False(
            WindowsAuthenticodeVerifier.HasProtectedOwnershipAndWriteAcl(
                users,
                protectedRules,
                hasDiscretionaryAcl: true));
        Assert.False(
            WindowsAuthenticodeVerifier.HasProtectedOwnershipAndWriteAcl(
                system,
                [],
                hasDiscretionaryAcl: false));
        Assert.True(
            WindowsAuthenticodeVerifier.HasProtectedOwnershipAndWriteAcl(
                system,
                [],
                hasDiscretionaryAcl: true));
    }

    [Theory]
    [InlineData("CN=Microsoft Windows, O=Microsoft Corporation, C=US", true)]
    [InlineData("CN=Microsoft Corporation Test Certificate, O=Example Corp, C=US", false)]
    [InlineData("CN=Other Publisher, O=Microsoft Corporation Services, C=US", false)]
    public void HasMicrosoftPublisherIdentity_RequiresExactOrganization(
        string subject,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsAuthenticodeVerifier.HasMicrosoftPublisherIdentity(subject));
    }

    [Fact]
    public void CreateExpectedWslGatewayProbe_UsesStdinAndDoesNotAssumeRelayFamilyParity()
    {
        var probe = WindowsManagedLocalGatewayPortPlatform.CreateExpectedWslGatewayProbe(
            "OpenClawE2E-test",
            18789);

        Assert.True(probe.StartInfo.RedirectStandardInput);
        Assert.Equal(
            ["-d", "OpenClawE2E-test", "--", "bash", "-s"],
            probe.StartInfo.ArgumentList);
        Assert.Contains("pid=$(systemctl", probe.StandardInput, StringComparison.Ordinal);
        Assert.Contains("ss -ltnp", probe.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain("ss -4", probe.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain("ss -6", probe.StandardInput, StringComparison.Ordinal);
        Assert.Contains("pid=$pid,", probe.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain("$pid", string.Join(" ", probe.StartInfo.ArgumentList), StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_WslRelayOnTargetAddress_IsExpectedManagedGateway()
    {
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            18789,
            100,
            "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe"));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, result.Kind);
    }

    [Fact]
    public void Inspect_DualStackWslRelay_VerifiesExternalProofsOnce()
    {
        var platform = new FakePlatform();
        var start = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc);
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            18789,
            100,
            "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe",
            start));
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.IPv6Loopback,
            18789,
            100,
            "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe",
            start));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, result.Kind);
        Assert.Equal(1, platform.TrustedWslRelayChecks);
        Assert.Equal(1, platform.ExpectedDistroChecks);
    }

    [Fact]
    public void Inspect_CompleteEmptyWindowsSnapshotWithGuestOwner_IsExpectedWithoutWindowsIdentity()
    {
        var platform = new FakePlatform();
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, result.Kind);
        Assert.Null(result.ProcessId);
        Assert.Null(result.ProcessName);
        Assert.Null(result.ProcessStartTimeUtc);
        Assert.Null(result.ProcessPath);
        Assert.Null(result.ScheduledTaskName);
        Assert.Equal(1, platform.ExpectedDistroChecks);
    }

    [Fact]
    public void Inspect_CompleteEmptyWindowsSnapshotWithoutGuestOwner_IsNoListener()
    {
        var platform = new FakePlatform { ExpectedDistroListening = false };
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.NoListener, result.Kind);
        Assert.Equal(1, platform.ExpectedDistroChecks);
    }

    [Fact]
    public void Inspect_WindowsOwnerAppearsDuringRelaylessGuestProof_IsUnknown()
    {
        var platform = new FakePlatform
        {
            IntroduceUnknownListenerDuringGuestProof = true,
        };
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
        Assert.Equal(
            GatewayEndpointProvenanceFailureReason.ListenerSnapshotChanged,
            result.FailureReason);
        Assert.Null(result.ProcessId);
        Assert.Contains("changed during relayless provenance verification", result.Detail);
    }

    [Fact]
    public void Inspect_SpoofedWslRelayPath_IsUnknown()
    {
        var platform = new FakePlatform { TrustedWslRelay = false };
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            18789,
            101,
            "wslrelay",
            @"C:\Temp\WSL\wslrelay.exe"));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
        Assert.Contains("Authenticode verification failed", result.Detail);
    }

    [Fact]
    public void Inspect_UnprovenButCanonicalRelayWithDistroOwner_IsExpectedWithWarningDetail()
    {
        var platform = new FakePlatform
        {
            TrustedWslRelay = true,
            WarnedWslRelay = true,
        };
        var start = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc);
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            18789,
            100,
            "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe",
            start));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, result.Kind);
        Assert.Contains("binary trust is unproven", result.Detail, StringComparison.Ordinal);
        Assert.Contains("no installed WSL package could corroborate", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_UnprovenButCanonicalRelayWithoutDistroOwner_IsUnknown()
    {
        var platform = new FakePlatform
        {
            TrustedWslRelay = true,
            WarnedWslRelay = true,
            ExpectedDistroListening = false,
        };
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            18789,
            100,
            "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe"));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
        Assert.Contains("Binary trust is unproven for the relay", result.Detail, StringComparison.Ordinal);
        Assert.Contains("does not report its systemd gateway MainPID owning port", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_DualStackUntrustedRelay_DeduplicatesFailureDetail()
    {
        var platform = new FakePlatform { TrustedWslRelay = false };
        var start = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc);
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            18789,
            101,
            "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe",
            start));
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.IPv6Loopback,
            18789,
            101,
            "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe",
            start));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
        Assert.Equal(
            result.Detail!.IndexOf("Authenticode verification failed", StringComparison.Ordinal),
            result.Detail.LastIndexOf("Authenticode verification failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_RelayForDifferentOrInactiveDistro_IsUnknown()
    {
        var platform = new FakePlatform { ExpectedDistroListening = false };
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            18789,
            102,
            "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe"));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
        Assert.Contains("does not report its systemd gateway MainPID owning port", result.Detail);
    }

    [Fact]
    public void Inspect_LocalhostWithIncompleteIpv6Capture_FailsClosed()
    {
        var platform = new FakePlatform { Ipv6Complete = false };
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            18789,
            102,
            "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe",
            new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc)));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
    }

    [Fact]
    public void Inspect_OwnerChangesDuringSlowVerification_FailsClosed()
    {
        var platform = new FakePlatform { ReplaceOwnerOnSecondCapture = true };
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            18789,
            102,
            "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe",
            new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc)));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
        Assert.Equal(
            GatewayEndpointProvenanceFailureReason.ListenerSnapshotChanged,
            result.FailureReason);
    }

    [Fact]
    public void InteractiveCredentialGate_ExpectedCacheThenOwnerChanges_FailsClosed()
    {
        var platform = new FakePlatform();
        var start = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc);
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            18789,
            102,
            "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe",
            start));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);
        var record = ManagedRecord();
        var credential = new GatewayCredential(
            "shared-token",
            IsBootstrapToken: false,
            CredentialResolver.SourceSharedGatewayToken);
        Assert.Equal(
            GatewayEndpointProvenanceKind.ExpectedManagedGateway,
            service.Inspect(record).Kind);
        Assert.True(service.IsStrongCredentialAllowed(record, credential));

        platform.Listeners[0] = new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            18789,
            999,
            "unknown",
            @"C:\unknown.exe",
            start.AddSeconds(1));

        Assert.False(service.IsStrongCredentialAllowed(record, credential));
    }

    [Fact]
    public void InteractiveCredentialGate_BrowserControlInspectDoesNotEvictGatewayProof()
    {
        var platform = new FakePlatform();
        var start = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc);
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback, 18789, 102, "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe", start));
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback, 18791, 102, "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe", start));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);
        var gateway = ManagedRecord();
        var control = gateway with { Url = "ws://localhost:18791" };
        var credential = new GatewayCredential(
            "shared-token", false, CredentialResolver.SourceSharedGatewayToken);

        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, service.Inspect(gateway).Kind);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, service.Inspect(control).Kind);
        Assert.True(service.IsStrongCredentialAllowed(gateway, credential));
    }

    [Fact]
    public void InteractiveCredentialGate_GuestSocketOwnershipChanges_FailsClosed()
    {
        var platform = new FakePlatform();
        var start = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc);
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback, 18789, 102, "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe", start));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);
        var gateway = ManagedRecord();
        var credential = new GatewayCredential(
            "shared-token", false, CredentialResolver.SourceSharedGatewayToken);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, service.Inspect(gateway).Kind);

        platform.ExpectedDistroListening = false;

        Assert.False(service.IsStrongCredentialAllowed(gateway, credential));
    }

    [Fact]
    public void InteractiveCredentialGate_CachedWslRelayReplacedDuringGuestProbe_FailsClosed()
    {
        var platform = new FakePlatform();
        var start = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc);
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback, 18789, 102, "wslrelay",
            @"C:\Program Files\WSL\wslrelay.exe", start));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);
        var gateway = ManagedRecord();
        var credential = new GatewayCredential(
            "shared-token", false, CredentialResolver.SourceSharedGatewayToken);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, service.Inspect(gateway).Kind);

        Assert.True(service.IsStrongCredentialAllowed(gateway, credential));

        platform.ReplaceListenerDuringGuestProof = true;

        Assert.False(service.IsStrongCredentialAllowed(gateway, credential));
    }

    [Fact]
    public void InteractiveCredentialGate_RelaylessProofRevalidatesEmptySnapshotAndGuestOwner()
    {
        var platform = new FakePlatform();
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);
        var gateway = ManagedRecord();
        var credential = new GatewayCredential(
            "shared-token", false, CredentialResolver.SourceSharedGatewayToken);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, service.Inspect(gateway).Kind);

        Assert.True(service.IsStrongCredentialAllowed(gateway, credential));

        platform.ExpectedDistroListening = false;

        Assert.False(service.IsStrongCredentialAllowed(gateway, credential));
    }

    [Fact]
    public void InteractiveCredentialGate_RelaylessProofWithAppearingWindowsListener_FailsClosed()
    {
        var platform = new FakePlatform();
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);
        var gateway = ManagedRecord();
        var credential = new GatewayCredential(
            "shared-token", false, CredentialResolver.SourceSharedGatewayToken);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, service.Inspect(gateway).Kind);
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback, 18789, 999, "unknown", @"C:\unknown.exe"));

        Assert.False(service.IsStrongCredentialAllowed(gateway, credential));
    }

    [Fact]
    public void InteractiveCredentialGate_RelaylessProofWithIncompleteCapture_FailsClosed()
    {
        var platform = new FakePlatform();
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);
        var gateway = ManagedRecord();
        var credential = new GatewayCredential(
            "shared-token", false, CredentialResolver.SourceSharedGatewayToken);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, service.Inspect(gateway).Kind);
        platform.Ipv6Complete = false;

        Assert.False(service.IsStrongCredentialAllowed(gateway, credential));
    }

    [Fact]
    public void InteractiveCredentialGate_WindowsOwnerAppearsDuringRelaylessGuestReproof_FailsClosed()
    {
        var platform = new FakePlatform();
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);
        var gateway = ManagedRecord();
        var credential = new GatewayCredential(
            "shared-token", false, CredentialResolver.SourceSharedGatewayToken);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, service.Inspect(gateway).Kind);
        platform.IntroduceUnknownListenerDuringGuestProof = true;

        Assert.False(service.IsStrongCredentialAllowed(gateway, credential));
    }

    [Fact]
    public void Inspect_MixedRelevantOwners_IsUnknown()
    {
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback, 18789, 100, "wslrelay", @"C:\Program Files\WSL\wslrelay.exe"));
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.IPv6Loopback, 18789, 200, "other", @"C:\other.exe"));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = service.Inspect(ManagedRecord());

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
    }

    [Fact]
    public async Task RepairConflict_ProvenNativeOpenClaw_DisablesTaskThenStopsExactPid()
    {
        var platform = FakePlatform.WithProvenNativeGateway();
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var proof = service.Inspect(ManagedRecord());
        var result = await service.RepairConflictAsync(ManagedRecord(), CancellationToken.None);

        Assert.Equal(GatewayEndpointProvenanceKind.ConflictingOpenClawGateway, proof.Kind);
        Assert.Equal(ManagedLocalPortConflictRepairOutcome.Repaired, result.Outcome);
        Assert.Equal(
            ["disable:OpenClaw Gateway (OpenClawGateway)",
             "end:OpenClaw Gateway (OpenClawGateway)",
             "stop:2144"],
            platform.Actions);
    }

    [Fact]
    public async Task RepairConflict_TaskEndAlreadyReleasedPort_IsSuccessWithoutPidKill()
    {
        var platform = FakePlatform.WithProvenNativeGateway();
        platform.EndRemovesListener = true;
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = await service.RepairConflictAsync(ManagedRecord(), CancellationToken.None);

        Assert.Equal(ManagedLocalPortConflictRepairOutcome.Repaired, result.Outcome);
        Assert.Equal(
            ["disable:OpenClaw Gateway (OpenClawGateway)",
             "end:OpenClaw Gateway (OpenClawGateway)"],
            platform.Actions);
    }

    [Fact]
    public async Task RepairConflict_ReusedPidAfterTaskEnd_IsNeverKilled()
    {
        var platform = FakePlatform.WithProvenNativeGateway();
        platform.ReplaceProcessIdentityOnEnd = true;
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = await service.RepairConflictAsync(ManagedRecord(), CancellationToken.None);

        Assert.Equal(ManagedLocalPortConflictRepairOutcome.BlockedUnknownOwner, result.Outcome);
        Assert.DoesNotContain("stop:2144", platform.Actions);
    }

    [Fact]
    public async Task RepairConflict_UserIntentChangesBeforeDisable_NoDestructiveActionRuns()
    {
        var platform = FakePlatform.WithProvenNativeGateway();
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);
        var checks = 0;

        var result = await service.RepairConflictAsync(
            ManagedRecord(),
            CancellationToken.None,
            canContinue: () => ++checks < 2);

        Assert.Equal(ManagedLocalPortConflictRepairOutcome.BlockedUnknownOwner, result.Outcome);
        Assert.Empty(platform.Actions);
    }

    [Fact]
    public async Task RepairConflict_UnknownOwner_NeverDisablesOrStops()
    {
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback, 18789, 300, "unknown", @"C:\unknown.exe"));
        var service = new ManagedLocalGatewayPortProvenanceService(platform, NullLogger.Instance);

        var result = await service.RepairConflictAsync(ManagedRecord(), CancellationToken.None);

        Assert.Equal(ManagedLocalPortConflictRepairOutcome.BlockedUnknownOwner, result.Outcome);
        Assert.Empty(platform.Actions);
    }

    [Fact]
    public void Inspect_TaskXmlEscapedProfilePath_IsStillProven()
    {
        const string profileRoot = @"C:\Users\A & B";
        var platform = FakePlatform.WithProvenNativeGateway(profileRoot);
        var service = new ManagedLocalGatewayPortProvenanceService(
            platform,
            NullLogger.Instance,
            () => profileRoot);

        Assert.Equal(
            GatewayEndpointProvenanceKind.ConflictingOpenClawGateway,
            service.Inspect(ManagedRecord()).Kind);
    }

    private sealed class FakePlatform : IManagedLocalGatewayPortPlatform
    {
        public List<WindowsTcpListenerInfo> Listeners { get; } = [];
        public Dictionary<int, string> CommandLines { get; } = [];
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? TaskXml { get; set; }
        public List<string> Actions { get; } = [];
        public bool TrustedWslRelay { get; set; } = true;
        public bool WarnedWslRelay { get; set; }
        public bool ExpectedDistroListening { get; set; } = true;
        public bool EndRemovesListener { get; set; }
        public bool ReplaceProcessIdentityOnEnd { get; set; }
        public bool ReplaceOwnerOnSecondCapture { get; set; }
        public bool IntroduceUnknownListenerDuringGuestProof { get; set; }
        public bool ReplaceListenerDuringGuestProof { get; set; }
        public bool Ipv4Complete { get; set; } = true;
        public bool Ipv6Complete { get; set; } = true;
        public int TrustedWslRelayChecks { get; private set; }
        public int ExpectedDistroChecks { get; private set; }
        private int _captureCount;

        public static FakePlatform WithProvenNativeGateway(string? userProfilePath = null)
        {
            var platform = new FakePlatform();
            var profileDir = Path.Combine(
                userProfilePath ??
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".openclaw-OpenClawGateway");
            var vbsPath = Path.Combine(profileDir, "gateway.vbs");
            var cmdPath = Path.Combine(profileDir, "gateway.cmd");
            platform.Listeners.Add(new WindowsTcpListenerInfo(
                IPAddress.Loopback,
                18789,
                2144,
                "node",
                @"C:\Program Files\nodejs\node.exe",
                new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc)));
            platform.CommandLines[2144] =
                @"""C:\Program Files\nodejs\node.exe"" C:\Users\test\AppData\Local\OpenClawTray\native-cli\node_modules\openclaw\dist\index.js gateway --port 18789";
            platform.TaskXml = new System.Xml.Linq.XDocument(
                new System.Xml.Linq.XElement(
                    "Task",
                    new System.Xml.Linq.XElement(
                        "Actions",
                        new System.Xml.Linq.XElement(
                            "Exec",
                            new System.Xml.Linq.XElement("Command", vbsPath)))))
                .ToString();
            platform.Files[vbsPath] = $"Run \"{cmdPath}\"";
            platform.Files[cmdPath] = $"""
                set "OPENCLAW_STATE_DIR={profileDir}"
                set "OPENCLAW_WINDOWS_TASK_NAME=OpenClaw Gateway (OpenClawGateway)"
                set "OPENCLAW_GATEWAY_PORT=18789"
                C:\Users\test\AppData\Local\OpenClawTray\native-cli\node_modules\openclaw\dist\index.js gateway --port 18789
                """;
            return platform;
        }

        public WindowsTcpListenerSnapshotResult CaptureListeners()
        {
            _captureCount++;
            if (ReplaceOwnerOnSecondCapture && _captureCount == 2 && Listeners.Count > 0)
            {
                return new(
                [
                    Listeners[0] with
                    {
                        ProcessId = 999,
                        ProcessName = "unknown",
                        ProcessPath = @"C:\unknown.exe",
                        ProcessStartTimeUtc = Listeners[0].ProcessStartTimeUtc?.AddSeconds(1)
                    }
                ],
                Ipv4Complete,
                Ipv6Complete);
            }
            return new(Listeners.ToArray(), Ipv4Complete, Ipv6Complete);
        }
        public string? GetProcessCommandLine(int processId) =>
            CommandLines.GetValueOrDefault(processId);
        public WslRelayTrustResult InspectWslRelayBinary(string processPath)
        {
            TrustedWslRelayChecks++;
            if (!TrustedWslRelay)
                return WslRelayTrustResult.Rejected(
                    "WSL relay Authenticode verification failed.");
            return WarnedWslRelay
                ? WslRelayTrustResult.Warned(
                    "no installed WSL package could corroborate the relay at 'C:\\Program Files\\WSL\\wslrelay.exe'")
                : WslRelayTrustResult.Trusted();
        }

        public bool IsExpectedWslGatewayListening(string distroName, int port)
        {
            ExpectedDistroChecks++;
            if (IntroduceUnknownListenerDuringGuestProof)
            {
                IntroduceUnknownListenerDuringGuestProof = false;
                Listeners.Add(new WindowsTcpListenerInfo(
                    IPAddress.Loopback,
                    port,
                    999,
                    "unknown",
                    @"C:\unknown.exe"));
            }

            if (ReplaceListenerDuringGuestProof && Listeners.Count > 0)
            {
                ReplaceListenerDuringGuestProof = false;
                Listeners[0] = new WindowsTcpListenerInfo(
                    Listeners[0].Address,
                    port,
                    999,
                    "unknown",
                    @"C:\unknown.exe",
                    Listeners[0].ProcessStartTimeUtc?.AddSeconds(1));
            }

            return ExpectedDistroListening;
        }
        public string? ReadScheduledTaskXml(string taskName) => TaskXml;
        public string? ReadFile(string path) => Files.GetValueOrDefault(path);

        public Task<bool> DisableScheduledTaskAsync(string taskName, CancellationToken cancellationToken)
        {
            Actions.Add($"disable:{taskName}");
            return Task.FromResult(true);
        }

        public Task EndScheduledTaskAsync(string taskName, CancellationToken cancellationToken)
        {
            Actions.Add($"end:{taskName}");
            if (EndRemovesListener)
                Listeners.Clear();
            else if (ReplaceProcessIdentityOnEnd && Listeners.Count > 0)
                Listeners[0] = Listeners[0] with
                {
                    ProcessStartTimeUtc = Listeners[0].ProcessStartTimeUtc?.AddMinutes(1)
                };
            return Task.CompletedTask;
        }

        public Task<bool> StopProcessAsync(
            int processId,
            DateTime? expectedStartTimeUtc,
            CancellationToken cancellationToken)
        {
            Actions.Add($"stop:{processId}");
            Listeners.RemoveAll(listener => listener.ProcessId == processId);
            return Task.FromResult(true);
        }
    }
}
