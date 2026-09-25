namespace OpenClaw.SetupEngine;

/// <summary>
/// Opens the Gateway Microsoft Store listing. Store owns package selection, signature
/// validation, installation consent and deployment; opening the listing is not readiness.
/// </summary>
public sealed class NativeGatewayMsixInstaller
{
    public static Uri StoreUri { get; } =
        new("https://apps.microsoft.com/detail/9nv70lv3d6xc?hl=en-US&gl=US");

    public async Task OpenAsync(
        Func<Uri, CancellationToken, Task<bool>> openInstaller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openInstaller);
        cancellationToken.ThrowIfCancellationRequested();
        var opened = await openInstaller(StoreUri, cancellationToken).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!opened)
            throw new InvalidOperationException(
                $"The OpenClaw Gateway Microsoft Store listing could not be opened. Open {StoreUri} to install it, then retry native setup.");
    }
}
