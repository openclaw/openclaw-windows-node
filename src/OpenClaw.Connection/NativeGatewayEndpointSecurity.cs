using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>Native records never use the WSL or remote endpoint credential exemptions.</summary>
internal static class NativeGatewayEndpointSecurity
{
    internal static readonly TimeSpan CredentialHandoffTimeout =
        IsolatedGatewayRuntime.StartupTimeout + IsolatedGatewayRuntime.StartupConfirmationTimeout + TimeSpan.FromSeconds(20);
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
                provenance.Kind == GatewayEndpointProvenanceKind.NoListener || IsInspectionUnavailable(provenance)
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
            return new(false,
                IsInspectionUnavailable(ex.Provenance)
                    ? GatewayErrorKind.Network
                    : GatewayErrorKind.LocalPortConflict,
                ex.Provenance.Detail ?? ex.Message);
        }
        catch (NativeGatewayContractException ex)
        {
            return new(false, GatewayErrorKind.Network, ex.Message);
        }
        catch (NativeGatewayStartupTimeoutException ex)
        {
            return new(false, GatewayErrorKind.Network, ex.Message);
        }
        catch (TimeoutException)
        {
            return new(false, GatewayErrorKind.Network,
                "The native Gateway readiness check timed out. Check Gateway status before retrying. Credentials were not sent.");
        }
        catch (Exception)
        {
            return new(false, GatewayErrorKind.Network,
                "The native Gateway could not start or verify its owned endpoint. Check the manually installed MSIX and retry. Credentials were not sent.");
        }

    }

    internal static bool IsInspectionUnavailable(GatewayEndpointProvenance provenance) =>
        provenance.FailureReason is GatewayEndpointProvenanceFailureReason.ProcessIdentityUnavailable
            or GatewayEndpointProvenanceFailureReason.InspectionUnavailable;
}
