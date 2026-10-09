namespace OpenClaw.Shared;

public enum SystemRunFilesystemScope
{
    SelectedFolders,
    UserFilesReadOnly,
    UserFilesReadWrite,
}

public enum SystemRunAccessPreset { Strict, Balanced, Open }

/// <summary>Preset-owned file and internet fields. Clipboard, UI, limits and approval policy stay independent.</summary>
public static class SystemRunPermissionPresets
{
    public static SystemRunAccessPreset? Detect(SettingsData settings)
    {
        if (settings.SandboxDocumentsAccess is not null || settings.SandboxDownloadsAccess is not null ||
            settings.SandboxDesktopAccess is not null || settings.SandboxCustomFolders is { Count: > 0 })
            return null;

        return (settings.SystemRunFilesystemScope ?? SystemRunFilesystemScope.SelectedFolders,
            settings.SystemRunAllowOutbound) switch
        {
            (SystemRunFilesystemScope.SelectedFolders, false) => SystemRunAccessPreset.Strict,
            (SystemRunFilesystemScope.UserFilesReadOnly, true) => SystemRunAccessPreset.Balanced,
            (SystemRunFilesystemScope.UserFilesReadWrite, true) => SystemRunAccessPreset.Open,
            _ => null,
        };
    }

    public static SettingsData Apply(SettingsData settings, SystemRunAccessPreset preset) => settings with
    {
        SystemRunFilesystemScope = preset switch
        {
            SystemRunAccessPreset.Strict => SystemRunFilesystemScope.SelectedFolders,
            SystemRunAccessPreset.Balanced => SystemRunFilesystemScope.UserFilesReadOnly,
            SystemRunAccessPreset.Open => SystemRunFilesystemScope.UserFilesReadWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        },
        SystemRunAllowOutbound = preset != SystemRunAccessPreset.Strict,
        SandboxDocumentsAccess = null,
        SandboxDownloadsAccess = null,
        SandboxDesktopAccess = null,
        SandboxCustomFolders = [],
    };
}

/// <summary>
/// Clipboard access policy for sandboxed payloads. Mirrors MXC's
/// <c>ClipboardPolicy</c> values (none / read / write / all).
/// </summary>
public enum SandboxClipboardMode
{
    None,
    Read,
    Write,
    Both,
}

/// <summary>
/// Whether a folder is exposed read-only or read-write to the sandbox.
/// </summary>
public enum SandboxFolderAccess
{
    ReadOnly,
    ReadWrite,
    Blocked,
}

/// <summary>
/// User-picked custom folder grant. Persisted in SettingsData.
/// </summary>
public sealed class SandboxCustomFolder
{
    public string Path { get; set; } = "";
    public SandboxFolderAccess Access { get; set; } = SandboxFolderAccess.ReadOnly;
}
