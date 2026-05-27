using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using Windows.ApplicationModel;

namespace OpenClaw.SetupEngine.UI;

internal static class Program
{
    private const int ClassFactoryCannotSupplyRequestedClass = unchecked((int)0x80040111);
    private const int UnspecifiedFailure = unchecked((int)0x80004005);
    private const uint WinAppSdkMajorMinor = 0x00020000;
    private const string WinAppSdkVersionTag = "";
    private static readonly TimeSpan FreshPackageMinimumAge = TimeSpan.FromSeconds(45);

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
    private static int Main(string[] args)
    {
        // Headless mode: bypass UI entirely, run pipeline directly
        if (Array.Exists(args, a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase)))
        {
            return OpenClaw.SetupEngine.Program.Main(args).GetAwaiter().GetResult();
        }

        WriteStartupBreadcrumb("Program.Main.begin");
        TryInitializeWindowsAppSdkBootstrap();
        WaitForFreshPackageActivationGrace();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        RunWithXamlFactoryRetry(
            () => Application.Start(p =>
            {
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                var context = new DispatcherQueueSynchronizationContext(dispatcher);
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            }),
            Thread.Sleep,
            XamlFactoryRetryDelays);
        WriteStartupBreadcrumb("Program.ApplicationStart.returned");
        return 0;
    }

    private static void TryInitializeWindowsAppSdkBootstrap()
    {
        try
        {
            WriteStartupBreadcrumb($"Program.WindowsAppSdkBootstrap.begin majorMinor=0x{WinAppSdkMajorMinor:X8}");
            Bootstrap.Initialize(WinAppSdkMajorMinor, WinAppSdkVersionTag);
            WriteStartupBreadcrumb("Program.WindowsAppSdkBootstrap.succeeded");
        }
        catch (Exception ex)
        {
            WriteStartupBreadcrumb("Program.WindowsAppSdkBootstrap.failedContinuing", ex);
        }
    }

    private static void WaitForFreshPackageActivationGrace()
    {
        try
        {
            var installedDate = Package.Current.InstalledDate.ToUniversalTime();
            var packageAge = DateTimeOffset.UtcNow - installedDate;
            if (packageAge < TimeSpan.Zero)
                packageAge = TimeSpan.Zero;

            if (packageAge >= FreshPackageMinimumAge)
            {
                WriteStartupBreadcrumb($"Program.packageInstallAge.ready ageMs={packageAge.TotalMilliseconds:F0}");
                return;
            }

            var wait = FreshPackageMinimumAge - packageAge;
            WriteStartupBreadcrumb($"Program.packageInstallAge.wait ageMs={packageAge.TotalMilliseconds:F0} waitMs={wait.TotalMilliseconds:F0}");
            Thread.Sleep(wait);
            WriteStartupBreadcrumb($"Program.packageInstallAge.readyAfterWait waitMs={wait.TotalMilliseconds:F0}");
        }
        catch (Exception ex)
        {
            WriteStartupBreadcrumb("Program.packageInstallAge.unavailable", ex);
        }
    }

    private static void RunWithXamlFactoryRetry(
        Action startApplication,
        Action<TimeSpan> delay,
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
            catch (COMException ex) when (IsTransientXamlFactoryFailure(ex))
            {
                if (attempt >= retryDelays.Count)
                {
                    WriteStartupBreadcrumb($"Program.ApplicationStart.xamlFactoryUnavailable.final attempts={attempt}", ex);
                    throw;
                }

                var wait = retryDelays[attempt];
                attempt++;
                WriteStartupBreadcrumb($"Program.ApplicationStart.xamlFactoryUnavailable.retry attempt={attempt} waitMs={wait.TotalMilliseconds:F0}", ex);
                delay(wait);
            }
        }
    }

    private static bool IsTransientXamlFactoryFailure(COMException exception) =>
        exception.HResult is ClassFactoryCannotSupplyRequestedClass or UnspecifiedFailure;

    private static void WriteStartupBreadcrumb(string phase, Exception? exception = null)
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
                args = Environment.GetCommandLineArgs(),
                exception = exception?.ToString()
            };

            File.AppendAllText(
                Path.Combine(directory, "setup-engine-startup.log"),
                JsonSerializer.Serialize(payload) + Environment.NewLine);
        }
        catch
        {
            // Last-ditch startup breadcrumbs must never prevent setup launch.
        }
    }
}
