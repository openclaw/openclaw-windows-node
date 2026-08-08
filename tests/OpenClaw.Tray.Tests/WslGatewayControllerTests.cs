using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.Tray.Tests;

public class WslGatewayControllerTests
{
    [Theory]
    [InlineData(WslGatewayControlAction.Start, "start")]
    [InlineData(WslGatewayControlAction.Stop, "stop")]
    [InlineData(WslGatewayControlAction.Restart, "restart")]
    public void Build_UsesBashLoginShellPathPrefixAndGatewayCommand(WslGatewayControlAction action, string verb)
    {
        var command = WslGatewayControlCommandBuilder.Build(action);

        Assert.Equal(["bash", "-lc"], command.Take(2).ToArray());
        Assert.Equal(
            $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && openclaw gateway {verb}",
            command[2]);
    }

    [Fact]
    public async Task RunAsync_InvokesGatewayCommandInsideRegisteredDistro()
    {
        var runner = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Running", 2)],
            Result = new WslCommandResult(0, "started", string.Empty),
        };
        var controller = new WslGatewayController(runner, NullLogger.Instance);

        var result = await controller.RunAsync("OpenClawGateway", WslGatewayControlAction.Start);

        Assert.True(result.Success);
        Assert.Equal("OpenClawGateway", runner.LastDistroName);
        Assert.Equal(WslGatewayControlCommandBuilder.Build(WslGatewayControlAction.Start), runner.LastDistroCommand);
    }

    [Fact]
    public async Task RunAsync_ReturnsFailure_WhenDistroIsNotRegistered()
    {
        var runner = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OtherGateway", "Running", 2)],
        };
        var controller = new WslGatewayController(runner, NullLogger.Instance);

        var result = await controller.RunAsync("OpenClawGateway", WslGatewayControlAction.Restart);

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Null(runner.LastDistroCommand);
        Assert.Contains("not registered", result.StandardError);
    }

    [Fact]
    public async Task RunAsync_AttemptsCommand_WhenDistroEnumerationIsEmpty()
    {
        // An empty distro list is ambiguous — the `wsl --list` probe may have failed
        // or timed out — so the controller should fail open and still attempt the
        // control command rather than dead-ending with a misleading "not registered".
        var runner = new FakeWslCommandRunner
        {
            Distros = [],
            Result = new WslCommandResult(0, "restarted", string.Empty),
        };
        var controller = new WslGatewayController(runner, NullLogger.Instance);

        var result = await controller.RunAsync("OpenClawGateway", WslGatewayControlAction.Restart);

        Assert.True(result.Success);
        Assert.Equal("OpenClawGateway", runner.LastDistroName);
        Assert.Equal(WslGatewayControlCommandBuilder.Build(WslGatewayControlAction.Restart), runner.LastDistroCommand);
        Assert.DoesNotContain("not registered", result.StandardError);
    }

    [Fact]
    public async Task ForceRestartAsync_TerminatesDistroThenColdRestarts()
    {
        var runner = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Running", 2)],
            Result = new WslCommandResult(0, "ok", string.Empty),
        };
        var controller = new WslGatewayController(runner, NullLogger.Instance);

        var result = await controller.ForceRestartAsync("OpenClawGateway");

        Assert.True(result.Success);
        Assert.Equal(1, runner.TerminateCount);                      // host-side terminate happened first
        Assert.Equal("OpenClawGateway", runner.LastTerminatedDistro);
        Assert.Equal(                                                 // then a cold in-distro restart
            WslGatewayControlCommandBuilder.Build(WslGatewayControlAction.Restart),
            runner.LastDistroCommand);
    }

    [Fact]
    public async Task Restarter_WhenInPlaceRestartFails_EscalatesToTerminateAndForceRestart()
    {
        // Wedged-VM case: the in-place `gateway restart` fails; the restarter must escalate to a
        // host-side terminate + cold restart rather than giving up (Sol-A).
        var runner = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Running", 2)],
            InDistroResults = new Queue<WslCommandResult>(
            [
                new WslCommandResult(1, string.Empty, "wedged"),  // in-place restart fails
                new WslCommandResult(0, "ok", string.Empty),      // cold restart after terminate succeeds
            ]),
        };
        var controller = new WslGatewayController(runner, NullLogger.Instance);
        var restarter = new OpenClawTray.Services.WslManagedLocalGatewayRestarter(controller);

        var result = await restarter.RestartAsync("OpenClawGateway", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, runner.TerminateCount);   // escalated: host-side terminate happened
        Assert.Equal(2, runner.InDistroCount);     // in-place attempt + post-terminate cold restart
    }

    [Fact]
    public async Task Restarter_WhenBothRestartsFail_ReportsFailure()
    {
        var runner = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Running", 2)],
            Result = new WslCommandResult(1, string.Empty, "still wedged"),
        };
        var controller = new WslGatewayController(runner, NullLogger.Instance);
        var restarter = new OpenClawTray.Services.WslManagedLocalGatewayRestarter(controller);

        var result = await restarter.RestartAsync("OpenClawGateway", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, runner.TerminateCount);   // escalation was attempted
        Assert.Equal(2, runner.InDistroCount);
    }

    [Fact]
    public async Task Restarter_GatewayChangesAfterInPlaceFailure_DoesNotForceTerminate()
    {
        var runner = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Running", 2)],
            InDistroResults = new Queue<WslCommandResult>(
            [
                new WslCommandResult(1, string.Empty, "wedged"),
            ]),
        };
        var controller = new WslGatewayController(runner, NullLogger.Instance);
        var restarter = new OpenClawTray.Services.WslManagedLocalGatewayRestarter(controller);
        var checks = 0;

        var result = await restarter.RestartAsync(
            "OpenClawGateway",
            CancellationToken.None,
            canContinue: () => ++checks == 1);

        Assert.False(result.Success);
        Assert.Contains("changed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.TerminateCount);
        Assert.Equal(1, runner.InDistroCount);
    }

    private sealed class FakeWslCommandRunner : IWslCommandRunner
    {
        public IReadOnlyList<WslDistroInfo> Distros { get; init; } = [];
        public WslCommandResult Result { get; init; } = new(0, string.Empty, string.Empty);
        public Queue<WslCommandResult>? InDistroResults { get; init; }
        public string? LastDistroName { get; private set; }
        public IReadOnlyList<string>? LastDistroCommand { get; private set; }
        public int InDistroCount { get; private set; }
        public int TerminateCount { get; private set; }
        public string? LastTerminatedDistro { get; private set; }

        public Task<WslCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            return Task.FromResult(Result);
        }

        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Distros);
        }

        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default)
        {
            TerminateCount++;
            LastTerminatedDistro = name;
            return Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
        }

        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result);
        }

        public Task<WslCommandResult> RunInDistroAsync(
            string name,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            LastDistroName = name;
            LastDistroCommand = command;
            InDistroCount++;
            var result = InDistroResults is { Count: > 0 } ? InDistroResults.Dequeue() : Result;
            return Task.FromResult(result);
        }
    }
}
