using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

/// <summary>Immutable read state shared by Usage sections and the host-owned provider source.</summary>
internal enum UsageLoadPhase
{
    Idle,
    Loading,
    Loaded,
    Failed,
}

/// <summary>One section's read. Failure names the outcome when the Gateway sent no error text.</summary>
internal sealed record UsageRead<T>(UsageLoadPhase Phase, T? Value, string? Error, UsageReadStatus? Failure = null) where T : class
{
    public static readonly UsageRead<T> Idle = new(UsageLoadPhase.Idle, null, null);
    public bool IsLoading => Phase == UsageLoadPhase.Loading;
}
