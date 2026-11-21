using System.Globalization;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.SetupEngine.UI;

namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class NativeGatewayCliTimeoutTests
{
    [Fact]
    public void Timeout_DefaultCoversObservedSlowPackagedCli()
    {
        var previous = Environment.GetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable, null);
            // An isolated-session `openclaw config validate --json` was observed at ~131 s on ARM64.
            Assert.True(NativeGatewayCliTimeout.ResolveTimeout() > TimeSpan.FromSeconds(131));
            Assert.Equal(NativeGatewayCliTimeout.ResolveTimeout(),
                NativeGatewaySetupService.ConfigurationValidationTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable, previous);
        }
    }

    [Theory]
    [InlineData(" 90 ", 90)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("not-a-number", null)]
    [InlineData("2147484", null)]
    public void Timeout_EnvironmentOverride_IsUsedOnlyForValidValues(string value, int? expectedSeconds)
    {
        var previous = Environment.GetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable, value);
            var expected = expectedSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : NativeGatewayCliTimeout.DefaultTimeout;
            Assert.Equal(expected, NativeGatewayCliTimeout.ResolveTimeout());
            Assert.Equal(expected, NativeGatewaySetupService.ConfigurationValidationTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable, previous);
        }
    }

    [Theory]
    [InlineData(null, 240000)]
    [InlineData("90", 90000)]
    [InlineData("1", 1000)]
    [InlineData("2147483", 2147483000)]
    [InlineData("invalid", 240000)]
    public async Task HealthCommand_UsesConfiguredDeadlineForGatewayRpc(string? overrideValue, int expectedMilliseconds)
    {
        var previous = Environment.GetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable, overrideValue);
            using var logger = new SetupLogger(filePath: null);
            var runner = new RecordingCommandRunner();
            var host = new NativeGatewaySetupHost(logger, commandRunner: runner);
            var package = new NativeGatewayPackage("fixture-family", "1.0", "openclaw-fixture", "clawctl-fixture");
            var environment = new Dictionary<string, string>
            {
                ["OPENCLAW_STATE_DIR"] = "fixture-state",
                ["OPENCLAW_GATEWAY_TOKEN"] = "fixture-token",
            };

            await host.VerifyHealthAsync(package, environment, CancellationToken.None);

            var command = Assert.Single(runner.Calls);
            Assert.Equal("openclaw-fixture", command.Executable);
            Assert.Equal(
                ["gateway", "health", "--json", "--timeout", expectedMilliseconds.ToString(CultureInfo.InvariantCulture)],
                command.Arguments);
            Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), command.Timeout);
            Assert.Same(environment, command.Environment);
            Assert.Equal("fixture-state", command.WorkingDirectory);
            Assert.DoesNotContain("fixture-token", command.Arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task HealthCommand_FailureIsSurfacedWithoutRepeatingTheCall()
    {
        using var logger = new SetupLogger(filePath: null);
        var runner = new RecordingCommandRunner
        {
            Result = new CommandResult(1, "", "health failed", TimeSpan.Zero, TimedOut: false),
        };
        var host = new NativeGatewaySetupHost(logger, commandRunner: runner);
        var package = new NativeGatewayPackage("fixture-family", "1.0", "openclaw-fixture", "clawctl-fixture");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.VerifyHealthAsync(package, new Dictionary<string, string>(), CancellationToken.None));

        Assert.Contains("health failed", error.Message);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task HealthCommand_CancellationAfterProcessExitDoesNotReportSuccess()
    {
        using var logger = new SetupLogger(filePath: null);
        using var cancellation = new CancellationTokenSource();
        var runner = new RecordingCommandRunner { BeforeReturn = cancellation.Cancel };
        var host = new NativeGatewaySetupHost(logger, commandRunner: runner);
        var package = new NativeGatewayPackage("fixture-family", "1.0", "openclaw-fixture", "clawctl-fixture");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.VerifyHealthAsync(package, new Dictionary<string, string>(), cancellation.Token));

        Assert.Single(runner.Calls);
    }

    private sealed class RecordingCommandRunner : ICommandRunner
    {
        public List<(string Executable, string[] Arguments, TimeSpan Timeout,
            IReadOnlyDictionary<string, string>? Environment, string? WorkingDirectory)> Calls { get; } = [];
        public CommandResult Result { get; set; } = new(0, "{}", "", TimeSpan.Zero, TimedOut: false);
        public Action? BeforeReturn { get; set; }

        public Task<CommandResult> RunAsync(string executable, string[] arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null,
            string? stdinInput = null, CancellationToken ct = default, Stream? stdinStream = null)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((executable, arguments, timeout, environment, workingDirectory));
            BeforeReturn?.Invoke();
            return Task.FromResult(Result);
        }

        public Task<CommandResult> RunInWslAsync(string distroName, string command, TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null, CancellationToken ct = default,
            string? user = null, bool inputViaStdin = false) => throw new NotSupportedException();
    }
}
