namespace OpenClaw.Tray.Tests;

public sealed class ConnectionRegressionSourceTests
{
    [Fact]
    public void Dashboard_TokenQuery_IsLimitedToSharedGatewayToken()
    {
        var appSource = ReadSource("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        Assert.Contains("credentialSource == CredentialResolver.SourceSharedGatewayToken", appSource);
        Assert.DoesNotContain("if (!isBootstrapToken && !string.IsNullOrEmpty(token))", appSource);
    }

    [Fact]
    public void DirectConnect_WaitsForTerminalConnectionOutcome()
    {
        var pageSource = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs");

        Assert.Contains("ConnectAndWaitForDirectConnectOutcomeAsync(recordId)", pageSource);
        Assert.Contains("Task.Delay(TimeSpan.FromSeconds(15))", pageSource);
        Assert.Contains("RollbackDirectConnect(previousActiveId", pageSource);
    }

    private static string ReadSource(params string[] relativePathParts)
    {
        var root = GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(relativePathParts).ToArray()));
    }

    private static string GetRepositoryRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }
}
