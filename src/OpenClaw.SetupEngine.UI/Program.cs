using System;
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

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var context = new DispatcherQueueSynchronizationContext(dispatcher);
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
