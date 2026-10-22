using System.Runtime.Versioning;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
[Collection("Migration preparation")]
public sealed class StoreMigrationCompletionCoordinatorTests
{
    [Fact]
    public void NoActiveGateway_CompletesBecauseNothingNeedsPreserving()
    {
        using var fixture = new Fixture();
        fixture.Prepare();

        var result = fixture.Complete();

        Assert.Equal(StoreMigrationCompletionState.Completed, result.State);
    }

    [Fact]
    public void UnpairedActiveGateway_CompletesWithoutACredential()
    {
        using var fixture = new Fixture();
        fixture.AddActiveGateway();
        fixture.Prepare();

        var result = fixture.Complete();

        Assert.Equal(StoreMigrationCompletionState.Completed, result.State);
    }

    [Fact]
    public void DamagedDeviceIdentity_FailsClosedDuringPreparation()
    {
        using var fixture = new Fixture();
        fixture.AddActiveGateway();
        fixture.DamageActiveGatewayIdentity();

        // Inventory capture rejects damaged identities before completion is ever
        // reachable, so completion never has to re-judge readable credential state.
        var exception = Assert.Throws<InvalidDataException>(() => fixture.Prepare());

        Assert.Equal("Device identity is malformed or unreadable.", exception.Message);
        fixture.AssertNoReceipt();
    }

    [Fact]
    public void DeviceIdentityDamagedAfterPreparation_FailsClosedDuringCompletion()
    {
        using var fixture = new Fixture();
        fixture.AddActiveGateway();
        fixture.Prepare();
        fixture.DamageActiveGatewayIdentity();

        var result = fixture.Complete();

        Assert.Equal(StoreMigrationCompletionState.ValidationFailed, result.State);
        fixture.AssertNoReceipt();
    }

    [Fact]
    public void ChangedPreparedInventory_CannotComplete()
    {
        using var fixture = new Fixture();
        fixture.AddActiveGateway(sharedToken: "shared");
        fixture.Prepare();
        File.WriteAllText(Path.Combine(fixture.Binding.RoamingDirectory, "settings.json"), """{"AutoStart":true}""");

        var result = fixture.Complete();

        Assert.Equal(StoreMigrationCompletionState.SourceChanged, result.State);
        fixture.AssertNoReceipt();
    }

