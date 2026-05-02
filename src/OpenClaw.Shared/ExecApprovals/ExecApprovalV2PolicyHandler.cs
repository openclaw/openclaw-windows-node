using System.Threading.Tasks;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// V2 exec approval handler: validates input and evaluates the configured
/// ExecApprovalPolicy to decide Allow / SecurityDeny / AllowlistMiss.
///
/// This is the "PR7 coordinator" referred to in SystemCapability comments.
/// When installed via SetV2Handler and a command is approved, SystemCapability
/// executes it via the command runner already wired on the legacy path.
///
/// Shell-wrapper expansion mirrors the legacy path: after the top-level policy
/// check passes, each shell-wrapped sub-command is also evaluated so that
/// `cmd /c "rm /"` is not approved by a rule for `cmd`.
/// </summary>
public sealed class ExecApprovalV2PolicyHandler : IExecApprovalV2Handler
{
    private readonly ExecApprovalPolicy _policy;
    private readonly IOpenClawLogger _logger;

    public ExecApprovalV2PolicyHandler(ExecApprovalPolicy policy, IOpenClawLogger logger)
    {
        _policy = policy;
        _logger = logger;
    }

    public Task<ExecApprovalV2Result> HandleAsync(NodeInvokeRequest request, string correlationId)
    {
        // Step 1: Structural input validation.
        var validation = ExecApprovalV2InputValidator.Validate(request);
        if (!validation.IsValid)
        {
            _logger.Info($"[exec-v2] corr={correlationId} validation-failed reason={validation.Error!.Reason}");
            return Task.FromResult(validation.Error!);
        }

        var validated = validation.Request!;
        var commandString = ShellQuoting.FormatExecCommand(validated.Argv);

        // Step 2: Top-level policy check.
        var topResult = _policy.Evaluate(commandString, validated.Shell);
        _logger.Info($"[exec-v2] corr={correlationId} policy={topResult.Action} pattern={topResult.MatchedPattern ?? "(default)"}");

        if (topResult.Action == ExecApprovalAction.Deny)
            return Task.FromResult(ExecApprovalV2Result.SecurityDeny(topResult.Reason ?? "policy-deny"));

        if (topResult.Action == ExecApprovalAction.Prompt)
            return Task.FromResult(ExecApprovalV2Result.AllowlistMiss("prompt-required"));

        // Step 3: Shell-wrapper expansion — ensure wrapped sub-commands also pass policy.
        var parseResult = ExecShellWrapperParser.Expand(commandString, validated.Shell);
        if (!string.IsNullOrWhiteSpace(parseResult.Error))
        {
            _logger.Warn($"[exec-v2] corr={correlationId} shell-parse-denied reason={parseResult.Error}");
            return Task.FromResult(ExecApprovalV2Result.SecurityDeny(parseResult.Error));
        }

        foreach (var target in parseResult.Targets)
        {
            var innerResult = _policy.Evaluate(target.Command, target.Shell);
            if (innerResult.Action == ExecApprovalAction.Deny)
            {
                _logger.Warn($"[exec-v2] corr={correlationId} inner-policy-deny cmd={target.Command}");
                return Task.FromResult(ExecApprovalV2Result.SecurityDeny(innerResult.Reason ?? "inner-policy-deny"));
            }
            if (innerResult.Action == ExecApprovalAction.Prompt)
                return Task.FromResult(ExecApprovalV2Result.AllowlistMiss("inner-prompt-required"));
        }

        return Task.FromResult(ExecApprovalV2Result.Allowed());
    }
}
