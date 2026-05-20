namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Prompt request passed to <see cref="IExecApprovalV2PromptHandler.PromptAsync"/>.
/// </summary>
public sealed class ExecApprovalV2PromptRequest
{
    /// <summary>
    /// Command text as received from the agent — NOT sanitized. Presenters must strip
    /// control characters and BiDi overrides before rendering to prevent command spoofing.
    /// </summary>
    public required string DisplayCommand { get; init; }
    public string? Cwd { get; init; }
    public string? Host { get; init; }
    public required ExecSecurity Security { get; init; }
    public required ExecAsk Ask { get; init; }
    public required string AgentId { get; init; }
    public string? ResolvedPath { get; init; }
    /// <summary>
    /// Opaque key scoping AllowOnce/AllowAlways decisions to a conversation session.
    /// Minted by the gateway per session; null means no session context is available.
    /// Not safe to display — internal identifier only.
    /// </summary>
    public string? SessionKey { get; init; }
    /// <summary>Short identifier propagated through logging for this approval request.</summary>
    public required string CorrelationId { get; init; }
}
