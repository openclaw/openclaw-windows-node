using OpenClaw.Connection;

namespace OpenClawTray.Services;

internal sealed record LocalAiGatewayDistroResolution(
    bool Success,
    string? DistroName,
    string? Detail)
{
    public static LocalAiGatewayDistroResolution Resolved(string distroName) =>
        new(true, distroName, null);

    public static LocalAiGatewayDistroResolution Failed(string detail) =>
        new(false, null, detail);
}

internal interface ILocalAiGatewayDistroResolver
{
    LocalAiGatewayDistroResolution Resolve();
}

/// <summary>
/// Pins the singleton Local AI installation to the one explicitly setup-managed
/// local WSL gateway in the loaded registry. Every resolution revalidates the
/// pinned record so a registry replacement or ownership drift cannot redirect
/// lifecycle commands to another distro.
/// </summary>
internal sealed class LocalAiGatewayDistroResolver : ILocalAiGatewayDistroResolver
{
    private readonly GatewayRegistry? _registry;
    private readonly string? _gatewayId;
    private readonly string? _distroName;
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
            _initialFailure =
                "No explicit setup-managed WSL gateway owns the Local AI installation; refusing to change its gateway route.";
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
        _distroName = owner.SetupManagedDistroName!.Trim();
    }

    public LocalAiGatewayDistroResolution Resolve()
    {
        if (_initialFailure is not null)
            return LocalAiGatewayDistroResolution.Failed(_initialFailure);
        if (_registry is null || _gatewayId is null || _distroName is null)
        {
            return LocalAiGatewayDistroResolution.Failed(
                "The gateway registry is unavailable; refusing to change the Local AI gateway route.");
        }

        IReadOnlyList<GatewayRecord> owners = FindOwners(_registry.GetAll());
        if (owners.Count != 1 ||
            !string.Equals(owners[0].Id, _gatewayId, StringComparison.Ordinal) ||
            !string.Equals(
                owners[0].SetupManagedDistroName?.Trim(),
                _distroName,
                StringComparison.Ordinal))
        {
            return LocalAiGatewayDistroResolution.Failed(
                "The setup-managed WSL gateway owner changed after Local AI startup; refusing to route lifecycle commands to an unknown distro.");
        }

        return LocalAiGatewayDistroResolution.Resolved(_distroName);
    }

    private static IReadOnlyList<GatewayRecord> FindOwners(IEnumerable<GatewayRecord> records) =>
        records
            .Where(record =>
                WslKeepAlivePolicy.IsSetupManagedLocalRecord(record) &&
                !string.IsNullOrWhiteSpace(record.Id) &&
                !string.IsNullOrWhiteSpace(record.SetupManagedDistroName))
            .ToArray();
}
