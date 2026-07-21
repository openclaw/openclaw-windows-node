using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

public class ExecApprovalV2UiPromptHandlerTests
{
    private static string U(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static ExecApprovalV2PromptRequest Request(
        string command = "echo hello", string? cwd = null, string? resolvedPath = null,
        string agentId = "agent-1") =>
        new()
        {
            DisplayCommand = command,
            Cwd = cwd,
            ResolvedPath = resolvedPath,
            Security = ExecSecurity.Full,
            Ask = ExecAsk.Always,
            AgentId = agentId,
            CorrelationId = "corr-1",
        };

    private static ExecApprovalV2UiPromptHandler Handler(
        Func<ExecApprovalPromptView, CancellationToken, Task<ExecApprovalPromptOutcome>> showDialog,
        Func<Action, bool>? tryEnqueue = null,
        IOpenClawLogger? logger = null) =>
        new(
            tryEnqueue ?? (action => { action(); return true; }),
            showDialog,
            logger ?? new CapturingLogger());

    [Fact]
    public async Task CancellationBeforeEnqueue_Denies_WithoutInvokingEnqueue()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var enqueueInvoked = false;
        var handler = Handler(
            (_, _) => Task.FromResult(ExecApprovalPromptOutcome.AllowOnce),
            tryEnqueue: action => { enqueueInvoked = true; action(); return true; });

        var result = await handler.PromptAsync(Request(), cts.Token);

        Assert.Equal(ExecApprovalPromptOutcome.Deny, result);
        Assert.False(enqueueInvoked);
    }

