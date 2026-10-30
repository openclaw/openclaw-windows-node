using OpenClaw.Connection.NativeGateway;
using System.Security.Cryptography;
using System.Text.Json;

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

    /// <summary>
    /// Binds destructive consent to the saved target. Credentials are excluded so the value is safe to
    /// pass on a child command line and to log, and so a token rotation cannot invalidate consent.
    /// </summary>
    public static string CaptureRemovalAuthority(GatewayRecord record) =>
        Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(record with
            {
                LastConnected = null,
                SharedGatewayToken = null,
                BootstrapToken = null,
            })));

    public static string? DisplayedGatewayUrl(
        GatewayRecord? active, GatewayConnectionSnapshot? snapshot, string? legacyUrl) =>
        active is null ? legacyUrl :
        snapshot?.GatewayId == active.Id ? snapshot.GatewayUrl ?? active.Url : active.Url;
}
