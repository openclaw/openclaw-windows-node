using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace OpenClaw.SetupEngine.UI;

internal static class Program
{
    private const int ClassFactoryCannotSupplyRequestedClass = unchecked((int)0x80040111);
    private const int UnspecifiedFailure = unchecked((int)0x80004005);

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
        EnsureWindowsAppRuntimeLoaded();
        WriteStartupBreadcrumb("Program.ComWrappers.begin");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        WriteStartupBreadcrumb("Program.ComWrappers.succeeded");
        RunWithXamlFactoryRetry(
            () =>
            {
                WriteStartupBreadcrumb("Program.ApplicationStart.begin");
                Application.Start(p =>
                {
                    WriteStartupBreadcrumb("Program.ApplicationStart.callback.enter");
                    var dispatcher = DispatcherQueue.GetForCurrentThread();
                    WriteStartupBreadcrumb("Program.ApplicationStart.callback.dispatcher");
                    var context = new DispatcherQueueSynchronizationContext(dispatcher);
                    SynchronizationContext.SetSynchronizationContext(context);
                    WriteStartupBreadcrumb("Program.ApplicationStart.callback.beforeApp");
                    new App();
                    WriteStartupBreadcrumb("Program.ApplicationStart.callback.afterApp");
                });
            },
            Thread.Sleep,
            XamlFactoryRetryDelays);
        WriteStartupBreadcrumb("Program.ApplicationStart.returned");
        return 0;
    }

    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();

    private static void EnsureWindowsAppRuntimeLoaded()
    {
        WriteStartupBreadcrumb("Program.WindowsAppRuntimeEnsureLoaded.begin");
        var hresult = WindowsAppRuntime_EnsureIsLoaded();
        if (hresult != 0)
        {
            var exception = Marshal.GetExceptionForHR(hresult);
            WriteStartupBreadcrumb($"Program.WindowsAppRuntimeEnsureLoaded.failed hresult=0x{hresult:X8}", exception);
            Marshal.ThrowExceptionForHR(hresult);
        }

        WriteStartupBreadcrumb("Program.WindowsAppRuntimeEnsureLoaded.succeeded");
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
                WriteStartupBreadcrumb($"Program.ApplicationStart.attempt attempt={attempt + 1}");
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

    internal static void WriteStartupBreadcrumb(string phase, Exception? exception = null)
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
                Path.Combine(directory, "setup-engine-startup.log"),
                JsonSerializer.Serialize(payload) + Environment.NewLine);
        }
        catch
        {
            // Last-ditch startup breadcrumbs must never prevent setup launch.
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
