using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using WinRT;

namespace OpenClawTray.Helpers;

/// <summary>
/// Helper class to apply Mica backdrop with IsInputActive always true.
/// This prevents the backdrop from changing appearance when the window loses focus,
/// which can cause issues with rapidly created/destroyed windows.
/// </summary>
public static class BackdropHelper
{
    private static WindowsSystemDispatcherQueueHelper? _dispatcherQueueHelper;

    /// <summary>
    /// Applies Mica backdrop to a window with IsInputActive always true.
    /// </summary>
    public static MicaController? TrySetMicaBackdrop(Window window, bool useBaseAlt = false)
    {
        if (!MicaController.IsSupported())
            return null;

        // Ensure dispatcher queue exists
        _dispatcherQueueHelper ??= new WindowsSystemDispatcherQueueHelper();
        _dispatcherQueueHelper.EnsureWindowsSystemDispatcherQueueController();

        var configSource = new SystemBackdropConfiguration
        {
            // Keep IsInputActive always true to prevent appearance changes on focus loss
            IsInputActive = true
        };

        // Set initial theme
        if (window.Content is FrameworkElement rootElement)
        {
            configSource.Theme = ConvertToBackdropTheme(rootElement.ActualTheme);
            rootElement.ActualThemeChanged += (s, e) =>
            {
                configSource.Theme = ConvertToBackdropTheme(rootElement.ActualTheme);
            };
        }

        var controller = new MicaController();
        if (useBaseAlt)
        {
            controller.Kind = MicaKind.BaseAlt;
        }

        controller.AddSystemBackdropTarget(window.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(configSource);

        // Clean up on window close
        window.Closed += (s, e) =>
        {
            controller.Dispose();
        };

        return controller;
    }

    /// <summary>
    /// Applies Desktop Acrylic backdrop to a window with IsInputActive always true.
    /// </summary>
    public static DesktopAcrylicController? TrySetAcrylicBackdrop(Microsoft.UI.Xaml.Window window)
    {
        if (!DesktopAcrylicController.IsSupported())
            return null;

        _dispatcherQueueHelper ??= new WindowsSystemDispatcherQueueHelper();
        _dispatcherQueueHelper.EnsureWindowsSystemDispatcherQueueController();

        var configSource = new SystemBackdropConfiguration
        {
            IsInputActive = true
        };

        if (window.Content is FrameworkElement rootElement)
        {
            configSource.Theme = ConvertToBackdropTheme(rootElement.ActualTheme);
            rootElement.ActualThemeChanged += (s, e) =>
            {
                configSource.Theme = ConvertToBackdropTheme(rootElement.ActualTheme);
            };
        }

        var controller = new DesktopAcrylicController();
        controller.AddSystemBackdropTarget(window.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(configSource);

        window.Closed += (s, e) =>
        {
            controller.Dispose();
        };

        return controller;
    }

    private static SystemBackdropTheme ConvertToBackdropTheme(ElementTheme theme) => theme switch
    {
        ElementTheme.Dark => SystemBackdropTheme.Dark,
        ElementTheme.Light => SystemBackdropTheme.Light,
        _ => SystemBackdropTheme.Default
    };
}

/// <summary>
/// Helper to ensure a Windows.System.DispatcherQueue exists on the current thread.
/// </summary>
internal class WindowsSystemDispatcherQueueHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int dwSize;
        internal int threadType;
        internal int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        [In] DispatcherQueueOptions options,
        [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object? dispatcherQueueController);

    private object? _dispatcherQueueController;

    public void EnsureWindowsSystemDispatcherQueueController()
    {
        if (global::Windows.System.DispatcherQueue.GetForCurrentThread() != null)
            return;

        if (_dispatcherQueueController == null)
        {
            DispatcherQueueOptions options;
            options.dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions));
            options.threadType = 2;    // DQTYPE_THREAD_CURRENT
            options.apartmentType = 2; // DQTAT_COM_STA

            CreateDispatcherQueueController(options, ref _dispatcherQueueController);
        }
    }
}
