namespace OpenClaw.SetupEngine;

/// <summary>
/// Opens the Gateway Microsoft Store listing. Store owns package selection, signature
/// validation, installation consent and deployment; opening the listing is not readiness.
/// The Store app product page is launched directly; the web listing is only a fallback
/// for hosts where the <c>ms-windows-store:</c> protocol has no handler.
/// </summary>
public sealed class NativeGatewayMsixInstaller
{
    public const string StoreProductId = "9NV70LV3D6XC";

    public static Uri StoreAppUri { get; } =
        new($"ms-windows-store://pdp/?ProductId={StoreProductId}");

    public static Uri StoreWebUri { get; } =
        new("https://apps.microsoft.com/detail/9nv70lv3d6xc?hl=en-US&gl=US");

    public async Task OpenAsync(
        Func<Uri, CancellationToken, Task<bool>> openInstaller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openInstaller);
        if (await TryOpenAsync(openInstaller, StoreAppUri, cancellationToken))
            return;
        if (await TryOpenAsync(openInstaller, StoreWebUri, cancellationToken))
            return;
        throw new InvalidOperationException(
            $"The OpenClaw Gateway Microsoft Store listing could not be opened. Open {StoreWebUri} to install it, then retry native setup.");
    }

    private static async Task<bool> TryOpenAsync(
        Func<Uri, CancellationToken, Task<bool>> openInstaller,
        Uri uri,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var opened = await openInstaller(uri, cancellationToken).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return opened;
    }
}
