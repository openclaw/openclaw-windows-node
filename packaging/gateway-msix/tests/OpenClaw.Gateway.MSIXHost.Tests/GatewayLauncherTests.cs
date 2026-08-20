namespace OpenClaw.MSIXHost.Tests;

public sealed class GatewayLauncherTests : IDisposable
{
    private readonly string _payloadDirectory = TestDirectory.Create();

    public GatewayLauncherTests()
    {
        File.WriteAllText(
            Path.Combine(_payloadDirectory, "openclaw.mjs"),
            "console.log('fixture');");
    }

    [Fact]
    public void CreateStartInfoDefaultsToForegroundGateway()
    {
        var startInfo = GatewayLauncher.CreateStartInfo(
            "node",
            _payloadDirectory,
            []);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(_payloadDirectory, startInfo.WorkingDirectory);
        Assert.Equal(
            [
                Path.Combine(_payloadDirectory, "openclaw.mjs"),
                "gateway",
                "run"
            ],
            startInfo.ArgumentList);
    }

    [Fact]
    public async Task ForwardStandardErrorSuppressesModulePreparationClixml()
    {
        const string input = """
            #< CLIXML
            <Objs><Obj S="progress"><MS><PR><AV>Preparing modules for first use.</AV></PR></MS></Obj></Objs>
            Missing config.
            """;
        var output = new StringWriter();

        await GatewayLauncher.ForwardStandardErrorAsync(
            new StringReader(input),
            output,
            CancellationToken.None);

        Assert.Equal($"Missing config.{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task ForwardStandardErrorPreservesOtherClixml()
    {
        const string input = """
            #< CLIXML
            <Objs><S>Actual PowerShell failure</S></Objs>
            """;
        var output = new StringWriter();

        await GatewayLauncher.ForwardStandardErrorAsync(
            new StringReader(input),
            output,
            CancellationToken.None);

        Assert.Contains("#< CLIXML", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "Actual PowerShell failure",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForwardStandardErrorPreservesMixedProgressAndErrorClixml()
    {
        const string input = """
            #< CLIXML
            <Objs><Obj S="progress"><S>Preparing modules for first use.</S></Obj><Obj S="error"><S>Gateway failure</S></Obj></Objs>
            """;
        var output = new StringWriter();

        await GatewayLauncher.ForwardStandardErrorAsync(
            new StringReader(input),
            output,
            CancellationToken.None);

        Assert.Contains(
            "Preparing modules for first use.",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("Gateway failure", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateStartInfoPreservesExplicitArguments()
    {
        string[] arguments = ["status", "--json", "value with spaces"];

        var startInfo = GatewayLauncher.CreateStartInfo(
            "node",
            _payloadDirectory,
            arguments);

        Assert.False(startInfo.RedirectStandardError);
        Assert.Equal(
            [Path.Combine(_payloadDirectory, "openclaw.mjs"), .. arguments],
            startInfo.ArgumentList);
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("onboard")]
    public void CreateStartInfoSkipsDaemonInstallationDuringSetup(string command)
    {
        var startInfo = GatewayLauncher.CreateStartInfo(
            "node",
            _payloadDirectory,
            [command, "--mode", "local"]);

        Assert.Equal(
            [
                Path.Combine(_payloadDirectory, "openclaw.mjs"),
                command,
                "--mode",
                "local",
                "--skip-daemon"
            ],
            startInfo.ArgumentList);
    }

    [Fact]
    public void CreateStartInfoPreservesExplicitDaemonSkip()
    {
        var startInfo = GatewayLauncher.CreateStartInfo(
            "node",
            _payloadDirectory,
            ["setup", "--no-install-daemon"]);

        Assert.Equal(
            [
                Path.Combine(_payloadDirectory, "openclaw.mjs"),
                "setup",
                "--no-install-daemon"
            ],
            startInfo.ArgumentList);
    }

    [Theory]
    [InlineData("gateway", "install")]
    [InlineData("setup", "--install-daemon")]
    [InlineData("onboard", "--install-daemon")]
    public void CreateStartInfoRejectsDaemonInstallation(
        string command,
        string installArgument)
    {
        HostUsageException exception = Assert.Throws<HostUsageException>(() =>
            GatewayLauncher.CreateStartInfo(
                "node",
                _payloadDirectory,
                [command, installArgument]));

        Assert.Contains(
            "not support",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Directory.Delete(_payloadDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
