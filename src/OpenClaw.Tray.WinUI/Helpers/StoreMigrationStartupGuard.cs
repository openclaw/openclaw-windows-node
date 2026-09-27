using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using OpenClaw.Connection;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Helpers;

internal static class StoreMigrationStartupGuard
{
    public static bool ShouldStopLaunch()
    {
#if !STORE_MIGRATION_PREVIEW
        return false;
#else
        if (!PackageHelper.IsPackaged || AppIdentity.IsDev)
            return false;

        var identity = global::Windows.ApplicationModel.Package.Current.Id;
        if (identity.Name != MigrationRecordCodec.PackageName ||
            identity.Publisher != MigrationRecordCodec.PackagePublisher ||
            HasPathOverride())
        {
            Logger.Error("Store migration preview requires the production package identity and default data paths.");
            ShowGuidance("Migration_StoreUnsupported");
            return true;
        }

        var localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var binding = new MigrationBinding
        {
            InstallDirectory = Path.Combine(localRoot, AppIdentity.DataDirectoryName),
            RoamingDirectory = AppIdentity.ResolveRoamingDataDirectory(),
            LocalDirectory = AppIdentity.ResolveSetupLocalDataDirectory(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            UserSid = WindowsIdentity.GetCurrent().User?.Value ?? ""
        };
        var logger = new AppLogger();
        var minimum = typeof(StoreMigrationStartupGuard).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "StoreMigrationPreviewMinimumSourceVersion")?.Value;
        var detector = new InnoInstallationDetector(localRoot, logger);
        var coordinator = new StoreMigrationStartupCoordinator(
            detector,
            new MigrationStartupRecordReader(binding, logger),
            logger);
        var decision = coordinator.Evaluate(true, minimum, binding.Architecture);
        if (decision.AllowsNormalStartup)
            return false;

        if (decision.State == StoreMigrationStartupState.FinalizationRequired)
        {
            var finalization = FinalizeCompletedMigration(binding, detector, logger);
            if (finalization.AllowsNormalStartup)
            {
                // A durable refusal is the one finalization outcome the user must act on
                // themselves: only Windows can re-enable the startup task. Notify, then
                // continue launching, because the migration itself succeeded.
                if (finalization.State == StoreMigrationFinalizationState.StartupPreferenceRefused)
                    ShowGuidance("Migration_StoreStartupRefused");
                return false;
            }

            ShowGuidance(finalization.State switch
            {
                StoreMigrationFinalizationState.AwaitingInnoRemoval => "Migration_StoreAwaitingInnoRemoval",
                StoreMigrationFinalizationState.InspectionFailed => "Migration_StoreInspectionFailed",
                _ => "Migration_StoreFinalizationFailed"
            });
            return true;
        }

        if (decision.State == StoreMigrationStartupState.ConsentRequired)
        {
            RunConsentWorkflow(decision, binding, detector, logger);
            return true;
        }

        ShowGuidance(decision.State switch
        {
            StoreMigrationStartupState.UpdateInno => "Migration_StoreUpdateRequired",
            StoreMigrationStartupState.UnsupportedInstallation => "Migration_StoreUnsupported",
            StoreMigrationStartupState.InspectionFailed => "Migration_StoreInspectionFailed",
            StoreMigrationStartupState.AwaitingInnoRemoval => "Migration_StoreAwaitingInnoRemoval",
            _ => "Migration_StorePending"
        });
        return true;
#endif
    }

#if STORE_MIGRATION_PREVIEW
    private static bool HasPathOverride() =>
        new[]
        {
            "OPENCLAW_TRAY_DATA_DIR", "OPENCLAW_TRAY_APPDATA_DIR",
            "OPENCLAW_TRAY_LOCALAPPDATA_DIR", "OPENCLAW_TRAY_LOCAL_DATA_DIR"
        }.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));

    private static void RunConsentWorkflow(
        StoreMigrationStartupDecision admission,
        MigrationBinding binding,
        IInnoInstallationDetector detector,
        IOpenClawLogger logger)
    {
        var coordinator = new StoreMigrationConsentCoordinator(new InnoMutexProbe());
        var consent = coordinator.Begin(admission, ShowChoice("Migration_StoreConsent"));

        while (consent.State == StoreMigrationConsentState.WaitingForInnoExit)
        {
            if (!ShowChoice("Migration_StoreCloseInno"))
                return;

            consent = coordinator.Retry();
        }

        if (consent.State != StoreMigrationConsentState.ReadyForAdoption ||
            admission.Installation is null)
            return;

        var preparation = new StoreMigrationAdoptionPreparationCoordinator(
            new InnoMutexLeaseProvider(), detector, new MigrationPreparation(binding), logger);
        while (true)
        {
            var result = preparation.Prepare(admission.Installation);
            switch (result.State)
            {
                case StoreMigrationPreparationState.Prepared:
                    CompletePreparedMigration(admission.Installation, binding, detector, logger);
                    return;
                case StoreMigrationPreparationState.InnoRunning:
                    if (!ShowChoice("Migration_StoreCloseInno"))
                        return;
                    continue;
                case StoreMigrationPreparationState.SourceChanged:
                    ShowGuidance("Migration_StoreUnsupported");
                    return;
                default:
                    ShowGuidance("Migration_StoreValidationFailed");
                    return;
            }
        }
    }

    private static void CompletePreparedMigration(
        InnoInstallation installation,
        MigrationBinding binding,
        IInnoInstallationDetector detector,
        IOpenClawLogger logger)
    {
        var version = global::Windows.ApplicationModel.Package.Current.Id.Version;
        var targetVersion = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        var completion = new StoreMigrationCompletionCoordinator(
            new InnoMutexLeaseProvider(),
            detector,
            binding,
            new CredentialResolver(DeviceIdentityFileReader.Instance),
            logger);
        var result = completion.Complete(installation, targetVersion);
        while (result.State == StoreMigrationCompletionState.InnoRunning)
        {
            if (!ShowChoice("Migration_StoreCloseInno"))
                return;
            result = completion.Complete(installation, targetVersion);
        }
        ShowGuidance(result.State switch
        {
            StoreMigrationCompletionState.Completed => "Migration_StoreAwaitingInnoRemoval",
            StoreMigrationCompletionState.NoActiveGateway or StoreMigrationCompletionState.CredentialUnavailable
                => "Migration_StoreCredentialUnavailable",
            StoreMigrationCompletionState.SourceChanged => "Migration_StoreUnsupported",
            _ => "Migration_StoreValidationFailed"
        });
    }

    private static StoreMigrationFinalizationDecision FinalizeCompletedMigration(
        MigrationBinding binding,
        IInnoInstallationDetector detector,
        IOpenClawLogger logger)
    {
        var records = new MigrationStartupRecordReader(binding, logger);
        if (records.Read().Status != MigrationStartupRecordStatus.Completed)
            return new(StoreMigrationFinalizationState.InspectionFailed);

        return new StoreMigrationFinalizationCoordinator(
                binding,
                detector,
                new InnoSourceRemovalVerifier(binding, AppIdentity.MutexBaseName),
                records,
                new MigrationInventoryCapture(binding),
                new StoreMigrationAutoStartApplier(),
                new MigrationFinalizationRecordCleaner(binding),
                logger)
            .FinalizeAsync()
            .GetAwaiter()
            .GetResult();
    }

    private static bool ShowChoice(string contentKey)
    {
        var content = LocalizationHelper.Format(contentKey,
            LocalizationHelper.GetString("Migration_StoreYes"),
            LocalizationHelper.GetString("Migration_StoreNo"));
        return MessageBoxW(IntPtr.Zero, content, LocalizationHelper.GetString("Migration_StorePreviewTitle"),
            0x00000124) == 6;
    }

    private static void ShowGuidance(string resourceKey)
    {
        if (MessageBoxW(IntPtr.Zero, LocalizationHelper.GetString(resourceKey),
                LocalizationHelper.GetString("Migration_StorePreviewTitle"), 0x00000040) == 0)
            Logger.Error($"Could not show Store migration preview guidance (Win32 {Marshal.GetLastWin32Error()}).");
    }

    private sealed class InnoMutexProbe : IInnoInstanceProbe
    {
        public bool IsRunning()
        {
            using var lease = new InnoMutexLeaseProvider().TryAcquire();
            return lease is null;
        }
    }

    private sealed class StoreMigrationAutoStartApplier : IStoreMigrationAutoStartApplier
    {
        public async Task ApplyAsync(bool enabled)
        {
            try
            {
                await Task.Run(() => AutoStartManager.SetAutoStartAsync(enabled)).ConfigureAwait(false);
            }
            catch (AutoStartRefusedException exception) when (exception.IsDurable)
            {
                throw new StoreMigrationAutoStartRefusedException(exception.Message, exception);
            }
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
#endif
}
