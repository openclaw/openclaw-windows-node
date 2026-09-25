using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.SetupEngine;

/// <summary>Hands missing packages to Microsoft Store and waits for verified registration.</summary>
public static class NativeGatewayPackageAcquisition
{
    /// <summary>
    /// Resolves before opening the installer, opens it at most once, and retries only missing
    /// registration. The total deadline includes resolution and installer handoff.
    /// Cancellation stops waiting, not Microsoft Store or its consent flow.
    /// </summary>
    public static async Task<NativeGatewayPackage> EnsureAsync(
        INativeGatewayPackageResolver resolver,
        Func<CancellationToken, Task> openInstaller,
        Action? waitingForInstallation = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(openInstaller);
        var deadline = timeout ?? TimeSpan.FromMinutes(5);
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero, nameof(timeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(pollInterval));
        cancellationToken.ThrowIfCancellationRequested();

        using var expiration = new CancellationTokenSource(deadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiration.Token);
        var token = linked.Token;
        try
        {
            try
            {
                return await ResolveAsync();
            }
            catch (NativeGatewayPackageNotInstalledException)
            {
                token.ThrowIfCancellationRequested();
            }

            await openInstaller(token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            waitingForInstallation?.Invoke();

            while (true)
            {
                try
                {
                    return await ResolveAsync();
                }
                catch (NativeGatewayPackageNotInstalledException)
                {
                    await Task.Delay(interval, token);
                }
            }
        }
        catch (OperationCanceledException exception) when (
            expiration.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Timed out waiting for a verified OpenClaw Gateway package. " +
                "Complete installation from Microsoft Store, then retry native setup.", exception);
        }

        async Task<NativeGatewayPackage> ResolveAsync()
        {
            token.ThrowIfCancellationRequested();
            var package = await resolver.ResolveAsync(token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            return package;
        }
    }
}
