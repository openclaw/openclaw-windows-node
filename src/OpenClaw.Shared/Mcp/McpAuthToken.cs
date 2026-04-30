using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace OpenClaw.Shared.Mcp;

/// <summary>
/// Manages the MCP server's bearer token.
///
/// The token lives next to the rest of the tray's settings, at
/// <c>%APPDATA%\OpenClawTray\mcp-token.txt</c> (the exact path is composed by
/// the tray from <c>SettingsManager.SettingsDirectoryPath</c> and surfaced as
/// <c>NodeService.McpTokenPath</c> — that's the source of truth, not anything
/// in this file). Co-locating with settings means the test-suite override
/// <c>OPENCLAW_TRAY_DATA_DIR</c> isolates the token file too.
///
/// The token is **created lazily on first MCP server start** (i.e. the first
/// time the user enables Local MCP Server in Settings — until then the file
/// does not exist) and then **persists across tray restarts**. Local CLIs and
/// per-user agent registrations read the file and send the contents on every
/// request as <c>Authorization: Bearer &lt;contents&gt;</c>.
///
/// Defense in depth: the file inherits the parent directory's ACL — by default
/// only the current user (and SYSTEM/Administrators) can read it; the listener
/// is bound to loopback so the endpoint is invisible to other machines; and
/// Origin/Host checks block browser cross-origin attacks. The bearer is the
/// last line of defense against an untrusted local process on the same box.
/// </summary>
public static class McpAuthToken
{
    private const string FileName = "mcp-token.txt";

    /// <summary>
    /// Fallback path used only when a caller doesn't supply one. The tray itself
    /// passes a path computed from <c>SettingsManager.SettingsDirectoryPath</c>
    /// (exposed as <c>NodeService.McpTokenPath</c>) so this constant is **not**
    /// the live location for OpenClaw Tray installations — it's only a default
    /// for non-tray consumers (CLIs, tests) that don't want to compute one.
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "OpenClaw", FileName);
        }
    }

    /// <summary>
    /// Load the token from <see cref="DefaultPath"/>, creating a fresh random
    /// one if the file does not exist. Returns the token string.
    /// </summary>
    public static string LoadOrCreate() => LoadOrCreate(DefaultPath);

    public static string LoadOrCreate(string path)
    {
        // The previous behavior would catch any read exception and silently
        // regenerate. A transient lock or AV scan would then *rotate the
        // token*, breaking every configured MCP client. Distinguish missing
        // (regenerate) from unreadable (throw — visible in startup logs).
        if (File.Exists(path))
        {
            string existing;
            try
            {
                existing = File.ReadAllText(path).Trim();
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"MCP token file at '{path}' exists but could not be read: {ex.Message}. " +
                    "Refusing to regenerate (would invalidate all configured clients).", ex);
            }
            if (!string.IsNullOrEmpty(existing)) return existing;
            // Empty file: treat as missing. The atomic write below replaces it.
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            TryRestrictDirectoryAcl(dir);
        }
        // Atomic create: stage to a sibling temp file, lock its ACL, then
        // rename over the target. Without this, a power-loss / process-kill
        // mid-write would leave a zero-byte token file which the next
        // LoadOrCreate would treat as "missing" and overwrite — silently
        // rotating the token.
        var token = Generate();
        var tempPath = Path.Combine(
            string.IsNullOrEmpty(dir) ? Environment.CurrentDirectory : dir,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, token, Encoding.UTF8);
            TryRestrictFileAcl(tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
        TryRestrictFileAcl(path);
        return token;
    }

    public static string Reset(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Token path is required", nameof(path));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            TryRestrictDirectoryAcl(dir);
        }

        var token = Generate();
        var tempPath = Path.Combine(
            string.IsNullOrEmpty(dir) ? Environment.CurrentDirectory : dir,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, token, Encoding.UTF8);
            TryRestrictFileAcl(tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
        // Move on Windows preserves the source's DACL; re-apply defensively in
        // case a future rename strategy substitutes a different file.
        TryRestrictFileAcl(path);
        return token;
    }

    /// <summary>Read the token without creating a new one. Returns null when missing.</summary>
    public static string? TryLoad(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            var token = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch { return null; }
    }

    /// <summary>
    /// Verify that the token file at <paramref name="path"/> is owned by the
    /// current user and not readable by anyone outside (Owner, SYSTEM,
    /// Administrators). Returns null if the file looks fine; returns a
    /// human-readable warning otherwise so callers can log/toast at startup.
    /// On non-Windows or when the file does not exist, returns null.
    /// </summary>
    public static string? VerifyAcl(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (!OperatingSystem.IsWindows()) return null;
        return VerifyFileAclWindows(path);
    }

    /// <summary>
    /// Best-effort: lock the supplied directory's ACL to current user + SYSTEM
    /// + Administrators with inheritance disabled. No-op on non-Windows.
    /// Callers should call this when the tray's data directory is created so
    /// other locally-installed apps under the same user can't read the token
    /// (or anything else we drop alongside it).
    /// </summary>
    public static void TryRestrictDataDirectoryAcl(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        if (!OperatingSystem.IsWindows()) return;
        try { RestrictDirectoryAclWindows(dir); }
        catch { /* best-effort; acl restriction is defense-in-depth, not load-bearing */ }
    }

    private static void TryRestrictFileAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { RestrictFileAclWindows(path); }
        catch { /* see above */ }
    }

    private static void TryRestrictDirectoryAcl(string dir) => TryRestrictDataDirectoryAcl(dir);

    [SupportedOSPlatform("windows")]
    private static void RestrictFileAclWindows(string path)
    {
        var info = new FileInfo(path);
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = WindowsIdentity.GetCurrent().User;
        if (owner == null) return;
        sec.SetOwner(owner);
        sec.AddAccessRule(new FileSystemAccessRule(owner,
            FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(sec);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictDirectoryAclWindows(string dir)
    {
        var info = new DirectoryInfo(dir);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = WindowsIdentity.GetCurrent().User;
        if (owner == null) return;
        sec.SetOwner(owner);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(owner,
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(sec);
    }

    [SupportedOSPlatform("windows")]
    private static string? VerifyFileAclWindows(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var sec = info.GetAccessControl();
            var ownerSid = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var current = WindowsIdentity.GetCurrent().User;
            if (current == null) return null;
            if (ownerSid == null || !ownerSid.Equals(current))
            {
                return $"MCP token file owner is {ownerSid?.Value ?? "<unknown>"}; expected current user {current.Value}. Treat the token as compromised and reset it.";
            }
            // Walk the ACL — anything granting read rights to a principal
            // outside {current user, SYSTEM, Administrators} is broader than
            // expected.
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & (FileSystemRights.Read | FileSystemRights.ReadAndExecute | FileSystemRights.ReadData | FileSystemRights.FullControl | FileSystemRights.Modify)) == 0) continue;
                if (rule.IdentityReference is SecurityIdentifier sid &&
                    (sid.Equals(current) || sid.Equals(system) || sid.Equals(admins)))
                    continue;
                return $"MCP token file ACL grants read access to {rule.IdentityReference.Value}, broader than expected. Reset the token if this is unexpected.";
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"MCP token ACL inspection failed: {ex.Message}";
        }
    }

    /// <summary>32 bytes (256 bits) of CSPRNG → base64url → 43 ASCII chars (no padding).</summary>
    private static string Generate()
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToBase64String(raw)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
