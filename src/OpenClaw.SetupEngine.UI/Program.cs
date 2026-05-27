using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace OpenClaw.SetupEngine.UI;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Headless mode: bypass UI entirely, run pipeline directly
        if (Array.Exists(args, a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase)))
        {
            return OpenClaw.SetupEngine.Program.Main(args).GetAwaiter().GetResult();
        }

        WriteStartupBreadcrumb("Program.Main.begin");
        WriteStartupBreadcrumb("Program.ComWrappers.begin");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        WriteStartupBreadcrumb("Program.ComWrappers.succeeded");
        WriteStartupBreadcrumb("Program.ApplicationStart.begin");
        Application.Start(p =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var context = new DispatcherQueueSynchronizationContext(dispatcher);
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        WriteStartupBreadcrumb("Program.ApplicationStart.returned");
        return 0;
    }

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
