using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trace = System.Diagnostics.Trace;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Owns completed runtime-set membership for the Local AI archive cache and the
/// pruning that keeps or deletes whole sets.
/// <para>
/// A set record (<c>LocalAICache\sets\&lt;set-id&gt;.json</c>) is written only after the
/// runtime extracted from its archives was accepted, and only when every member is in
/// the cache. Pruning keeps the current pins plus every member of the newest
/// <c>retainedStaleSets</c> complete older sets; any other entry, including an archive
/// left behind by a failed acquisition, never counts as a set and is deleted. Archives
/// shared by several sets survive while any kept set references them.
/// </para>
/// <para>
/// Records are bookkeeping only. They never make cached bytes trusted: reuse still
/// requires the full SHA-256 check against the compiled-in pin. Every failure warns
/// and leaves the install result unchanged.
/// </para>
/// </summary>
internal static class LocalAiArchiveCacheRetention
{
    internal const int SetRecordSchemaVersion = 1;

    internal sealed record SetMember(string Sha256, string FileName);

    internal sealed record SetRecord(
        int SchemaVersion,
        IReadOnlyList<SetMember>? Archives,
        DateTimeOffset LastUsedUtc);

    private sealed record CompleteSet(string Id, SetRecord Record);

    /// <summary>
    /// Records <paramref name="archives"/> as the most recently used complete set, then
    /// prunes. Call only after the installed runtime passed inspection.
    /// </summary>
    public static void Commit(
        string localDataDirectory,
        IReadOnlyCollection<LocalAiPinnedArchive> archives,
        int retainedStaleSets,
        DateTimeOffset now)
    {
        try
        {
            SetMember[] members = Canonical(archives.Select(archive => new SetMember(archive.Sha256, archive.FileName)));
            string setId = SetId(members);
            if (members.All(member => IsCached(localDataDirectory, member)))
                WriteRecord(localDataDirectory, setId, new SetRecord(SetRecordSchemaVersion, members, now));
            else
                Trace.TraceWarning("Local AI runtime set was not recorded because an archive is missing from the cache.");

            Prune(localDataDirectory, setId, members, retainedStaleSets);
        }
        catch (Exception ex) when (IsCacheIoFailure(ex))
        {
            Trace.TraceWarning("Could not update the Local AI archive cache: {0}", ex.Message);
        }
    }

    internal static string SetId(IEnumerable<SetMember> members) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', Canonical(members).Select(member => $"{member.Sha256}:{member.FileName}")))));

    private static SetMember[] Canonical(IEnumerable<SetMember> members) =>
        members
            .OrderBy(member => member.Sha256, StringComparer.Ordinal)
            .ThenBy(member => member.FileName, StringComparer.Ordinal)
            .ToArray();

