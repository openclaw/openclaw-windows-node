using System;
using System.Runtime.InteropServices;

namespace OpenClawTray.Services;

internal static class WinUiStartupGate
{
    internal const int ClassFactoryCannotSupplyRequestedClass = unchecked((int)0x80040111);

    internal readonly record struct PackageReadiness(
        bool IsAvailable,
        bool IsReady,
        string Description,
        Exception? Exception)
    {
        public static PackageReadiness Unavailable(Exception? exception) =>
            new(false, false, "unavailable", exception);
    }

    internal static void WaitForPackageReady(
        Func<PackageReadiness> getReadiness,
        Action<TimeSpan> delay,
        Func<DateTimeOffset> getUtcNow,
        Action<string, Exception?> writeBreadcrumb,
        TimeSpan timeout,
        TimeSpan retryDelay)
    {
        var readiness = getReadiness();
        if (!readiness.IsAvailable)
        {
            writeBreadcrumb("Program.packageStatus.unavailable", readiness.Exception);
            return;
        }

        if (readiness.IsReady)
        {
            writeBreadcrumb($"Program.packageStatus.ready {readiness.Description}", null);
            return;
        }

        var deadline = getUtcNow().Add(timeout);
        var attempts = 0;

        while (!readiness.IsReady && getUtcNow() < deadline)
        {
            attempts++;
            writeBreadcrumb($"Program.packageStatus.wait attempt={attempts} {readiness.Description}", readiness.Exception);
            delay(retryDelay);
            readiness = getReadiness();
        }

        var finalPhase = readiness.IsReady
            ? $"Program.packageStatus.readyAfterWait attempts={attempts} {readiness.Description}"
            : $"Program.packageStatus.timeout attempts={attempts} {readiness.Description}";
        writeBreadcrumb(finalPhase, readiness.Exception);
    }

    internal static void WaitForFreshPackageActivationGrace(
        Func<DateTimeOffset?> getInstalledDate,
        Action<TimeSpan> delay,
        Func<DateTimeOffset> getUtcNow,
        Action<string, Exception?> writeBreadcrumb,
        TimeSpan minimumPackageAge)
    {
        var installedDate = getInstalledDate();
        if (installedDate is null)
        {
            writeBreadcrumb("Program.packageInstallDate.unavailable", null);
            return;
        }

        var now = getUtcNow();
        var packageAge = now - installedDate.Value.ToUniversalTime();
        if (packageAge < TimeSpan.Zero)
            packageAge = TimeSpan.Zero;

        if (packageAge >= minimumPackageAge)
        {
            writeBreadcrumb($"Program.packageInstallAge.ready ageMs={packageAge.TotalMilliseconds:F0}", null);
            return;
        }

        var wait = minimumPackageAge - packageAge;
        writeBreadcrumb($"Program.packageInstallAge.wait ageMs={packageAge.TotalMilliseconds:F0} waitMs={wait.TotalMilliseconds:F0}", null);
        delay(wait);
        writeBreadcrumb($"Program.packageInstallAge.readyAfterWait waitMs={wait.TotalMilliseconds:F0}", null);
    }

    internal static void RunWithXamlFactoryRetry(
        Action startApplication,
        Action<TimeSpan> delay,
        Action<string, Exception?> writeBreadcrumb,
        IReadOnlyList<TimeSpan> retryDelays)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                startApplication();
                return;
            }
            catch (COMException ex) when (IsXamlFactoryClassUnavailable(ex))
            {
                if (attempt >= retryDelays.Count)
                {
                    writeBreadcrumb($"Program.ApplicationStart.xamlFactoryUnavailable.final attempts={attempt}", ex);
                    throw;
                }

                var wait = retryDelays[attempt];
                attempt++;
                writeBreadcrumb($"Program.ApplicationStart.xamlFactoryUnavailable.retry attempt={attempt} waitMs={wait.TotalMilliseconds:F0}", ex);
                delay(wait);
            }
        }
    }

    internal static bool IsXamlFactoryClassUnavailable(COMException exception) =>
        exception.HResult == ClassFactoryCannotSupplyRequestedClass;
}
