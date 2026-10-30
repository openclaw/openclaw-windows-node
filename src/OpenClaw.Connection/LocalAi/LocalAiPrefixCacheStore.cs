// Names, recency, and pruning for llama-server slot saves of the shared prompt prefix.
// The file name is the whole index: it is a hash of everything that makes the saved state
// valid, so no manifest is needed. Modification time is the recency record: a save writes
// it and a restore refreshes it. Only generated names are ever touched or deleted.
//
// Usage:
//   string key = LocalAiPrefixCacheKey.Compute(profile, model, prefixMessages, tools);
//   var store = new LocalAiPrefixCacheStore(paths.PrefixCacheDirectory);
//   if (store.Exists(LocalAiPrefixCacheStore.FileNameFor(key))) { /* restore, then store.Touch */ }
//   store.Prune(); // keep the four most recently used
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenClaw.Connection.LocalAi;

internal static class LocalAiPrefixCacheKey
{
    /// <summary>A 40-hex-character digest of the profile, model, prefix messages, and tool definitions.</summary>
    public static string Compute(string profileFingerprint, string model, JsonArray prefixMessages, JsonNode? tools)
    {
        var envelope = new JsonArray
        {
            profileFingerprint,
            model,
            prefixMessages.DeepClone(),
            tools?.DeepClone(),
        };
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
        return Convert.ToHexStringLower(digest.AsSpan(0, 20));
    }
}

internal sealed class LocalAiPrefixCacheStore
{
    /// <summary>Distinct prefixes kept on disk; each file is hundreds of MB to over 1 GB.</summary>
    public const int DefaultKeep = 4;

    private static readonly Regex s_fileName = new(
        "^openclaw-prefix-[0-9a-f]{40}\\.bin$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private const string PrefixCacheFilePrefix = "openclaw-prefix-";

    private readonly int _keep;

    public LocalAiPrefixCacheStore(string directory, int keep = DefaultKeep)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        DirectoryPath = directory;
        _keep = keep;
    }

    public string DirectoryPath { get; }

    public static string FileNameFor(string key) => $"{PrefixCacheFilePrefix}{key}.bin";

    public bool Exists(string fileName) =>
        s_fileName.IsMatch(fileName) && File.Exists(Path.Combine(DirectoryPath, fileName));

    /// <summary>Counts a restore as use so a prefix that is restored every start is not pruned.</summary>
    public void Touch(string fileName)
    {
        if (!s_fileName.IsMatch(fileName))
            return;
        try
        {
            File.SetLastWriteTimeUtc(Path.Combine(DirectoryPath, fileName), DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Recency is advisory; a failed refresh only risks pruning this file earlier.
        }
    }

    /// <summary>The most recently used generated file, or null when none exist.</summary>
    public string? Newest() => GeneratedFilesNewestFirst().FirstOrDefault()?.Name;

    public static string KeyFromFileName(string fileName) =>
        fileName[PrefixCacheFilePrefix.Length..^".bin".Length];

    /// <summary>Deletes all but the most recently used files and returns the names removed.</summary>
    public IReadOnlyList<string> Prune()
    {
        var removed = new List<string>();
        foreach (FileInfo stale in GeneratedFilesNewestFirst().Skip(_keep).ToArray())
        {
            try
            {
                stale.Delete();
                removed.Add(stale.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or already gone; the next save prunes again.
            }
        }
        return removed;
    }

    private IEnumerable<FileInfo> GeneratedFilesNewestFirst()
    {
        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(DirectoryPath)
                .EnumerateFiles()
                .Where(file => s_fileName.IsMatch(file.Name))
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        return files
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal);
    }
}
