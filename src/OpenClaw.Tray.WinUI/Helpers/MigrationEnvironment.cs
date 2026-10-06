using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using OpenClaw.Connection.Migration;

namespace OpenClawTray.Helpers;

internal static class MigrationEnvironment
{
    public static bool HasPathOverride =>
        new[]
        {
            "OPENCLAW_TRAY_DATA_DIR", "OPENCLAW_TRAY_APPDATA_DIR",
            "OPENCLAW_TRAY_LOCALAPPDATA_DIR", "OPENCLAW_TRAY_LOCAL_DATA_DIR"
        }.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));

    public static string? Metadata(string key) => typeof(MigrationEnvironment).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(attribute => attribute.Key == key)?.Value;

    public static string? StoreProductId =>
        Metadata("MigrationStoreProductId") ?? Metadata("MigrationPreviewStoreProductId");

    public static string? MinimumSourceVersion =>
        Metadata("MigrationMinimumSourceVersion") ?? Metadata("StoreMigrationPreviewMinimumSourceVersion");

    public static MigrationBinding CreateBinding()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new MigrationBinding
        {
            InstallDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppIdentity.DataDirectoryName),
            RoamingDirectory = AppIdentity.ResolveRoamingDataDirectory(),
            LocalDirectory = AppIdentity.ResolveSetupLocalDataDirectory(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            UserSid = identity.User?.Value ?? ""
        };
    }

    public static InnoInstallationDetector CreateDetector() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), new AppLogger());
}
