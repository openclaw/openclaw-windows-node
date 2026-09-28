using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>Authorizes HTTP credential handoff without taking ownership of the shared native runtime.</summary>
public sealed class InteractiveGatewayEndpointAuthorizer(
    INativeGatewayRuntime nativeRuntime,
    Func<GatewayRecord, GatewayCredential, bool> authorizeNonNative,
    IOpenClawLogger logger)
{
    public bool IsCredentialAllowed(GatewayRecord record, GatewayCredential credential)
    {
        if (record.NativePackageFamilyName is null)
            return authorizeNonNative(record, credential);

        try
        {
            // Never substitute connection state or a cached result for inspection at this handoff.
            var provenance = nativeRuntime.Inspect(record);
            if (provenance.Kind == GatewayEndpointProvenanceKind.ExpectedManagedGateway)
                return true;
            logger.Warn($"Native Gateway HTTP credential handoff denied: {provenance.Kind}.");
        }
        catch (Exception ex)
        {
            logger.Warn($"Native Gateway HTTP ownership inspection failed ({ex.GetType().Name}). Credentials were not sent.");
        }
        return false;
    }
}
