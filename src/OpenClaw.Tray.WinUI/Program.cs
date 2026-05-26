using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Xaml;
using OpenClawTray.Services;
using Windows.ApplicationModel;
using WinRT;

namespace OpenClawTray;

internal static class Program
{
    private static readonly TimeSpan PackageReadyTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PackageReadyRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FreshPackageMinimumAge = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan[] XamlFactoryRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(60),
    ];

    [STAThread]
    private static void Main(string[] args)
    {
        WriteEarlyStartupBreadcrumb("Program.Main.begin");

        ComWrappersSupport.InitializeComWrappers();
        WinUiStartupGate.WaitForPackageReady(
            GetPackageReadiness,
            Thread.Sleep,
            () => DateTimeOffset.UtcNow,
            WriteEarlyStartupBreadcrumb,
            PackageReadyTimeout,
            PackageReadyRetryDelay);
        WinUiStartupGate.WaitForFreshPackageActivationGrace(
            GetPackageInstalledDate,
            Thread.Sleep,
            () => DateTimeOffset.UtcNow,
            WriteEarlyStartupBreadcrumb,
            FreshPackageMinimumAge);

        WinUiStartupGate.RunWithXamlFactoryRetry(
            () => Application.Start(p =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            }),
            Thread.Sleep,
            WriteEarlyStartupBreadcrumb,
            XamlFactoryRetryDelays);
        WriteEarlyStartupBreadcrumb("Program.ApplicationStart.returned");
    }

    private static WinUiStartupGate.PackageReadiness GetPackageReadiness()
    {
        try
        {
            var package = Package.Current;
            var status = package.Status;
            var flags = FormatPackageStatus(status);
            var installedDate = package.InstalledDate.ToUniversalTime();
            var description = $"package={package.Id.FullName}; status={flags}; installed={installedDate:O}";
            return new WinUiStartupGate.PackageReadiness(true, status.VerifyIsOK(), description, null);
        }
        catch (Exception ex)
        {
            return WinUiStartupGate.PackageReadiness.Unavailable(ex);
        }
    }

    private static DateTimeOffset? GetPackageInstalledDate()
    {
        try
        {
            return Package.Current.InstalledDate;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatPackageStatus(PackageStatus status)
    {
        var flags = new List<string>();

        if (status.NotAvailable) flags.Add(nameof(status.NotAvailable));
        if (status.PackageOffline) flags.Add(nameof(status.PackageOffline));
        if (status.DataOffline) flags.Add(nameof(status.DataOffline));
        if (status.Disabled) flags.Add(nameof(status.Disabled));
        if (status.NeedsRemediation) flags.Add(nameof(status.NeedsRemediation));
        if (status.LicenseIssue) flags.Add(nameof(status.LicenseIssue));
        if (status.Modified) flags.Add(nameof(status.Modified));
        if (status.Tampered) flags.Add(nameof(status.Tampered));
        if (status.DependencyIssue) flags.Add(nameof(status.DependencyIssue));
        if (status.Servicing) flags.Add(nameof(status.Servicing));
        if (status.DeploymentInProgress) flags.Add(nameof(status.DeploymentInProgress));
        if (status.IsPartiallyStaged) flags.Add(nameof(status.IsPartiallyStaged));

        return flags.Count == 0 ? "OK" : string.Join(",", flags);
    }

    private static void WriteEarlyStartupBreadcrumb(string phase, Exception? exception = null)
    {
        try
        {
            var directory = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OpenClawTray");
            }

            Directory.CreateDirectory(directory);
            var payload = new
            {
                timestamp = DateTimeOffset.UtcNow,
                phase,
                pid = Environment.ProcessId,
                processPath = Environment.ProcessPath,
                args = Environment.GetCommandLineArgs().Select(RedactStartupArg).ToArray(),
                exception = exception?.ToString()
            };

            File.AppendAllText(
                Path.Combine(directory, "startup.log"),
                JsonSerializer.Serialize(payload) + Environment.NewLine);
        }
        catch
        {
            // Last-ditch startup breadcrumbs must never prevent app launch.
        }
    }

    private static string RedactStartupArg(string arg)
    {
        if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, "openclaw", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(uri.Query)
                ? arg
                : arg[..^uri.Query.Length] + "?<redacted>";
        }

        return arg.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
               arg.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
               arg.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            ? "<redacted>"
            : arg;
    }
}
