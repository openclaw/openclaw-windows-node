using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>Native records never use the WSL or remote endpoint credential exemptions.</summary>
internal static class NativeGatewayEndpointSecurity
{
    internal static async Task<EndpointCredentialAuthorization> AuthorizeAsync(
        INativeGatewayRuntime? runtime,
        GatewayRecord record,
        CancellationToken cancellationToken)
    {
        if (runtime is null)
        {
            return new(false, GatewayErrorKind.Network,
                "The native Gateway runtime is unavailable. Install the approved MSIX manually and restart Companion. Credentials were not sent.");
        }

        try
        {
            await runtime.EnsureRunningAsync(record, cancellationToken).ConfigureAwait(false);
            var provenance = await runtime.InspectAsync(record, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (provenance.Kind == GatewayEndpointProvenanceKind.ExpectedManagedGateway)
            {
                // Runtime inspection attributes the workload to its owned package launcher on every handoff.
                // A crash replacement remains trusted without pinning the previous process ID.
                return EndpointCredentialAuthorization.AllowWithProof(
                    new EndpointOwnershipProof("native-managed", null, null, null, record.NativePackageFamilyName));
            }

            return new(false,
                provenance.Kind == GatewayEndpointProvenanceKind.NoListener
                    ? GatewayErrorKind.Network
                    : GatewayErrorKind.LocalPortConflict,
                provenance.Detail ?? "Native Gateway endpoint ownership could not be verified. Credentials were not sent.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NativeGatewayListenerException ex)
        {
            return new(false, GatewayErrorKind.LocalPortConflict,
                ex.Provenance.Detail ?? ex.Message);
        }
        catch (Exception)
        {
            return new(false, GatewayErrorKind.Network,
                "The native Gateway could not start or verify its owned endpoint. Check the manually installed MSIX and retry. Credentials were not sent.");
        }
    }
}
