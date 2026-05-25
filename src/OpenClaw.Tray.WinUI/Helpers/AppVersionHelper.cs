using System;

namespace OpenClawTray.Helpers;

internal static class AppVersionHelper
{
    public static string CurrentVersionText
    {
        get
        {
            if (PackageHelper.IsPackaged)
            {
                try
                {
                    var version = global::Windows.ApplicationModel.Package.Current.Id.Version;
                    return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
                }
                catch
                {
                    // Fall through to assembly version for unpackaged/test contexts
                    // where Package.Current may be unavailable despite stale state.
                }
            }

            return typeof(AppVersionHelper).Assembly.GetName().Version?.ToString() ?? "unknown";
        }
    }

    public static string DisplayVersion => $"v{CurrentVersionText}";
}
