namespace OpenClaw.Connection.NativeGateway;

/// <summary>
/// Separates an occupied or unverifiable endpoint from package/configuration/start failures.
/// Preserves the failed inspection even when cleanup removes the owned listener afterward.
/// </summary>
public sealed class NativeGatewayListenerException : InvalidOperationException
{
    public NativeGatewayListenerException(GatewayEndpointProvenance provenance)
        : base("The native gateway port is occupied or its listener cannot be verified.")
    {
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    public GatewayEndpointProvenance Provenance { get; }
}
