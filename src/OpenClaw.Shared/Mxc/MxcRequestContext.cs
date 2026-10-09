using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Mxc;

/// <summary>One command's OS-resolved runtime, personal-folder and sensitive-root snapshot.</summary>
public sealed record MxcRequestContext
{
    public required string WindowsDirectory { get; init; }
    public required string SystemDirectory { get; init; }
    public required IReadOnlyList<MxcUserFolder> UserFolders { get; init; }
    public required IReadOnlyList<string> ProtectedPaths { get; init; }

    public static MxcRequestContext Capture(string settingsDirectory)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folders = new List<MxcUserFolder>
        {
            Existing("Profile", profile),
            Existing("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            Existing("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            KnownFolder("Downloads", new("374DE290-123F-4565-9164-39C4925E467B")),
            KnownFolder("OneDrive", new("A52BBA46-E9E1-435F-B3D9-28DAA648C0F6"), optional: true),
        };
        var denied = new List<string> { settingsDirectory };
        if (!string.IsNullOrWhiteSpace(profile))
        {
            denied.Add(Path.Combine(profile, ".ssh"));
            denied.Add(Path.Combine(profile, ".openclaw"));
        }
        if (!string.IsNullOrWhiteSpace(local))
            denied.AddRange([
                Path.Combine(local, "OpenClawTray"),
                Path.Combine(local, "OpenClawTray-Dev"),
                Path.Combine(local, "Google", "Chrome", "User Data"),
                Path.Combine(local, "Microsoft", "Edge", "User Data"),
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
            ]);
        if (!string.IsNullOrWhiteSpace(roaming))
            denied.AddRange([
                Path.Combine(roaming, "OpenClawTray"),
                Path.Combine(roaming, "OpenClawTray-Dev"),
                Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"),
                Path.Combine(roaming, "Microsoft", "Windows", "PowerShell", "PSReadLine"),
                Path.Combine(roaming, "Microsoft", "PowerShell", "PSReadLine"),
            ]);
        foreach (var key in new[] { "OPENCLAW_TRAY_APPDATA_DIR", "OPENCLAW_TRAY_LOCALAPPDATA_DIR" })
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } root)
                denied.Add(Path.Combine(root, "OpenClawTray"));
        if (Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR") is { Length: > 0 } direct)
            denied.Add(direct);
        return new()
        {
            WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            SystemDirectory = Environment.SystemDirectory,
            UserFolders = folders.AsReadOnly(),
            ProtectedPaths = denied.AsReadOnly(),
        };
    }

    internal string RequireFolder(string name)
    {
        var folder = UserFolders.FirstOrDefault(f => f.Name == name);
        if (folder?.Path is null || folder.Error is not null)
            throw new NotSupportedException(
                $"{name} could not be resolved or accessed. {folder?.Error} " +
                "Review the OS folder location or choose Selected folders and grant an available non-root folder.");
        return folder.Path;
    }

    internal static string Downloads() =>
        KnownFolder("Downloads", new("374DE290-123F-4565-9164-39C4925E467B")).RequirePath();

    private static MxcUserFolder Existing(string name, string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
            ? new(name, path)
            : new(name, path, "The OS-resolved folder is not available.");

    private static MxcUserFolder KnownFolder(string name, Guid id, bool optional = false)
    {
        if (!OperatingSystem.IsWindows())
            return new(name, null, "Windows known folders are unavailable.");
        var hr = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer);
        try
        {
            if (hr != 0 || pointer == IntPtr.Zero)
            {
                // These optional known-folder results mean no available configured OneDrive root.
                // The UI declares it not included; access-denied/other errors block user-files policy.
                if (optional && hr is unchecked((int)0x80070002) or unchecked((int)0x80070003) or unchecked((int)0x80004005))
                    return new(name, null, null, IsOptional: true);
                return new(name, null, $"Windows known-folder resolution failed (0x{hr:X8}).", optional);
            }
            return Existing(name, Marshal.PtrToStringUni(pointer)) with { IsOptional = optional };
        }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}

public sealed record MxcUserFolder(string Name, string? Path, string? Error = null, bool IsOptional = false)
{
    internal string RequirePath() => Path is not null && Error is null ? Path :
        throw new NotSupportedException($"{Name} is unavailable. {Error} Select its available actual location as a custom folder.");
}
