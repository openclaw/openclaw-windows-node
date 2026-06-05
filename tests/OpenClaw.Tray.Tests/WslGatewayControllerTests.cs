using OpenClaw.Shared;
using OpenClawTray.Services;

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

    private sealed class FakeWslCommandRunner : IWslCommandRunner
    {
        public IReadOnlyList<WslDistroInfo> Distros { get; init; } = [];
        public WslCommandResult Result { get; init; } = new(0, string.Empty, string.Empty);
        public string? LastDistroName { get; private set; }
        public IReadOnlyList<string>? LastDistroCommand { get; private set; }

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
            return Task.FromResult(Result);
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
            return Task.FromResult(Result);
        }
    }
}
