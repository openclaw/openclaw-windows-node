using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// On-disk announcement of a running devtools session. Written by the server on
/// <see cref="DevtoolsMcpServer.AnnounceReady"/>, removed on <c>Dispose</c>.
/// Readers (the CLI's endpoint discovery, `session list`, the single-instance
/// check) consume it to locate the MCP endpoint without any configuration.
/// Spec 025 §5.
/// </summary>
internal sealed class LockfileEntry
{
    [JsonPropertyName("schema")] public string Schema { get; set; } = LockfileRegistry.SchemaTag;
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
    [JsonPropertyName("transport")] public string Transport { get; set; } = "http";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("buildTag")] public string BuildTag { get; set; } = "";
    [JsonPropertyName("project")] public string Project { get; set; } = "";
    [JsonPropertyName("startedAt")] public string StartedAt { get; set; } = "";
}

// Dedicated context with WriteIndented so humans `cat`-ing the lockfile see
// pretty-printed JSON. DevtoolsJsonContext is shared for wire traffic and
// stays compact.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LockfileEntry))]
internal partial class LockfileJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads, writes, and liveness-probes devtools session lockfiles under
/// <c>%TEMP%/reactor-devtools/</c>. Owned by the server so the producer controls
/// the contract; the CLI links to the same file to consume lockfiles without a
/// project reference. Spec 025 §5.
/// </summary>
internal static class LockfileRegistry
{
    public const string SchemaTag = "reactor-devtools-lockfile/1";
    public const string McpSchemaTag = "reactor-devtools-mcp/1";

    /// <summary>
    /// <c>%TEMP%/reactor-devtools/</c> on Windows. On non-Windows hosts this
    /// falls through to the platform temp directory — acceptable for the
    /// WinUI-only world we ship in today and a placeholder for a future
    /// <c>$XDG_RUNTIME_DIR</c> move (spec §14.3).
    /// </summary>
    public static string Directory => Path.Combine(Path.GetTempPath(), "reactor-devtools");

    /// <summary>
    /// Lockfile path for a project. Hash is SHA-256 of the canonicalized
    /// .csproj path, truncated to 16 hex chars, so
    /// <c>C:\foo\bar.csproj</c> and <c>c:/foo/bar.csproj</c> collide
    /// deliberately (they address the same project).
    /// </summary>
    public static string PathFor(string projectIdentifier)
    {
        var hash = ComputeHash(projectIdentifier);
        return Path.Combine(Directory, $"{hash}.json");
    }

    internal static string ComputeHash(string projectIdentifier)
    {
        var canonical = Canonicalize(projectIdentifier);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    internal static string Canonicalize(string projectIdentifier)
    {
        string full;
        try { full = Path.GetFullPath(projectIdentifier); }
        catch { full = projectIdentifier; }

        var normalized = full.Replace('/', '\\');
        return normalized.ToLowerInvariant();
    }

    /// <summary>
    /// Writes the lockfile atomically via write-to-tmp + overwrite-rename, so
    /// a concurrent reader never observes a missing or half-written file.
    /// Creates the parent directory if missing. Skips the rename when the
    /// on-disk content already matches so a reload loop doesn't thrash
    /// filesystem watchers.
    /// </summary>
    public static void Write(string path, LockfileEntry entry)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) global::System.IO.Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(entry, LockfileJsonContext.Default.LockfileEntry);

        if (File.Exists(path))
        {
            try
            {
                if (string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal))
                    return;
            }
            catch { /* fall through to rewrite */ }
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Deletes the lockfile if it exists. Non-throwing — deletion failures are
    /// swallowed; the file gets GC'd by the next reader's staleness check.
    /// </summary>
    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static bool TryRead(string path, out LockfileEntry? entry)
    {
        entry = null;
        if (!File.Exists(path)) return false;
        try
        {
            var json = File.ReadAllText(path);
            entry = JsonSerializer.Deserialize(json, LockfileJsonContext.Default.LockfileEntry);
            return entry is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enumerates every lockfile under <see cref="Directory"/>. Returns
    /// parse-failing or missing entries as <c>null</c>-entry tuples so callers
    /// can decide whether to GC them.
    /// </summary>
    public static IEnumerable<(string Path, LockfileEntry? Entry)> EnumerateAll()
    {
        if (!global::System.IO.Directory.Exists(Directory)) yield break;
        string[] files;
        try { files = global::System.IO.Directory.GetFiles(Directory, "*.json", SearchOption.TopDirectoryOnly); }
        catch { yield break; }

        foreach (var f in files)
        {
            TryRead(f, out var entry);
            yield return (f, entry);
        }
    }

    /// <summary>
    /// A lockfile is live iff its pid names a running process AND — for HTTP
    /// transport — a <c>GET /mcp</c> returns the expected schema tag. Stdio
    /// sessions are considered live on pid match alone because the HTTP probe
    /// doesn't apply; the CLI refuses to use them anyway.
    /// </summary>
    public static bool IsLive(LockfileEntry entry)
    {
        if (entry is null || entry.Pid <= 0) return false;
        if (!PidAlive(entry.Pid)) return false;
        if (string.Equals(entry.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
            return true;
        return HttpProbe(entry.Endpoint);
    }

    private static bool PidAlive(int pid)
    {
        try
        {
            var p = global::System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch { return false; }
    }

    private static bool HttpProbe(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint)) return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            var resp = http.GetAsync(endpoint).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return false;
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("schema", out var s)) return false;
            return string.Equals(s.GetString(), McpSchemaTag, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
