using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

internal static class GatewayWizardRestartRecoveryPolicy
{
    private const string TerminalRestartVersion = "2026.7.1";
    private const string TerminalStepKey = "model-check";
    private const string FinalWizardStepKey = "done";

    /// <summary>
    /// Exact terminal error the gateway reports when a config-driven gateway restart
    /// terminates the hosted wizard TUI process.
    /// </summary>
    public const string HostedWizardTerminationError =
        "Error: TUI exited from signal SIGTERM";

    public static bool IsTerminalRestartCandidate(
        string? gatewayVersion,
        string? stepId,
        string? title = null,
        string? message = null) =>
        string.Equals(
            gatewayVersion?.Trim(),
            TerminalRestartVersion,
            StringComparison.Ordinal) &&
        MatchesStepKey(TerminalStepKey, stepId, title, message);

    /// <summary>
    /// True when a wizard step is the authoritative final <c>done</c> step. The gateway
    /// assigns opaque step ids, so the step is identified by its normalized id or title.
    /// It must be a plain acknowledgement note with no options, and when the gateway
    /// supplies position metadata the step must also be the last one.
    /// </summary>
    public static bool IsAuthoritativeFinalWizardStep(
        string? stepType,
        string? stepId,
        string? title,
        bool hasOptions,
        int stepIndex,
        int totalSteps) =>
        !hasOptions &&
        WizardStepClassifier.Categorize(stepType, hasOptions) ==
            WizardStepCategory.Acknowledge &&
        (MatchesStepKey(FinalWizardStepKey, stepId) ||
            MatchesStepKey(FinalWizardStepKey, title)) &&
        (totalSteps <= 0 || stepIndex == totalSteps - 1 || stepIndex == totalSteps);

    /// <summary>
    /// True only when a terminal wizard payload reports the exact hosted-TUI termination
    /// error after the authoritative final <c>done</c> step was already answered. Only
    /// surrounding whitespace is tolerated: every other terminal error, an earlier step,
    /// a non-terminal payload, and any other message keeps the wizard failure.
    /// </summary>
    public static bool IsHostedWizardTerminationAfterFinalStep(
        bool payloadIsTerminal,
        string? error,
        bool answeredAuthoritativeFinalStep) =>
        payloadIsTerminal &&
        answeredAuthoritativeFinalStep &&
        string.Equals(
            error?.Trim(),
            HostedWizardTerminationError,
            StringComparison.Ordinal);

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
                current.Message.Contains("during gateway restart", StringComparison.OrdinalIgnoreCase) ||
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

    private static bool MatchesStepKey(
        string expectedKey,
        string? stepId,
        string? title,
        string? message) =>
        MatchesStepKey(expectedKey, stepId) ||
        MatchesStepKey(expectedKey, title) ||
        MatchesStepKey(expectedKey, message);

    private static bool MatchesStepKey(string expectedKey, string? value)
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
        return string.Equals(normalized, expectedKey, StringComparison.Ordinal);
    }
}
