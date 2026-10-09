using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

internal static class MxcSyntheticContext
{
    internal static MxcRequestContext Create(string root)
    {
        var profile = Path.Combine(root, "profile");
        var folders = new[]
        {
            new MxcUserFolder("Profile", profile),
            new MxcUserFolder("Documents", Path.Combine(profile, "Documents")),
            new MxcUserFolder("Desktop", Path.Combine(profile, "Desktop")),
            new MxcUserFolder("Downloads", Path.Combine(root, "redirected-downloads")),
            new MxcUserFolder("OneDrive", Path.Combine(root, "redirected-onedrive"), IsOptional: true),
        };
        var denied = new[]
        {
            Path.Combine(profile, ".ssh"),
            Path.Combine(profile, "AppData", "Roaming", "OpenClawTray"),
            Path.Combine(profile, "AppData", "Local", "Microsoft", "Edge", "User Data"),
            Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "PowerShell", "PSReadLine"),
        };
        foreach (var folder in folders) Directory.CreateDirectory(folder.Path!);
        foreach (var path in denied) Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(profile, "tools"));
        return new()
        {
            WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            SystemDirectory = Environment.SystemDirectory,
            UserFolders = folders,
            ProtectedPaths = denied,
        };
    }
}
