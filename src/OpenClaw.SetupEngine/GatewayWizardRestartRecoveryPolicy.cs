using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

internal static class GatewayWizardRestartRecoveryPolicy
{
    private const string TerminalRestartVersion = "2026.7.1";
    private const string TerminalStepKey = "model-check";

    public static bool IsTerminalRestartCandidate(
        string? gatewayVersion,
        string? stepId,
        string? title = null,
        string? message = null) =>
        string.Equals(
            gatewayVersion?.Trim(),
            TerminalRestartVersion,
            StringComparison.Ordinal) &&
        IsTerminalStep(stepId, title, message);

    public static bool IsExpectedTerminalRestart(
        string? gatewayVersion,
        string? stepId,
        Exception exception,
        string? title = null,
        string? message = null) =>
        IsTerminalRestartCandidate(gatewayVersion, stepId, title, message) &&
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

    public static bool IsRetryableGatewayStartupDisconnect(int? closeStatusCode) =>
        closeStatusCode == 1013;

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
            var retryable =
                result.Kind == GatewayEndpointProvenanceKind.NoListener ||
                result is
                {
                    Kind: GatewayEndpointProvenanceKind.UnknownListener,
                    FailureReason:
                        GatewayEndpointProvenanceFailureReason.ListenerSnapshotChanged,
                };
            if (!retryable ||
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

    private static bool IsTerminalStep(
        string? stepId,
        string? title,
        string? message) =>
        IsTerminalStepKey(stepId) ||
        IsTerminalStepKey(title) ||
        IsTerminalStepKey(message);

    private static bool IsTerminalStepKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = string.Join(
            '-',
            value.Trim()
                .ToLowerInvariant()
                .Split(
                    [' ', '\t', '\r', '\n', '_', '-'],
                    StringSplitOptions.RemoveEmptyEntries));
        return string.Equals(normalized, TerminalStepKey, StringComparison.Ordinal);
    }
}
