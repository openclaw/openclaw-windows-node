namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Pure function: <see cref="SettingsData"/> + capability name → <see cref="SandboxPolicy"/>.
/// </summary>
/// <remarks>
/// Currently covers system.run only. The signature is stable so adding other
/// capabilities is an internal extension.
///
/// Policy decisions:
/// <list type="bullet">
/// <item><c>readonlyPaths</c> — populated from user-granted folders (Documents,
/// Downloads, Desktop, custom). The Node bridge additionally merges PATH-specific
/// tool directories so spawned shells can find git/node/python/etc.</item>
/// <item><c>readwritePaths</c> — user-granted read+write folders. The Node bridge
/// adds a per-invocation scratch directory and rewrites TEMP/TMP/TMPDIR to point
/// at it, so the user's real %TEMP% stays out of reach.</item>
/// <item><c>deniedPaths</c> — settings directory (protect MCP token, gateway
/// credentials, ElevenLabs key), <c>~/.ssh</c>, and the common browser profile
/// roots (Chrome / Edge / Firefox / Brave). Always blocked regardless of grants.</item>
/// <item><c>network.allowOutbound</c> — bound by <see cref="SettingsData.SystemRunAllowOutbound"/>.</item>
/// <item><c>ui</c> — default-deny across the board; shell exec doesn't need windows.</item>
/// </list>
/// </remarks>
public static class MxcPolicyBuilder
{
    /// <summary>
    /// Policy schema version. Per the @microsoft/mxc-sdk validator, this must be
    /// in the supported range (currently MIN 0.4.0-alpha, SUPPORTED 0.5.0-alpha).
    /// </summary>
    public const string SupportedPolicyVersion = "0.4.0-alpha";

    /// <summary>
    /// Build the policy for a system.run invocation given current settings.
    /// </summary>
    /// <param name="settings">Live settings snapshot from <see cref="SettingsManager"/> (or test stub).</param>
    /// <param name="settingsDirectoryPath">
    /// Path to <see cref="SettingsManager.SettingsDirectoryPath"/>. Passed in (rather than
    /// read statically) so tests can isolate via <c>OPENCLAW_TRAY_DATA_DIR</c>.
    /// </param>
    public static SandboxPolicy ForSystemRun(SettingsData settings, string settingsDirectoryPath)
    {
        var deniedPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(settingsDirectoryPath))
            deniedPaths.Add(settingsDirectoryPath);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshPath = Path.Combine(userProfile, ".ssh");
        if (!string.IsNullOrWhiteSpace(sshPath))
            deniedPaths.Add(sshPath);

        // Always-blocked browser profile roots. Cookies, saved passwords, autofill,
        // and session tokens live here — they must remain unreachable even if the
        // user (or a malicious settings.json) tries to grant a parent folder.
        // Add these regardless of whether the browser is installed; the AppContainer
        // policy treats nonexistent denies as a no-op.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            deniedPaths.Add(Path.Combine(localAppData, "Google", "Chrome", "User Data"));
            deniedPaths.Add(Path.Combine(localAppData, "Microsoft", "Edge", "User Data"));
            deniedPaths.Add(Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"));
        }
        if (!string.IsNullOrWhiteSpace(appData))
        {
            deniedPaths.Add(Path.Combine(appData, "Mozilla", "Firefox", "Profiles"));
            deniedPaths.Add(Path.Combine(appData, "Microsoft", "Windows", "PowerShell", "PSReadLine"));
        }

        var readonlyPaths = new List<string>();
        var readwritePaths = new List<string>();

        AddWellKnownFolder(Environment.SpecialFolder.MyDocuments, settings.SandboxDocumentsAccess, readonlyPaths, readwritePaths);
        AddWellKnownFolder(Environment.SpecialFolder.Desktop, settings.SandboxDesktopAccess, readonlyPaths, readwritePaths);
        AddDownloadsFolder(userProfile, settings.SandboxDownloadsAccess, readonlyPaths, readwritePaths);

        if (settings.SandboxCustomFolders is { Count: > 0 } customFolders)
        {
            foreach (var folder in customFolders)
            {
                if (string.IsNullOrWhiteSpace(folder.Path)) continue;
                if (folder.Access == SandboxFolderAccess.ReadWrite)
                    readwritePaths.Add(folder.Path);
                else
                    readonlyPaths.Add(folder.Path);
            }
        }

