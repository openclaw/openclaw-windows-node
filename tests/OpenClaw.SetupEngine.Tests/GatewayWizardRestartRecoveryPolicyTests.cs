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
