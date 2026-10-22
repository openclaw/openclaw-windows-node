using OpenClaw.Connection.Migration;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal enum StoreMigrationStage
{
    Inspecting, Consent, ClosingSource, Preparing, Completing, Finalizing,
    CloseSource, AwaitingRemoval, ValidationFailed,
    UpdateRequired, Unsupported, InspectionFailed, Recovery, FinalizationFailed,
    StartupRefused, Ready
}

internal interface IStoreMigrationOperations
{
    StoreMigrationStartupDecision Inspect();
    bool HasConsent(InnoInstallation installation);
    void GrantConsent(InnoInstallation installation);
    Task<bool> CloseSourceAsync(StoreMigrationStartupDecision admission, CancellationToken cancellationToken);
    Task<StoreMigrationPreparationState> PrepareAsync(InnoInstallation installation);
    Task<StoreMigrationCompletionState> CompleteAsync(InnoInstallation installation);
    Task<StoreMigrationFinalizationDecision> FinalizeAsync();

    /// <summary>
    /// Whether recovery can be cleared from inside the app. Must not throw: it runs while recovery
    /// is already showing. See <see cref="StoreMigrationRecoveryDiscard.CanDiscard"/>.
    /// </summary>
    bool CanDiscardRecords();

    /// <summary>Removes discardable records. See <see cref="StoreMigrationRecoveryDiscard"/>.</summary>
    StoreMigrationDiscardState DiscardUnreadableRecords();

    /// <summary>
    /// Receipt presence only, for when <see cref="Inspect"/> itself fails and never produces a
    /// decision. Must answer without requiring a successful inspection.
    /// </summary>
    bool HoldsCompletionReceipt();
}

