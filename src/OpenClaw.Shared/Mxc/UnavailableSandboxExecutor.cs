namespace OpenClaw.Shared.Mxc;

/// <summary>
/// <see cref="ISandboxExecutor"/> implementation that always throws
/// <see cref="SandboxUnavailableException"/>. Used when MXC is not installed
/// on the host so <see cref="MxcCommandRunner"/> can still honor the
/// <see cref="SettingsData.SystemRunSandboxEnabled"/> toggle: when sandbox
/// is enabled and MXC is absent, the invocation is denied (fail-closed)
/// rather than silently routed to the host.
/// </summary>
public sealed class UnavailableSandboxExecutor : ISandboxExecutor
{
    public string Name => "mxc-unavailable";
    public bool IsContained => false;

    private readonly string _reason;

    public UnavailableSandboxExecutor(string reason)
    {
        _reason = reason;
    }

    public Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken ct = default)
    {
        throw new SandboxUnavailableException(_reason);
    }
}
