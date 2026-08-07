using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

internal static class GatewayWizardRestartRecoveryPolicy
{
    private const string TerminalRestartVersion = "2026.7.1";
    private const string TerminalStepId = "done";

    public static bool IsTerminalRestartCandidate(string? gatewayVersion, string? stepId) =>
        string.Equals(
            gatewayVersion?.Trim(),
            TerminalRestartVersion,
            StringComparison.Ordinal) &&
        string.Equals(stepId, TerminalStepId, StringComparison.Ordinal);

    public static bool IsExpectedTerminalRestart(
        string? gatewayVersion,
        string? stepId,
        Exception exception) =>
        IsTerminalRestartCandidate(gatewayVersion, stepId) &&
        IsServiceRestartDisconnect(exception);

    public static bool IsRestartLikeDisconnect(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is GatewayConnectionLostException ||
                current.Message.Contains("connection lost", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("gateway restarting", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("service restart", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<GatewayEndpointProvenance> WaitForExpectedManagedGatewayAsync(
        Func<CancellationToken, Task<GatewayEndpointProvenance>> inspectAsync,
        int noListenerRetryCount,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspectAsync);
        ArgumentOutOfRangeException.ThrowIfNegative(noListenerRetryCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);

        for (var attempt = 0; ; attempt++)
        {
            var result = await inspectAsync(cancellationToken);
            if (result.Kind != GatewayEndpointProvenanceKind.NoListener ||
                attempt >= noListenerRetryCount)
            {
                return result;
            }

            await Task.Delay(retryDelay, cancellationToken);
        }
    }

    private static bool IsServiceRestartDisconnect(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is GatewayConnectionLostException { CloseStatusCode: 1012 })
                return true;
        }

        return false;
    }
}
