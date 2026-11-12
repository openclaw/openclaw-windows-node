// <summary>
// Shared runtime state models for the local AI subsystem: runtime/ownership/model-availability
// enums, LocalAiModelEvidence (digest-backed model verification), LocalAiRuntimeSnapshot
// (published state including process and KV-cache details), its change-event args, and the
// ILocalAiRuntime contract that LlamaServerRuntimeService implements.
// </summary>
// Usage:
//   runtime.StateChanged += (_, args) => UpdateUi(args.Snapshot);
//   LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync(cancellationToken);
//   if (snapshot.State == LocalAiRuntimeState.Healthy) { /* use snapshot.Endpoint */ }
//   snapshot = await runtime.ReplaceInstallAsync(replacement, cancellationToken);
//   // Replacement stops the runtime. Call EnsureStartedAsync to publish the new route.
// </summary>
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Connection.LocalAi;

public enum LocalAiRuntimeState
{
    NotInstalled,
    Stopped,
    Starting,
    Healthy,
    Stopping,
    Conflict,
    Failed,
}

public enum LocalAiOwnership
{
    None,
    CompanionManaged,
}

/// <summary>
/// Evidence-backed availability of the exact model recorded in the managed manifest.
/// Verified and Loaded states always carry the artifact digest and observed size.
/// </summary>
public enum LocalAiModelAvailabilityState
{
    Unknown,
    NotInstalled,
    Verified,
    Loaded,
}

