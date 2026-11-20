using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenClawTray.Widgets;

namespace OpenClawTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--widget-provider", StringComparer.Ordinal))
            return WidgetProviderHost.Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
        return 0;
    }
}
