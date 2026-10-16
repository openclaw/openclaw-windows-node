using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal enum SetupNativeLaunchFailure { Invalid, Changed, Unavailable }

/// <summary>Consumes only a leased native receipt after fresh proof and the bound native page has mounted.</summary>
internal sealed class SetupNativeHandoffLauncher(
    Func<GatewayRecord?> getActive,
    Func<GatewayAiSetupCompletion, CancellationToken, Task<SetupVerifiedNativeRoute>> verify,
    Func<SetupNativeCompletion, CancellationToken, Task> open,
    Action<SetupNativeLaunchFailure> reportFailure,
    TimeProvider? timeProvider = null)
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
                return false;
            if (acquisition.Status == SetupHandoffAcquisitionStatus.Unavailable)
                failure = SetupNativeLaunchFailure.Unavailable;
            using var lease = acquisition.Lease;
            if (lease is not null)
            {
                try
                {
                    void RequireCurrent()
                    {
                        var active = getActive();
                        if (lease.IsExpired)
                            throw new TimeoutException("The native setup execution lease expired.");
                        if (lease.NativeTarget is null ||
                            active is null || active.Id != lease.Completion.GatewayId ||
                            GatewayDashboardBinding.Capture(active) != lease.Completion.EndpointBinding)
                            throw new SetupNativeOwnershipException();
                    }
                    RequireCurrent();
                    var budget = TimeSpan.FromTicks(Math.Max(0, Math.Min(LaunchTimeout.Ticks, lease.RemainingLifetime.Ticks)));
                    using var deadline = new CancellationTokenSource(budget, timeProvider ?? TimeProvider.System);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                    var current = await verify(lease.Completion, timeout.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    SetupNativeVerification.RequireSame(lease.Completion, current);
                    if (current.SessionKey != lease.NativeTarget!.SessionKey)
                        throw new SetupNativeOwnershipException();
                    RequireCurrent();
                    timeout.Token.ThrowIfCancellationRequested();
                    using var navigationDeadline = new CancellationTokenSource(
                        SetupNativeCompletionTiming.Navigation, timeProvider ?? TimeProvider.System);
                    using var navigation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, navigationDeadline.Token);
                    await open(new(current.Verification, lease.NativeTarget), navigation.Token);
                    navigation.Token.ThrowIfCancellationRequested();
                    timeout.Token.ThrowIfCancellationRequested();
                    RequireCurrent();
                    timeout.Token.ThrowIfCancellationRequested();
                    lease.Consume();
                    ClearRestartRecovery();
                    return true;
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
}
