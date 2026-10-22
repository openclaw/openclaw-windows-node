using OpenClaw.Connection;

namespace OpenClaw.SetupEngine.Tests;

public class SetupWizardReloadModeRestoreTests
{
    [Fact]
    public async Task RestoreReloadMode_PipesScriptAndKeepsUserAndModeOutOfWslArgv()
    {
        const string linuxUser = "reloaduser";
        const string reloadMode = "$PATH";
        var commands = new RecordingCommandRunner();
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var ctx = new SetupContext(
            new SetupConfig
            {
                Wsl = new WslConfig { User = linuxUser },
                Gateway = new GatewayConfig { ReloadMode = reloadMode },
            },
            logger,
            new TransactionJournal(filePath: null),
            commands,
            CancellationToken.None);
        ctx.DistroName = "test-distro";
        ctx.EndpointProvenanceProbe = (_, _) => Task.FromResult(
            new GatewayEndpointProvenance(
                GatewayEndpointProvenanceKind.ExpectedManagedGateway,
                ctx.Config.GatewayPort));

        var result = await new SetupWizardRunner(ctx).RestoreReloadModeAsync();

        Assert.True(result.IsSuccess, result.Message);
        var restore = Assert.Single(
            commands.WslCalls,
            call => call.Command.Contains("config set gateway.reload.mode", StringComparison.Ordinal));
        Assert.True(restore.InputViaStdin);
        Assert.Contains("'$PATH'", restore.Command, StringComparison.Ordinal);
        Assert.Contains($"/home/{linuxUser}/", restore.Command, StringComparison.Ordinal);

        var argv = string.Join('\n', BuildWslArguments(restore));
        Assert.DoesNotContain(reloadMode, argv, StringComparison.Ordinal);
        Assert.DoesNotContain(linuxUser, argv, StringComparison.Ordinal);
        Assert.DoesNotContain("bash\n-c", argv, StringComparison.Ordinal);
    }

    private static string[] BuildWslArguments(
        (string DistroName, string Command, string? User, bool InputViaStdin) call)
    {
        var command = call.Command.Replace("\r", "");
        var args = new List<string> { "-d", call.DistroName };
        if (!string.IsNullOrWhiteSpace(call.User))
        {
            args.Add("-u");
            args.Add(call.User);
        }

        if (call.InputViaStdin)
            args.AddRange(["--", "bash", "-s"]);
        else
            args.AddRange(["--", "bash", "-c", command]);

        return args.ToArray();
    }

    private sealed class RecordingCommandRunner : ICommandRunner
    {
        public List<(string DistroName, string Command, string? User, bool InputViaStdin)> WslCalls { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default,
            Stream? stdinStream = null) =>
            Task.FromResult(new CommandResult(0, "", "", TimeSpan.Zero, TimedOut: false));

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false)
        {
            WslCalls.Add((distroName, command, user, inputViaStdin));
            return Task.FromResult(new CommandResult(0, "200", "", TimeSpan.Zero, TimedOut: false));
        }
    }
}
