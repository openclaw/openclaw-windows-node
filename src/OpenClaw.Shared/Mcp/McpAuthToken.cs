using System;
using System.IO;
using System.Security.Cryptography;
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
        if (File.Exists(path))
        {
            try
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing)) return existing;
            }
            catch { /* fall through and regenerate */ }
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var token = Generate();
        File.WriteAllText(path, token);
        return token;
    }

    public static string Reset(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Token path is required", nameof(path));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var token = Generate();
        var tempPath = Path.Combine(
            string.IsNullOrEmpty(dir) ? Environment.CurrentDirectory : dir,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        File.WriteAllText(tempPath, token, Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
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
