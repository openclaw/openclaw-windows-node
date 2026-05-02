using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for ExecApprovalV2PolicyHandler: the PR7 coordinator that bridges
/// V2 input validation and ExecApprovalPolicy evaluation.
/// </summary>
public class ExecApprovalV2PolicyHandlerTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static NodeInvokeRequest RunRequest(string commandJson = """{"command":["echo","hello"]}""")
        => new() { Id = "r1", Command = "system.run", Args = Parse(commandJson) };

    private (ExecApprovalV2PolicyHandler handler, string tempDir) MakeHandler(
        Action<ExecApprovalPolicy>? configure = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"v2policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var policy = new ExecApprovalPolicy(tempDir, NullLogger.Instance);
        configure?.Invoke(policy);
        return (new ExecApprovalV2PolicyHandler(policy, NullLogger.Instance), tempDir);
    }

    // -------------------------------------------------------------------------
    // 1. Allow rules → Allowed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AllowRule_ReturnsAllowed()
    {
        var (handler, dir) = MakeHandler(p =>
            p.SetRules(
                [new ExecApprovalRule { Pattern = "echo *", Action = ExecApprovalAction.Allow }],
                ExecApprovalAction.Deny));
        try
        {
            var result = await handler.HandleAsync(RunRequest(), "corr01");
            Assert.Equal(ExecApprovalV2Code.Allowed, result.Code);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task AllowRuleNoArgs_ReturnsAllowed()
    {
        var (handler, dir) = MakeHandler(p =>
            p.SetRules(
                [new ExecApprovalRule { Pattern = "echo*", Action = ExecApprovalAction.Allow }],
                ExecApprovalAction.Deny));
        try
        {
            // echo with no args
            var result = await handler.HandleAsync(RunRequest("""{"command":["echo"]}"""), "corr02");
            Assert.Equal(ExecApprovalV2Code.Allowed, result.Code);
        }
        finally { TryDelete(dir); }
    }

    // -------------------------------------------------------------------------
    // 2. Deny rules → SecurityDeny
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DenyRule_ReturnsSecurityDeny()
    {
        var (handler, dir) = MakeHandler(p =>
            p.SetRules(
                [new ExecApprovalRule { Pattern = "*", Action = ExecApprovalAction.Deny }],
                ExecApprovalAction.Deny));
        try
        {
            var result = await handler.HandleAsync(RunRequest(), "corr03");
            Assert.Equal(ExecApprovalV2Code.SecurityDeny, result.Code);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task DenyRule_ReasonPreserved()
    {
        var (handler, dir) = MakeHandler(p =>
            p.SetRules(
                [new ExecApprovalRule { Pattern = "*", Action = ExecApprovalAction.Deny, Description = "blocked by test" }],
                ExecApprovalAction.Deny));
        try
        {
            var result = await handler.HandleAsync(RunRequest(), "corr04");
            Assert.Equal(ExecApprovalV2Code.SecurityDeny, result.Code);
            Assert.NotEmpty(result.Reason);
        }
        finally { TryDelete(dir); }
    }

    // -------------------------------------------------------------------------
    // 3. Prompt rules → AllowlistMiss
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PromptRule_ReturnsAllowlistMiss()
    {
        var (handler, dir) = MakeHandler(p =>
            p.SetRules(
                [new ExecApprovalRule { Pattern = "*", Action = ExecApprovalAction.Prompt }],
                ExecApprovalAction.Deny));
        try
        {
            var result = await handler.HandleAsync(RunRequest(), "corr05");
            Assert.Equal(ExecApprovalV2Code.AllowlistMiss, result.Code);
        }
        finally { TryDelete(dir); }
    }

    // -------------------------------------------------------------------------
    // 4. Input validation failures → ValidationFailed (not SecurityDeny)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MissingCommand_ReturnsValidationFailed()
    {
        var (handler, dir) = MakeHandler(p =>
            p.SetRules(
                [new ExecApprovalRule { Pattern = "*", Action = ExecApprovalAction.Allow }],
                ExecApprovalAction.Allow));
        try
        {
            var req = new NodeInvokeRequest { Id = "r1", Command = "system.run", Args = Parse("""{"timeout":5000}""") };
            var result = await handler.HandleAsync(req, "corr06");
            Assert.Equal(ExecApprovalV2Code.ValidationFailed, result.Code);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task MalformedCommand_ReturnsValidationFailed()
    {
        var (handler, dir) = MakeHandler();
        try
        {
            var req = new NodeInvokeRequest { Id = "r1", Command = "system.run", Args = Parse("""{"command":42}""") };
            var result = await handler.HandleAsync(req, "corr07");
            Assert.Equal(ExecApprovalV2Code.ValidationFailed, result.Code);
        }
        finally { TryDelete(dir); }
    }

    // -------------------------------------------------------------------------
    // 5. Shell-wrapper inner command also evaluated
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ShellWrapper_InnerDeniedCommand_ReturnsSecurityDeny()
    {
        // Allow cmd.exe but deny rmdir. A wrapped "cmd /c rmdir /s /q C:\" must be denied.
        var (handler, dir) = MakeHandler(p =>
            p.SetRules(
                [
                    new ExecApprovalRule { Pattern = "cmd*", Action = ExecApprovalAction.Allow },
                    new ExecApprovalRule { Pattern = "rmdir*", Action = ExecApprovalAction.Deny },
                ],
                ExecApprovalAction.Deny));
        try
        {
            var req = RunRequest("""{"command":["cmd","/c","rmdir /s /q C:\\"]}""");
            var result = await handler.HandleAsync(req, "corr08");
            Assert.Equal(ExecApprovalV2Code.SecurityDeny, result.Code);
        }
        finally { TryDelete(dir); }
    }

    // -------------------------------------------------------------------------
    // 6. Handler never throws (defensive)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Handler_DoesNotThrow_OnValidRequest()
    {
        var (handler, dir) = MakeHandler();
        try
        {
            var ex = await Record.ExceptionAsync(() => handler.HandleAsync(RunRequest(), "corr09"));
            Assert.Null(ex);
        }
        finally { TryDelete(dir); }
    }

    // -------------------------------------------------------------------------
    // 7. Integration: policy handler + SystemCapability → execution
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Integration_PolicyAllow_V2PathExecutesCommand()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"v2int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, NullLogger.Instance);
            policy.SetRules(
                [new ExecApprovalRule { Pattern = "echo*", Action = ExecApprovalAction.Allow }],
                ExecApprovalAction.Deny);

            var handler = new ExecApprovalV2PolicyHandler(policy, NullLogger.Instance);
            var runner = new FakeRunner();
            var cap = new SystemCapability(NullLogger.Instance);
            cap.SetCommandRunner(runner);
            cap.SetV2Handler(handler);

            var res = await cap.ExecuteAsync(RunRequest());

            Assert.True(res.Ok, $"Expected Ok: {res.Error}");
            Assert.NotNull(runner.LastRequest);
        }
        finally { TryDelete(tempDir); }
    }

    [Fact]
    public async Task Integration_PolicyDeny_V2PathReturnsError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"v2int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var policy = new ExecApprovalPolicy(tempDir, NullLogger.Instance);
            policy.SetRules(
                [new ExecApprovalRule { Pattern = "*", Action = ExecApprovalAction.Deny }],
                ExecApprovalAction.Deny);

            var handler = new ExecApprovalV2PolicyHandler(policy, NullLogger.Instance);
            var runner = new FakeRunner();
            var cap = new SystemCapability(NullLogger.Instance);
            cap.SetCommandRunner(runner);
            cap.SetV2Handler(handler);

            var res = await cap.ExecuteAsync(RunRequest());

            Assert.False(res.Ok);
            Assert.Null(runner.LastRequest); // runner not called
        }
        finally { TryDelete(tempDir); }
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed class FakeRunner : ICommandRunner
    {
        public string Name => "fake";
        public CommandRequest? LastRequest { get; private set; }

        public Task<CommandResult> RunAsync(CommandRequest request, System.Threading.CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new CommandResult { Stdout = "ok", ExitCode = 0 });
        }
    }

    private sealed class NullLogger : IOpenClawLogger
    {
        public static readonly NullLogger Instance = new();
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
