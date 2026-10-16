using OpenClaw.GatewayFixtureHost;
using System.Text.Json;

namespace OpenClaw.Tray.IntegrationTests;

public sealed class GatewayFixtureRunTests
{
    [Theory]
    [InlineData(GatewayFixtureStartupMode.Connected, "Connected", 5, 0, true)]
    [InlineData(GatewayFixtureStartupMode.Connected, "Connected", 0, 0, false)]
    [InlineData(GatewayFixtureStartupMode.Connected, "Disconnected", 5, 0, false)]
    [InlineData(GatewayFixtureStartupMode.McpOnly, "Disconnected", 0, 0, true)]
    [InlineData(GatewayFixtureStartupMode.McpOnly, "Connected", 0, 0, false)]
    [InlineData(GatewayFixtureStartupMode.McpOnly, "Disconnected", 5, 0, false)]
    [InlineData(GatewayFixtureStartupMode.GatewayUnavailable, "Error", 0, 1, true)]
    [InlineData(GatewayFixtureStartupMode.GatewayUnavailable, "Disconnected", 0, 0, false)]
    [InlineData(GatewayFixtureStartupMode.GatewayUnavailable, "Connected", 0, 1, false)]
    public void StartupReadinessMatchesTheDeclaredScenario(GatewayFixtureStartupMode mode, string state,
        int sessions, int rejections, bool expected)
    {
        var status = JsonSerializer.SerializeToElement(new { operatorState = state, sessionCount = sessions });
        Assert.Equal(expected, GatewayFixtureRun.IsReady(mode, status, rejections));
    }

    [Theory]
    [InlineData(true, true, null, "contract-passed")]
    [InlineData(false, true, null, "contract-failed")]
    [InlineData(true, false, null, "contract-failed")]
    [InlineData(true, true, "synthetic assertion failure", "contract-failed")]
    public async Task FinalReportRequiresAssertionsCleanupAndIsolation(bool cleanup, bool isolation, string? error, string outcome)
    {
        using var artifacts = new TempDirectory();
        await File.WriteAllTextAsync(artifacts.Combine("run.json"), """{"outcome":"contract-assertions-passed","RunId":"synthetic"}""");
        await GatewayFixtureRun.FinalizeContractReportAsync(artifacts.Path, cleanup, isolation, error);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(artifacts.Combine("run.json")));
        Assert.Equal(outcome, report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(cleanup, report.RootElement.GetProperty("cleanupSucceeded").GetBoolean());
        Assert.Equal(isolation, report.RootElement.GetProperty("isolationUnchanged").GetBoolean());
        Assert.Equal("synthetic", report.RootElement.GetProperty("RunId").GetString());
    }

    [Fact]
    public async Task MissingRunEvidenceCannotBecomeSuccessfulCloseout()
    {
        using var artifacts = new TempDirectory();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            GatewayFixtureRun.FinalizeContractReportAsync(artifacts.Path, true, true, null));
    }

    private const string Description = "history loaded for agent:main:fixture-long";
    private const string ArtifactsDirectory = @"C:\fixture-artifacts\test-run";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WaitForAsync_SlowConditionPreservesTimeoutContext()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                WaitAsync(() => pending.Task, timeout: TimeSpan.FromSeconds(1)));

            AssertTimeoutContext(error);
            Assert.IsType<TimeoutException>(error.InnerException);
        }
        finally
        {
            pending.TrySetResult(false);
        }
    }

    [Fact]
    public async Task WaitForAsync_ConditionTimeoutPreservesOriginalExceptionAndContext()
    {
        var original = new TimeoutException("Synthetic MCP timeout.");
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            WaitAsync(() => Task.FromException<bool>(original)));

        AssertTimeoutContext(error);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public async Task WaitForAsync_ExpiredDeadlinePreservesTimeoutContext()
    {
        var invoked = false;
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            WaitAsync(() =>
            {
                invoked = true;
                return Task.FromResult(true);
            }, timeout: TimeSpan.Zero));

        AssertTimeoutContext(error);
        Assert.False(invoked);
    }

    [Fact]
    public async Task WaitForAsync_RetriesFalseConditionAndChecksAppBeforeEachProbe()
    {
        var probes = 0;
        var checks = 0;
        await WaitAsync(() =>
        {
            Assert.Equal(probes + 1, checks);
            return Task.FromResult(++probes == 2);
        }, ensureRunning: () => checks++);

        Assert.Equal(2, probes);
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task WaitForAsync_AppFailurePropagatesBeforeCondition()
    {
        var original = new InvalidOperationException("Synthetic app exit.");
        var invoked = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitAsync(() =>
            {
                invoked = true;
                return Task.FromResult(true);
            }, ensureRunning: () => throw original));

        Assert.Same(original, error);
        Assert.False(invoked);
    }

    [Fact]
    public async Task WaitForAsync_ConditionFailureIsNotConvertedToTimeout()
    {
        var original = new InvalidDataException("Synthetic invalid MCP response.");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            WaitAsync(() => Task.FromException<bool>(original)));

        Assert.Same(original, error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WaitForAsync_CancellationIsNotConvertedToTimeout(bool cancelBeforeProbe)
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = false;
        if (cancelBeforeProbe)
            cancellation.Cancel();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                WaitAsync(() =>
                {
                    invoked = true;
                    cancellation.Cancel();
                    return pending.Task;
                }, cancellationToken: cancellation.Token));

            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(!cancelBeforeProbe, invoked);
        }
        finally
        {
            pending.TrySetResult(false);
        }
    }

    private static Task WaitAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        Action? ensureRunning = null,
        CancellationToken cancellationToken = default) =>
        GatewayFixtureRun.WaitForConditionAsync(
            condition, Description, ArtifactsDirectory, ensureRunning ?? (() => { }),
            timeout ?? Deadline, cancellationToken).WaitAsync(Deadline);

    private static void AssertTimeoutContext(TimeoutException error) =>
        Assert.Equal($"Timed out waiting for {Description}. Artifacts: {ArtifactsDirectory}", error.Message);
}
