namespace OpenClaw.Shared;

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
}

/// <summary>
/// User-picked custom folder grant. Persisted in SettingsData.
/// </summary>
public sealed class SandboxCustomFolder
{
    public string Path { get; set; } = "";
    public SandboxFolderAccess Access { get; set; } = SandboxFolderAccess.ReadOnly;
}
