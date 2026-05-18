namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Prompt request passed to <see cref="IExecApprovalV2PromptHandler.PromptAsync"/>.
/// DisplayCommand arrives unsanitized — the presenter sanitizes before rendering.
/// </summary>
public sealed class ExecApprovalV2PromptRequest
{
    public required string DisplayCommand { get; init; }
    public string? Cwd { get; init; }
    public string? Host { get; init; }
    public required ExecSecurity Security { get; init; }
    public required ExecAsk Ask { get; init; }
    public required string AgentId { get; init; }
    public string? ResolvedPath { get; init; }
    public string? SessionKey { get; init; }
}
