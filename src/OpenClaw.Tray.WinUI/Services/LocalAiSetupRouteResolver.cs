using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.SetupEngine;

namespace OpenClawTray.Services;

/// <summary>Shared route inspection for Settings and the already-open setup window.</summary>
internal sealed class LocalAiSetupRouteResolver(
    Func<GatewayRegistry?> getRegistry, string dataDir, string localDataDir, string defaultDistroName,
    Func<string, string?, ExistingConfigDetector.ExistingConfig>? detectExisting = null)
{
    public async Task<LocalAiSetupResolution> ResolveAsync(CancellationToken ct = default)
    {
        var registry = getRegistry();
        if (registry is null)
            return new(LocalAiSetupRoute.Blocked);
        var owners = LocalAiGatewayDistroResolver.FindOwners(registry.GetAll());
        var distro = owners.Count == 1
            ? GatewayRecordEditing.ResolveManagedDistroName(owners[0])!.Trim() : defaultDistroName;
        var ownerId = owners.Count == 1 ? owners[0].Id : null;
        var existing = await Task.Run(() => detectExisting is null
            ? ExistingConfigDetector.Detect(dataDir, distro, localDataDir, ownerId)
            : detectExisting(distro, ownerId), ct).WaitAsync(ct);
        if (owners.Count == 1 &&
            !GatewayRecordEditing.AreEquivalentLoopbackEndpoints(owners[0].Url, existing.LocalGatewayUrl))
            return new(LocalAiSetupRoute.Blocked);
        LocalAiResolvedInstall? install = null;
        try { install = await new LocalAiManifestStore(new(localDataDir)).LoadAsync(ct); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // A damaged Local AI receipt does not revoke ownership of the existing Gateway.
        }
        ct.ThrowIfCancellationRequested();
        return LocalAiSetupRoutePolicy.Decide(owners, existing.HasLocalGateway, existing.LocalGatewayId,
            existing.HasDistro, existing.HasDistroDataDirectory, existing.DistroIsAppOwned,
            install?.Manifest.ModelCatalogId, install?.Manifest.RequestedPort);
    }
}