    [Fact]
    public void ResolvedDeviceCredential_WritesAtomicCompletionWithoutDeletingIntent()
    {
        using var fixture = new Fixture();
        fixture.AddActiveGateway(sharedToken: "shared", deviceToken: "paired-device");
        var intent = fixture.Prepare();

        var result = fixture.Complete();

        var receipt = Assert.IsType<MigrationRecord>(result.Receipt);
        Assert.Equal(StoreMigrationCompletionState.Completed, result.State);
        Assert.Equal(intent.MigrationId, receipt.MigrationId);
        Assert.Equal(intent.SourceVersion, receipt.SourceVersion);
        Assert.Equal("2026.9.18.0", receipt.TargetVersion);
        Assert.Equal(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), receipt.ExpiresUtc);
        var completionPath = Path.Combine(fixture.Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName,
            MigrationRecordCodec.CompletionFileName);
        var completed = MigrationRecordCodec.ReadCompletion(completionPath, fixture.Binding, DateTime.UtcNow);
        Assert.Equal(intent.MigrationId, completed.MigrationId);
        Assert.True(File.Exists(Path.Combine(fixture.Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName,
            MigrationRecordCodec.IntentFileName)));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName),
            "*.tmp"));
    }

    [Fact]
    public void ExistingReceipt_IsNeverReplaced()
    {
        using var fixture = new Fixture();
        fixture.AddActiveGateway(sharedToken: "shared");
        fixture.Prepare();
        var completed = Assert.IsType<MigrationRecord>(fixture.Complete().Receipt);
        var completionPath = Path.Combine(fixture.Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName,
            MigrationRecordCodec.CompletionFileName);
        var original = File.ReadAllBytes(completionPath);

        Assert.Throws<InvalidOperationException>(() =>
            new MigrationCompletionReceiptWriter(fixture.Binding).Write(completed));

        Assert.Equal(original, File.ReadAllBytes(completionPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(completionPath)!, "*.tmp"));
    }

    [Fact]
    public void UninstallReadHandles_BlockCompletionUntilSourceRemoval()
    {
        using var fixture = new Fixture();
        fixture.AddActiveGateway(sharedToken: "shared");
        fixture.Prepare();
        var detector = new CallbackDetector(() => new(InnoInstallationStatus.NotInstalled));

        using (fixture.OpenUninstallLock())
        {
        // The Inno parent and cleanup child can coexist, but Store cannot
        // publish completion after the uninstaller's preservation check.
        using (fixture.OpenUninstallLock())
        {
            Assert.Equal(StoreMigrationCompletionState.InnoRunning, fixture.Complete(detector).State);
            Assert.Equal(0, detector.Calls);
            fixture.AssertNoReceipt();
        }
        Assert.Equal(StoreMigrationCompletionState.InnoRunning, fixture.Complete(detector).State);
        }

        Assert.Equal(StoreMigrationCompletionState.SourceChanged, fixture.Complete(detector).State);
        Assert.Equal(1, detector.Calls);
        fixture.AssertNoReceipt();
    }

    [Fact]
    public void Completion_OwnsExclusiveLockBeforeRecheckingSource()
    {
        using var fixture = new Fixture();
        fixture.AddActiveGateway(sharedToken: "shared");
        fixture.Prepare();
        var detector = new CallbackDetector(() =>
        {
        Assert.Throws<IOException>(() => fixture.OpenUninstallLock());
        return new(InnoInstallationStatus.Detected, fixture.Installation);
        });

        Assert.Equal(StoreMigrationCompletionState.Completed, fixture.Complete(detector).State);
        using var uninstall = fixture.OpenUninstallLock();
        Assert.True(File.Exists(Path.Combine(fixture.Binding.RoamingDirectory,
        MigrationRecordCodec.DirectoryName, MigrationRecordCodec.CompletionFileName)));
    }

    [Fact]
    public void FailedCompletion_ReleasesLockForUninstall()
    {
        using var fixture = new Fixture();
        fixture.AddActiveGateway();
        fixture.Prepare();

        var result = fixture.Complete(activity: new Activity(() => InnoSourceActivityStatus.InspectionFailed));

        Assert.Equal(StoreMigrationCompletionState.ValidationFailed, result.State);
        using var uninstall = fixture.OpenUninstallLock();
        fixture.AssertNoReceipt();
    }

    [Theory]
    [InlineData(InnoSourceActivityStatus.Running, StoreMigrationCompletionState.InnoRunning)]
    [InlineData(InnoSourceActivityStatus.InspectionFailed, StoreMigrationCompletionState.ValidationFailed)]
    [InlineData(InnoSourceActivityStatus.Stopped, StoreMigrationCompletionState.Completed)]
    public void CrossSessionInspection_RunsUnderExclusiveLockBeforeCompletion(
        InnoSourceActivityStatus activity, StoreMigrationCompletionState expectedState)
    {
        using var fixture = new Fixture();
        fixture.AddActiveGateway(sharedToken: "shared");
        fixture.Prepare();
        var result = fixture.Complete(activity: new Activity(() =>
        {
            Assert.Throws<IOException>(() => MigrationOperationLock.AcquireRuntime(fixture.Binding));
            return activity;
        }));

        Assert.Equal(expectedState, result.State);
        if (activity != InnoSourceActivityStatus.Stopped)
            fixture.AssertNoReceipt();
        using var released = MigrationOperationLock.AcquireRuntime(fixture.Binding);
    }

    [Fact]
    public void SourceRestartBetweenPreparationAndCompletion_BlocksUntilExit()
    {
        using var fixture = new Fixture();
        fixture.AddActiveGateway(sharedToken: "shared");
        fixture.Prepare();
        using (MigrationOperationLock.AcquireRuntime(fixture.Binding))
        {
            Assert.Equal(StoreMigrationCompletionState.InnoRunning, fixture.Complete().State);
            fixture.AssertNoReceipt();
        }

        Assert.Equal(StoreMigrationCompletionState.Completed, fixture.Complete().State);
        using var runtime = MigrationOperationLock.AcquireRuntime(fixture.Binding);
        var receipt = MigrationRecordCodec.ReadCompletion(
            Path.Combine(fixture.Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName,
                MigrationRecordCodec.CompletionFileName), fixture.Binding, DateTime.UtcNow);
        Assert.Equal("completed", receipt.Kind);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly EnvironmentScope _environment;
        private readonly InnoInstallation _installation = new(
            @"C:\fixture\OpenClawTray", @"C:\fixture\OpenClawTray\OpenClaw.Tray.WinUI.exe",
            @"C:\fixture\OpenClawTray\unins000.exe", "x64", new Version(2026, 9, 17, 0));

        public Fixture()
        {
            _environment = new EnvironmentScope().Set("OPENCLAW_STATE_DIR", null).Set("OPENCLAW_HOME", null);
            Binding = MigrationRecordTests.CreateRecord(_temp, "intent").Binding;
            Directory.CreateDirectory(Binding.RoamingDirectory);
            Directory.CreateDirectory(Binding.LocalDirectory);
            File.WriteAllText(Path.Combine(Binding.RoamingDirectory, "settings.json"), """{"AutoStart":false}""");
        }

        public MigrationBinding Binding { get; }
        public InnoInstallation Installation => _installation;

        public FileStream OpenUninstallLock() => new(
            Path.Combine(Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName, "prepare.lock"),
            FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);

        public void AddActiveGateway(string? sharedToken = null, string? deviceToken = null)
        {
            const string gatewayId = "12345678-1234-1234-1234-123456789abc";
            var registry = new GatewayRegistry(Binding.RoamingDirectory);
            registry.AddOrUpdate(new GatewayRecord
            {
                Id = gatewayId,
                Url = "wss://gateway.example.test",
                SharedGatewayToken = sharedToken,
            });
            registry.SetActive(gatewayId);
            registry.Save();
            if (deviceToken is not null)
            {
                var identity = new DeviceIdentity(registry.GetIdentityDirectory(gatewayId));
                identity.Initialize();
                identity.StoreDeviceToken(deviceToken);
            }
        }

        /// <summary>
        /// Writes well-formed identity JSON whose key material cannot be decoded, so the
        /// file survives JSON capture but reads as damaged rather than absent.
        /// </summary>
        public void DamageActiveGatewayIdentity()
        {
            const string gatewayId = "12345678-1234-1234-1234-123456789abc";
            var registry = new GatewayRegistry(Binding.RoamingDirectory);
            registry.Load();
            var directory = registry.GetIdentityDirectory(gatewayId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "device-key-ed25519.json"),
                """{"PrivateKeyBase64":"not-base64!!","PublicKeyBase64":"not-base64!!","DeviceId":"d","DeviceToken":"t","Algorithm":"Ed25519"}""");
        }

        public MigrationRecord Prepare() =>
            new MigrationPreparation(Binding).Prepare(_installation.Version.ToString());

        public StoreMigrationCompletionDecision Complete(IInnoInstallationDetector? detector = null,
            IInnoSourceActivityVerifier? activity = null) =>
            new StoreMigrationCompletionCoordinator(
                new LeaseProvider(),
                detector ?? new Detector(_installation),
                Binding,
                NullLogger.Instance,
                sourceActivity: activity ?? new Activity(() => InnoSourceActivityStatus.Stopped))
            .Complete(_installation, "2026.9.18.0");

        public void AssertNoReceipt() =>
            Assert.False(File.Exists(Path.Combine(Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName,
                MigrationRecordCodec.CompletionFileName)));

        public void Dispose()
        {
            _environment.Dispose();
            _temp.Dispose();
        }
    }

    private sealed class LeaseProvider : IMigrationSourceLeaseProvider
    {
        public IMigrationSourceLease TryAcquire() => new Lease();
    }

    private sealed class Activity(Func<InnoSourceActivityStatus> verify) : IInnoSourceActivityVerifier
    {
        public InnoSourceActivityStatus VerifyStopped() => verify();
    }

    private sealed class Lease : IMigrationSourceLease
    {
        public void Dispose() { }
    }

    private sealed class Detector(InnoInstallation installation) : IInnoInstallationDetector
    {
        public InnoInstallationDetection Detect() => new(InnoInstallationStatus.Detected, installation);
    }

    private sealed class CallbackDetector(Func<InnoInstallationDetection> detect) : IInnoInstallationDetector
    {
        public int Calls { get; private set; }

        public InnoInstallationDetection Detect()
        {
            Calls++;
            return detect();
        }
    }
}
