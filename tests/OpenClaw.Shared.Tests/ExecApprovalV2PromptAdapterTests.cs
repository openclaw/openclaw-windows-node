using System;
using System.IO;
using System.Linq;
using System.Threading;
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
            AgentId = "agent-1",
            CorrelationId = "test-corr-1"
        };

        var result = await ExecApprovalV2NullPromptHandler.Instance.PromptAsync(request, CancellationToken.None);

        Assert.Equal(ExecApprovalPromptOutcome.Deny, result);
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
            CorrelationId = "test-corr-2",
            Cwd = null,
            Host = null,
            ResolvedPath = null,
            SessionKey = null
        };

        ExecApprovalPromptOutcome result = default;
        var ex = await Record.ExceptionAsync(async () =>
        {
            result = await ExecApprovalV2NullPromptHandler.Instance.PromptAsync(request, CancellationToken.None);
        });

        Assert.Null(ex);
        Assert.Equal(ExecApprovalPromptOutcome.Deny, result);
    }

    [Fact]
    public void NullPromptHandler_Instance_IsNotNull()
        => Assert.NotNull(ExecApprovalV2NullPromptHandler.Instance);

    [Fact]
    public void NullPromptHandler_PromptAsync_ReturnsCompletedTask()
    {
        // Task.FromResult guarantee: the returned Task must be synchronously completed.
        // An async implementation of the stub would break fail-closed semantics under TryEnqueue.
        var task = ExecApprovalV2NullPromptHandler.Instance.PromptAsync(MinimalRequest(), CancellationToken.None);
        Assert.True(task.IsCompleted);
    }

    [Fact]
    public async Task NullPromptHandler_DoesNotThrow_WhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await ExecApprovalV2NullPromptHandler.Instance.PromptAsync(MinimalRequest(), cts.Token);
        Assert.Equal(ExecApprovalPromptOutcome.Deny, result);
    }

    [Fact]
    public void PromptOutcome_Default_IsDeny()
    {
        Assert.Equal(ExecApprovalPromptOutcome.Deny, default(ExecApprovalPromptOutcome));
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
            AgentId = "a",
            CorrelationId = "test-corr-3"
        };

        Assert.Equal(raw, req.DisplayCommand);
    }

    [Fact]
    public void PromptRequest_CorrelationId_IsStoredAsProvided()
    {
        const string id = "corr-abc-123";
        var req = new ExecApprovalV2PromptRequest
        {
            DisplayCommand = "echo hello",
            Security = ExecSecurity.Full,
            Ask = ExecAsk.Always,
            AgentId = "a",
            CorrelationId = id
        };

        Assert.Equal(id, req.CorrelationId);
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
    [InlineData(ExecApprovalPromptOutcome.Allow)]
    [InlineData(ExecApprovalPromptOutcome.AllowOnce)]
    [InlineData(ExecApprovalPromptOutcome.AllowAlways)]
    [InlineData(ExecApprovalPromptOutcome.Deny)]
    public async Task FixedOutcomeHandler_ReturnsExpectedOutcome(ExecApprovalPromptOutcome outcome)
    {
        var handler = new FixedOutcomePromptHandler(outcome);
        var result = await handler.PromptAsync(MinimalRequest(), CancellationToken.None);
        Assert.Equal(outcome, result);
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

    // Delete once real production wiring of IExecApprovalV2PromptHandler lands in src/.
    [Fact]
    public void ProductionWiring_NullPromptHandler_NotReferencedInSrc()
    {
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var srcDir = Path.Combine(repoRoot!, "src");
        var violations = Directory
            .GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
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
            AgentId = "agent-1",
            CorrelationId = "test-corr-1"
        };

    private sealed class FixedOutcomePromptHandler : IExecApprovalV2PromptHandler
    {
        private readonly ExecApprovalPromptOutcome _outcome;
        public FixedOutcomePromptHandler(ExecApprovalPromptOutcome outcome) => _outcome = outcome;

        public Task<ExecApprovalPromptOutcome> PromptAsync(ExecApprovalV2PromptRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_outcome);
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
