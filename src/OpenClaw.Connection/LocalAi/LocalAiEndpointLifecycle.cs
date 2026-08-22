namespace OpenClaw.Connection.LocalAi;

public sealed record LocalAiEndpointLifecycleResult(bool Success, string? Detail = null)
{
    public static LocalAiEndpointLifecycleResult Ok() => new(true);
    public static LocalAiEndpointLifecycleResult Failed(string detail) => new(false, detail);
}

/// <summary>
/// Coordinates consumers of the app-owned endpoint with native process changes.
/// Implementations must remove managed routing before a listener can disappear,
/// and publish routing only after the replacement endpoint is proven healthy.
/// </summary>
public interface ILocalAiEndpointLifecycle
{
    Task<LocalAiEndpointLifecycleResult> QuiesceAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default);

    Task<LocalAiEndpointLifecycleResult> PublishAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default);
}

internal sealed class NullLocalAiEndpointLifecycle : ILocalAiEndpointLifecycle
{
    public static NullLocalAiEndpointLifecycle Instance { get; } = new();

    public Task<LocalAiEndpointLifecycleResult> QuiesceAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
    }

    public Task<LocalAiEndpointLifecycleResult> PublishAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
    }
}
