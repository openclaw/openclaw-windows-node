using System.Net;
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
        public bool ExpectedDistroListening { get; set; } = true;
        public bool EndRemovesListener { get; set; }
        public bool ReplaceProcessIdentityOnEnd { get; set; }
        public bool ReplaceOwnerOnSecondCapture { get; set; }
        public bool IntroduceUnknownListenerDuringGuestProof { get; set; }
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
            return TrustedWslRelay
                ? WslRelayTrustResult.Trusted()
                : WslRelayTrustResult.Rejected(
                    "WSL relay Authenticode verification failed.");
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
