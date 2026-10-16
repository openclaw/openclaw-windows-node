namespace OpenClaw.Connection.LocalAi;

public sealed record LocalAiEndpointLifecycleResult(bool Success, string? Detail = null)
{
    public static LocalAiEndpointLifecycleResult Ok() => new(true);
    public static LocalAiEndpointLifecycleResult Failed(string detail) => new(false, detail);
}

public enum LocalAiQuiesceReason
{
    EndpointCycle,
    Teardown,
}

/// <summary>
/// Coordinates consumers of the app-owned endpoint with native process changes.
/// Implementations must remove managed routing before a listener can disappear,
/// and publish routing only after the replacement endpoint is proven healthy.
/// </summary>
public interface ILocalAiEndpointLifecycle
{
    bool HasReleasableOwnership => false;
    bool AutomaticRecoveryEnabled => true;
    Task ReleaseOwnershipAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("There is no native Local AI ownership to release.");
    Task PrepareStartAsync(LocalAiResolvedInstall install, CancellationToken cancellationToken)
        => Task.CompletedTask;
    Task<LocalAiEndpointLifecycleResult> CompleteStartAsync(LocalAiResolvedInstall install, CancellationToken cancellationToken)
        => Task.FromResult(LocalAiEndpointLifecycleResult.Ok());

    /// <summary>Durable owners persist explicit running intent; stateless transports need no receipt.</summary>
    Task SetAutomaticRecoveryEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Moves durable route ownership to a new model after the old route was withdrawn. Durable
    /// owners journal the previous model so <see cref="SettleModelRebindAsync"/> can recover
    /// from an interrupted manifest commit.
    /// </summary>
    Task RebindModelAsync(LocalAiResolvedInstall previous, LocalAiResolvedInstall next, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Resolves a journaled rebind against the committed installation: keeps the new model
    /// when the manifest names it, or restores the previous one when the manifest does.
    /// </summary>
    Task SettleModelRebindAsync(LocalAiResolvedInstall committed, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Records the non-Local-AI primary the Gateway returns to when Local AI withdraws.
    /// Idempotent, so a retry repairs an update whose manifest write did not land.
    /// </summary>
    Task SetFallbackModelAsync(string? model, CancellationToken cancellationToken) => Task.CompletedTask;

    Task<LocalAiEndpointLifecycleResult> QuiesceAsync(
        LocalAiResolvedInstall install,
        LocalAiQuiesceReason reason = LocalAiQuiesceReason.Teardown,
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
        LocalAiQuiesceReason reason = LocalAiQuiesceReason.Teardown,
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
