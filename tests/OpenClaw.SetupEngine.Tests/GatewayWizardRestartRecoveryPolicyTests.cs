using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.Tests;

public class GatewayWizardRestartRecoveryPolicyTests
{
    [Fact]
    public void Exact2026_7_1TerminalModelCheckServiceRestart_IsExpected()
    {
        Assert.True(GatewayWizardRestartRecoveryPolicy.IsExpectedTerminalRestart(
            "2026.7.1",
            "opaque-step-id",
            new GatewayConnectionLostException(
                closeStatusCode: 1012,
                closeStatusDescription: "service restart"),
            title: "Model check"));
    }

    [Theory]
    [InlineData("2026.7.1", "done", 1012)]
    [InlineData("2026.7.0", "model-check", 1012)]
    [InlineData("2026.7.2", "model-check", 1012)]
    [InlineData("2026.7.1", "model-check-in-progress", 1012)]
    [InlineData("2026.7.1", "model-check", 1001)]
    public void OtherVersionsStepsAndFailures_AreNotExpected(
        string version,
        string stepId,
        int closeStatusCode)
    {
        Assert.False(GatewayWizardRestartRecoveryPolicy.IsExpectedTerminalRestart(
            version,
            stepId,
            new GatewayConnectionLostException(
                closeStatusCode,
                "test close")));
    }

    [Fact]
    public void Exact2026_7_1TerminalModelCheckMessage_IsExpectedCandidate()
    {
        Assert.True(GatewayWizardRestartRecoveryPolicy.IsTerminalRestartCandidate(
            "2026.7.1",
            "opaque-step-id",
            message: "Model_Check"));
    }

    [Theory]
    [InlineData("connection lost")]
    [InlineData("The service restart closed the request")]
    [InlineData("Gateway restarting")]
    public void GenericDisconnectText_IsNotExpectedTerminalRestart(string message)
    {
        Assert.False(GatewayWizardRestartRecoveryPolicy.IsExpectedTerminalRestart(
            "2026.7.1",
            "model-check",
            new OperationCanceledException(message)));
    }

    [Theory]
    [InlineData("connection lost")]
    [InlineData("The service restart closed the request")]
    [InlineData("Gateway restarting")]
    [InlineData("wizard.next unavailable during gateway restart")]
    public void GenericDisconnectText_RemainsEligibleForWizardReplay(string message)
    {
        Assert.True(GatewayWizardRestartRecoveryPolicy.IsRestartLikeDisconnect(
            new OperationCanceledException(message)));
    }