        // Defense-in-depth: strip any allow-list entry that is equal to, or a
        // child of, a denied path. The AppContainer policy SHOULD already
        // prioritize deny over allow per the @microsoft/mxc-sdk schema, but
        // that's an undocumented invariant on an alpha SDK. Filtering here
        // means a misconfigured / malicious custom-folder grant pointing at
        // `~\.ssh` or a browser profile cannot bleed through.
        readonlyPaths = FilterOutDenied(readonlyPaths, deniedPaths);
        readwritePaths = FilterOutDenied(readwritePaths, deniedPaths);

        return new SandboxPolicy(
            Version: SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: readwritePaths,
                ReadonlyPaths: readonlyPaths,
                DeniedPaths: deniedPaths,
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(
                AllowOutbound: settings.SystemRunAllowOutbound,
                // LAN access (privateNetworkClientServer capability) intentionally not
                // exposed: MXC team confirmed only internetClient is validated today.
                AllowLocalNetwork: false),
            Ui: new UiPolicy(
                AllowWindows: false,
                Clipboard: MapClipboard(settings.SandboxClipboard),
                AllowInputInjection: false),
            TimeoutMs: settings.SandboxTimeoutMs > 0 ? settings.SandboxTimeoutMs : null);
    }

    /// <summary>
    /// Remove any allow-list entry that overlaps a denied path.
    /// Case-insensitive (NTFS semantics) and tolerant of trailing slashes.
    /// </summary>
    private static List<string> FilterOutDenied(List<string> allowed, List<string> denied)
    {
        if (allowed.Count == 0 || denied.Count == 0) return allowed;
        var normalizedDenied = denied
            .Select(NormalizePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        return allowed
            .Where(a =>
            {
                var na = NormalizePath(a);
                if (string.IsNullOrEmpty(na)) return false;
                foreach (var d in normalizedDenied)
                {
                    if (PathsOverlap(na, d)) return false;
                }
                return true;
            })
            .ToList();
    }

    private static bool PathsOverlap(string left, string right)
    {
        return IsSameOrNested(left, right) || IsSameOrNested(right, left);
    }

    private static bool IsSameOrNested(string path, string candidateParent)
    {
        if (string.Equals(path, candidateParent, StringComparison.OrdinalIgnoreCase))
            return true;

        return path.StartsWith(candidateParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(candidateParent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            // Path.GetFullPath collapses .. / . and resolves relative parts.
            // Trim trailing separators so "C:\foo\" and "C:\foo" compare equal.
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return path;
        }
    }

    private static void AddWellKnownFolder(
        Environment.SpecialFolder folder,
        SandboxFolderAccess? access,
        List<string> readonlyPaths,
        List<string> readwritePaths)
    {
        if (access is null) return;
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (access == SandboxFolderAccess.ReadWrite) readwritePaths.Add(path);
        else readonlyPaths.Add(path);
    }

    private static void AddDownloadsFolder(
        string userProfile,
        SandboxFolderAccess? access,
        List<string> readonlyPaths,
        List<string> readwritePaths)
    {
        if (access is null) return;
        // .NET has no SpecialFolder.Downloads. Use the Win32 known-folder API so we
        // honor user redirection (e.g., OneDrive\Downloads). Fall back to the
        // %USERPROFILE%\Downloads convention if the API isn't available.
        var path = ResolveKnownFolderDownloads() ?? Path.Combine(userProfile, "Downloads");
        if (access == SandboxFolderAccess.ReadWrite) readwritePaths.Add(path);
        else readonlyPaths.Add(path);
    }

    private static readonly Guid s_folderIdDownloads =
        new("374DE290-123F-4565-9164-39C4925E467B");

    private static string? ResolveKnownFolderDownloads()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var hr = SHGetKnownFolderPath(s_folderIdDownloads, 0, IntPtr.Zero, out var ptr);
            if (hr != 0 || ptr == IntPtr.Zero) return null;
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr); }
            finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(ptr); }
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    private static ClipboardPolicy MapClipboard(SandboxClipboardMode mode) => mode switch
    {
        SandboxClipboardMode.Read => ClipboardPolicy.Read,
        SandboxClipboardMode.Write => ClipboardPolicy.Write,
        SandboxClipboardMode.Both => ClipboardPolicy.All,
        _ => ClipboardPolicy.None,
    };
}
