using OpenClaw.Connection;

namespace OpenClawTray.Services;

internal sealed record LocalAiGatewayDistroResolution(
    bool Success,
    string? DistroName,
    string? Detail,
    string? GatewayId = null)
{
    public static LocalAiGatewayDistroResolution Resolved(string distroName, string gatewayId) =>
        new(true, distroName, null, gatewayId);

    public static LocalAiGatewayDistroResolution Failed(string detail) =>
        new(false, null, detail);
}

internal interface ILocalAiGatewayDistroResolver
{
    LocalAiGatewayDistroResolution Resolve();
}

internal enum LocalAiSetupRoute
{
    Recovery,
    Provision,
    Blocked,
}

internal sealed record LocalAiRecoveryTarget(
    string GatewayId,
    string DistroName,
    int GatewayPort,
    string? ModelCatalogId,
    int? RequestedLocalAiPort);

internal sealed record LocalAiSetupResolution(
    LocalAiSetupRoute Route,
    LocalAiRecoveryTarget? RecoveryTarget = null);

internal static class LocalAiSetupRoutePolicy
{
    public static LocalAiSetupResolution Decide(
        IReadOnlyList<GatewayRecord> owners,
        bool hasLocalGateway,
        string? localGatewayId,
        bool hasDistro,
        bool hasDistroDataDirectory,
        bool distroIsAppOwned,
        string? installedModelCatalogId = null,
        int? installedRequestedLocalAiPort = null)
    {
        if (owners.Count == 1)
        {
            var owner = owners[0];
            if (hasLocalGateway &&
                string.Equals(localGatewayId, owners[0].Id, StringComparison.Ordinal) &&
                hasDistro &&
                distroIsAppOwned &&
                Uri.TryCreate(owner.Url, UriKind.Absolute, out var uri) &&
                uri.Port is > 0 and <= 65535 &&
                GatewayRecordEditing.AreEquivalentLoopbackEndpoints(
                    owner.Url,
                    $"ws://127.0.0.1:{uri.Port}"))
            {
                return new(
                    LocalAiSetupRoute.Recovery,
                    new LocalAiRecoveryTarget(
                        owner.Id,
                        GatewayRecordEditing.ResolveManagedDistroName(owner)!.Trim(),
                        uri.Port,
                        installedModelCatalogId,
                        installedRequestedLocalAiPort));
            }

            return new(LocalAiSetupRoute.Blocked);
        }

        return new(owners.Count == 0 &&
            !hasLocalGateway &&
            !hasDistro &&
            !hasDistroDataDirectory
                ? LocalAiSetupRoute.Provision
                : LocalAiSetupRoute.Blocked);
    }
}

/// <summary>
/// Resolves and validates the WSL distro that owns the Local AI installation.
///
/// This module pins the Local AI lifecycle to exactly one explicitly setup-managed
/// local WSL gateway. It enforces ownership stability: once a gateway is bound as the
/// Local AI owner, any registry change that would redirect lifecycle commands to a
/// different distro is rejected.
///
/// The resolver runs in two phases:
///   1. Constructor-time binding: attempts to bind to an existing single setup-managed
///      gateway that meets ownership criteria. If none exists (first-run), the resolver
///      remains unbound until a registry update arrives.
///   2. Resolve() calls: revalidate the current registry state and return a resolution
///      record with the distro name and gateway ID, or a failure detail if ownership
///      changed or prerequisites are missing.
///
/// Use <see cref="LocalAiSetupRoutePolicy.Decide"/> to determine the setup route
/// (Provision/Recovery/Blocked) based on current system state. This resolver validates
/// that an existing Local AI binding remains valid before executing lifecycle commands.
///
/// <example>
/// // Basic resolution with error handling
/// var registry = new GatewayRegistry(dataDir);
/// var resolver = new LocalAiGatewayDistroResolver(registry);
///
/// LocalAiGatewayDistroResolution result = resolver.Resolve();
/// if (result.Success)
/// {
///     Console.WriteLine($"Local AI using distro '{result.DistroName}' (gateway {result.GatewayId})");
///     // Proceed with lifecycle commands
/// }
/// else
/// {
///     Console.WriteLine($"Local AI routing failed: {result.Detail}");
///     // Show setup prompt or recovery options
/// }
///
/// // Combine with setup route decision
/// LocalAiSetupResolution setup = LocalAiSetupRoutePolicy.Decide(
///     owners: registry.GetAll(),
///     hasLocalGateway: false,
///     localGatewayId: null,
///     hasDistro: false,
///     hasDistroDataDirectory: false,
///     distroIsAppOwned: false);
///
/// switch (setup.Route)
/// {
///     case LocalAiSetupRoute.Provision:
///         // Start first-time setup
///         break;
///     case LocalAiSetupRoute.Recovery:
///         // Use resolver to obtain the target for recovery
///         var resolution = resolver.Resolve();
///         var target = resolution.Success
///             ? new LocalAiRecoveryTarget(
///                 resolution.GatewayId!,
///                 resolution.DistroName!,
///                 GatewayPort: 1234,
///                 ModelCatalogId: null,
///                 RequestedLocalAiPort: null)
///             : null;
///         if (target is not null)
///         {
///             // Execute recovery against the resolved target
///         }
///         break;
///     case LocalAiSetupRoute.Blocked:
///         // Show error or guide user to fix configuration
///         break;
/// }
/// </example>
/// </summary>
internal sealed class LocalAiGatewayDistroResolver : ILocalAiGatewayDistroResolver
{
    private readonly GatewayRegistry? _registry;
    private readonly object _bindingGate = new();
    private string? _gatewayId;
    private string? _distroName;
    private readonly string? _initialFailure;