    private static void Prune(
        string localDataDirectory,
        string currentSetId,
        IEnumerable<SetMember> current,
        int retainedStaleSets)
    {
        CompleteSet[] olderSets = ReadCompleteSets(localDataDirectory)
            .Where(set => set.Id != currentSetId)
            .OrderByDescending(set => set.Record.LastUsedUtc)
            .ToArray();
        CompleteSet[] kept = olderSets.Take(retainedStaleSets).ToArray();

        // Drop records before their archives, so an interrupted prune never leaves a
        // record that claims a set whose members are gone.
        foreach (CompleteSet evicted in olderSets.Skip(retainedStaleSets))
            TryDelete(LocalAiPathPolicy.TryDeleteArchiveCacheSet, localDataDirectory, evicted.Id);

        HashSet<string> keep = current
            .Concat(kept.SelectMany(set => set.Record.Archives!))
            .Select(member => member.Sha256)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string entry in EnumerateNames(LocalAiPathPolicy.TryGetArchiveCacheArchivesDirectory, localDataDirectory, suffix: "")
                     .Where(name => !keep.Contains(name)))
        {
            TryDelete(LocalAiPathPolicy.TryDeleteArchiveCacheEntry, localDataDirectory, entry);
        }
    }

    /// <summary>
    /// Yields every usable set record and deletes unusable ones: unreadable or
    /// unknown-schema records, records whose name does not match their members, and
    /// records whose members are no longer all cached.
    /// </summary>
    private static IEnumerable<CompleteSet> ReadCompleteSets(string localDataDirectory)
    {
        foreach (string setId in EnumerateNames(LocalAiPathPolicy.TryGetArchiveCacheSetsDirectory, localDataDirectory, suffix: ".json"))
        {
            if (TryReadRecord(localDataDirectory, setId) is { } record && IsComplete(localDataDirectory, setId, record))
                yield return new CompleteSet(setId, record);
            else
                TryDelete(LocalAiPathPolicy.TryDeleteArchiveCacheSet, localDataDirectory, setId);
        }
    }

    private static bool IsComplete(string localDataDirectory, string setId, SetRecord record) =>
        record is { SchemaVersion: SetRecordSchemaVersion, Archives: { Count: > 0 } members } &&
        members.All(member => IsCached(localDataDirectory, member)) &&
        SetId(members) == setId;

    private static bool IsCached(string localDataDirectory, SetMember? member) =>
        member is { Sha256: not null, FileName: not null } &&
        LocalAiPathPolicy.TryGetArchiveCachePath(localDataDirectory, member.FileName, member.Sha256, out var path, out _) &&
        File.Exists(path);

    private static SetRecord? TryReadRecord(string localDataDirectory, string setId)
    {
        if (!LocalAiPathPolicy.TryGetArchiveCacheSetPath(localDataDirectory, setId, out var path, out _))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SetRecord>(File.ReadAllText(path), SetupConfig.JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException || IsCacheIoFailure(ex))
        {
            Trace.TraceWarning("Ignoring unreadable Local AI archive cache set '{0}': {1}", path, ex.Message);
            return null;
        }
    }

    private static void WriteRecord(string localDataDirectory, string setId, SetRecord record)
    {
        if (!LocalAiPathPolicy.TryGetArchiveCacheSetPath(localDataDirectory, setId, out var path, out var error))
        {
            Trace.TraceWarning("Could not record the Local AI runtime set: {0}", error);
            return;
        }

        // Create the directory, then resolve again so a swapped-in reparse point is refused.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!LocalAiPathPolicy.TryGetArchiveCacheSetPath(localDataDirectory, setId, out path, out error))
        {
            Trace.TraceWarning("Could not record the Local AI runtime set: {0}", error);
            return;
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(record, SetupConfig.JsonWriteOptions));
    }

    private delegate bool TryResolveDirectory(string localDataDirectory, out string directory, out string error);

    private delegate bool TryDeleteByName(string localDataDirectory, string name, out string error);

    /// <summary>
    /// Lists lowercase SHA-256 names (with <paramref name="suffix"/> stripped) directly
    /// under a cache directory, skipping reparse points and anything the cache does not own.
    /// </summary>
    private static IEnumerable<string> EnumerateNames(
        TryResolveDirectory resolve,
        string localDataDirectory,
        string suffix)
    {
        if (!resolve(localDataDirectory, out var directory, out var error))
        {
            Trace.TraceWarning("Could not read the Local AI archive cache: {0}", error);
            return [];
        }

        return Directory.Exists(directory)
            ? new DirectoryInfo(directory)
                .EnumerateFileSystemInfos()
                .Where(entry =>
                    !entry.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                    entry.Name.EndsWith(suffix, StringComparison.Ordinal))
                .Select(entry => entry.Name[..^suffix.Length])
                .Where(LocalAiPathPolicy.IsArchiveCacheEntryName)
                .ToArray()
            : [];
    }

    private static void TryDelete(TryDeleteByName delete, string localDataDirectory, string name)
    {
        if (!delete(localDataDirectory, name, out var error))
            Trace.TraceWarning("Could not prune Local AI archive cache item '{0}': {1}", name, error);
    }

    private static bool IsCacheIoFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
