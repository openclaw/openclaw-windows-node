using System.Diagnostics;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public static class StartupTaskRegistration
{
    internal const string TaskName = WindowsStartupTaskRegistration.TaskName;

    public static bool Register(string trayExecutablePath) =>
        WindowsStartupTaskRegistration.Register(trayExecutablePath);

    public static bool Unregister() =>
        WindowsStartupTaskRegistration.Unregister();

    internal static ProcessStartInfo CreateRegisterProcessStartInfo(string trayExecutablePath) =>
        WindowsStartupTaskRegistration.CreateRegisterProcessStartInfo(trayExecutablePath);

    internal static ProcessStartInfo CreateUnregisterProcessStartInfo() =>
        WindowsStartupTaskRegistration.CreateUnregisterProcessStartInfo();

    internal static ProcessStartInfo CreateQueryProcessStartInfo() =>
        WindowsStartupTaskRegistration.CreateQueryProcessStartInfo();

    internal static string ResolveSchtasksPath() =>
        WindowsStartupTaskRegistration.ResolveSchtasksPath();
}
