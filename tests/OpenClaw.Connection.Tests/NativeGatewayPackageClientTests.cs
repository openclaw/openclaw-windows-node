using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Tests;

public sealed class NativeGatewayPackageClientTests
{
    private static readonly NativeGatewayPackage s_package =
        new("OpenClaw.Gateway_123456789abcd", "2026.9.5.5",
            @"C:\package\openclaw.exe", @"C:\package\clawctl.exe");

    [Theory]
    [InlineData("starting", 0)]
    [InlineData("unknown", 1)]
    [InlineData("stopped", 0)]
    [InlineData("unhealthy", 1)]
    public async Task StatusPreservesReceiptBeforeListenerReadinessAndOnFailedInspection(string state, int exitCode)
    {
        var client = new NativeGatewayPackageClient((_, _, _) => Task.FromResult(
            new NativeGatewayCommandResult(exitCode, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, command = "gateway-service status", ok = exitCode == 0,
                integration = new { kind = "isolated-session", version = 1, gatewayLaunch = new { version = 1 } },
                gateway = new { state, launch = new { version = 1, attemptId = "11111111111111111111111111111111" } }
            }))));
        var status = await client.StatusAsync(s_package, default);
        Assert.True(status.SupportsLaunchReceipts);
        Assert.Equal("11111111111111111111111111111111", status.Launch!.AttemptId);
        Assert.Empty(status.Listeners);
        Assert.Null(status.Port);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"version\":\"1\"}")]
    public async Task MalformedAdvertisedExtensionCannotDowngradeToLegacy(string advertisement)
    {
        var client = new NativeGatewayPackageClient((_, _, _) => Task.FromResult(
            new NativeGatewayCommandResult(0,
                """{"schemaVersion":1,"command":"gateway-service status","integration":{"kind":"isolated-session","version":1,"gatewayLaunch":ADVERTISEMENT},"gateway":{"state":"stopped"}}"""
                    .Replace("ADVERTISEMENT", advertisement, StringComparison.Ordinal))));
        await Assert.ThrowsAsync<NativeGatewayContractException>(() => client.StatusAsync(s_package, default));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"attemptId\":\"11111111111111111111111111111111\",\"disposition\":\"created\"}")]
    [InlineData("{\"version\":1,\"attemptId\":\"00000000000000000000000000000000\",\"disposition\":\"created\"}")]
    [InlineData("{\"version\":1,\"attemptId\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"disposition\":\"created\"}")]
    [InlineData("{\"version\":1,\"attemptId\":\"22222222222222222222222222222222\",\"disposition\":\"created\"}")]
    [InlineData("{\"version\":1,\"attemptId\":\"11111111111111111111111111111111\",\"disposition\":\"unknown\"}")]
    public async Task ReceiptStartRejectsMissingMalformedAndMismatchedCreatedReceipt(string receipt)
    {
        var root = JsonNode.Parse(
            """{"schemaVersion":1,"command":"gateway-service start","ok":true,"integration":{"kind":"isolated-session","version":1,"gatewayLaunch":{"version":1}},"gateway":{}}""")!;
        if (receipt != "missing") root["gateway"]!["launch"] = JsonNode.Parse(receipt);
        var client = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(0, root.ToJsonString())));
        await Assert.ThrowsAsync<NativeGatewayContractException>(() =>
            client.StartAsync(s_package, "11111111111111111111111111111111", default));
    }

    [Fact]
    public async Task CapabilityAbsentStatusRemainsShippingV1WithoutReceipt()
    {
        var client = new NativeGatewayPackageClient((_, _, _) => Task.FromResult(new NativeGatewayCommandResult(0,
            """{"schemaVersion":1,"command":"gateway-service status","integration":{"kind":"isolated-session","version":1},"gateway":{"state":"stopped"}}""")));
        var status = await client.StatusAsync(s_package, default);
        Assert.False(status.SupportsLaunchReceipts);
        Assert.Null(status.Launch);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task FailedLifecycleLogsSanitizedStderrButKeepsItOutOfExceptionMessages(string operation)
    {
        var log = new RecordingLogger();
        var client = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(3, "not json",
                "fatal: gateway listener unavailable\nAuthorization: Bearer abcdefghijklmnop0123456789")),
            log);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation switch
        {
            "start" => client.StartAsync(s_package, "11111111111111111111111111111111", CancellationToken.None),
            "stop" => client.StopAsync(s_package, "11111111111111111111111111111111", CancellationToken.None),
            _ => client.StatusAsync(s_package, CancellationToken.None)
        });

        Assert.Contains("exit code 3", error.Message);
        Assert.DoesNotContain("fatal", error.Message);
        Assert.Contains(log.Warnings,
            warning => warning.Contains($"clawctl gateway-service {operation}") &&
                warning.Contains("stderr: fatal: gateway listener unavailable"));
        Assert.DoesNotContain(log.Warnings, warning => warning.Contains("abcdefghijklmnop0123456789"));
    }

    private sealed class RecordingLogger : IOpenClawLogger
    {
        public List<string> Warnings { get; } = [];
        public List<string> Debugs { get; } = [];
        public void Info(string message) { }
        public void Debug(string message) => Debugs.Add(message);
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) { }
    }

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

    [Theory]
    [InlineData("""{"schemaVersion":2,"command":"status","integration":{"kind":"isolated-session","version":1}}""")]
    [InlineData("""{"schemaVersion":1,"command":"other","integration":{"kind":"isolated-session","version":1}}""")]
    [InlineData("""{"schemaVersion":1,"command":"status","integration":{"kind":"isolated-session","version":1,"gatewayLaunch":{"version":2}}}""")]
    [InlineData("""{"schemaVersion":1,"command":"status","integration":{"kind":"isolated-session","version":1,"gatewayLaunch":7}}""")]
    public async Task DetectTreatsAnyInitialEnvelopeProblemAsAMissingContract(string json)
    {
        var client = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(0, json)));

        NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
            () => client.DetectAsync(s_package, CancellationToken.None));

        Assert.Contains("does not expose Companion's isolated-session contract", error.Message, StringComparison.Ordinal);
        Assert.Contains("Update the Gateway MSIX", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"sk-secret-1234\"")]
    public async Task DetectTreatsAResponseThatIsNotAnObjectAsAMissingContract(string json)
    {
        var client = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(0, json)));

        NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
            () => client.DetectAsync(s_package, CancellationToken.None));

        Assert.Contains("Update the Gateway MSIX", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, error.Message, StringComparison.Ordinal);
        Assert.Equal(NativeGatewayContract.Legacy,
            await client.DetectAsync(s_package with { Version = "0.0.0.1" }, CancellationToken.None));
    }

    [Fact]
    public async Task DetectTreatsSuccessOutputThatIsNotJsonAsAMissingContract()
    {
        var client = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(0, "not json")));

        NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
            () => client.DetectAsync(s_package, CancellationToken.None));

        Assert.Contains("Update the Gateway MSIX", error.Message, StringComparison.Ordinal);
        Assert.Equal(NativeGatewayContract.Legacy,
            await client.DetectAsync(s_package with { Version = "0.0.0.1" }, CancellationToken.None));
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

        NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
            () => client.SetupAsync(s_package, CancellationToken.None));

        Assert.Contains("no ready isolated session", error.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task CollectLogsReturnsPackageReportedBundle()
    {
        var client = new NativeGatewayPackageClient((_, args, _) =>
        {
            Assert.Equal(["collect-logs", "--json"], args);
            return Task.FromResult(new NativeGatewayCommandResult(0,
                """
                {"ok":true,"schemaVersion":1,"command":"collect-logs",
                 "integration":{"kind":"isolated-session","version":1},
                 "bundle":{"path":"C:\\pkg\\state\\openclaw-diagnostics-x.zip","included":"host-only",
                           "notes":["agent/x: not collected"]}}
                """));
        });

        NativeGatewayDiagnosticsBundle bundle = await client.CollectLogsAsync(s_package, CancellationToken.None);

        Assert.Equal(@"C:\pkg\state\openclaw-diagnostics-x.zip", bundle.Path);
        Assert.Equal("host-only", bundle.Included);
        Assert.Equal(["agent/x: not collected"], bundle.Notes);
    }

    [Theory]
    [InlineData(1, """{"ok":false,"schemaVersion":1,"command":"collect-logs","integration":{"kind":"isolated-session","version":1},"error":{"type":"cli_error","message":"fixture failure"}}""", typeof(InvalidOperationException))]
    [InlineData(0, """{"ok":true,"schemaVersion":1,"command":"status","integration":{"kind":"isolated-session","version":1},"bundle":{"path":"C:\\x.zip","included":"host-only","notes":[]}}""", typeof(NativeGatewayContractException))]
    public async Task CollectLogsRejectsFailureAndForeignCommand(int exitCode, string response, Type expectedException)
    {
        var client = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(exitCode, response)));

        Exception? error = await Record.ExceptionAsync(
            () => client.CollectLogsAsync(s_package, CancellationToken.None));
        Assert.Equal(expectedException, error?.GetType());
        if (exitCode != 0)
            Assert.Contains("fixture failure", error!.Message, StringComparison.Ordinal);
    }

    private const string Attempt = "11111111111111111111111111111111";
    private const string SecretValue = "sk-secret-1234";

    // One valid response per operation that decodes a package response after the initial probe.
    private static readonly Dictionary<string, string> s_validResponses = new()
    {
        ["companion prepare"] = """{"ok":true,"schemaVersion":1,"command":"companion prepare","integration":{"kind":"isolated-session","version":1},"companion":{"port":20123,"token":"fixture-token"}}""",
        ["setup"] = """{"ok":true,"schemaVersion":1,"command":"setup","integration":{"kind":"isolated-session","version":1},"session":{"state":"ready","sandboxId":"iso:fixture"}}""",
        ["gateway-service status"] = """{"ok":true,"schemaVersion":1,"command":"gateway-service status","integration":{"kind":"isolated-session","version":1},"gateway":{"state":"running","port":19001,"ownership":{"sandboxId":"iso:fixture","agentUserSid":"S-1-fixture","listeners":[{"port":19001,"processId":4321,"processStartTimeUtc":"2026-09-29T12:00:00Z","sequenceNumber":77}]}}}""",
        ["gateway-service start"] = """{"ok":true,"schemaVersion":1,"command":"gateway-service start","integration":{"kind":"isolated-session","version":1,"gatewayLaunch":{"version":1}},"gateway":{"state":"starting","launch":{"version":1,"attemptId":"11111111111111111111111111111111","disposition":"created"}}}""",
        ["gateway-service stop"] = """{"ok":true,"schemaVersion":1,"command":"gateway-service stop","integration":{"kind":"isolated-session","version":1,"gatewayLaunch":{"version":1}},"gateway":{"state":"stopped","launch":{"version":1,"attemptId":"11111111111111111111111111111111","stopOutcome":"stopped"}}}""",
        ["collect-logs"] = """{"ok":true,"schemaVersion":1,"command":"collect-logs","integration":{"kind":"isolated-session","version":1},"bundle":{"path":"C:\\pkg\\x.zip","included":"host-only","notes":["agent/x: not collected"]}}"""
    };

    // Values a response or its stderr can carry that a user-visible message must never repeat.
    private static readonly string[] s_neverRepeated =
        [SecretValue, "fixture-token", @"C:\pkg", @"C:\\pkg", "S-1-fixture", "iso:fixture", @"C:\Users\private"];

    public static TheoryData<string> Operations => new(s_validResponses.Keys);

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task EveryOperationAcceptsItsValidResponse(string operation)
    {
        await InvokeOperationAsync(ClientReturning(s_validResponses[operation]), operation);
    }

    [Theory]
    [InlineData("companion prepare", "companion", null, "'companion' is missing")]
    [InlineData("companion prepare", "companion.port", null, "'companion.port' is missing")]
    [InlineData("companion prepare", "companion.port", "\"sk-secret-1234\"", "'companion.port' must be a 32-bit integer, found string")]
    [InlineData("companion prepare", "companion.token", "{\"value\":\"sk-secret-1234\"}", "'companion.token' must be a string, found object")]
    [InlineData("setup", "session.sandboxId", null, "'session.sandboxId' is missing")]
    [InlineData("setup", "session.state", "{}", "'session.state' must be a string, found object")]
    [InlineData("gateway-service status", "gateway.port", null, "'gateway.port' is missing")]
    [InlineData("gateway-service status", "gateway.ownership.agentUserSid", "7", "'gateway.ownership.agentUserSid' must be a string, found number")]
    [InlineData("gateway-service status", "gateway.ownership.listeners.0.sequenceNumber", null, "'gateway.ownership.listeners[].sequenceNumber' is missing")]
    [InlineData("gateway-service start", "integration.gatewayLaunch", null, "'integration.gatewayLaunch' is missing")]
    [InlineData("gateway-service start", "gateway.launch", null, "'gateway.launch' is missing")]
    [InlineData("gateway-service start", "gateway.launch.attemptId", null, "'gateway.launch.attemptId' is missing")]
    [InlineData("gateway-service start", "gateway.launch.disposition", "\"sk-secret-1234\"", "'gateway.launch.disposition' has an unsupported value")]
    [InlineData("gateway-service stop", "gateway.launch.attemptId", "\"sk-secret-1234\"", "'gateway.launch.attemptId' does not match the requested attempt")]
    [InlineData("gateway-service stop", "gateway.launch.stopOutcome", null, "'gateway.launch.stopOutcome' is missing")]
    [InlineData("collect-logs", "bundle.included", null, "'bundle.included' is missing")]
    [InlineData("collect-logs", "bundle.path", "7", "'bundle.path' must be a string or null, found number")]
    [InlineData("collect-logs", "bundle.notes", "\"sk-secret-1234\"", "'bundle.notes' must be an array, found string")]
    [InlineData("collect-logs", "bundle.notes", "[7]", "'bundle.notes[]' must be a string, found number")]
    [InlineData("gateway-service status", "gateway.state", null, "'gateway.state' is missing")]
    [InlineData("gateway-service status", "gateway.state", "7", "'gateway.state' must be a string, found number")]
    [InlineData("gateway-service status", "session", "{}", "'session.state' is missing")]
    [InlineData("gateway-service status", "gateway.readiness", "\"sk-secret-1234\"", "'gateway.readiness' must be an object, found string")]
    [InlineData("gateway-service status", "gateway.readiness", "{}", "'gateway.readiness.state' is missing")]
    [InlineData("gateway-service status", "gateway.readiness", "{\"state\":\"absent\",\"reason\":7}", "'gateway.readiness.reason' must be a string, found number")]
    [InlineData("gateway-service status", "gateway.ownership.listeners", null, "'gateway.ownership.listeners' is missing")]
    [InlineData("gateway-service status", "gateway.ownership.listeners", "\"sk-secret-1234\"", "'gateway.ownership.listeners' must be an array, found string")]
    [InlineData("gateway-service status", "gateway.ownership.listeners", "[42]", "'gateway.ownership.listeners[]' must be an object, found number")]
    [InlineData("gateway-service status", "gateway.ownership.listeners.0.sequenceNumber", "\"sk-secret-1234\"", "'gateway.ownership.listeners[].sequenceNumber' must be an unsigned 64-bit integer, found string")]
    [InlineData("gateway-service start", "gateway.launch.attemptId", "\"sk-secret-1234\"", "'gateway.launch.attemptId' is not a valid attempt identifier")]
    [InlineData("gateway-service start", "gateway.launch.attemptId", "\"22222222222222222222222222222222\"", "'gateway.launch.attemptId' does not match the requested attempt")]
    [InlineData("gateway-service start", "gateway.launch.version", "2", "'gateway.launch.version' has an unsupported value (expected 1)")]
    [InlineData("gateway-service start", "gateway.launch.disposition", null, "'gateway.launch.disposition' is missing")]
    [InlineData("gateway-service status", "gateway.launch", "{\"version\":1,\"attemptId\":\"11111111111111111111111111111111\"}", "'gateway.launch' is present but integration.gatewayLaunch is not advertised")]
    public async Task InvalidResponseNamesTheOperationAndTheRejectedField(
        string operation, string jsonPath, string? replacement, string expectedProblem)
    {
        var invocations = new List<string>();
        NativeGatewayPackageClient client = ClientReturning(
            Mutate(s_validResponses[operation], jsonPath, replacement), invocations);

        NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
            () => InvokeOperationAsync(client, operation));

        Assert.Contains($"'clawctl {operation}'", error.Message, StringComparison.Ordinal);
        Assert.Contains(expectedProblem, error.Message, StringComparison.Ordinal);
        Assert.Contains("Credentials were not sent", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not expose", error.Message, StringComparison.Ordinal);
        AssertNothingObservedIsRepeated(error.Message);
        Assert.Single(invocations);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task EnvelopeMismatchNamesTheOperationAndTheEnvelopeField(string operation)
    {
        foreach ((string path, string replacement, string expectedProblem) in new[]
        {
            ("schemaVersion", "2", "'schemaVersion' has an unsupported value (expected 1)"),
            ("command", $"\"{SecretValue}\"", $"'command' is not '{operation}'")
        })
        {
            NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
                () => InvokeOperationAsync(
                    ClientReturning(Mutate(s_validResponses[operation], path, replacement)), operation));

            Assert.Contains($"'clawctl {operation}'", error.Message, StringComparison.Ordinal);
            Assert.Contains(expectedProblem, error.Message, StringComparison.Ordinal);
            AssertNothingObservedIsRepeated(error.Message);
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task AnotherIntegrationKindOrVersionStillAsksForAnUpdatedGatewayPackage(string operation)
    {
        foreach ((string path, string replacement) in new[]
        {
            ("integration.version", "2"),
            ("integration.kind", "\"other\"")
        })
        {
            NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
                () => InvokeOperationAsync(
                    ClientReturning(Mutate(s_validResponses[operation], path, replacement)), operation));

            Assert.Contains("does not expose Companion's isolated-session contract", error.Message, StringComparison.Ordinal);
            Assert.Contains("Update the Gateway MSIX", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task AbsentOrMalformedIntegrationMarkerNamesTheOperationAndTheField(string operation)
    {
        foreach ((string path, string? replacement, string expectedProblem) in new (string, string?, string)[]
        {
            ("integration", null, "'integration' is missing"),
            ("integration", "\"sk-secret-1234\"", "'integration' must be an object, found string"),
            ("integration.kind", null, "'integration.kind' is missing"),
            ("integration.kind", "7", "'integration.kind' must be a string, found number"),
            ("integration.version", null, "'integration.version' is missing"),
            ("integration.version", "\"sk-secret-1234\"", "'integration.version' must be a 32-bit integer, found string")
        })
        {
            NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
                () => InvokeOperationAsync(
                    ClientReturning(Mutate(s_validResponses[operation], path, replacement)), operation));

            Assert.Contains($"'clawctl {operation}'", error.Message, StringComparison.Ordinal);
            Assert.Contains(expectedProblem, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("does not expose", error.Message, StringComparison.Ordinal);
            AssertNothingObservedIsRepeated(error.Message);
        }
    }

    [Theory]
    [InlineData("[]", "array")]
    [InlineData("null", "null")]
    [InlineData("42", "number")]
    [InlineData("\"sk-secret-1234\"", "string")]
    public async Task ResponseThatIsNotAnObjectNamesTheOperationAndTheJsonKind(string json, string kind)
    {
        foreach (string operation in s_validResponses.Keys)
        {
            var invocations = new List<string>();

            NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
                () => InvokeOperationAsync(ClientReturning(json, invocations), operation));

            Assert.Contains($"'clawctl {operation}'", error.Message, StringComparison.Ordinal);
            Assert.Contains($"'response' must be a JSON object, found {kind}", error.Message, StringComparison.Ordinal);
            AssertNothingObservedIsRepeated(error.Message);
            Assert.Single(invocations);
        }
    }

    [Fact]
    public async Task NonJsonSuccessOutputNamesTheOperationWithoutRepeatingIt()
    {
        var client = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(0, $"{SecretValue} is not json")));

        NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
            () => client.StatusAsync(s_package, CancellationToken.None));

        Assert.Contains("'clawctl gateway-service status'", error.Message, StringComparison.Ordinal);
        Assert.Contains("the output is not valid JSON", error.Message, StringComparison.Ordinal);
        Assert.Contains("Credentials were not sent", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not expose", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAndRestoreNameTheOperationOfAnInvalidResponse()
    {
        string invalid = Mutate(s_validResponses["companion prepare"], "companion.port", null);

        foreach (Func<NativeGatewayPackageClient, Task> call in new Func<NativeGatewayPackageClient, Task>[]
        {
            client => client.PrepareAsync(s_package, 20123, CancellationToken.None),
            client => client.RestoreAsync(s_package, 20123, "fixture-token", CancellationToken.None)
        })
        {
            NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
                () => call(ClientReturning(invalid)));

            Assert.Contains("'clawctl companion prepare'", error.Message, StringComparison.Ordinal);
            Assert.Contains("'companion.port' is missing", error.Message, StringComparison.Ordinal);
            AssertNothingObservedIsRepeated(error.Message);
        }
    }

    [Theory]
    [InlineData("start", "stop")]
    [InlineData("stop", "start")]
    public async Task LegacyLifecycleCallsNameTheOperationOfAMismatchedResponse(string operation, string answered)
    {
        NativeGatewayPackageClient client = ClientReturning(s_validResponses[$"gateway-service {answered}"]);

        NativeGatewayContractException error = await Assert.ThrowsAsync<NativeGatewayContractException>(
            () => operation == "start"
                ? client.StartAsync(s_package, CancellationToken.None)
                : client.StopAsync(s_package, CancellationToken.None));

        Assert.Contains($"'clawctl gateway-service {operation}'", error.Message, StringComparison.Ordinal);
        Assert.Contains($"'command' is not 'gateway-service {operation}'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public async Task CollectLogsAcceptsAnAbsentOrNullBundlePath(string? replacement)
    {
        NativeGatewayDiagnosticsBundle bundle = await ClientReturning(
            Mutate(s_validResponses["collect-logs"], "bundle.path", replacement))
            .CollectLogsAsync(s_package, CancellationToken.None);

        Assert.Null(bundle.Path);
        Assert.Equal("host-only", bundle.Included);
    }

    private static NativeGatewayPackageClient ClientReturning(string json, List<string>? invocations = null) =>
        new((_, args, _) =>
        {
            invocations?.Add(string.Join(' ', args));
            return Task.FromResult(new NativeGatewayCommandResult(
                0, json, @$"failed at C:\Users\private with token {SecretValue}"));
        });

    private static Task InvokeOperationAsync(NativeGatewayPackageClient client, string operation) =>
        operation switch
        {
            "companion prepare" => client.CheckAsync(s_package, CancellationToken.None),
            "setup" => client.SetupAsync(s_package, CancellationToken.None),
            "gateway-service status" => client.StatusAsync(s_package, CancellationToken.None),
            "gateway-service start" => client.StartAsync(s_package, Attempt, CancellationToken.None),
            "gateway-service stop" => client.StopAsync(s_package, Attempt, CancellationToken.None),
            "collect-logs" => client.CollectLogsAsync(s_package, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

    // Dotted path into the response; numeric segments index arrays. A null replacement removes the property.
    private static string Mutate(string json, string path, string? replacement)
    {
        JsonNode root = JsonNode.Parse(json)!;
        JsonNode parent = root;
        string[] segments = path.Split('.');
        foreach (string segment in segments[..^1])
            parent = int.TryParse(segment, out int index) ? parent[index]! : parent[segment]!;
        if (replacement is null)
            ((JsonObject)parent).Remove(segments[^1]);
        else
            parent[segments[^1]] = JsonNode.Parse(replacement);
        return root.ToJsonString();
    }

    private static void AssertNothingObservedIsRepeated(string message)
    {
        foreach (string observed in s_neverRepeated)
            Assert.DoesNotContain(observed, message, StringComparison.Ordinal);
    }
}
