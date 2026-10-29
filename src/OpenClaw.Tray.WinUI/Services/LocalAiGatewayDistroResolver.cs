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
/// Resolves the WSL distro that owns the Local AI installation and rejects any
/// registry change that would redirect lifecycle commands to a different distro.
/// The resolver pins a unique owner at construction when one is already registered.
/// During first-run setup it binds on the first <see cref="Resolve"/> after the
/// canonical registry adopts the completed setup; subsequent calls revalidate
/// the pinned owner against the current registry.
///
/// Use <see cref="LocalAiSetupRouteResolver.ResolveAsync"/> (not raw
/// <see cref="LocalAiSetupRoutePolicy.Decide"/> flags) for setup route decisions:
/// it observes real gateway/distro state and supplies the RecoveryTarget.
///
/// <example>
/// var resolver = new LocalAiGatewayDistroResolver(registry);
/// LocalAiGatewayDistroResolution result = resolver.Resolve();
/// if (result.Success) { /* run lifecycle commands against result.DistroName */ }
/// else { /* show setup or recovery guidance for result.Detail */ }
///
/// // Setup route (production path, also the only supported way to get RecoveryTarget):
/// LocalAiSetupResolution setup = await new LocalAiSetupRouteResolver(
///     () => registry, dataDir, localDataDir, defaultDistroName).ResolveAsync(ct);
/// LocalAiRecoveryTarget? target = setup.Route is LocalAiSetupRoute.Recovery
///     ? setup.RecoveryTarget : null;
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
