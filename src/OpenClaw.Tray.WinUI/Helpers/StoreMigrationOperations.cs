using System.Diagnostics;
using OpenClaw.Connection;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Helpers;

internal sealed class StoreMigrationOperations(string pipeName) : IStoreMigrationOperations
{
    private readonly AppLogger _logger = new();
    private MigrationBinding? _binding;
    private IInnoInstallationDetector? _detector;

    public StoreMigrationStartupDecision Inspect()
    {
        var identity = global::Windows.ApplicationModel.Package.Current.Id;
        if (identity.Name != MigrationRecordCodec.PackageName ||
            identity.Publisher != MigrationRecordCodec.PackagePublisher || MigrationEnvironment.HasPathOverride)
        {
            Logger.Error("Store migration requires the production package identity and default data paths.");
            return new(StoreMigrationStartupState.UnsupportedInstallation);
        }
        _binding ??= MigrationEnvironment.CreateBinding();
        _detector ??= MigrationEnvironment.CreateDetector();
        return new StoreMigrationStartupCoordinator(_detector, new MigrationStartupRecordReader(_binding, _logger), _logger)
            .Evaluate(true, MigrationEnvironment.MinimumSourceVersion, _binding.Architecture);
    }

    /// <summary>
    /// Deliberately independent of <see cref="Inspect"/>: it is called precisely when inspection
    /// threw, so it must not depend on detection, policy, or package identity.
    /// </summary>
    public bool HoldsCompletionReceipt()
    {
        try
        {
            _binding ??= MigrationEnvironment.CreateBinding();
            return new MigrationStartupRecordReader(_binding, _logger).Read().CompletionPresent;
        }
        catch (Exception exception)
        {
            Logger.Error($"Could not confirm the migration receipt: {exception}");
            return true;
        }
    }

    public bool HasConsent(InnoInstallation installation) =>
        new InnoMigrationConsentStore(_binding!, _logger).HasValidConsent(installation.Version.ToString());

    public void GrantConsent(InnoInstallation installation) =>
        new InnoMigrationConsentStore(_binding!, _logger).Grant(installation.Version.ToString());

    public async Task<bool> CloseSourceAsync(StoreMigrationStartupDecision admission, CancellationToken cancellationToken)
    {
        var coordinator = new StoreMigrationConsentCoordinator(new InnoMutexProbe());
        if (coordinator.Begin(admission, true).State == StoreMigrationConsentState.ReadyForAdoption)
            return true;

        await using var router = new ActivationRouter(AppIdentity.ProtocolScheme, pipeName);
        if (!await router.RequestMigrationShutdownAsync(cancellationToken))
            return false;

        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(200, cancellationToken);
            if (coordinator.Retry().State == StoreMigrationConsentState.ReadyForAdoption)
                return true;
        }
        Logger.Warn("Migration shutdown did not release the Inno instance. Manual close and Retry are required.");
        return false;
    }

    public Task<StoreMigrationPreparationState> PrepareAsync(InnoInstallation installation) => Task.Run(() =>
        new StoreMigrationAdoptionPreparationCoordinator(
            new InnoMutexLeaseProvider(), _detector!, new MigrationPreparation(_binding!), _logger)
            .Prepare(installation).State);

    public Task<StoreMigrationCompletionState> CompleteAsync(InnoInstallation installation)
    {
        var version = global::Windows.ApplicationModel.Package.Current.Id.Version;
        var targetVersion = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        return Task.Run(() => new StoreMigrationCompletionCoordinator(
            new InnoMutexLeaseProvider(), _detector!, _binding!, _logger)
            .Complete(installation, targetVersion).State);
    }

    public Task<StoreMigrationFinalizationDecision> FinalizeAsync()
    {
        var records = new MigrationStartupRecordReader(_binding!, _logger);
        return new StoreMigrationFinalizationCoordinator(
            _binding!, _detector!, new InnoSourceRemovalVerifier(_binding!, AppIdentity.MutexBaseName),
            records, new MigrationInventoryCapture(_binding!), new StoreMigrationAutoStartApplier(),
            new MigrationFinalizationRecordCleaner(_binding!), _logger).FinalizeAsync();
    }

    /// <summary>
    /// Like <see cref="HoldsCompletionReceipt"/>, independent of policy and package identity:
    /// recovery may be showing because inspection itself could not run.
    /// </summary>
    public bool CanDiscardRecords()
    {
        _binding ??= MigrationEnvironment.CreateBinding();
        _detector ??= MigrationEnvironment.CreateDetector();
        return new StoreMigrationRecoveryDiscard(_binding, _detector, _logger).CanDiscard();
    }

    public StoreMigrationDiscardState DiscardUnreadableRecords()
    {
        _binding ??= MigrationEnvironment.CreateBinding();
        _detector ??= MigrationEnvironment.CreateDetector();
        return new StoreMigrationRecoveryDiscard(_binding, _detector, _logger).Discard();
    }

    private sealed class StoreMigrationAutoStartApplier : IStoreMigrationAutoStartApplier
    {
        public async Task ApplyAsync(bool enabled)
        {
            try
            {
                await AutoStartManager.SetAutoStartAsync(enabled);
            }
            catch (AutoStartRefusedException exception) when (exception.IsDurable)
            {
                // Only Windows can re-enable a durably refused startup task. Translating it here
                // is what lets finalization report StartupPreferenceRefused and clear the receipt
                // instead of retrying a preference that can never succeed.
                throw new StoreMigrationAutoStartRefusedException(exception.Message, exception);
            }
        }
    }

    private sealed class InnoMutexProbe : IInnoInstanceProbe
    {
        public bool IsRunning()
        {
            using var lease = new InnoMutexLeaseProvider().TryAcquire();
            return lease is null;
        }
    }

    private sealed class InnoMutexLeaseProvider : IMigrationSourceLeaseProvider
    {
        public IMigrationSourceLease? TryAcquire()
        {
            try
            {
                var mutex = new Mutex(true, AppIdentity.MutexBaseName, out var createdNew);
                if (createdNew)
                    return new InnoMutexLease(mutex);
                try
                {
                    if (mutex.WaitOne(0))
                        return new InnoMutexLease(mutex);
                }
                catch (AbandonedMutexException)
                {
                    return new InnoMutexLease(mutex);
                }
                mutex.Dispose();
                return null;
            }
            catch (UnauthorizedAccessException exception)
            {
                Logger.Error($"Could not acquire the Inno instance mutex: {exception.Message}");
                return null;
            }
        }
    }

    private sealed class InnoMutexLease(Mutex mutex) : IMigrationSourceLease
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
