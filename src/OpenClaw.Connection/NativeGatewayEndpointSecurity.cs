using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>Native records never use the WSL or remote endpoint credential exemptions.</summary>
internal static class NativeGatewayEndpointSecurity
{
    internal static readonly TimeSpan CredentialHandoffTimeout = CredentialHandoffTimeoutFor(IsolatedGatewayRuntime.StartupTimeout);

    internal static TimeSpan CredentialHandoffTimeoutFor(TimeSpan startupTimeout) =>
        startupTimeout + IsolatedGatewayRuntime.StartupConfirmationTimeout + TimeSpan.FromSeconds(20);

    internal static async Task<EndpointCredentialAuthorization> AuthorizeAsync(
        INativeGatewayRuntime? runtime,
        GatewayRecord record,
        CancellationToken cancellationToken,
        bool allowStart = true)
    {
        if (runtime is null)
        {
            return new(false, GatewayErrorKind.Network,
                "The native Gateway runtime is unavailable. Install the approved MSIX manually and restart Companion. Credentials were not sent.");
        }

        try
        {
            if (allowStart)
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

            return Deny(provenance,
                provenance.Detail ?? "Native Gateway endpoint ownership could not be verified. Credentials were not sent.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NativeGatewayListenerException ex)
        {
            return Deny(ex.Provenance, ex.Provenance.Detail ?? ex.Message, ownedStartEnded: ex.DuringOwnedStart);
        }
        catch (NativeGatewayEndpointUnverifiedException ex)
        {
            // The runtime keeps any start it requested here, so a retry only re-verifies the endpoint.
            return new(false, GatewayErrorKind.Network, ex.Message, Retryable: ex.Transient);
        }
        catch (NativeGatewayContractException ex)
        {
            return new(false, GatewayErrorKind.Network, ex.Message,
                Retryable: ex.AwaitingExternalLifecycle);
        }
        catch (NativeGatewayStartupTimeoutException ex)
        {
            return new(false, GatewayErrorKind.Network, ex.Message, Retryable: ex.ExternalStart);
        }
        catch (TimeoutException)
        {
            // Untyped timeouts include the legacy runtime's own failed launch, so they stay terminal.
            return new(false, GatewayErrorKind.Network,
                "The native Gateway readiness check timed out. Check Gateway status before retrying. Credentials were not sent.");
        }
        catch (Exception)
        {
            return new(false, GatewayErrorKind.Network,
                "The native Gateway could not start or verify its owned endpoint. Check the manually installed MSIX and retry. Credentials were not sent.");
        }

    }

    // A retryable state is still settling, so it reports Network rather than port-conflict guidance.
    // A failure that ended Companion's own start stays terminal so reconnect cannot start it again.
    private static EndpointCredentialAuthorization Deny(
        GatewayEndpointProvenance provenance, string detail, bool ownedStartEnded = false)
    {
        bool retryable = !ownedStartEnded && IsTemporarilyUnavailable(provenance);
        return new(false,
            IsTemporarilyUnavailable(provenance) || IsInspectionUnavailable(provenance)
                ? GatewayErrorKind.Network
                : GatewayErrorKind.LocalPortConflict,
            detail,
            Retryable: retryable);
    }

    internal static bool IsInspectionUnavailable(GatewayEndpointProvenance provenance) =>
        provenance.FailureReason is GatewayEndpointProvenanceFailureReason.ProcessIdentityUnavailable
            or GatewayEndpointProvenanceFailureReason.InspectionUnavailable;

    /// <summary>
    /// No listener, a busy/timed-out package inspection, or a listener snapshot that changed while
    /// it was read can clear on its own. Missing OS process identity and unattributed listeners cannot.
    /// </summary>
    private static bool IsTemporarilyUnavailable(GatewayEndpointProvenance provenance) =>
        provenance.Kind == GatewayEndpointProvenanceKind.NoListener ||
        provenance.FailureReason is GatewayEndpointProvenanceFailureReason.InspectionUnavailable
            or GatewayEndpointProvenanceFailureReason.ListenerSnapshotChanged;
}
