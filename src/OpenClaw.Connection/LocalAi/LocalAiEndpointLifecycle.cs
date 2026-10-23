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
    /// <summary>Presentation hint only. Start must still revalidate the selected Gateway.</summary>
    bool RequiresNativeSelection => false;
    event EventHandler? SelectionChanged { add { } remove { } }
    bool AutomaticRecoveryEnabled => true;
    Task ReleaseOwnershipAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("There is no native Local AI ownership to release.");
    Task PrepareStartAsync(LocalAiResolvedInstall install, CancellationToken cancellationToken)
        => Task.CompletedTask;
    Task PrepareUnownedStopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
    /// <summary>Confirm withdrawal through the original owner without inferring provider ownership from damaged artifacts.</summary>
    Task ConfirmWithdrawnWithoutInstallAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("Restore the Local AI installation receipt before resolving its Gateway route.");
    Task<LocalAiEndpointLifecycleResult> CompleteStartAsync(LocalAiResolvedInstall install, CancellationToken cancellationToken)
        => Task.FromResult(LocalAiEndpointLifecycleResult.Ok());

    /// <summary>Durable owners persist explicit running intent; stateless transports need no receipt.</summary>
    Task SetAutomaticRecoveryEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Moves durable route ownership to a new model after the old route was withdrawn and runs
    /// <paramref name="commitManifest"/> inside the owner's receipt transaction. Durable owners
    /// journal the previous model first and hold their receipt lease until the journal is
    /// cleared, so a journal is only visible after an interrupted commit. A throwing commit
    /// restores the previous model and rethrows.
    /// </summary>
    Task CommitModelRebindAsync(LocalAiResolvedInstall previous, LocalAiResolvedInstall next,
        Func<Task> commitManifest, CancellationToken cancellationToken)
        => commitManifest();

    /// <summary>
    /// Settles a journal left by an interrupted <see cref="CommitModelRebindAsync"/> against the
    /// manifest returned by <paramref name="loadCommitted"/>, which durable owners invoke under
    /// their receipt lease. No-op when nothing is journaled.
    /// </summary>
    Task SettleModelRebindAsync(Func<CancellationToken, Task<LocalAiResolvedInstall?>> loadCommitted,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Records the non-Local-AI primary the Gateway returns to when Local AI withdraws.
    /// Idempotent, so a retry repairs an update whose manifest write did not land.
    /// </summary>
    Task SetFallbackModelAsync(string? model, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs <paramref name="writePrimary"/> to set a non-Local-AI Gateway primary, then records
    /// <paramref name="model"/> as the fallback, as one receipt transaction. Durable owners refuse
    /// before <paramref name="writePrimary"/> runs while a route write is unconfirmed: recovery can
    /// only settle a primary that matches the saved Local AI model or its fallback.
    /// </summary>
    async Task SetPrimaryAndFallbackModelAsync(string model, Func<CancellationToken, Task> writePrimary,
        CancellationToken cancellationToken)
    {
        await writePrimary(cancellationToken).ConfigureAwait(false);
        await SetFallbackModelAsync(model, cancellationToken).ConfigureAwait(false);
    }

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
