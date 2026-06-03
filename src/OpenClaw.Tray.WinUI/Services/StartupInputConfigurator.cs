using System;
using System.Runtime.InteropServices;

namespace OpenClawTray.Services;

/// <summary>
/// Configures process-wide mouse-in-pointer input before WinUI initialization
/// so precision touchpad scrolling works. Best-effort; failures are logged and
/// never propagate. Must run before <c>InitializeComponent()</c>.
/// </summary>
internal static class StartupInputConfigurator
{
    private const string DisableMouseInPointerEnv = "OPENCLAW_DISABLE_MOUSE_IN_POINTER";

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableMouseInPointer([MarshalAs(UnmanagedType.Bool)] bool fEnable);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsMouseInPointerEnabled();

    public static void Configure()
    {
        if (IsTruthyEnvVar(Environment.GetEnvironmentVariable(DisableMouseInPointerEnv)))
        {
            Logger.Warn($"[Input] Mouse-in-pointer startup configuration disabled by {DisableMouseInPointerEnv}=1.");
            return;
        }

        try
        {
            var before = IsMouseInPointerEnabled();
            if (before)
            {
                Logger.Info("[Input] Mouse-in-pointer was already enabled before WinUI initialization.");
                return;
            }

            var enabled = EnableMouseInPointer(true);
            var error = enabled ? 0 : Marshal.GetLastWin32Error();
            var after = IsMouseInPointerEnabled();
            if (enabled && after)
            {
                Logger.Info("[Input] Enabled mouse-in-pointer before WinUI initialization for precision touchpad scrolling.");
            }
            else
            {
                Logger.Warn($"[Input] EnableMouseInPointer(true) did not enable mouse-in-pointer. Before={before}, After={after}, LastError={error}.");
            }
        }
        catch (DllNotFoundException ex)
        {
            Logger.Warn($"[Input] EnableMouseInPointer startup configuration failed: {ex.Message}");
        }
        catch (EntryPointNotFoundException ex)
        {
            Logger.Warn($"[Input] EnableMouseInPointer startup configuration failed: {ex.Message}");
        }
        catch (SEHException ex)
        {
            Logger.Warn($"[Input] EnableMouseInPointer startup configuration failed: {ex.Message}");
        }
    }

    private static bool IsTruthyEnvVar(string? value) =>
        value is not null &&
        (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
}
