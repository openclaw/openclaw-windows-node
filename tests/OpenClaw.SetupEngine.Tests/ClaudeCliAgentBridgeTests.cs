using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public sealed class ClaudeCliAgentBridgeTests : IDisposable
{
    private static readonly TimeSpan s_agentShellDeadline = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_TEST_AGENT_SHELL_TIMEOUT_SECONDS"), out var seconds) &&
        seconds > 0 ? seconds : 120);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"claude-bridge-test-{Guid.NewGuid():N}");
    private readonly string _profile;
    private readonly string _shared;
    private readonly FakeAgent _agent;

    public ClaudeCliAgentBridgeTests()
    {
        _profile = Path.Combine(_root, "user");
        _shared = Path.Combine(_root, "agent", "Shared");
        Directory.CreateDirectory(Path.Combine(_profile, ".local", "bin"));
        Directory.CreateDirectory(Path.Combine(_profile, ".claude"));
        Directory.CreateDirectory(_shared);
        _agent = new FakeAgent(_shared);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(GatewayAiSetupChoiceKind.Auth, "anthropic-cli", true)]
    [InlineData(GatewayAiSetupChoiceKind.Auth, "claude-cli", true)]
    [InlineData(GatewayAiSetupChoiceKind.Candidate, "claude-cli", true)]
    [InlineData(GatewayAiSetupChoiceKind.Candidate, "anthropic-cli", false)]
    [InlineData(GatewayAiSetupChoiceKind.Auth, "setup-token", false)]
    [InlineData(GatewayAiSetupChoiceKind.ManualProvider, "claude-cli", false)]
    [InlineData(GatewayAiSetupChoiceKind.Prepare, "claude-cli", false)]
    public void Applies_OnlyToTheGatewaysClaudeCliRoutes(GatewayAiSetupChoiceKind kind, string id, bool expected) =>
        Assert.Equal(expected, ClaudeCliAgentBridge.Applies(kind, id));

    [Fact]
    public async Task FirstShare_StagesProgramAndSignInForTheAgentAndLeavesNothingBehind()
    {
        InstallForUser(program: "program-bytes", credentials: "sign-in-bytes");
        Assert.Equal(ClaudeCliShareResult.Shared, await Bridge().ShareAsync(CancellationToken.None));
        Assert.Equal("program-bytes", _agent.StagedProgram);
        Assert.Equal("sign-in-bytes", _agent.StagedCredentials);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_shared));
    }

    [Fact]
    public async Task LaterShare_RefreshesOnlyTheSignInWhenTheAgentAlreadyHasThisProgram()
    {
        InstallForUser(program: "program-bytes", credentials: "newer-sign-in");
        _agent.ProgramSize = "program-bytes".Length;
        Assert.Equal(ClaudeCliShareResult.Shared, await Bridge().ShareAsync(CancellationToken.None));
        Assert.Null(_agent.StagedProgram);
        Assert.Equal("newer-sign-in", _agent.StagedCredentials);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_shared));
    }

    [Fact]
    public async Task AgentThatNeedsAGatewayRestart_IsReportedToTheCaller()
    {
        InstallForUser(program: "program-bytes", credentials: "sign-in-bytes");
        _agent.RestartNeeded = true;
        Assert.Equal(ClaudeCliShareResult.SharedRestartRequired, await Bridge().ShareAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WithoutAUserProgramOrSignIn_SharesNothingAndNeverCallsTheGateway(bool program, bool credentials)
    {
        InstallForUser(program ? "program-bytes" : null, credentials ? "sign-in-bytes" : null);
        Assert.Equal(ClaudeCliShareResult.NotAvailable, await Bridge().ShareAsync(CancellationToken.None));
        Assert.Empty(_agent.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_shared));
    }

    [Fact]
    public async Task ProgramOnThePathAndARelocatedConfigDirectory_AreFound()
    {
        var tools = Path.Combine(_root, "tools");
        var config = Path.Combine(_root, "claude-config");
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(tools, "claude.exe"), "path-program");
        File.WriteAllText(Path.Combine(config, ".credentials.json"), "relocated-sign-in");
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = Path.Combine(_root, "missing") + Path.PathSeparator + tools,
            ["CLAUDE_CONFIG_DIR"] = config,
        };
        var bridge = new ClaudeCliAgentBridge(_agent.RunAsync, _profile, environment.GetValueOrDefault);
        Assert.Equal(ClaudeCliShareResult.Shared, await bridge.ShareAsync(CancellationToken.None));
        Assert.Equal("path-program", _agent.StagedProgram);
        Assert.Equal("relocated-sign-in", _agent.StagedCredentials);
    }

    [Fact]
    public async Task FailedAgentStep_StillRemovesTheSignInFromTheSharedFolder()
    {
        InstallForUser(program: "program-bytes", credentials: "sign-in-bytes");
        _agent.FailApply = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Bridge().ShareAsync(CancellationToken.None));
        Assert.True(_agent.SawStagedCredentials);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_shared));
    }

    [Fact]
    public async Task SignInLeftByAnInterruptedShare_IsRemovedByTheNextShare()
    {
        InstallForUser(program: "program-bytes", credentials: "sign-in-bytes");
        var leftover = Path.Combine(_shared, ".openclaw-companion-claude-interrupted");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, ".credentials.json"), "old-sign-in");
        Assert.Equal(ClaudeCliShareResult.Shared, await Bridge().ShareAsync(CancellationToken.None));
        Assert.Equal(1, _agent.StagingFoldersSeenByTheAgentStep);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_shared));
    }

    [Theory]
    [InlineData("""{"session":{}}""")]
    [InlineData("""{"session":{"sharedFolder":"relative\\path"}}""")]
    [InlineData("not json")]
    public async Task UnusableSharedFolderReport_StopsBeforeAnythingIsCopied(string status)
    {
        InstallForUser(program: "program-bytes", credentials: "sign-in-bytes");
        _agent.Status = status;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Bridge().ShareAsync(CancellationToken.None));
        Assert.Single(_agent.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_shared));
    }

    [Theory]
    [InlineData(@"C:\Users\Agent\Shared\x", @"'C:\Users\Agent\Shared\x'")]
    [InlineData("C:\\Users\\O'Brien\\x", "'C:\\Users\\O''Brien\\x'")]
    [InlineData("C:\\Users\\O\u2019Brien\u2018\u201A\u201B", "'C:\\Users\\O\u2019\u2019Brien\u2018\u2018\u201A\u201A\u201B\u201B'")]
    public void PowerShellLiteral_DoublesEveryCharacterThatEndsASingleQuotedString(string value, string expected) =>
        Assert.Equal(expected, ClaudeCliAgentBridge.QuotePowerShellLiteral(value));

    /// <summary>
    /// Runs the real agent-side scripts in Windows PowerShell against a throwaway profile. The profile's
    /// WindowsApps folder is on the child's PATH, so the script never reaches its user-PATH fallback.
    /// </summary>
    [Fact]
    public async Task AgentScripts_PlaceProgramAndSignInWhereARunningGatewayFindsThem()
    {
        var agentProfile = Path.Combine(_root, "agent's profile");
        var localAppData = Path.Combine(agentProfile, "AppData", "Local");
        var windowsApps = Path.Combine(localAppData, "Microsoft", "WindowsApps");
        var staging = Path.Combine(_shared, ".openclaw-companion-claude-it's staged");
        Directory.CreateDirectory(windowsApps);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "claude.exe"), "new-program");
        File.WriteAllText(Path.Combine(staging, ".credentials.json"), "sign-in-bytes");
        File.WriteAllText(Path.Combine(windowsApps, "claude.exe"), "old");
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var shell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        var environment = new Dictionary<string, string>
        {
            ["USERPROFILE"] = agentProfile,
            ["LOCALAPPDATA"] = localAppData,
            ["Path"] = system + Path.PathSeparator + windowsApps,
        };
        using var logger = new SetupLogger(filePath: null);
        var runner = new CommandRunner(logger);
        async Task<string> RunAsync(string script)
        {
            var result = await runner.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-Command", script],
                s_agentShellDeadline, environment);
            Assert.False(result.TimedOut);
            Assert.True(result.ExitCode == 0, result.Stderr);
            return result.Stdout;
        }

        Assert.Contains("openclaw-claude-size=3", await RunAsync(ClaudeCliAgentBridge.ProbeScript));
        Assert.Contains("openclaw-claude-restart=no", await RunAsync(ClaudeCliAgentBridge.ApplyScript(staging)));
        Assert.Equal("new-program", File.ReadAllText(Path.Combine(windowsApps, "claude.exe")));
        Assert.False(File.Exists(Path.Combine(windowsApps, "claude.exe.new")));
        Assert.Equal("sign-in-bytes", File.ReadAllText(Path.Combine(agentProfile, ".claude", ".credentials.json")));
        Assert.Contains("openclaw-claude-size=11", await RunAsync(ClaudeCliAgentBridge.ProbeScript));
    }

    private ClaudeCliAgentBridge Bridge() => new(_agent.RunAsync, _profile, _ => null);

    private void InstallForUser(string? program, string? credentials)
    {
        if (program is not null)
            File.WriteAllText(Path.Combine(_profile, ".local", "bin", "claude.exe"), program);
        if (credentials is not null)
            File.WriteAllText(Path.Combine(_profile, ".claude", ".credentials.json"), credentials);
    }

    /// <summary>
    /// Stands in for <c>clawctl</c>: reports the shared folder, answers the program-size probe, and records
    /// what the one-time shared folder held when the agent step ran. It does not interpret the scripts;
    /// <see cref="AgentScripts_PlaceProgramAndSignInWhereARunningGatewayFindsThem"/> runs them for real.
    /// </summary>
    private sealed class FakeAgent(string shared)
    {
        public List<string[]> Calls { get; } = [];
        public string Status { get; set; } = JsonSerializer.Serialize(new { session = new { sharedFolder = shared } });
        public long ProgramSize { get; set; }
        public bool RestartNeeded { get; set; }
        public bool FailApply { get; set; }
        public bool SawStagedCredentials { get; private set; }
        public int StagingFoldersSeenByTheAgentStep { get; private set; }
        public string? StagedProgram { get; private set; }
        public string? StagedCredentials { get; private set; }

        public Task<string> RunAsync(string[] arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            if (arguments is ["status", "--json"])
                return Task.FromResult(Status);
            Assert.Equal(["pwsh", "--no-color", "--command"], arguments[..3]);
            var script = Assert.Single(arguments[3..]);
            Assert.DoesNotContain('"', script);
            if (script == ClaudeCliAgentBridge.ProbeScript)
                return Task.FromResult($"openclaw-claude-size={ProgramSize}\r\n");
            var folders = Directory.GetDirectories(shared);
            StagingFoldersSeenByTheAgentStep = folders.Length;
            var staging = Assert.Single(folders, folder => script.Contains(folder, StringComparison.Ordinal));
            var credentials = Path.Combine(staging, ".credentials.json");
            SawStagedCredentials = File.Exists(credentials);
            if (FailApply)
                throw new InvalidOperationException("Synthetic agent failure.");
            StagedCredentials = File.ReadAllText(credentials);
            var program = Path.Combine(staging, "claude.exe");
            StagedProgram = File.Exists(program) ? File.ReadAllText(program) : null;
            return Task.FromResult("openclaw-claude-restart=" + (RestartNeeded ? "yes" : "no") + "\r\n");
        }
    }
}
