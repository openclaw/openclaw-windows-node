using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.SetupEngine;

/// <summary>Installs missing packages and waits for verified current-user registration.</summary>
public static class NativeGatewayPackageAcquisition
{
    /// <summary>
    /// Resolves before installing, installs at most once, and retries only missing registration.
    /// The total deadline includes installation and verification. Cancellation reaches the
    /// installer, which must honor its token. Await its cleanup before releasing setup
    /// ownership; Windows deployment may outlive the cancelled WinGet process.
    /// </summary>
    public static async Task<NativeGatewayPackage> EnsureAsync(
        INativeGatewayPackageResolver resolver,
        Func<CancellationToken, Task> installPackage,
        Action? waitingForInstallation = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(installPackage);
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

            await installPackage(token);
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
                "Windows may still finish deployment. Check WinGet and Microsoft Store access, then retry native setup.", exception);
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
