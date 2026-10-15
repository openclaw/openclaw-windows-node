using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.Connection.Tests;

public sealed class NativeGatewayPackageClientTests
{
    private static readonly NativeGatewayPackage s_package =
        new("OpenClaw.Gateway_123456789abcd", "2026.9.5.5",
            @"C:\package\openclaw.exe", @"C:\package\clawctl.exe");

    [Fact]
    public async Task DetectAcceptsOnlyVersionedIsolatedContract()
    {
        var client = new NativeGatewayPackageClient((_, args, _) =>
        {
            Assert.Equal(["status", "--json"], args);
            return Task.FromResult(new NativeGatewayCommandResult(0,
                """{"schemaVersion":1,"command":"status","integration":{"kind":"isolated-session","version":1}}"""));
        });
        Assert.Equal(NativeGatewayContract.IsolatedSessionV1,
            await client.DetectAsync(s_package, CancellationToken.None));

        var unsupported = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(0,
                """{"schemaVersion":1,"command":"status","session":{"state":"ready"}}""")));
        await Assert.ThrowsAsync<NativeGatewayContractException>(
            () => unsupported.DetectAsync(s_package, CancellationToken.None));
    }

    [Fact]
    public async Task KnownLegacyProofPackageDoesNotPretendToSupportIsolation()
    {
        var client = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(1, "unsupported --json")));

        Assert.Equal(NativeGatewayContract.Legacy,
            await client.DetectAsync(s_package with { Version = "0.0.0.1" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DetectAsync(s_package, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DetectAsync(s_package with { Version = "0.0.0.2" }, CancellationToken.None));
    }

    [Fact]
    public async Task PrepareReturnsOnlyTheAgentsChosenPortAndToken()
    {
        var client = new NativeGatewayPackageClient((_, args, _) =>
        {
            Assert.Equal(["companion", "prepare", "--port", "19001", "--json"], args);
            return Task.FromResult(new NativeGatewayCommandResult(0,
                """
                {"ok":true,"schemaVersion":1,"command":"companion prepare",
                 "integration":{"kind":"isolated-session","version":1},
                 "companion":{"port":20123,"token":"fixture-token"}}
                """));
        });

        IsolatedGatewayConfiguration config = await client.PrepareAsync(s_package, 19001, CancellationToken.None);

        Assert.Equal(20123, config.Port);
        Assert.Equal("fixture-token", config.Token);
    }

    [Fact]
    public async Task RestorePassesTheSavedTokenOnlyThroughStandardInput()
    {
        var client = new NativeGatewayPackageClient((_, args, input, _) =>
        {
            Assert.Equal(
                ["companion", "prepare", "--port", "19001", "--restore-token-stdin", "--json"],
                args);
            Assert.Equal("saved-companion-token", input);
            return Task.FromResult(new NativeGatewayCommandResult(0,
                """
                {"ok":true,"schemaVersion":1,"command":"companion prepare",
                 "integration":{"kind":"isolated-session","version":1},
                 "companion":{"port":19001,"token":"saved-companion-token"}}
                """));
        });

        IsolatedGatewayConfiguration config = await client.RestoreAsync(
            s_package, 19001, "saved-companion-token", CancellationToken.None);

        Assert.Equal(19001, config.Port);
        Assert.Equal("saved-companion-token", config.Token);
    }

    [Fact(Timeout = 10_000)]
    public async Task CancellationDuringStandardInputTerminatesThePackageProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var start = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("ping -t 127.0.0.1 >nul");
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Cancellation fixture failed to start.");
        using var cancellation = new CancellationTokenSource();

        Task<NativeGatewayCommandResult> pending =
            NativeGatewayPackageClient.CompleteInvocationAsync(
                process,
                new string('x', 1_000_000),
                cancellation.Token);
        Assert.False(pending.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(process.HasExited);
    }

    [Theory]
    [InlineData(1, "stale", "absent", "config-file-missing")]
    [InlineData(0, "running", "startup-eligible", "gateway-mode-local")]
    public async Task StatusReturnsTypedRecoveryFactsEvenWhenThePackageNeedsAttention(
        int exitCode, string sessionState, string readinessState, string readinessReason)
    {
        var client = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(exitCode, JsonSerializer.Serialize(new
            {
                ok = exitCode == 0,
                schemaVersion = 1,
                command = "gateway-service status",
                integration = new { kind = "isolated-session", version = 1 },
                session = new { state = sessionState },
                gateway = new
                {
                    state = "not-started",
                    readiness = new { state = readinessState, reason = readinessReason }
                },
                error = new { message = "fixture attention" }
            }))));

        IsolatedGatewayStatus status = await client.StatusAsync(s_package, CancellationToken.None);

        Assert.Equal(sessionState, status.SessionState);
        Assert.Equal(readinessState, status.ReadinessState);
        Assert.Equal(readinessReason, status.ReadinessReason);
    }

    [Fact]
    public async Task CheckUsesTheReadOnlyPrepareContract()
    {
        var client = new NativeGatewayPackageClient((_, args, _) =>
        {
            Assert.Equal(["companion", "prepare", "--check", "--json"], args);
            return Task.FromResult(new NativeGatewayCommandResult(0,
                """
                {"ok":true,"schemaVersion":1,"command":"companion prepare",
                 "integration":{"kind":"isolated-session","version":1},
                 "companion":{"port":20123,"token":"fixture-token"}}
                """));
        });

        IsolatedGatewayConfiguration config = await client.CheckAsync(s_package, CancellationToken.None);

        Assert.Equal(20123, config.Port);
        Assert.Equal("fixture-token", config.Token);
    }

    [Fact]
    public async Task RunningWithoutOwnershipFailsClosedWithWindowsRecoveryGuidance()
    {
        var client = new NativeGatewayPackageClient((_, args, _) =>
        {
            Assert.Equal(["gateway-service", "status", "--json"], args);
            return Task.FromResult(new NativeGatewayCommandResult(0,
                """
                {"ok":true,"schemaVersion":1,"command":"gateway-service status",
                 "integration":{"kind":"isolated-session","version":1},
                 "gateway":{"state":"running","port":19001}}
                """));
        });

        NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
            () => client.StatusAsync(s_package, CancellationToken.None));

        Assert.Contains("did not send credentials", error.Message, StringComparison.Ordinal);
        Assert.Contains("26100.4770", error.Message, StringComparison.Ordinal);
        Assert.Contains("cannot add this Windows capability", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, """{"ok":true,"schemaVersion":1,"command":"wrong","integration":{"kind":"isolated-session","version":1},"companion":{"port":20123,"token":"fixture-token"}}""")]
    [InlineData(0, """{"ok":true,"schemaVersion":1,"command":"companion prepare","integration":{"kind":"isolated-session","version":1},"companion":{"port":"invalid","token":"fixture-token"}}""")]
    [InlineData(1, """{"ok":false,"schemaVersion":1,"command":"companion prepare","integration":{"kind":"isolated-session","version":1},"error":{"message":"failed"}}""")]
    public async Task CheckRejectsMismatchedMalformedAndFailedResponses(int exitCode, string response)
    {
        var client = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(exitCode, response)));

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.CheckAsync(s_package, CancellationToken.None));
    }

    [Fact]
    public async Task SetupRejectsAnIncompleteAgentSession()
    {
        var client = new NativeGatewayPackageClient((_, args, _) =>
        {
            Assert.Equal(["setup", "--json"], args);
            return Task.FromResult(new NativeGatewayCommandResult(0,
                """{"ok":true,"schemaVersion":1,"command":"setup","integration":{"kind":"isolated-session","version":1},"session":{"state":"not-configured"}}"""));
        });

        await Assert.ThrowsAsync<NativeGatewayContractException>(
            () => client.SetupAsync(s_package, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"ok":true,"schemaVersion":1,"command":"gateway-service status","integration":{"kind":"isolated-session","version":1},"gateway":{"state":"running","port":19001}}""", typeof(NativeGatewayContractException))]
    [InlineData("""{"ok":true,"schemaVersion":1,"command":"gateway-service status","integration":{"kind":"isolated-session","version":1},"gateway":{"state":"running","port":19001,"ownership":{"sandboxId":"iso:fixture","agentUserSid":"S-1-fixture","listeners":[]}}}""", typeof(InvalidOperationException))]
    [InlineData("""{"ok":true,"schemaVersion":1,"command":"gateway-service status","integration":{"kind":"isolated-session","version":1},"gateway":{"state":"running","port":19001,"ownership":{"sandboxId":"iso:fixture","agentUserSid":"S-1-fixture","listeners":[{"port":19001,"processId":4321,"processStartTimeUtc":"2026-09-29T12:00:00Z"}]}}}""", typeof(NativeGatewayContractException))]
    [InlineData("""{"ok":true,"schemaVersion":1,"command":"gateway-service status","integration":{"kind":"isolated-session","version":2},"gateway":{"state":"running","port":19001}}""", typeof(NativeGatewayContractException))]
    public async Task StatusRejectsMissingAttributionOrUnsupportedVersion(
        string response, Type expectedException)
    {
        var client = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(0, response)));

        Exception? error = await Record.ExceptionAsync(
            () => client.StatusAsync(s_package, CancellationToken.None));
        Assert.Equal(expectedException, error?.GetType());
    }
}