    public LocalAiGatewayDistroResolver(GatewayRegistry? registry)
    {
        _registry = registry;
        if (registry is null)
        {
            _initialFailure =
                "The gateway registry is unavailable; refusing to change the Local AI gateway route.";
            return;
        }

        IReadOnlyList<GatewayRecord> owners = FindOwners(registry.GetAll());
        if (owners.Count == 0)
        {
            // First-run setup has not saved its owner yet. Bind once when the
            // canonical registry adopts the completed setup, never on a disk-only discovery.
            return;
        }

        if (owners.Count != 1)
        {
            _initialFailure =
                "Multiple explicit setup-managed WSL gateways could own the Local AI installation; refusing to choose one.";
            return;
        }

        GatewayRecord owner = owners[0];
        _gatewayId = owner.Id;
        _distroName = GatewayRecordEditing.ResolveManagedDistroName(owner)!.Trim();
    }

    public LocalAiGatewayDistroResolution Resolve()
    {
        lock (_bindingGate)
            return ResolveCore();
    }

    private LocalAiGatewayDistroResolution ResolveCore()
    {
        if (_initialFailure is not null)
            return LocalAiGatewayDistroResolution.Failed(_initialFailure);
        if (_registry is null)
        {
            return LocalAiGatewayDistroResolution.Failed(
                "The gateway registry is unavailable; refusing to change the Local AI gateway route.");
        }

        IReadOnlyList<GatewayRecord> owners = FindOwners(_registry.GetAll());
        if (_gatewayId is null && owners.Count == 0)
            return LocalAiGatewayDistroResolution.Failed(
                "No explicit setup-managed WSL gateway owns the Local AI installation; refusing to change its gateway route.");
        if (_gatewayId is null && owners.Count == 1)
        {
            _gatewayId = owners[0].Id;
            _distroName = GatewayRecordEditing.ResolveManagedDistroName(owners[0])!.Trim();
        }
        if (_gatewayId is null || _distroName is null || owners.Count != 1 ||
            !string.Equals(owners[0].Id, _gatewayId, StringComparison.Ordinal) ||
            !string.Equals(
                GatewayRecordEditing.ResolveManagedDistroName(owners[0])?.Trim(),
                _distroName,
                StringComparison.Ordinal))
        {
            return LocalAiGatewayDistroResolution.Failed(
                "The setup-managed WSL gateway owner changed after Local AI startup; refusing to route lifecycle commands to an unknown distro.");
        }

        return LocalAiGatewayDistroResolution.Resolved(_distroName, _gatewayId);
    }

    internal static IReadOnlyList<GatewayRecord> FindOwners(IEnumerable<GatewayRecord> records) =>
        records
            .Where(record =>
                GatewayRecordEditing.IsSetupManagedLocalRecord(record) &&
                !string.IsNullOrWhiteSpace(record.Id) &&
                !string.IsNullOrWhiteSpace(
                    GatewayRecordEditing.ResolveManagedDistroName(record)))
            .ToArray();
}
