using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal enum SetupNativeLaunchFailure { Invalid, Changed, Unavailable }
internal enum SetupDeferredPresentation { None, CloseUnboundShell, OfferRetry }

internal static class SetupDeferredPresentationPolicy
{
    public static SetupDeferredPresentation Project(SetupHandoffAcquisitionStatus status, bool hasUnboundStartupShell,
        bool hasInFlightPresentation = false) =>
        !hasUnboundStartupShell || hasInFlightPresentation ? SetupDeferredPresentation.None : status switch
        {
            SetupHandoffAcquisitionStatus.Busy => SetupDeferredPresentation.CloseUnboundShell,
            SetupHandoffAcquisitionStatus.RetryRequired => SetupDeferredPresentation.OfferRetry,
            _ => SetupDeferredPresentation.None
        };
}

/// <summary>Process-local presentation lifetime only. It carries no verification or receipt authority.</summary>
internal sealed class SetupHandoffPresentationOwnership
{
    private int _active;
    public bool IsActive => Volatile.Read(ref _active) != 0;

    public IDisposable Acquire()
    {
        Interlocked.Increment(ref _active);
        return new Scope(this);
    }

    private sealed class Scope(SetupHandoffPresentationOwnership owner) : IDisposable
    {
        private SetupHandoffPresentationOwnership? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } current)
                Interlocked.Decrement(ref current._active);
        }
    }
}

