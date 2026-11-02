using System.Diagnostics;
using System.Globalization;
using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.SetupEngine;

/// <summary>Refreshes Store packages and waits for verified current-user registration.</summary>
public static class NativeGatewayPackageAcquisition
{
    /// <summary>Environment variable overriding <see cref="DefaultTimeout"/>. Unset or blank
    /// silently keeps the default. A positive integer from 1 through 2147483 is seconds.
    /// Any other value warns and keeps the default. The name stays under the shared log
    /// sanitizer's 43-character base64url redaction so a warning keeps the variable name.</summary>
    internal const string TimeoutEnvironmentVariable = "OPENCLAW_SETUP_PACKAGE_ACQUISITION_TIMEOUT";

    /// <summary>Default bound for one native Gateway Store install plus registration
    /// verification. A cold Microsoft Store deployment can outlive 5 minutes; this budget
    /// covers the observed first-install timeline without leaving setup unbounded.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Resolves the acquisition deadline. Unset or blank keeps <see cref="DefaultTimeout"/>.
    /// A positive integer of at most <c>int.MaxValue / 1000</c> is seconds; any other value
    /// warns and keeps the default.
    /// </summary>
    internal static TimeSpan ResolveTimeout()
    {
        var raw = Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultTimeout;

        if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0 &&
            seconds <= int.MaxValue / 1000)
            return TimeSpan.FromSeconds(seconds);

        Trace.TraceWarning(
            "Ignoring {0}='{1}': expected a positive integer of at most {2}s. Using the default {3}-minute native Gateway package acquisition timeout.",
            TimeoutEnvironmentVariable,
            raw,
            int.MaxValue / 1000,
            (int)DefaultTimeout.TotalMinutes);
        return DefaultTimeout;
    }

    /// <summary>
    /// Resolves before installing, installs at most once, and retries only missing registration.
    /// The total deadline includes installation and verification and defaults to 30 minutes
    /// unless <paramref name="timeout"/> or <c>OPENCLAW_SETUP_PACKAGE_ACQUISITION_TIMEOUT</c>
    /// overrides it. Cancellation reaches the installer, which must honor its token. Await its
    /// cleanup before releasing setup ownership; Windows deployment may outlive the cancelled
    /// WinGet process.
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
        var deadline = timeout ?? ResolveTimeout();
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
                var package = await ResolveAsync();
                if (!package.PackageFamilyName.StartsWith(NativeGatewayPackageIdentity.StoreName + "_", StringComparison.Ordinal))
                    return package;
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