/// <summary>Serializes UI actions without replacing the admission, adoption, or finalization owners.</summary>
internal sealed class StoreMigrationWorkflow(
    IStoreMigrationOperations operations,
    IOpenClawLogger logger,
    StoreMigrationStartupDecision? initialAdmission = null)
{
    public StoreMigrationStage Stage { get; private set; } = StoreMigrationStage.Inspecting;
    public bool IsBusy { get; private set; }

    /// <summary>
    /// Whether recovery can be cleared from inside the app. Only undecodable records qualify:
    /// every other recovery cause is repaired outside the window, by removing the previous app.
    /// </summary>
    public bool CanDiscardRecords { get; private set; }

    /// <summary>
    /// The last discard outcome, so the window can say that nothing happened. A running previous
    /// app holds the migration lease for its whole lifetime, so <see cref="StoreMigrationDiscardState.Busy"/>
    /// is the expected answer there and silence would look like a dead button.
    /// </summary>
    public StoreMigrationDiscardState? LastDiscard { get; private set; }

    /// <summary>
    /// Closing the window abandons migration, so only a handoff whose completion receipt already
    /// exists, or an installed source the Store app must not run beside, may keep the app from
    /// starting. This tracks
    /// <see cref="StoreMigrationStartupDecision.BlocksStartup"/> rather than the visible stage,
    /// because a receipt can survive a stage that later reports an inspection failure. Every
    /// other state has nothing to protect, and refusing to launch would leave the user stuck.
    /// <para>
    /// <see cref="StoreMigrationStage.Consent"/> is the exception, and it is a product contract
    /// rather than a data-safety rule. Issue #1374 specifies that Not now "leaves Inno unchanged
    /// and MSIX inactive", so declining, or dismissing the window at the same stage, must not
    /// hand the user a running Store app while the previous app is still installed. This stage is
    /// only ever reached with a detected installation, so the block cannot strand a user whose
    /// previous app is already gone.
    /// </para>
    /// <para>
    /// The remaining informational stages are deliberately not included. An unsupported source
    /// without its payload, a failed inspection, or an undecodable record can all occur after the
    /// previous app has been removed, where refusing to launch would leave no working app at all.
    /// An unsupported source that is genuinely installed does block, decided at admission where
    /// the payload evidence lives.
    /// </para>
    /// <para>
    /// <c>_sourceRemoved</c> overrides the receipt hold outright. Finalization sets it once it has
    /// verified the previous installation is gone, and from that point a failure has nothing left
    /// to protect: the receipt would block an app that no longer exists while this window refuses
    /// to release the one that does. The failing stage stays visible, but the user can close it
    /// and start. This override deliberately does not reach <see cref="StoreMigrationStage.Consent"/>,
    /// which stays blocked as a product contract; the two are ordered so the Consent block wins.
    /// Reaching Consent again requires a fresh admission that still detects an installation, so
    /// the two rules cannot contradict each other.
    /// </para>
    /// <para>
    /// Before the first admission resolves, nothing is known yet, so closing is treated as
    /// blocking. Otherwise a close raced against startup inspection would wave the user through
    /// a handoff that had already moved data.
    /// </para>
    /// <para>
    /// <see cref="StoreMigrationStage.StartupRefused"/> is excluded alongside
    /// <see cref="StoreMigrationStage.Ready"/>: finalization succeeded and cleared the receipt,
    /// so the window is only reporting that Windows refused the startup task. Blocking there
    /// would withhold an app whose migration is complete.
    /// </para>
    /// </summary>
    public bool BlocksStartup =>
        Stage is StoreMigrationStage.Consent ||
        (!_sourceRemoved &&
         (!_admissionResolved || _holdsStartupBlock || _sourceBlocksStartup) &&
         Stage is not (StoreMigrationStage.Ready or StoreMigrationStage.StartupRefused));
    public event Action? Changed;
    private InnoInstallation? _promptInstallation;
    // Seeded from the caller's admission so a receipt observed before this workflow existed is
    // not lost when a later inspection pass fails. Latches only durable reasons startup must not
    // resume: data that has moved, or is about to.
    private bool _holdsStartupBlock = initialAdmission?.HoldsDurableBlock ?? false;
    private bool _admissionResolved;

    // Deliberately not latched. This asserts a source is installed right now, and removing that
    // source is how the user is meant to end the block. Latching it would keep the Store app
    // closed after the previous app was gone, which is the lockout this whole path avoids.
    private bool _sourceBlocksStartup = initialAdmission?.SourceBlocksStartup ?? false;

    // Latched, never cleared: once finalization has proven the previous installation is gone, no
    // later pass may re-impose a startup block that would leave the user with no usable app.
    private bool _sourceRemoved;

    public Task StartAsync(CancellationToken cancellationToken) => RunAsync(false, cancellationToken);
    public Task ContinueAsync(CancellationToken cancellationToken) =>
        RunAsync(Stage == StoreMigrationStage.Consent, cancellationToken);

    /// <summary>
    /// Deletes records that cannot be decoded, then re-inspects. The receipt hold is released only
    /// on success: the hold exists because an undecodable receipt might mean data moved, and after
    /// the records are gone there is nothing left for a later pass to read it from.
    /// </summary>
    public async Task DiscardRecordsAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || Stage != StoreMigrationStage.Recovery)
            return;

        IsBusy = true;
        var result = StoreMigrationDiscardState.Failed;
        try
        {
            LastDiscard = null;
            Changed?.Invoke();
            result = await Task.Run(operations.DiscardUnreadableRecords, cancellationToken);
            if (result != StoreMigrationDiscardState.Discarded)
                logger.Error($"Could not discard the migration records ({result}).");
        }
        catch (Exception exception)
        {
            logger.Error($"Discarding the migration records failed: {exception}");
        }
        finally
        {
            IsBusy = false;
            LastDiscard = result;
            // A contended or failing discard stays offered so the user can retry. Readable
            // records mean the offer was wrong, and repeating it would only mislead.
            if (result == StoreMigrationDiscardState.RecordsAreReadable)
                CanDiscardRecords = false;
            Changed?.Invoke();
        }

        if (result != StoreMigrationDiscardState.Discarded)
            return;

        _holdsStartupBlock = false;
        await RunAsync(false, cancellationToken);
    }

    private async Task RunAsync(bool confirmed, CancellationToken cancellationToken)
    {
        // Recovery is deliberately retryable. Its usual cause, a receipt whose source version no
        // longer matches the installed Inno, clears once the user removes that app, and a pass
        // that refused to re-inspect would leave them looking at advice they had already followed.
        if (IsBusy || Stage is StoreMigrationStage.Ready)
            return;

        IsBusy = true;
        // A new pass supersedes whatever the last discard reported.
        LastDiscard = null;
        try
        {
            SetStage(StoreMigrationStage.Inspecting);
            cancellationToken.ThrowIfCancellationRequested();
            var admission = operations.Inspect();
            // Never cleared by a later pass: Retry re-inspects, and a transient failure there
            // must not drop a receipt this session already observed or wrote.
            _holdsStartupBlock |= admission.HoldsDurableBlock;
            // Replaced, never latched: this pass just measured whether the source is installed.
            _sourceBlocksStartup = admission.SourceBlocksStartup;
            if (admission.AllowsNormalStartup)
            {
                SetStage(StoreMigrationStage.Ready);
                return;
            }

            if (admission.State == StoreMigrationStartupState.FinalizationRequired)
            {
                SetStage(StoreMigrationStage.Finalizing);
                var result = await operations.FinalizeAsync();
                _sourceRemoved |= result.SourceRemoved;
                // A durable startup refusal still finalizes the migration, so it must not be
                // folded into Ready: the user has to be told that only Windows can re-enable
                // the startup task. Launch still proceeds once they close the window.
                if (result.State == StoreMigrationFinalizationState.StartupPreferenceRefused)
                {
                    SetStage(StoreMigrationStage.StartupRefused);
                    return;
                }
                SetStage(result.AllowsNormalStartup ? StoreMigrationStage.Ready : result.State switch
                {
                    StoreMigrationFinalizationState.AwaitingInnoRemoval => StoreMigrationStage.AwaitingRemoval,
                    StoreMigrationFinalizationState.InspectionFailed => StoreMigrationStage.InspectionFailed,
                    _ => StoreMigrationStage.FinalizationFailed
                });
                return;
            }

            if (admission.State != StoreMigrationStartupState.ConsentRequired || admission.Installation is null)
            {
                SetStage(admission.State switch
                {
                    StoreMigrationStartupState.AwaitingInnoRemoval => StoreMigrationStage.AwaitingRemoval,
                    StoreMigrationStartupState.UpdateInno => StoreMigrationStage.UpdateRequired,
                    StoreMigrationStartupState.UnsupportedInstallation => StoreMigrationStage.Unsupported,
                    StoreMigrationStartupState.InspectionFailed => StoreMigrationStage.InspectionFailed,
                    _ => StoreMigrationStage.Recovery
                });
                return;
            }

            if ((confirmed && _promptInstallation != admission.Installation) ||
                (!confirmed && !operations.HasConsent(admission.Installation)))
            {
                _promptInstallation = admission.Installation;
                SetStage(StoreMigrationStage.Consent);
                return;
            }
            if (confirmed)
            {
                try
                {
                    operations.GrantConsent(admission.Installation);
                }
                catch (Exception exception) when (exception is InvalidDataException or System.Security.Cryptography.CryptographicException)
                {
                    logger.Error($"Migration consent needs recovery; the existing record was preserved: {exception.Message}");
                    SetStage(StoreMigrationStage.Recovery);
                    return;
                }
            }

            SetStage(StoreMigrationStage.ClosingSource);
            if (!await operations.CloseSourceAsync(admission, cancellationToken))
            {
                SetStage(StoreMigrationStage.CloseSource);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            SetStage(StoreMigrationStage.Preparing);
            var prepared = await operations.PrepareAsync(admission.Installation);
            if (prepared != StoreMigrationPreparationState.Prepared)
            {
                SetStage(prepared switch
                {
                    StoreMigrationPreparationState.InnoRunning => StoreMigrationStage.CloseSource,
                    StoreMigrationPreparationState.SourceChanged => StoreMigrationStage.Unsupported,
                    _ => StoreMigrationStage.ValidationFailed
                });
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            SetStage(StoreMigrationStage.Completing);
            var completed = await operations.CompleteAsync(admission.Installation);
            // A written receipt means data has moved; the window may no longer be dismissed
            // into normal startup until finalization succeeds.
            _holdsStartupBlock |= completed == StoreMigrationCompletionState.Completed;
            SetStage(completed switch
            {
                StoreMigrationCompletionState.Completed => StoreMigrationStage.AwaitingRemoval,
                StoreMigrationCompletionState.InnoRunning => StoreMigrationStage.CloseSource,
                StoreMigrationCompletionState.SourceChanged => StoreMigrationStage.Unsupported,
                _ => StoreMigrationStage.ValidationFailed
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStage(StoreMigrationStage.CloseSource);
        }
        catch (Exception exception)
        {
            // This is the UI error boundary. A failure here is reported as an inspection problem;
            // whether it also blocks startup is decided by the receipt, not by the failure. The
            // receipt is probed directly because a throwing inspection never observed one, and
            // leaving the flag clear would let a close resume startup against moved data.
            logger.Error($"Migration workflow failed: {exception}");
            _holdsStartupBlock |= HoldsReceiptWithoutInspection();
            // This pass detected no source at all, so it cannot assert one is installed. Keeping
            // a previous pass's answer would block a user who has since removed the source and
            // now only has a failing inspection standing between them and a usable app.
            _sourceBlocksStartup = false;
            SetStage(StoreMigrationStage.InspectionFailed);
        }
        finally
        {
            // The pass reached a definite stage, so closing may now be judged on the receipt.
            _admissionResolved = true;
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Fails safe: if the receipt cannot be confirmed after the workflow already failed, nothing
    /// is known about whether data moved, and the app must not resume normal startup.
    /// </summary>
    private bool HoldsReceiptWithoutInspection()
    {
        try
        {
            return operations.HoldsCompletionReceipt();
        }
        catch (Exception exception)
        {
            logger.Error($"Could not confirm the migration receipt after a workflow failure: {exception}");
            return true;
        }
    }

    private void SetStage(StoreMigrationStage stage)
    {
        Stage = stage;
        if (stage == StoreMigrationStage.Recovery)
            CanDiscardRecords = ProbeDiscardOffer();
        Changed?.Invoke();
    }

    /// <summary>
    /// Recovery is already showing when this runs, so a failure here must not replace it with an
    /// error. An unanswerable probe hides the offer rather than promising an escape that is not
    /// there; the guidance text still names the remedies that work from outside the app.
    /// </summary>
    private bool ProbeDiscardOffer()
    {
        try
        {
            return operations.CanDiscardRecords();
        }
        catch (Exception exception)
        {
            logger.Error($"Could not classify the migration records during recovery: {exception}");
            return false;
        }
    }
}