/// <summary>Consumes only a leased native receipt after fresh proof and the bound native page has mounted.</summary>
internal sealed class SetupNativeHandoffLauncher(
    Func<GatewayRecord?> getActive,
    Func<GatewayAiSetupCompletion, CancellationToken, Task<SetupVerifiedNativeRoute>> verify,
    Func<SetupNativeCompletion, CancellationToken, Task> open,
    Action<SetupNativeLaunchFailure> reportFailure,
    TimeProvider? timeProvider = null,
    Func<GatewayAiSetupCompletion, CancellationToken, Task>? showPreparing = null,
    Func<SetupVerifiedNativeRoute, CancellationToken, Task>? showReady = null,
    Func<GatewayAiSetupCompletion, CancellationToken, Task<SetupVerifiedNativeRoute>>? verifyPreparation = null,
    Action? readyConsumed = null,
    Func<GatewayAiSetupCompletion, CancellationToken, Task>? confirmStableAuthority = null,
    Action<SetupHandoffAcquisitionStatus>? acquisitionDeferred = null,
    Func<IDisposable?>? acquirePresentation = null)
{
    internal const string FailureNotificationId = "setup-native-launch";
    internal static readonly TimeSpan LaunchTimeout = SetupNativeCompletionTiming.Execution;

    public async Task<bool> OpenAsync(SetupDashboardHandoffStore store, string? handle,
        bool explicitRetry = false, CancellationToken ct = default, NativeRestartRecoveryStore? restartRecovery = null)
    {
        var failure = SetupNativeLaunchFailure.Invalid;
        try
        {
            var acquisition = store.Acquire(handle, explicitRetry);
            if (acquisition.Status is SetupHandoffAcquisitionStatus.Busy or SetupHandoffAcquisitionStatus.RetryRequired)
            {
                acquisitionDeferred?.Invoke(acquisition.Status);
                return false;
            }
            if (acquisition.Status == SetupHandoffAcquisitionStatus.Unavailable)
                failure = SetupNativeLaunchFailure.Unavailable;
            using var lease = acquisition.Lease;
            using var presentation = lease is not null ? acquirePresentation?.Invoke() : null;
            if (lease is not null)
            {
                try
                {
                    void RequireCurrent()
                    {
                        var active = getActive();
                        if (lease.IsExpired)
                            throw new TimeoutException("The native setup execution lease expired.");
                        if ((!lease.IsPreparation && lease.NativeTarget is null) ||
                            active is null || active.Id != lease.Completion.GatewayId ||
                            GatewayDashboardBinding.Capture(active) != lease.Completion.EndpointBinding)
                            throw new SetupNativeOwnershipException();
                    }
                    RequireCurrent();
                    var budget = TimeSpan.FromTicks(Math.Max(0, Math.Min(LaunchTimeout.Ticks, lease.RemainingLifetime.Ticks)));
                    using var deadline = new CancellationTokenSource(budget, timeProvider ?? TimeProvider.System);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                    if (lease.IsPreparation)
                        await (showPreparing?.Invoke(lease.Completion, timeout.Token) ??
                            Task.FromException(new InvalidOperationException("The setup preparation host is unavailable.")));
                    var current = await (lease.IsPreparation && verifyPreparation is not null
                        ? verifyPreparation(lease.Completion, timeout.Token)
                        : verify(lease.Completion, timeout.Token));
                    timeout.Token.ThrowIfCancellationRequested();
                    SetupNativeVerification.RequireSame(lease.Completion, current);
                    if (current.SessionKey != lease.SessionKey)
                        throw new SetupNativeOwnershipException();
                    RequireCurrent();
                    timeout.Token.ThrowIfCancellationRequested();
                    using var navigationDeadline = new CancellationTokenSource(
                        SetupNativeCompletionTiming.Navigation, timeProvider ?? TimeProvider.System);
                    using var navigation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, navigationDeadline.Token);
                    if (lease.IsPreparation)
                        await (showReady?.Invoke(current, navigation.Token) ??
                            Task.FromException(new InvalidOperationException("The setup Ready host is unavailable.")));
                    else
                        await open(new(current.Verification, lease.NativeTarget!), navigation.Token);
                    navigation.Token.ThrowIfCancellationRequested();
                    timeout.Token.ThrowIfCancellationRequested();
                    RequireCurrent();
                    timeout.Token.ThrowIfCancellationRequested();
                    current.ReadyBinding?.RequireCurrent();
                    lease.Consume();
                    ClearRestartRecovery();
                    if (lease.IsPreparation) readyConsumed?.Invoke();
                    return true;
                }
                catch (SetupNativeReadinessExpiredException)
                {
                    failure = await SettleExpiredReadinessAsync(lease, ct);
                }
                catch (Exception error) when (error is SetupNativeOwnershipException or DeviceIdentityLoadException)
                {
                    Logger.Warn($"Native setup destination rejected changed authority ({error.GetType().Name}).");
                    lease.Consume();
                    failure = SetupNativeLaunchFailure.Changed;
                }
                catch (Exception error) when (error is InvalidOperationException or IOException or
                    NotSupportedException or InvalidDataException or System.Text.Json.JsonException or
                    UnauthorizedAccessException or OperationCanceledException or TimeoutException or ArgumentException or
                    System.Runtime.InteropServices.COMException)
                {
                    failure = lease.IsExpired ? SetupNativeLaunchFailure.Invalid : SetupNativeLaunchFailure.Unavailable;
                    var phase = (error as SetupNativeCompletionTimeoutException)?.Phase.ToString() ?? "none";
                    Logger.Warn($"Native setup destination failed ({error.GetType().Name}); phase: {phase}; result: {failure}.");
                    lease.RetainForExplicitRetry();
                }

            }
        }
        catch (InvalidDataException)
        {
            failure = SetupNativeLaunchFailure.Invalid;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            failure = SetupNativeLaunchFailure.Unavailable;
        }
        if (failure is SetupNativeLaunchFailure.Invalid or SetupNativeLaunchFailure.Changed)
            ClearRestartRecovery();
        reportFailure(failure);
        return false;

        void ClearRestartRecovery()
        {
            if (handle is null || restartRecovery is null) return;
            try { restartRecovery.Clear(handle); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Logger.Warn("The completed setup restart recovery record could not be removed.");
            }
        }
    }

    private async Task<SetupNativeLaunchFailure> SettleExpiredReadinessAsync(
        SetupDashboardHandoffStore.Lease lease, CancellationToken ct)
    {
        try
        {
            if (!lease.IsPreparation || confirmStableAuthority is null || lease.IsExpired)
                throw new InvalidOperationException("Stable readiness authority cannot be confirmed.");
            var remaining = TimeSpan.FromTicks(Math.Max(0,
                Math.Min(SetupNativeCompletionTiming.Connection.Ticks, lease.RemainingLifetime.Ticks)));
            using var deadline = new CancellationTokenSource(remaining, timeProvider ?? TimeProvider.System);
            using var confirmation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            await confirmStableAuthority(lease.Completion, confirmation.Token);
            confirmation.Token.ThrowIfCancellationRequested();
            if (getActive() is not { } active || active.Id != lease.Completion.GatewayId ||
                GatewayDashboardBinding.Capture(active) != lease.Completion.EndpointBinding)
                throw new SetupNativeOwnershipException();
            if (lease.IsExpired) throw new TimeoutException("The original readiness lease expired.");
            lease.RetainForExplicitRetry();
            return SetupNativeLaunchFailure.Unavailable;
        }
        catch (Exception error) when (error is SetupNativeOwnershipException or DeviceIdentityLoadException)
        {
            lease.Consume();
            return SetupNativeLaunchFailure.Changed;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            InvalidDataException or System.Text.Json.JsonException or NotSupportedException or
            OperationCanceledException or TimeoutException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Freshness alone grants no retry authority. Unknown confirmation is
            // rejected, never used to renew or replay this pending receipt.
            lease.Consume();
            return SetupNativeLaunchFailure.Invalid;
        }
    }
}