    [Fact]
    public async Task CancellationWhileDialogPending_DeniesPromptly_NotBlockedOnDialog()
    {
        using var cts = new CancellationTokenSource();
        var neverCompleting = new TaskCompletionSource<ExecApprovalPromptOutcome>();
        var handler = Handler((_, _) => neverCompleting.Task);

        var promptTask = handler.PromptAsync(Request(), cts.Token);
        cts.Cancel();

        var completed = await Task.WhenAny(promptTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(promptTask, completed);
        Assert.Equal(ExecApprovalPromptOutcome.Deny, await promptTask);
    }

    [Fact]
    public async Task UserDecisionBeforeCancellation_IsHonored()
    {
        using var cts = new CancellationTokenSource();
        var handler = Handler((_, _) => Task.FromResult(ExecApprovalPromptOutcome.AllowOnce));

        var result = await handler.PromptAsync(Request(), cts.Token);
        cts.Cancel();

        Assert.Equal(ExecApprovalPromptOutcome.AllowOnce, result);
    }

    [Fact]
    public async Task EnqueueFailure_Denies_WithoutThrowing()
    {
        var handler = Handler(
            (_, _) => Task.FromResult(ExecApprovalPromptOutcome.AllowOnce),
            tryEnqueue: _ => false);

        var result = await handler.PromptAsync(Request());

        Assert.Equal(ExecApprovalPromptOutcome.Deny, result);
    }

    [Fact]
    public async Task DialogThrowingSynchronously_Denies_WithoutThrowing()
    {
        var handler = Handler((_, _) => throw new InvalidOperationException("boom"));

        var result = await handler.PromptAsync(Request());

        Assert.Equal(ExecApprovalPromptOutcome.Deny, result);
    }

    [Fact]
    public async Task DialogReturningFaultedTask_Denies_WithoutThrowing()
    {
        var handler = Handler(async (_, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("post-await boom");
        });

        var result = await handler.PromptAsync(Request());

        Assert.Equal(ExecApprovalPromptOutcome.Deny, result);
    }

    [Theory]
    [InlineData(ExecApprovalPromptOutcome.Deny)]
    [InlineData(ExecApprovalPromptOutcome.AllowOnce)]
    [InlineData(ExecApprovalPromptOutcome.AllowAlways)]
    public async Task DialogOutcomes_PropagateUnchanged(ExecApprovalPromptOutcome outcome)
    {
        var handler = Handler((_, _) => Task.FromResult(outcome));

        var result = await handler.PromptAsync(Request());

        Assert.Equal(outcome, result);
    }

    [Fact]
    public async Task PlainAllowFromDialog_IsClampedToDeny_AndLogged()
    {
        var logger = new CapturingLogger();
        var handler = Handler((_, _) => Task.FromResult(ExecApprovalPromptOutcome.Allow), logger: logger);

        var result = await handler.PromptAsync(Request());

        Assert.Equal(ExecApprovalPromptOutcome.Deny, result);
        Assert.Contains(logger.Errors, m => m.Contains("Allow"));
    }

    [Fact]
    public async Task ViewPassedToDialog_CarriesSanitizedCommandCwdAndPath()
    {
        ExecApprovalPromptView? seen = null;
        var handler = Handler((view, _) =>
        {
            seen = view;
            return Task.FromResult(ExecApprovalPromptOutcome.Deny);
        });

        await handler.PromptAsync(Request(
            command: "echo " + U(0x202E) + "gpj.exe",
            cwd: "C:\\work" + U(0x200B) + "dir",
            resolvedPath: "C:\\bin\\echo\nfake.exe"));

        Assert.NotNull(seen);
        Assert.Equal(@"echo \u{202E}gpj.exe", seen!.CommandText);
        Assert.Equal("agent-1", seen.AgentLabel);
        Assert.Equal(@"C:\work\u{200B}dir", seen.CwdText);
        Assert.Equal("C:\\bin\\echo" + @"\u{A}" + "fake.exe", seen.ExecutablePathText);
    }

    [Fact]
    public async Task SpoofedAgentId_IsSanitizedOnView()
    {
        ExecApprovalPromptView? seen = null;
        var handler = Handler((view, _) => { seen = view; return Task.FromResult(ExecApprovalPromptOutcome.Deny); });

        // The agent label is agent-controlled and rendered like the other rows, so a
        // BiDi override in it must be escaped before reaching the dialog.
        await handler.PromptAsync(Request(agentId: "age" + U(0x202E) + "nt"));

        Assert.NotNull(seen);
        Assert.Equal(@"age\u{202E}nt", seen!.AgentLabel);
    }

    [Fact]
    public async Task MixedScriptCommand_SetsConfusableWarningOnView()
    {
        ExecApprovalPromptView? seen = null;
        var handler = Handler((view, _) => { seen = view; return Task.FromResult(ExecApprovalPromptOutcome.Deny); });

        // "curl" with a Cyrillic 'а' (U+0430) standing in for the Latin 'a'.
        await handler.PromptAsync(Request(command: "cur" + U(0x0430) + "l https://example.test"));

        Assert.NotNull(seen);
        Assert.True(seen!.HasConfusableWarning);
    }

    [Fact]
    public async Task PlainCommand_LeavesConfusableWarningUnset()
    {
        ExecApprovalPromptView? seen = null;
        var handler = Handler((view, _) => { seen = view; return Task.FromResult(ExecApprovalPromptOutcome.Deny); });

        await handler.PromptAsync(Request(command: "git commit -m 'ok'", cwd: "C:\\work", resolvedPath: "C:\\bin\\git.exe"));

        Assert.NotNull(seen);
        Assert.False(seen!.HasConfusableWarning);
    }

    [Fact]
    public async Task MixedScriptOnlyInCwd_SetsConfusableWarningOnView()
    {
        ExecApprovalPromptView? seen = null;
        var handler = Handler((view, _) => { seen = view; return Task.FromResult(ExecApprovalPromptOutcome.Deny); });

        // Warning is an OR across command, cwd, and path: a clean command with a
        // spoofed working directory must still raise it.
        await handler.PromptAsync(Request(command: "git status", cwd: "C:\\w" + U(0x043E) + "rk"));

        Assert.NotNull(seen);
        Assert.True(seen!.HasConfusableWarning);
    }

    [Fact]
    public async Task MixedScriptOnlyInResolvedPath_SetsConfusableWarningOnView()
    {
        ExecApprovalPromptView? seen = null;
        var handler = Handler((view, _) => { seen = view; return Task.FromResult(ExecApprovalPromptOutcome.Deny); });

        await handler.PromptAsync(Request(command: "git status", resolvedPath: "C:\\bin\\g" + U(0x0456) + "t.exe"));

        Assert.NotNull(seen);
        Assert.True(seen!.HasConfusableWarning);
    }

    [Fact]
    public async Task CancellationBeforeDispatchedActionRuns_DoesNotInvokeDialog()
    {
        using var cts = new CancellationTokenSource();
        Action? pending = null;
        var dialogInvoked = false;
        var handler = Handler(
            (_, _) => { dialogInvoked = true; return Task.FromResult(ExecApprovalPromptOutcome.AllowOnce); },
            tryEnqueue: action => { pending = action; return true; });

        var promptTask = handler.PromptAsync(Request(), cts.Token);
        cts.Cancel();
        Assert.Equal(ExecApprovalPromptOutcome.Deny, await promptTask);

        // The dispatcher runs the queued work only after the cancellation already
        // denied the request: the dialog must never be shown.
        pending!.Invoke();
        Assert.False(dialogInvoked);
    }

    [Fact]
    public async Task CancellationDuringEnqueue_DeniesWithoutInvokingDialog()
    {
        using var cts = new CancellationTokenSource();
        var dialogInvoked = false;
        var handler = Handler(
            (_, _) => { dialogInvoked = true; return Task.FromResult(ExecApprovalPromptOutcome.AllowOnce); },
            tryEnqueue: action =>
            {
                cts.Cancel();
                action();
                return true;
            });

        var result = await handler.PromptAsync(Request(), cts.Token);

        Assert.Equal(ExecApprovalPromptOutcome.Deny, result);
        Assert.False(dialogInvoked);
    }

    [Fact]
    public async Task CancellingAfterCompletion_IsANoOp()
    {
        using var cts = new CancellationTokenSource();
        var handler = Handler((_, _) => Task.FromResult(ExecApprovalPromptOutcome.AllowAlways));

        var result = await handler.PromptAsync(Request(), cts.Token);
        var ex = Record.Exception(cts.Cancel);

        Assert.Null(ex);
        Assert.Equal(ExecApprovalPromptOutcome.AllowAlways, result);
    }

    private sealed class CapturingLogger : IOpenClawLogger
    {
        public List<string> Warns { get; } = [];
        public List<string> Errors { get; } = [];
        public void Info(string m) { }
        public void Debug(string m) { }
        public void Warn(string m) => Warns.Add(m);
        public void Error(string m, Exception? _ = null) => Errors.Add(m);
    }
}
