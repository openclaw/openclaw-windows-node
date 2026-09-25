using System.Runtime.Versioning;
using System.Security.Principal;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class StoreMigrationFinalizationCoordinatorTests
{
    [Fact]
    public void SameNameProcessAtCanonicalSourcePath_BlocksRemoval()
    {
        using var fixture = new Fixture();

        var result = VerifyProcesses(fixture, new InnoSourceProcessCandidate(
            InnoSourceProcessState.ImageResolved,
            Path.Combine(fixture.Binding.InstallDirectory, "OpenClaw.Tray.WinUI.exe")));

        Assert.Equal(InnoSourceRemovalStatus.SourcePresent, result);
    }

    [Fact]
    public void SameNameProcessAtUnrelatedPath_DoesNotBlockRemoval()
    {
        using var fixture = new Fixture();

        var result = VerifyProcesses(fixture, new InnoSourceProcessCandidate(
            InnoSourceProcessState.ImageResolved,
            @"C:\OtherUser\OpenClaw.Tray.WinUI.exe"));

        Assert.Equal(InnoSourceRemovalStatus.Removed, result);
    }

    [Fact]
    public void InaccessibleSameNameProcessOwnedByOtherUser_DoesNotBlockRemoval()
    {
        using var fixture = new Fixture();

        var result = VerifyProcesses(fixture, new InnoSourceProcessCandidate(InnoSourceProcessState.OwnedByOtherUser));

        Assert.Equal(InnoSourceRemovalStatus.Removed, result);
    }

    [Fact]
    public void UnresolvedSameNameProcess_FailsClosed()
    {
        using var fixture = new Fixture();

        var result = VerifyProcesses(fixture, new InnoSourceProcessCandidate(InnoSourceProcessState.Unresolved));

        Assert.Equal(InnoSourceRemovalStatus.InspectionFailed, result);
    }

    [Fact]
    public void ExitedSameNameProcess_DoesNotBlockRemoval()
    {
        using var fixture = new Fixture();

        var result = VerifyProcesses(fixture, new InnoSourceProcessCandidate(InnoSourceProcessState.Exited));

        Assert.Equal(InnoSourceRemovalStatus.Removed, result);
    }

    [Fact]
    public async Task RegistryGoneButSourcePayloadRemains_WaitsWithoutMutation()
    {
        using var fixture = new Fixture();
        fixture.WriteRecords();
        File.WriteAllText(Path.Combine(fixture.Binding.InstallDirectory, "OpenClaw.Tray.WinUI.exe"), "source");
        var autoStart = new AutoStart();

        var result = await fixture.FinalizeAsync(
            new(InnoInstallationStatus.NotInstalled),
            new InnoSourceRemovalVerifier(fixture.Binding, $"OpenClawMigrationTest-{Guid.NewGuid():N}"),
            autoStart);

        Assert.Equal(StoreMigrationFinalizationState.AwaitingInnoRemoval, result.State);
        Assert.Equal(0, autoStart.Calls);
        Assert.True(File.Exists(fixture.IntentPath));
        Assert.True(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task RegistryGoneButSourceRuntimeEvidenceRemains_WaitsWithoutMutation()
    {
        using var fixture = new Fixture();
        fixture.WriteRecords();
        var autoStart = new AutoStart();

        var result = await fixture.FinalizeAsync(
            new(InnoInstallationStatus.NotInstalled),
            new SourceRemoval(InnoSourceRemovalStatus.SourcePresent),
            autoStart);

        Assert.Equal(StoreMigrationFinalizationState.AwaitingInnoRemoval, result.State);
        Assert.Equal(0, autoStart.Calls);
        Assert.True(File.Exists(fixture.CompletionPath));
    }

    [Theory]
    [InlineData(InnoInstallationStatus.Unsupported)]
    [InlineData(InnoInstallationStatus.InspectionFailed)]
    public async Task UnverifiableRegistrySource_FailsClosedAndKeepsReceipt(InnoInstallationStatus status)
    {
        using var fixture = new Fixture();
        fixture.WriteRecords();

        var result = await fixture.FinalizeAsync(new(status));

        Assert.Equal(StoreMigrationFinalizationState.InspectionFailed, result.State);
        // Removal is unproven, so the previous app may still be usable and must stay the one the
        // user runs.
        Assert.False(result.SourceRemoved);
        Assert.True(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task UncertainSourceRemoval_FailsClosedAndKeepsReceipt()
    {
        using var fixture = new Fixture();
        fixture.WriteRecords();

        var result = await fixture.FinalizeAsync(
            new(InnoInstallationStatus.NotInstalled),
            new SourceRemoval(InnoSourceRemovalStatus.InspectionFailed));

        Assert.Equal(StoreMigrationFinalizationState.InspectionFailed, result.State);
        Assert.False(result.SourceRemoved);
        Assert.True(File.Exists(fixture.IntentPath));
        Assert.True(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task StaleCompletionReceiptWithChangedInventory_BlocksBeforeStartupPreferenceOrCleanup()
    {
        using var fixture = new Fixture();
        fixture.WriteRecords();
        var autoStart = new AutoStart();
        var cleaner = new Cleaner();

        var result = await fixture.FinalizeAsync(
            new(InnoInstallationStatus.NotInstalled),
            new SourceRemoval(InnoSourceRemovalStatus.Removed),
            autoStart,
            cleaner,
            new Inventory(new string('b', 64)));

        Assert.Equal(StoreMigrationFinalizationState.InspectionFailed, result.State);
        // The previous app is already gone, so blocking here would leave no usable app at all.
        Assert.True(result.SourceRemoved);
        Assert.Equal(0, autoStart.Calls);
        Assert.Equal(0, cleaner.Calls);
        Assert.True(File.Exists(fixture.IntentPath));
        Assert.True(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task StartupPreferenceFailure_KeepsReceiptAndDoesNotCleanRecords()
    {
        using var fixture = new Fixture();
        fixture.WriteRecords();
        var cleaner = new Cleaner();

        var result = await fixture.FinalizeAsync(
            new(InnoInstallationStatus.NotInstalled),
            new SourceRemoval(InnoSourceRemovalStatus.Removed),
            new AutoStart(new InvalidOperationException("Startup task unavailable")),
            cleaner);

        Assert.Equal(StoreMigrationFinalizationState.StartupPreferenceFailed, result.State);
        // The receipt is kept so the preference retries next launch, but a failed
        // cosmetic preference must never block the app: Inno removal is already proven.
        Assert.True(result.AllowsNormalStartup);
        Assert.True(result.SourceRemoved);
        Assert.Equal(0, cleaner.Calls);
        Assert.True(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task StartupPreferenceRefusal_CleansReceiptAndAllowsStartup()
    {
        using var fixture = new Fixture();
        fixture.WriteRecords(autoStart: true);
        var cleaner = new Cleaner();

        var result = await fixture.FinalizeAsync(
            new(InnoInstallationStatus.NotInstalled),
            new SourceRemoval(InnoSourceRemovalStatus.Removed),
            new AutoStart(new StoreMigrationAutoStartRefusedException(
                "Windows startup is disabled by the user.")),
            cleaner);

        Assert.Equal(StoreMigrationFinalizationState.StartupPreferenceRefused, result.State);
        Assert.True(result.AllowsNormalStartup);
        Assert.True(result.SourceRemoved);
        Assert.Equal(1, cleaner.Calls);
    }

    [Fact]
    public async Task StartupPreferenceRefusal_RemovesReceiptSoLaunchIsNotBlockedAgain()
    {
        using var fixture = new Fixture();
        fixture.WriteRecords(autoStart: true);

        var result = await fixture.FinalizeAsync(
            new(InnoInstallationStatus.NotInstalled),
            new SourceRemoval(InnoSourceRemovalStatus.Removed),
            new AutoStart(new StoreMigrationAutoStartRefusedException(
                "Windows startup is disabled by policy.")));

        Assert.Equal(StoreMigrationFinalizationState.StartupPreferenceRefused, result.State);
        Assert.True(result.AllowsNormalStartup);
        Assert.False(File.Exists(fixture.IntentPath));
        Assert.False(File.Exists(fixture.CompletionPath));
        Assert.Equal(MigrationStartupRecordStatus.None, fixture.ReadRecords().Status);
    }

    [Fact]
    public async Task ProvenRemoval_AppliesSavedPreferenceOnceCleansSameReceiptAndAllowsStartup()
    {
        using var fixture = new Fixture();
        fixture.WriteRecords(autoStart: true);
        var consentLockPath = Path.Combine(fixture.Directory, InnoMigrationConsentStore.WriterLockFileName);
        File.WriteAllBytes(consentLockPath, []);
        var autoStart = new AutoStart();

        var result = await fixture.FinalizeAsync(
            new(InnoInstallationStatus.NotInstalled),
            new SourceRemoval(InnoSourceRemovalStatus.Removed),
            autoStart);

        Assert.Equal(StoreMigrationFinalizationState.Finalized, result.State);
        Assert.True(result.AllowsNormalStartup);
        Assert.Equal(1, autoStart.Calls);
        Assert.True(autoStart.LastEnabled);
        Assert.False(File.Exists(fixture.IntentPath));
        Assert.False(File.Exists(fixture.ConsentPath));
        Assert.False(File.Exists(consentLockPath));
        Assert.False(File.Exists(fixture.CompletionPath));
        Assert.Equal(MigrationStartupRecordStatus.None, fixture.ReadRecords().Status);
    }

    [Fact]
    public void Cleaner_RejectsChangedReceiptBeforeDeletingAnything()
    {
        using var fixture = new Fixture();
        fixture.WriteRecords();
        var changed = fixture.Receipt;
        changed.Fingerprint = new string('b', 64);

        Assert.Throws<InvalidDataException>(() =>
            new MigrationFinalizationRecordCleaner(fixture.Binding).ClearCompleted(changed));

        Assert.True(File.Exists(fixture.IntentPath));
        Assert.True(File.Exists(fixture.ConsentPath));
        Assert.True(File.Exists(fixture.CompletionPath));
    }

    [Theory]
    [InlineData("consent-lock")]
    [InlineData("consent")]
    [InlineData("intent")]
    [InlineData("completed")]
    public async Task CleanupFailure_PreservesCompletionAndDeletesOnlyEarlierRecords(string lockedKind)
    {
        using var fixture = new Fixture();
        fixture.WriteRecords();
        var consent = File.ReadAllBytes(fixture.ConsentPath);
        var completion = File.ReadAllBytes(fixture.CompletionPath);
        var consentLockPath = Path.Combine(fixture.Directory, InnoMigrationConsentStore.WriterLockFileName);
        File.WriteAllBytes(consentLockPath, []);
        var lockedPath = lockedKind switch
        {
            "consent-lock" => consentLockPath,
            "consent" => fixture.ConsentPath,
            "intent" => fixture.IntentPath,
            _ => fixture.CompletionPath
        };
        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await fixture.FinalizeAsync(new(InnoInstallationStatus.NotInstalled));

            Assert.Equal(StoreMigrationFinalizationState.RecordCleanupFailed, result.State);
            // Cleanup runs only after removal is proven. The receipt survives so the retry below
            // still happens, but it must not also withhold the only app the user has left.
            Assert.True(result.SourceRemoved);
            Assert.False(result.AllowsNormalStartup);
            Assert.Equal(lockedKind is "consent-lock" or "consent", File.Exists(fixture.ConsentPath));
            Assert.Equal(lockedKind != "completed", File.Exists(fixture.IntentPath));
            Assert.Equal(completion, File.ReadAllBytes(fixture.CompletionPath));
            if (lockedKind is "consent-lock" or "consent")
                Assert.Equal(consent, File.ReadAllBytes(fixture.ConsentPath));
        }

        var retry = await fixture.FinalizeAsync(new(InnoInstallationStatus.NotInstalled));
        Assert.Equal(StoreMigrationFinalizationState.Finalized, retry.State);
        Assert.False(File.Exists(fixture.ConsentPath));
        Assert.False(File.Exists(fixture.IntentPath));
        Assert.False(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task MissingConsent_DoesNotPreventLegacyReceiptFinalization()
    {
        using var fixture = new Fixture();
        fixture.WriteRecords();
        File.Delete(fixture.ConsentPath);

        var result = await fixture.FinalizeAsync(new(InnoInstallationStatus.NotInstalled));

        Assert.Equal(StoreMigrationFinalizationState.Finalized, result.State);
        Assert.False(File.Exists(fixture.IntentPath));
        Assert.False(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task CleanupInterruptionAfterIntentDeletion_RetriesFromReceipt()
    {
        using var fixture = new Fixture();
        fixture.WriteRecords();
        var interrupted = new IntentDeletingCleaner(fixture.IntentPath);

        var first = await fixture.FinalizeAsync(
            new(InnoInstallationStatus.NotInstalled),
            new SourceRemoval(InnoSourceRemovalStatus.Removed),
            new AutoStart(),
            interrupted);
        Assert.Equal(StoreMigrationFinalizationState.RecordCleanupFailed, first.State);
        Assert.True(File.Exists(fixture.CompletionPath));

        var second = await fixture.FinalizeAsync(
            new(InnoInstallationStatus.NotInstalled),
            new SourceRemoval(InnoSourceRemovalStatus.Removed),
            new AutoStart());

        Assert.Equal(StoreMigrationFinalizationState.Finalized, second.State);
        Assert.False(File.Exists(fixture.IntentPath));
        Assert.False(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task ConcurrentFinalization_OnlyLockOwnerAppliesStartupPreference()
    {
        using var fixture = new Fixture();
        fixture.WriteRecords();
        var firstAutoStart = new GateAutoStart();
        var first = fixture.FinalizeAsync(
            new(InnoInstallationStatus.NotInstalled),
            new SourceRemoval(InnoSourceRemovalStatus.Removed),
            firstAutoStart);
        await firstAutoStart.Started.Task;

        var secondAutoStart = new AutoStart();
        var second = await fixture.FinalizeAsync(
            new(InnoInstallationStatus.NotInstalled),
            new SourceRemoval(InnoSourceRemovalStatus.Removed),
            secondAutoStart);
        firstAutoStart.Continue.TrySetResult();
        var firstResult = await first;

        Assert.Equal(StoreMigrationFinalizationState.RecordCleanupFailed, second.State);
        Assert.Equal(0, secondAutoStart.Calls);
        Assert.Equal(StoreMigrationFinalizationState.Finalized, firstResult.State);
        Assert.Equal(1, firstAutoStart.Calls);
    }

    private static InnoSourceRemovalStatus VerifyProcesses(
        Fixture fixture,
        params InnoSourceProcessCandidate[] candidates) =>
        new InnoSourceRemovalVerifier(
            fixture.Binding,
            $"OpenClawMigrationTest-{Guid.NewGuid():N}",
            new ProcessInspector(candidates)).VerifyRemoved();

    private sealed class Fixture : IDisposable
    {
        private static readonly DateTime Now = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);
        private readonly TempDirectory _temp = new();

        public Fixture()
        {
            Binding = new MigrationBinding
            {
                InstallDirectory = _temp.Combine("install"),
                RoamingDirectory = _temp.Combine("roaming"),
                LocalDirectory = _temp.Combine("local"),
                Architecture = "x64",
                UserSid = WindowsIdentity.GetCurrent().User!.Value
            };
            System.IO.Directory.CreateDirectory(Binding.InstallDirectory);
        }

        public MigrationBinding Binding { get; }
        public MigrationRecord Receipt { get; private set; } = null!;
        public string Directory => Path.Combine(Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        public string IntentPath => Path.Combine(Directory, MigrationRecordCodec.IntentFileName);
        public string ConsentPath => Path.Combine(Directory, MigrationRecordCodec.ConsentFileName);
        public string CompletionPath => Path.Combine(Directory, MigrationRecordCodec.CompletionFileName);

        public void WriteRecords(bool autoStart = false)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(Path.Combine(Directory, "prepare.lock"), []);
            var consent = Record("consent", autoStart: false);
            consent.Fingerprint = MigrationRecordCodec.ConsentFingerprint;
            File.WriteAllBytes(ConsentPath, MigrationRecordCodec.Encode(consent, Now));
            File.WriteAllBytes(IntentPath, MigrationRecordCodec.Encode(Record("intent", autoStart), Now));
            Receipt = Record("completed", autoStart);
            File.WriteAllBytes(CompletionPath, MigrationRecordCodec.Encode(Receipt, Now));
        }

        public MigrationStartupRecord ReadRecords() =>
            new MigrationStartupRecordReader(Binding, NullLogger.Instance, new Clock()).Read();

        public Task<StoreMigrationFinalizationDecision> FinalizeAsync(
            InnoInstallationDetection detection,
            IInnoSourceRemovalVerifier? sourceRemoval = null,
            IStoreMigrationAutoStartApplier? autoStart = null,
            IStoreMigrationRecordCleaner? cleaner = null,
            IMigrationInventoryCapture? inventory = null) =>
            new StoreMigrationFinalizationCoordinator(
                Binding,
                new Detector(detection),
                sourceRemoval ?? new SourceRemoval(InnoSourceRemovalStatus.Removed),
                new MigrationStartupRecordReader(Binding, NullLogger.Instance, new Clock()),
                inventory ?? new Inventory(Receipt.Fingerprint),
                autoStart ?? new AutoStart(),
                cleaner ?? new MigrationFinalizationRecordCleaner(Binding),
                NullLogger.Instance).FinalizeAsync();

        private MigrationRecord Record(string kind, bool autoStart) => new()
        {
            Kind = kind,
            MigrationId = "f1d81447-a535-4f63-85e8-6d2cd7cae61f",
            SourceVersion = "2026.9.1",
            TargetVersion = kind == "completed" ? "2026.9.2" : "",
            Fingerprint = new string('a', 64),
            InventoryJson = kind == "intent" ? "{}" : "",
            AutoStart = autoStart,
            CreatedUtc = Now,
            ExpiresUtc = kind == "completed" ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) : Now.AddDays(30),
            Binding = Binding
        };

        public void Dispose() => _temp.Dispose();
    }

    private sealed class Detector(InnoInstallationDetection detection) : IInnoInstallationDetector
    {
        public InnoInstallationDetection Detect() => detection;
    }

    private sealed class SourceRemoval(InnoSourceRemovalStatus status) : IInnoSourceRemovalVerifier
    {
        public InnoSourceRemovalStatus VerifyRemoved() => status;
    }

    private sealed class ProcessInspector(
        IEnumerable<InnoSourceProcessCandidate> candidates) : IInnoSourceProcessInspector
    {
        public IEnumerable<InnoSourceProcessCandidate> FindSameNameProcesses() => candidates;
    }

    private sealed class Inventory(string fingerprint) : IMigrationInventoryCapture
    {
        public MigrationInventory Capture() => new(fingerprint, false, "", "", []);
    }

    private class AutoStart(Exception? failure = null) : IStoreMigrationAutoStartApplier
    {
        public int Calls { get; private set; }
        public bool LastEnabled { get; private set; }

        public virtual Task ApplyAsync(bool enabled)
        {
            Calls++;
            LastEnabled = enabled;
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }

    private sealed class GateAutoStart : AutoStart
    {
        public TaskCompletionSource Started { get; } = new();
        public TaskCompletionSource Continue { get; } = new();

        public override async Task ApplyAsync(bool enabled)
        {
            await base.ApplyAsync(enabled);
            Started.TrySetResult();
            await Continue.Task.ConfigureAwait(false);
        }
    }

    private sealed class Cleaner(Exception? failure = null) : IStoreMigrationRecordCleaner
    {
        public int Calls { get; private set; }

        public void ClearCompleted(MigrationRecord receipt)
        {
            Calls++;
            if (failure is not null)
                throw failure;
        }
    }

    private sealed class IntentDeletingCleaner(string intentPath) : IStoreMigrationRecordCleaner
    {
        public void ClearCompleted(MigrationRecord receipt)
        {
            File.Delete(intentPath);
            throw new IOException("Simulated interruption after intent deletion.");
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
    }
}