    [Theory]
    [InlineData(1013, true)]
    [InlineData(1012, false)]
    [InlineData(1000, false)]
    [InlineData(null, false)]
    public void RetryableGatewayStartupDisconnect_IsNarrow(
        int? closeStatusCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            GatewayWizardRestartRecoveryPolicy.IsRetryableGatewayStartupDisconnect(
                closeStatusCode));
    }

    [Theory]
    [InlineData("done")]
    [InlineData("Done")]
    [InlineData("  DONE  ")]
    public void AuthoritativeFinalWizardStep_MatchesNormalizedDoneNote(string title)
    {
        Assert.True(GatewayWizardRestartRecoveryPolicy.IsAuthoritativeFinalWizardStep(
            "note",
            "5c1c6f22-2f4f-4b0a-9d0e-2c0a6f1a7c31",
            title,
            hasOptions: false,
            stepIndex: 41,
            totalSteps: 42));
    }

    [Theory]
    [InlineData(41, 42)]
    [InlineData(42, 42)]
    public void AuthoritativeFinalWizardStep_AcceptsZeroAndOneBasedFinalPosition(
        int stepIndex,
        int totalSteps)
    {
        Assert.True(GatewayWizardRestartRecoveryPolicy.IsAuthoritativeFinalWizardStep(
            "note",
            "5c1c6f22-2f4f-4b0a-9d0e-2c0a6f1a7c31",
            "Done",
            hasOptions: false,
            stepIndex,
            totalSteps));
    }

    [Fact]
    public void AuthoritativeFinalWizardStep_AcceptsAbsentPositionMetadata()
    {
        Assert.True(GatewayWizardRestartRecoveryPolicy.IsAuthoritativeFinalWizardStep(
            "note",
            stepId: "done",
            title: "",
            hasOptions: false,
            stepIndex: 0,
            totalSteps: 0));
    }

    [Theory]
    // Not the final key.
    [InlineData("note", "step-7", "Almost done", false, 0, 0)]
    [InlineData("note", "done-later", "All done", false, 0, 0)]
    [InlineData("note", "model-check", "Model check", false, 0, 0)]
    // Answerable or option-bearing steps are never the terminal acknowledgement.
    [InlineData("text", "done", "Done", false, 0, 0)]
    [InlineData("confirm", "done", "Done", false, 0, 0)]
    [InlineData("select", "done", "Done", true, 0, 0)]
    [InlineData("progress", "done", "Done", false, 0, 0)]
    [InlineData("note", "done", "Done", true, 0, 0)]
    // Position metadata says the step is not last, or is impossible.
    [InlineData("note", "done", "Done", false, 5, 42)]
    [InlineData("note", "done", "Done", false, 43, 42)]
    public void AuthoritativeFinalWizardStep_RejectsEverythingElse(
        string stepType,
        string stepId,
        string title,
        bool hasOptions,
        int stepIndex,
        int totalSteps)
    {
        Assert.False(GatewayWizardRestartRecoveryPolicy.IsAuthoritativeFinalWizardStep(
            stepType,
            stepId,
            title,
            hasOptions,
            stepIndex,
            totalSteps));
    }

    [Fact]
    public void AuthoritativeFinalWizardStep_IgnoresStepMessageText()
    {
        Assert.False(GatewayWizardRestartRecoveryPolicy.IsAuthoritativeFinalWizardStep(
            "note",
            stepId: "security-disclaimer",
            title: "Security disclaimer",
            hasOptions: false,
            stepIndex: 0,
            totalSteps: 0));
    }

    [Fact]
    public void HostedWizardTermination_AfterFinalStep_IsAccepted()
    {
        Assert.True(
            GatewayWizardRestartRecoveryPolicy.IsHostedWizardTerminationAfterFinalStep(
                payloadIsTerminal: true,
                "Error: TUI exited from signal SIGTERM",
                answeredAuthoritativeFinalStep: true));
    }

    [Fact]
    public void HostedWizardTermination_ToleratesOnlySurroundingWhitespace()
    {
        Assert.True(
            GatewayWizardRestartRecoveryPolicy.IsHostedWizardTerminationAfterFinalStep(
                payloadIsTerminal: true,
                "  Error: TUI exited from signal SIGTERM\n",
                answeredAuthoritativeFinalStep: true));
    }

    [Theory]
    // Early SIGTERM: the final step was never answered.
    [InlineData(true, "Error: TUI exited from signal SIGTERM", false)]
    // Non-terminal payload never completes the wizard.
    [InlineData(false, "Error: TUI exited from signal SIGTERM", true)]
    // Inexact SIGTERM-like errors stay failures.
    [InlineData(true, "Error: TUI exited from signal SIGKILL", true)]
    [InlineData(true, "TUI exited from signal SIGTERM", true)]
    [InlineData(true, "Error: TUI exited from signal SIGTERM (worker crashed)", true)]
    [InlineData(true, "error: tui exited from signal sigterm", true)]
    [InlineData(true, "Error: TUI exited from signal SIGTERM\nModel check failed", true)]
    [InlineData(true, "PROTOCOL_MISMATCH", true)]
    [InlineData(true, "", true)]
    [InlineData(true, null, true)]
    public void HostedWizardTermination_RejectsEverythingElse(
        bool payloadIsTerminal,
        string? error,
        bool answeredAuthoritativeFinalStep)
    {
        Assert.False(
            GatewayWizardRestartRecoveryPolicy.IsHostedWizardTerminationAfterFinalStep(
                payloadIsTerminal,
                error,
                answeredAuthoritativeFinalStep));
    }

    [Fact]
    public async Task WaitForExpectedManagedGateway_NoListenerThenExpected_Retries()
    {
        var attempts = 0;

        var result =
            await GatewayWizardRestartRecoveryPolicy.WaitForExpectedManagedGatewayAsync(
                _ => Task.FromResult(
                    ++attempts < 3
                        ? Provenance(GatewayEndpointProvenanceKind.NoListener)
                        : Provenance(GatewayEndpointProvenanceKind.ExpectedManagedGateway)),
                noListenerRetryCount: 2,
                retryDelay: TimeSpan.Zero,
                CancellationToken.None);

        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, result.Kind);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task WaitForExpectedManagedGateway_NoListenerExhaustsRetryBound()
    {
        var attempts = 0;

        var result =
            await GatewayWizardRestartRecoveryPolicy.WaitForExpectedManagedGatewayAsync(
                _ =>
                {
                    attempts++;
                    return Task.FromResult(
                        Provenance(GatewayEndpointProvenanceKind.NoListener));
                },
                noListenerRetryCount: 2,
                retryDelay: TimeSpan.Zero,
                CancellationToken.None);

        Assert.Equal(GatewayEndpointProvenanceKind.NoListener, result.Kind);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task WaitForExpectedManagedGateway_UnknownListenerFailsClosedWithoutRetry()
    {
        var attempts = 0;

        var result =
            await GatewayWizardRestartRecoveryPolicy.WaitForExpectedManagedGatewayAsync(
                _ =>
                {
                    attempts++;
                    return Task.FromResult(
                        Provenance(GatewayEndpointProvenanceKind.UnknownListener));
                },
                noListenerRetryCount: 30,
                retryDelay: TimeSpan.Zero,
                CancellationToken.None);

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WaitForExpectedManagedGateway_SnapshotChangeThenExpected_Retries()
    {
        var attempts = 0;

        var result =
            await GatewayWizardRestartRecoveryPolicy.WaitForExpectedManagedGatewayAsync(
                _ => Task.FromResult(
                    ++attempts == 1
                        ? Provenance(
                            GatewayEndpointProvenanceKind.UnknownListener,
                            GatewayEndpointProvenanceFailureReason.ListenerSnapshotChanged)
                        : Provenance(GatewayEndpointProvenanceKind.ExpectedManagedGateway)),
                noListenerRetryCount: 1,
                retryDelay: TimeSpan.Zero,
                CancellationToken.None);

        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, result.Kind);
        Assert.Equal(2, attempts);
    }

    private static GatewayEndpointProvenance Provenance(
        GatewayEndpointProvenanceKind kind,
        GatewayEndpointProvenanceFailureReason failureReason =
            GatewayEndpointProvenanceFailureReason.None) =>
        new(kind, 18789, FailureReason: failureReason);
}
