using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Testable, UI-free recovery rules for gateway-backed onboarding wizard flows.
/// </summary>
public interface IWizardGateway
{
    bool IsConnectedToGateway { get; }
    event EventHandler<ConnectionStatus>? StatusChanged;
    Task<JsonElement> SendWizardRequestAsync(string method, object? parameters = null, int timeoutMs = 30000);
}

public sealed class OpenClawWizardGatewayAdapter : IWizardGateway
{
    private readonly OpenClawGatewayClient _client;

    public OpenClawWizardGatewayAdapter(OpenClawGatewayClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool IsConnectedToGateway => _client.IsConnectedToGateway;

    public event EventHandler<ConnectionStatus>? StatusChanged
    {
        add => _client.StatusChanged += value;
        remove => _client.StatusChanged -= value;
    }

    public Task<JsonElement> SendWizardRequestAsync(string method, object? parameters = null, int timeoutMs = 30000) =>
        _client.SendWizardRequestAsync(method, parameters, timeoutMs);
}

/// <summary>
/// Mutable recovery guard stored by reference in FunctionalUI state. Do not replace this
/// with UseState&lt;bool&gt;: render closures must observe current fields synchronously.
/// </summary>
public sealed class WizardRecoveryGuardState
{
    private int _restartAttempted;
    private long _connectionLossEpoch;

    public bool HasRestartedForCurrentLostSession => Volatile.Read(ref _restartAttempted) != 0;
    public long ConnectionLossEpoch => Interlocked.Read(ref _connectionLossEpoch);

    public void ObserveConnectionStatus(ConnectionStatus status)
    {
        if (status is ConnectionStatus.Disconnected or ConnectionStatus.Connecting or ConnectionStatus.Error)
        {
            Interlocked.Increment(ref _connectionLossEpoch);
        }
    }

    public bool TryMarkRestartAttempted() => Interlocked.CompareExchange(ref _restartAttempted, 1, 0) == 0;

    public void ResetAfterSuccessfulStart() => Volatile.Write(ref _restartAttempted, 0);

    public void ResetForManualRestart() => Volatile.Write(ref _restartAttempted, 0);
}

public readonly record struct WizardRequestContext(long ConnectionLossEpoch);

public enum WizardRecoveryKind
{
    NotEligible,
    AlreadyAttempted,
    Recovered,
    Failed
}

public sealed record WizardRecoveryResult(WizardRecoveryKind Kind, JsonElement? Payload = null, Exception? Exception = null)
{
    public static WizardRecoveryResult NotEligible { get; } = new(WizardRecoveryKind.NotEligible);
    public static WizardRecoveryResult AlreadyAttempted { get; } = new(WizardRecoveryKind.AlreadyAttempted);
    public static WizardRecoveryResult Recovered(JsonElement payload) => new(WizardRecoveryKind.Recovered, payload);
    public static WizardRecoveryResult Failed(Exception exception) => new(WizardRecoveryKind.Failed, null, exception);
}

public static class WizardFlowController
{
    public const string RecoveryFailureMessage = "Setup couldn't continue. Restart wizard to try again.";
    public const string SlowStepRetryMessage = "Setup is taking longer than expected. Retry?";

    public static WizardRequestContext CaptureRequestContext(WizardRecoveryGuardState guard) =>
        new(guard.ConnectionLossEpoch);

    public static bool IsStartPayload(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("sessionId", out _);

    public static bool ShouldRecover(Exception exception, IWizardGateway? client, WizardRecoveryGuardState guard, WizardRequestContext requestContext)
    {
        if (exception is OperationCanceledException)
        {
            return true;
        }

        if (exception is InvalidOperationException invalidOperation)
        {
            return invalidOperation.Message.Contains("wizard not found", StringComparison.OrdinalIgnoreCase)
                || invalidOperation.Message.Contains("wizard not running", StringComparison.OrdinalIgnoreCase);
        }

        if (exception is TimeoutException)
        {
            return client?.IsConnectedToGateway != true
                || guard.ConnectionLossEpoch != requestContext.ConnectionLossEpoch;
        }

        return false;
    }

    public static async Task<JsonElement> RestartWizardAsync(
        WizardRecoveryGuardState guard,
        Action clearWizardSessionState,
        Func<Task<JsonElement>> startWizardAsync)
    {
        guard.ResetForManualRestart();
        clearWizardSessionState();
        return await startWizardAsync();
    }

    public static async Task<WizardRecoveryResult> TryRecoverAsync(
        Exception exception,
        IWizardGateway? client,
        WizardRecoveryGuardState guard,
        WizardRequestContext requestContext,
        Func<Task<JsonElement>> startWizardAsync)
    {
        if (!ShouldRecover(exception, client, guard, requestContext))
        {
            return WizardRecoveryResult.NotEligible;
        }

        if (!guard.TryMarkRestartAttempted())
        {
            return WizardRecoveryResult.AlreadyAttempted;
        }

        try
        {
            var payload = await startWizardAsync();
            return WizardRecoveryResult.Recovered(payload);
        }
        catch (Exception ex)
        {
            return WizardRecoveryResult.Failed(ex);
        }
    }

    /// <summary>
    /// Waits up to <paramref name="maxPollCount"/> poll intervals for the gateway to
    /// (re-)connect. Returns true if connected at exit, false on timeout. The
    /// <paramref name="delayAsync"/> delegate is injected so unit tests can run instantly.
    /// Pass <paramref name="cancellationToken"/> to abort polling early (e.g., on app shutdown
    /// or page navigation away); throws <see cref="OperationCanceledException"/> if cancelled.
    /// </summary>
    public static async Task<bool> WaitForConnectionAsync(
        IWizardGateway? client,
        int maxPollCount = 30,
        Func<Task>? delayAsync = null,
        CancellationToken cancellationToken = default)
    {
        delayAsync ??= () => Task.Delay(1000, cancellationToken);
        for (int poll = 0; poll < maxPollCount && client?.IsConnectedToGateway != true; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await delayAsync();
        }
        return client?.IsConnectedToGateway == true;
    }

    /// <summary>
    /// Attempts to resume a live wizard session via wizard.next (no answer) before
    /// falling back to wizard.start. Caller must NOT clear WizardSessionId before calling.
    /// Call <see cref="WaitForConnectionAsync"/> first so IsConnectedToGateway is true
    /// when this method runs.
    /// </summary>
    public static async Task<(bool Resumed, JsonElement Payload)> TryResumeWithSessionAsync(
        string? sessionId,
        IWizardGateway? client,
        Func<string, Task<JsonElement>> sendWizardNextNoAnswerAsync,
        Func<Task<JsonElement>> fallbackStartWizardAsync)
    {
        if (!string.IsNullOrEmpty(sessionId) && client?.IsConnectedToGateway == true)
        {
            try
            {
                Logger.Info($"[WizardFlow] TryResume: wizard.next(no answer) sessionId={sessionId}");
                var stepPayload = await sendWizardNextNoAnswerAsync(sessionId);
                Logger.Info("[WizardFlow] TryResume: resume succeeded");
                return (true, stepPayload);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("wizard not found", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("wizard not running", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("session not found", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warn($"[WizardFlow] TryResume: session not found ({ex.Message}) → fallback wizard.start");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warn($"[WizardFlow] TryResume: unexpected error ({ex.GetType().Name}: {ex.Message}) → fallback wizard.start");
            }
        }
        var startPayload = await fallbackStartWizardAsync();
        return (false, startPayload);
    }
}
