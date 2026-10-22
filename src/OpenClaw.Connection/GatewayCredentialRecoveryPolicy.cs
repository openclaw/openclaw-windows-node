namespace OpenClaw.Connection;

/// <summary>Transport admission only; owned SSH and managed-loopback provenance must also be rechecked.</summary>
internal static class GatewayCredentialRecoveryPolicy
{
    public static bool IsTransportSafe(GatewayRecord record, bool allowUnmanagedLoopback = false) =>
        record.SshTunnel is not null ||
        GatewayRecordEditing.IsLoopbackEndpoint(record.Url) &&
            (allowUnmanagedLoopback || record.IsLocal || GatewayRecordEditing.ResolveManagedDistroName(record) is not null) ||
        Uri.TryCreate(record.Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
}
