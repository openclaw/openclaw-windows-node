using Microsoft.Windows.ApplicationModel.Resources;
using OpenClaw.Shared;

namespace OpenClawTray.Helpers;

public static class LocalizationHelper
{
    private static ResourceLoader? _loader;

    private static ResourceLoader Loader => _loader ??= new ResourceLoader();

    public static string GetString(string resourceKey)
    {
        try
        {
            var value = Loader.GetString(resourceKey);
            return string.IsNullOrEmpty(value) ? resourceKey : value;
        }
        catch
        {
            return resourceKey;
        }
    }

    public static string GetConnectionStatusText(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => GetString("StatusDisplay_Connected"),
        ConnectionStatus.Connecting => GetString("StatusDisplay_Connecting"),
        ConnectionStatus.Disconnected => GetString("StatusDisplay_Disconnected"),
        ConnectionStatus.Error => GetString("StatusDisplay_Error"),
        _ => GetString("StatusDisplay_Unknown")
    };
}
