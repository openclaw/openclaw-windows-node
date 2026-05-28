using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace OpenClaw.SetupEngine.UI;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            WriteStartupBreadcrumb("Program.Main.begin");

            // Headless mode: bypass UI entirely, run pipeline directly
            if (Array.Exists(args, a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase)))
            {
                return OpenClaw.SetupEngine.Program.Main(args).GetAwaiter().GetResult();
            }

            EnsureWindowsAppRuntimeLoaded();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                WriteStartupBreadcrumb("Program.ApplicationStart.callback");
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                var context = new DispatcherQueueSynchronizationContext(dispatcher);
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
                WriteStartupBreadcrumb("Program.ApplicationStart.appCreated");
            });
            WriteStartupBreadcrumb("Program.ApplicationStart.returned");
            return 0;
        }
        catch (Exception ex)
        {
            WriteStartupBreadcrumb("Program.Main.failed", ex);
            throw;
        }
    }

    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();

    private static void EnsureWindowsAppRuntimeLoaded()
    {
        var hresult = WindowsAppRuntime_EnsureIsLoaded();
        if (hresult != 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

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
            File.AppendAllText(
                Path.Combine(directory, "setup-engine-startup.log"),
                $"{DateTimeOffset.UtcNow:O} {phase} processPath={Environment.ProcessPath} baseDir={AppContext.BaseDirectory} cwd={Environment.CurrentDirectory} exception={exception}\n");
        }
        catch
        {
        }
    }
}
