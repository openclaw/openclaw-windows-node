using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// End-to-end pipeline tests: the real coordinator wired to the real prompt handler
/// (ExecApprovalV2UiPromptHandler) through a capturing dialog delegate. Exercises the
/// full path — validate, normalize, resolve, evaluate, prompt, second pass, decision —
/// with the prompt handler that ships in production, not an outcome stub. The only
/// piece not covered here is the physical WinUI window (verified manually).
/// </summary>
public class ExecApprovalV2PipelineIntegrationTests : IDisposable
{
    private const int Rlo = 0x202E;   // right-to-left override (visual spoof)
    private const int Zwsp = 0x200B;  // zero-width space

    private readonly string _dir;

    public ExecApprovalV2PipelineIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"oca-pipeline-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, "exec-approvals.json"),
            """{"version":1,"defaults":{"security":"full","ask":"always"}}""");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string U(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static NodeInvokeRequest Req(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        return new NodeInvokeRequest { Id = "r1", Command = "system.run", Args = doc.RootElement.Clone() };
    }

    private void WriteStore(string json)
        => File.WriteAllText(Path.Combine(_dir, "exec-approvals.json"), json);

    // Coordinator with a real handler whose dialog records whether it was ever shown.
    private ExecApprovalsCoordinator MakeCoordinatorTracking(
        ExecApprovalPromptOutcome dialogOutcome, Action markShown)
        => MakeCoordinator(dialog: _ => { markShown(); return dialogOutcome; });

    // Real handler + synchronous dispatcher + a dialog delegate that records the view
    // it was handed and returns a scripted outcome.
    private ExecApprovalsCoordinator MakeCoordinator(
        Func<ExecApprovalPromptView, ExecApprovalPromptOutcome> dialog,
        Action<ExecApprovalPromptView>? onView = null)
    {
        var log = NullLogger.Instance;
        var handler = new ExecApprovalV2UiPromptHandler(
            tryEnqueue: action => { action(); return true; },
            showDialog: (view, _) =>
            {
                onView?.Invoke(view);
                return Task.FromResult(dialog(view));
            },
            logger: log);
        return new ExecApprovalsCoordinator(
            new ExecApprovalsStore(_dir, log),
            AlwaysCanPresentEvaluator.Instance,
            handler,
            log);
    }

    [Fact]
    public async Task AttackCommand_ReachesDialogSanitized_AllowOnceProducesAllow()
    {
        var attackArg = "safe" + U(Rlo) + "evil";
        var request = Req($$"""{"command":["where","{{attackArg}}"]}""");

        ExecApprovalPromptView? captured = null;
        var result = await MakeCoordinator(
            dialog: _ => ExecApprovalPromptOutcome.AllowOnce,
            onView: v => captured = v).HandleAsync(request, "p1");

        Assert.True(result.IsAllow);
        Assert.NotNull(captured);
        // The raw override reached the pipeline but was escaped before the dialog saw it.
        // Ordinal comparison: culture-sensitive search treats format characters as
        // ignorable and would match them in any string.
        Assert.DoesNotContain(U(Rlo), captured!.CommandText, StringComparison.Ordinal);
        Assert.Contains(@"\u{202E}", captured.CommandText);
    }

    [Fact]
    public async Task SpoofedCwd_ReachesDialogSanitized()
    {
        var spoofedCwd = "C:\\work" + U(Zwsp) + "dir";
        var request = Req($$"""{"command":["where","hello"],"cwd":"{{spoofedCwd.Replace("\\", "\\\\")}}"}""");

        ExecApprovalPromptView? captured = null;
        await MakeCoordinator(
            dialog: _ => ExecApprovalPromptOutcome.Deny,
            onView: v => captured = v).HandleAsync(request, "p2");

        Assert.NotNull(captured);
        Assert.NotNull(captured!.CwdText);
        Assert.DoesNotContain(U(Zwsp), captured.CwdText!, StringComparison.Ordinal);
        Assert.Contains(@"\u{200B}", captured.CwdText!);
    }

    [Fact]
    public async Task DialogDenies_ReturnsUserDenied()
    {
        var result = await MakeCoordinator(dialog: _ => ExecApprovalPromptOutcome.Deny)
            .HandleAsync(Req("""{"command":["where","hello"]}"""), "p3");

        Assert.Equal(ExecApprovalV2Code.UserDenied, result.Code);
    }

    [Fact]
    public async Task DialogAllowOnce_ReturnsAllow()
    {
        var result = await MakeCoordinator(dialog: _ => ExecApprovalPromptOutcome.AllowOnce)
            .HandleAsync(Req("""{"command":["where","hello"]}"""), "p4");

        Assert.True(result.IsAllow);
    }

    [Fact]
    public async Task MaliciousDialogReturnsPlainAllow_NeutralizedBeforeCoordinator()
    {
        // A rogue dialog returning plain Allow is clamped to Deny by the handler itself,
        // so the coordinator never sees an Allow: the end-to-end result is a denial, not
        // the coordinator's own invariant-violation path. Defense in depth.
        var result = await MakeCoordinator(dialog: _ => ExecApprovalPromptOutcome.Allow)
            .HandleAsync(Req("""{"command":["where","hello"]}"""), "p5");

        Assert.Equal(ExecApprovalV2Code.UserDenied, result.Code);
    }

    // ── Attack battery: the three hard-deny paths must cut BEFORE the prompt and
    //    cannot be flipped by the dialog, even when the dialog would say Allow Always. ──

    [Fact]
    public async Task SecurityDeny_CannotBeOverriddenByDialog_AndNeverShowsPrompt()
    {
        WriteStore("""{"version":1,"defaults":{"security":"deny"}}""");
        var shown = false;
        var result = await MakeCoordinatorTracking(ExecApprovalPromptOutcome.AllowAlways, () => shown = true)
            .HandleAsync(Req("""{"command":["where","hello"]}"""), "a1");

        Assert.Equal(ExecApprovalV2Code.SecurityDeny, result.Code);
        Assert.False(shown);
        Assert.Null(result.Execution);
    }

    [Fact]
    public async Task AskDeny_SecurityFull_DeniesWithoutShowingPrompt()
    {
        WriteStore("""{"version":1,"defaults":{"security":"full","ask":"deny"}}""");
        var shown = false;
        var result = await MakeCoordinatorTracking(ExecApprovalPromptOutcome.AllowAlways, () => shown = true)
            .HandleAsync(Req("""{"command":["where","hello"]}"""), "a2");

        Assert.Equal(ExecApprovalV2Code.AskDeny, result.Code);
        Assert.False(shown);
    }

    [Fact]
    public async Task AllowlistMiss_SecurityAllowlist_AskOff_DeniesWithoutShowingPrompt()
    {
        WriteStore("""{"version":1,"defaults":{"security":"allowlist","ask":"off"}}""");
        var shown = false;
        var result = await MakeCoordinatorTracking(ExecApprovalPromptOutcome.AllowAlways, () => shown = true)
            .HandleAsync(Req("""{"command":["where","hello"]}"""), "a3");

        Assert.Equal(ExecApprovalV2Code.AllowlistMiss, result.Code);
        Assert.False(shown);
    }

    // The command that gets executed is the canonical argv evaluated and shown — never
    // re-derived from raw text — so approve-what-you-see holds at the execution boundary.
    [Fact]
    public async Task ApprovedExecution_CarriesCanonicalArgv_ForActualExecution()
    {
        var result = await MakeCoordinator(dialog: _ => ExecApprovalPromptOutcome.AllowOnce)
            .HandleAsync(Req("""{"command":["where","hello"]}"""), "a4");

        Assert.True(result.IsAllow);
        Assert.NotNull(result.Execution);
        Assert.NotEmpty(result.Execution!.Argv);
        Assert.Contains("hello", result.Execution.Argv);
    }

    // A command host (cmd, powershell, ...) re-parses its argument tail, so a stored rule
    // for the host executable would authorize arbitrary future commands. An interactive
    // Allow Once is honored (the user approved this exact invocation and the dialog showed
    // the full sanitized text), but Allow Always must fail closed instead of persisting.
    [Fact]
    public async Task CommandHost_AllowOnce_YieldsExecutionForThisInvocationOnly()
    {
        var attack = Req("""{"command":["cmd","/c","echo","SAFE&calc.exe"]}""");

        ExecApprovalPromptView? captured = null;
        var result = await MakeCoordinator(
            dialog: _ => ExecApprovalPromptOutcome.AllowOnce,
            onView: v => captured = v).HandleAsync(attack, "shell-1");

        Assert.True(result.IsAllow);
        Assert.NotNull(result.Execution);
        // The dialog is the barrier here: the user saw the complete wrapper invocation,
        // including the argument tail the host will re-parse.
        Assert.NotNull(captured);
        Assert.Contains("SAFE&calc.exe", captured!.CommandText);
    }

    [Fact]
    public async Task CommandHost_AllowAlways_FailsClosedWithNoExecution()
    {
        var attack = Req("""{"command":["cmd","/c","echo","SAFE&calc.exe"]}""");
        var result = await MakeCoordinator(dialog: _ => ExecApprovalPromptOutcome.AllowAlways)
            .HandleAsync(attack, "shell-2");

        Assert.False(result.IsAllow);
        Assert.Null(result.Execution);
        Assert.Equal(ExecApprovalV2Code.ValidationFailed, result.Code);
    }
}
