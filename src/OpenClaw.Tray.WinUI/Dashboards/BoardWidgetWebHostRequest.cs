using OpenClaw.Shared.Dashboards;

namespace OpenClawTray.Dashboards;

internal enum BoardWidgetWebHostStatus
{
    Loading,
    Slow,
    Ready,
    Unauthorized,
    Failed,
    /// <summary>The sandbox proxy never became ready after repeated fresh relay pages.</summary>
    SandboxUnreachable,
}

internal sealed record BoardWidgetWebHostState(BoardWidgetWebHostStatus Status, string? Detail = null);

internal static class BoardWidgetRetryPolicy
{
    public const int MaxFrameAttempts = 3;
    public const int MaxCrashRestarts = 3;

    public static BoardWidgetWebHostStatus? TerminalStatus(bool crashed, int frameAttempts, int crashRestarts) =>
        crashed
            ? crashRestarts > MaxCrashRestarts ? BoardWidgetWebHostStatus.Failed : null
            : frameAttempts >= MaxFrameAttempts ? BoardWidgetWebHostStatus.SandboxUnreachable : null;
}

/// <summary>One widget document to host.</summary>
internal sealed record BoardWidgetWebHostRequest(
    BoardWidget Widget,
    Uri GatewayOrigin,
    int Attempt = 0,
    IBoardGatewayClient? ConnectionOwner = null,
    int ConnectionRevision = 0,
    bool DocumentCurrent = true,
    Func<bool>? IsOwnerCurrent = null)
{
    public bool OwnerCurrent => IsOwnerCurrent?.Invoke() ?? true;

    /// <summary>
    /// Identity of the loaded document. Ticket renewals keep it (the ticket is
    /// not part of the key); a new revision, view generation, or sandbox policy replaces it.
    /// </summary>
    public string DocumentKey =>
        string.Join('\0',
            GatewayOrigin.AbsoluteUri,
            Widget.FrameUrl?.Split('?', 2)[0] ?? "",
            Widget.Revision,
            Widget.ViewGeneration ?? "",
            Widget.SandboxUrl ?? "",
            Widget.SandboxPort,
            Widget.SandboxOrigin ?? "");
}