/// <summary>Records time-stamped evidence for the model the managed runtime observed.</summary>
public sealed record LocalAiModelEvidence
{
    public LocalAiModelEvidence(
        LocalAiModelAvailabilityState state,
        DateTimeOffset observedAtUtc,
        string? sha256 = null,
        long? sizeBytes = null,
        string? serverModelId = null)
    {
        if (state is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded)
        {
            if (sha256?.Length != 64 || sha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new ArgumentException("Verified model evidence requires a lowercase SHA-256 digest.", nameof(sha256));
            if (sizeBytes is null or <= 0)
                throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Verified model evidence requires a positive observed size.");
        }
        else if (sha256 is not null || sizeBytes is not null || serverModelId is not null)
        {
            throw new ArgumentException("Unknown or missing model evidence cannot carry artifact or server claims.");
        }

        if (state == LocalAiModelAvailabilityState.Loaded && string.IsNullOrWhiteSpace(serverModelId))
            throw new ArgumentException("Loaded model evidence requires the server-observed model identifier.", nameof(serverModelId));
        if (state != LocalAiModelAvailabilityState.Loaded && serverModelId is not null)
            throw new ArgumentException("Only loaded model evidence can carry a server-observed model identifier.", nameof(serverModelId));

        State = state;
        ObservedAtUtc = observedAtUtc;
        Sha256 = sha256;
        SizeBytes = sizeBytes;
        ServerModelId = serverModelId;
    }

    public LocalAiModelAvailabilityState State { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string? Sha256 { get; }
    public long? SizeBytes { get; }
    public string? ServerModelId { get; }

    public static LocalAiModelEvidence Unknown(DateTimeOffset now) =>
        new(LocalAiModelAvailabilityState.Unknown, now);

    public static LocalAiModelEvidence NotInstalled(DateTimeOffset now) =>
        new(LocalAiModelAvailabilityState.NotInstalled, now);
}

/// <summary>Publishes the Local AI runtime state, endpoint, process identity, and verified model evidence.</summary>
public sealed record LocalAiRuntimeSnapshot(
    LocalAiRuntimeState State,
    LocalAiOwnership Ownership,
    Uri Endpoint,
    string? EngineVersion,
    string? ModelId,
    LocalAiModelEvidence ModelEvidence,
    int? ProcessId,
    DateTimeOffset? ProcessStartedAtUtc,
    string? Detail,
    DateTimeOffset UpdatedAtUtc,
    int? ContextLength = null,
    KvCachePrecision? KeyCachePrecision = null,
    KvCachePrecision? ValueCachePrecision = null,
    KvCachePrecision? DraftKeyCachePrecision = null,
    KvCachePrecision? DraftValueCachePrecision = null)
{
    /// <summary>False only when the runtime confirmed publication or terminal route cleanup.</summary>
    public bool GatewayRouteRequiresResolution { get; init; } = true;

    public static LocalAiRuntimeSnapshot Initial(Uri endpoint, DateTimeOffset now) =>
        new(
            LocalAiRuntimeState.Stopped,
            LocalAiOwnership.None,
            endpoint,
            null,
            null,
            LocalAiModelEvidence.Unknown(now),
            null,
            null,
            null,
            now);
}

/// <summary>Provides the snapshot supplied with a Local AI runtime state change.</summary>
public sealed class LocalAiRuntimeSnapshotChangedEventArgs(LocalAiRuntimeSnapshot snapshot) : EventArgs
{
    public LocalAiRuntimeSnapshot Snapshot { get; } = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
}

/// <summary>Controls the companion-managed Local AI runtime and its durable Gateway route.</summary>
public interface ILocalAiRuntime : IAsyncDisposable
{
    /// <summary>Whether this runtime has durable Gateway ownership that callers may release.</summary>
    bool HasReleasableOwnership => false;
    /// <summary>Whether callers must choose a native Gateway before starting this runtime.</summary>
    bool RequiresNativeSelection => false;
    /// <summary>Raised when the selected native Gateway changes.</summary>
    event EventHandler? SelectionChanged { add { } remove { } }
    /// <summary>Releases durable Gateway ownership after the route has been withdrawn.</summary>
    Task<LocalAiRuntimeSnapshot> ReleaseOwnershipAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("There is no native Local AI ownership to release.");
    /// <summary>
    /// Withdraws and stops the current model, then commits <paramref name="replacement"/> as the
    /// installation. The runtime stays stopped; callers start it explicitly.
    /// </summary>
    Task<LocalAiRuntimeSnapshot> ReplaceInstallAsync(LocalAiInstallManifest replacement, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("This Local AI runtime cannot change its installation.");
    /// <summary>Changes the non-Local-AI primary the Gateway returns to when Local AI withdraws.</summary>
    Task SetGatewayFallbackModelAsync(string? model, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("This Local AI runtime cannot change its Gateway fallback.");
    /// <summary>
    /// Writes a non-Local-AI Gateway primary through <paramref name="writePrimary"/> and records it as
    /// the fallback. Refuses, without calling <paramref name="writePrimary"/>, while the Gateway route
    /// has an unconfirmed write. When <paramref name="writePrimary"/> throws, neither the receipt nor the
    /// manifest fallback changes.
    /// </summary>
    Task SetGatewayPrimaryAndFallbackModelAsync(string model, Func<CancellationToken, Task> writePrimary,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("This Local AI runtime cannot change its Gateway fallback.");
    /// <summary>Returns the latest published runtime state.</summary>
    LocalAiRuntimeSnapshot Snapshot { get; }
    /// <summary>Raised after the runtime publishes a new snapshot.</summary>
    event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged;
    /// <summary>Starts the managed runtime and publishes its Gateway route when healthy.</summary>
    Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default);
    /// <summary>Starts the managed runtime while reporting observable startup stages.</summary>
    Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken,
        IProgress<LocalAiRuntimeStartStage>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(LocalAiRuntimeStartStage.StartingRuntime);
        return EnsureStartedAsync(cancellationToken);
    }
    /// <summary>Resumes an eligible stopped runtime.</summary>
    Task<LocalAiRuntimeSnapshot> ResumeAsync(CancellationToken cancellationToken = default);
    /// <summary>Reconciles a stopped runtime with its durable Gateway route.</summary>
    Task<LocalAiRuntimeSnapshot> ReconcileStoppedAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshot);
    /// <summary>Stops the runtime and withdraws its managed Gateway route.</summary>
    Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default);
    /// <summary>Restarts the runtime after stopping and withdrawing its route.</summary>
    Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default);
    /// <summary>Refreshes the runtime state from current process and endpoint observations.</summary>
    Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}
