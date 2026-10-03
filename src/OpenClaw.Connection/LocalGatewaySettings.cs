using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.Connection;

public enum LocalGatewayKind { None, Wsl, Native, LegacyNative }

/// <summary>Installation ownership, not a loopback URL, determines which local settings are safe.</summary>
public static class LocalGatewaySettings
{
    public static LocalGatewayKind Classify(GatewayRecord? record)
    {
        if (record is null) return LocalGatewayKind.None;
        if (record.NativePackageFamilyName is not null)
        {
            try { NativeGatewayPaths.ValidateRecord(record); }
            catch (ArgumentException) { return LocalGatewayKind.None; }
            return record.NativeRuntimeContract switch
            {
                NativeGatewayPackageClient.IsolatedContract => LocalGatewayKind.Native,
                null => LocalGatewayKind.LegacyNative,
                _ => LocalGatewayKind.None,
            };
        }
        return GatewayRecordEditing.IsSetupManagedLocalRecord(record)
            ? LocalGatewayKind.Wsl : LocalGatewayKind.None;
    }

    public static bool IsSameTarget(GatewayRecord expected, GatewayRecord? current) =>
        current is not null && Classify(expected) != LocalGatewayKind.None &&
        (expected with { LastConnected = null }) == (current with { LastConnected = null });

    public static string? DisplayedGatewayUrl(
        GatewayRecord? active, GatewayConnectionSnapshot? snapshot, string? legacyUrl) =>
        active is null ? legacyUrl :
        snapshot?.GatewayId == active.Id ? snapshot.GatewayUrl ?? active.Url : active.Url;
}
