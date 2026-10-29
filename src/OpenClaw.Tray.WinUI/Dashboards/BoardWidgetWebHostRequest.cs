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

/// <summary>
/// One widget document to host. <see cref="Scale"/> below 1 renders a
/// passive miniature for gallery previews.
/// </summary>
internal sealed record BoardWidgetWebHostRequest(BoardWidget Widget, Uri GatewayOrigin, double Scale = 1, int Attempt = 0)
{
    /// <summary>
    /// Identity of the loaded document. Ticket renewals and preview resizes keep
    /// it (neither the ticket nor the exact scale is part of the key); a new
    /// revision, view generation, sandbox policy, or preview/full mode replaces it.
    /// </summary>
    public string DocumentKey =>
        string.Join('\0',
            Widget.FrameUrl?.Split('?', 2)[0] ?? "",
            Widget.Revision,
            Widget.ViewGeneration ?? "",
            Widget.SandboxUrl ?? "",
            Widget.SandboxPort,
            Widget.SandboxOrigin ?? "",
            IsPreview);

    /// <summary>Previews are passive miniatures: no input, focus, or hit testing.</summary>
    public bool IsPreview => Scale < 1;
}
