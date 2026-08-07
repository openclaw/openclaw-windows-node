using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.Tests;

public class GatewayWizardRestartRecoveryPolicyTests
{
    [Fact]
    public void Exact2026_7_1TerminalDoneServiceRestart_IsExpected()
    {
        Assert.True(GatewayWizardRestartRecoveryPolicy.IsExpectedTerminalRestart(
            "2026.7.1",
            "done",
            new GatewayConnectionLostException(
                closeStatusCode: 1012,
                closeStatusDescription: "service restart")));
    }

    [Theory]
    [InlineData("2026.7.0", "done", 1012)]
    [InlineData("2026.7.2", "done", 1012)]
    [InlineData("2026.7.1", "model", 1012)]
    [InlineData("2026.7.1", "done", 1001)]
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

    [Theory]
    [InlineData("connection lost")]
    [InlineData("The service restart closed the request")]
    [InlineData("Gateway restarting")]
    public void GenericDisconnectText_IsNotExpectedTerminalRestart(string message)
    {
        Assert.False(GatewayWizardRestartRecoveryPolicy.IsExpectedTerminalRestart(
            "2026.7.1",
            "done",
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

    private static GatewayEndpointProvenance Provenance(
        GatewayEndpointProvenanceKind kind) =>
        new(kind, 18789);
}
