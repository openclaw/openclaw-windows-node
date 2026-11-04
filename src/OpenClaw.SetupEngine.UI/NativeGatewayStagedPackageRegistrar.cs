using OpenClaw.Connection.NativeGateway;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace OpenClaw.SetupEngine.UI;

/// <summary>
/// Registers the Store Gateway family that Windows already staged on this machine for the current user.
/// Never downloads, never elevates, never uses -AllUsers, and never targets development or patched families.
/// The caller still verifies the registration through <see cref="NativeGatewayPackageResolver"/>.
/// </summary>
public sealed class NativeGatewayStagedPackageRegistrar
{
    private readonly string _familyName;

    public NativeGatewayStagedPackageRegistrar()
        : this(NativeGatewayPackageIdentity.StoreFamilyName)
    {
    }

    /// <summary>Tests only: lets a real Windows probe use a family that cannot exist.</summary>
    internal NativeGatewayStagedPackageRegistrar(string familyName) => _familyName = familyName;

    public async Task<NativeGatewayStagedRegistrationResult> RegisterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = new PackageManager().RegisterPackageByFamilyNameAsync(
            _familyName, null, DeploymentOptions.None, null, null);
        try
        {
            // Cancellation calls operation.Cancel(); a cancelled operation is not an Error and propagates.
            await operation.AsTask(cancellationToken);
            return NativeGatewayStagedRegistrationResult.Registered;
        }
        catch (Exception) when (operation.Status == AsyncStatus.Error)
        {
            // Deployment operations still return a result on Error; its extended code identifies "not staged".
            var result = operation.GetResults();
            var error = operation.ErrorCode?.HResult ?? 0;
            var extended = result.ExtendedErrorCode?.HResult ?? 0;
            if (NativeGatewayPackageRegistration.IsNotStaged(error, extended))
                return NativeGatewayStagedRegistrationResult.NotStaged;
            throw new NativeGatewayPackageRegistrationException(error, extended, result.ActivityId);
        }
    }
}
