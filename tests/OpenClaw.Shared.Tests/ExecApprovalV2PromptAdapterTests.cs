using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

public class ExecApprovalV2PromptAdapterTests
{
    [Fact]
    public async Task NullPromptHandler_AlwaysReturnsDeny()
    {
        var request = new ExecApprovalV2PromptRequest
        {
            DisplayCommand = "echo hello",
            Security = ExecSecurity.Full,
            Ask = ExecAsk.Always,
            AgentId = "agent-1"
        };

        var result = await ExecApprovalV2NullPromptHandler.Instance.PromptAsync(request);

        Assert.Equal(ExecApprovalDecision.Deny, result);
    }

    [Fact]
    public async Task NullPromptHandler_DoesNotThrow_WithNullOptionals()
    {
        var request = new ExecApprovalV2PromptRequest
        {
            DisplayCommand = "ls",
            Security = ExecSecurity.Allowlist,
            Ask = ExecAsk.OnMiss,
            AgentId = "agent-2",
            Cwd = null,
            Host = null,
            ResolvedPath = null,
            SessionKey = null
        };

        ExecApprovalDecision result = default;
        var ex = await Record.ExceptionAsync(async () =>
        {
            result = await ExecApprovalV2NullPromptHandler.Instance.PromptAsync(request);
        });

        Assert.Null(ex);
        Assert.Equal(ExecApprovalDecision.Deny, result);
    }

    [Fact]
    public void NullPromptHandler_Instance_IsNotNull()
        => Assert.NotNull(ExecApprovalV2NullPromptHandler.Instance);

    [Fact]
    public void NullPromptHandler_PromptAsync_ReturnsCompletedTask()
    {
        // Task.FromResult guarantee: the returned Task must be synchronously completed.
        // An async implementation of the stub would break fail-closed semantics under TryEnqueue.
        var task = ExecApprovalV2NullPromptHandler.Instance.PromptAsync(MinimalRequest());
        Assert.True(task.IsCompleted);
    }

    [Fact]
    public void PromptRequest_DisplayCommand_IsStoredAsProvided()
    {
        const string raw = "cmd /c del C:\\important.txt";
        var req = new ExecApprovalV2PromptRequest
        {
            DisplayCommand = raw,
            Security = ExecSecurity.Full,
            Ask = ExecAsk.Always,
            AgentId = "a"
        };

        Assert.Equal(raw, req.DisplayCommand);
    }

    [Fact]
    public void PromptRequest_DoesNotExposeAllowAlwaysPatterns()
    {
        // allowAlwaysPatterns lives on ExecApprovalEvaluation, not on the prompt request.
        // Verified via reflection so an accidental future addition fails loudly.
        var prop = typeof(ExecApprovalV2PromptRequest)
            .GetProperty("AllowAlwaysPatterns");
        Assert.Null(prop);
    }

    [Theory]
    [InlineData(ExecApprovalDecision.AllowOnce)]
    [InlineData(ExecApprovalDecision.AllowAlways)]
    [InlineData(ExecApprovalDecision.Deny)]
    public async Task FixedDecisionHandler_ReturnsExpectedDecision(ExecApprovalDecision decision)
    {
        var handler = new FixedDecisionPromptHandler(decision);
        var result = await handler.PromptAsync(MinimalRequest());
        Assert.Equal(decision, result);
    }

    [Fact]
    public void V2PromptHandler_IsDistinctFromLegacyPromptHandler()
    {
        Assert.NotEqual(
            typeof(IExecApprovalV2PromptHandler),
            typeof(IExecApprovalPromptHandler));
    }

    [Fact]
    public void PromptAdapter_Interface_IsInSharedAssembly_NotTray()
    {
        var asm = typeof(IExecApprovalV2PromptHandler).Assembly.GetName().Name;
        Assert.Equal("OpenClaw.Shared", asm);
    }

    [Fact]
    public void ProductionWiring_NullPromptHandler_NotReferencedInSrc()
    {
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var srcDir = Path.Combine(repoRoot!, "src");
        var violations = Directory
            .GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("ExecApprovalV2NullPromptHandler.cs",
                                     StringComparison.OrdinalIgnoreCase))
            .Where(f => File.ReadAllText(f)
                .Contains("ExecApprovalV2NullPromptHandler", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(violations);
    }

    private static ExecApprovalV2PromptRequest MinimalRequest() =>
        new()
        {
            DisplayCommand = "echo hello",
            Security = ExecSecurity.Full,
            Ask = ExecAsk.Always,
            AgentId = "agent-1"
        };

    private sealed class FixedDecisionPromptHandler : IExecApprovalV2PromptHandler
    {
        private readonly ExecApprovalDecision _decision;
        public FixedDecisionPromptHandler(ExecApprovalDecision decision) => _decision = decision;

        public Task<ExecApprovalDecision> PromptAsync(ExecApprovalV2PromptRequest request)
            => Task.FromResult(_decision);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "openclaw-windows-node.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
