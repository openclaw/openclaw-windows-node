// <summary>
// Defines the endpoint-routing lifecycle shared by the Local AI runtime and its Gateway owner.
// Implementations quiesce routing before native listener changes and publish it only after a healthy
// endpoint is verified. Callers use the contract around start, stop, route ownership, and model changes.
// Usage:
//   await lifecycle.PrepareStartAsync(install, cancellationToken);
//   LocalAiEndpointLifecycleResult result = await lifecycle.PublishAsync(install, cancellationToken);
//   if (!result.Success) { /* preserve the receipt and report result.Detail */ }
//   await lifecycle.SetPrimaryAndFallbackModelAsync(model, WritePrimaryAsync, cancellationToken);
// </summary>
namespace OpenClaw.Connection.LocalAi;

/// <summary>Reports whether an endpoint lifecycle operation completed and, on failure, its user-facing detail.</summary>
public sealed record LocalAiEndpointLifecycleResult(bool Success, string? Detail = null)
{
    /// <summary>Creates a successful lifecycle result.</summary>
    public static LocalAiEndpointLifecycleResult Ok() => new(true);
    /// <summary>Creates a failed lifecycle result with user-facing detail.</summary>
    public static LocalAiEndpointLifecycleResult Failed(string detail) => new(false, detail);
}

/// <summary>Identifies why callers are withdrawing the managed Gateway route.</summary>
public enum LocalAiQuiesceReason
{
    EndpointCycle,
    Teardown,
}

/// <summary>Reports the observable stages of native runtime startup.</summary>
public enum LocalAiRuntimeStartStage { StartingRuntime, CheckingConfiguration, PublishingProvider, VerifyingEndpoint }

/// <summary>
/// Coordinates consumers of the app-owned endpoint with native process changes.
/// Implementations must remove managed routing before a listener can disappear,
/// and publish routing only after the replacement endpoint is proven healthy.
/// </summary>
public interface ILocalAiEndpointLifecycle
{
    /// <summary>Whether this lifecycle has a durable Gateway route that callers can release.</summary>
    bool HasReleasableOwnership => false;
    /// <summary>Presentation hint only. Start must still revalidate the selected Gateway.</summary>
    bool RequiresNativeSelection => false;
    /// <summary>Raised when the selected native Gateway changes.</summary>
    event EventHandler? SelectionChanged { add { } remove { } }
    /// <summary>Whether the owner should automatically recover the endpoint after a process exit.</summary>
    bool AutomaticRecoveryEnabled => true;
    /// <summary>Releases durable ownership after callers have stopped and withdrawn the route.</summary>
    Task ReleaseOwnershipAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("There is no native Local AI ownership to release.");
    /// <summary>Validates and prepares durable routing before the runtime starts.</summary>
    Task PrepareStartAsync(LocalAiResolvedInstall install, CancellationToken cancellationToken)
        => Task.CompletedTask;
    /// <summary>Prepares a stop for a runtime without a loadable installation receipt.</summary>
    Task PrepareUnownedStopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
    /// <summary>Confirm withdrawal through the original owner without inferring provider ownership from damaged artifacts.</summary>
    Task ConfirmWithdrawnWithoutInstallAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("Restore the Local AI installation receipt before resolving its Gateway route.");
    /// <summary>Publishes a verified endpoint after runtime startup.</summary>
    Task<LocalAiEndpointLifecycleResult> CompleteStartAsync(LocalAiResolvedInstall install, CancellationToken cancellationToken)
        => Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
    /// <summary>Publishes a verified endpoint and reports startup progress.</summary>
    Task<LocalAiEndpointLifecycleResult> CompleteStartAsync(LocalAiResolvedInstall install, CancellationToken cancellationToken,
        IProgress<LocalAiRuntimeStartStage>? progress) => CompleteStartAsync(install, cancellationToken);

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

    /// <summary>Withdraws managed routing before the endpoint changes or is torn down.</summary>
    Task<LocalAiEndpointLifecycleResult> QuiesceAsync(
        LocalAiResolvedInstall install,
        LocalAiQuiesceReason reason = LocalAiQuiesceReason.Teardown,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes managed routing for a verified Local AI endpoint.</summary>
    Task<LocalAiEndpointLifecycleResult> PublishAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes managed routing and reports the publication startup stage.</summary>
    Task<LocalAiEndpointLifecycleResult> PublishAsync(LocalAiResolvedInstall install, CancellationToken cancellationToken,
        IProgress<LocalAiRuntimeStartStage>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(LocalAiRuntimeStartStage.PublishingProvider);
        return PublishAsync(install, cancellationToken);
    }
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
