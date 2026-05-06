using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

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
}
