namespace OpenClaw.TestSupport.Ui;

public sealed record ProfileFileMetadata(long? Size, DateTime? LastWriteTimeUtc);
public sealed record ProfileMetadataChange(string Path, ProfileFileMetadata? Before, ProfileFileMetadata? After)
{
    public string Kind => Before is null ? "added" : After is null ? "removed" : "modified";
}

/// <summary>Records only relative paths and metadata, never profile file contents.</summary>
public static class ProfileMetadataSnapshot
{
    public static IReadOnlyDictionary<string, ProfileFileMetadata> Capture(IReadOnlyDictionary<string, string> roots)
    {
        var metadata = new Dictionary<string, ProfileFileMetadata>(StringComparer.Ordinal);
        foreach (var (name, root) in roots)
        {
            if (!Path.IsPathFullyQualified(root))
                throw new ArgumentException("Profile metadata roots must be absolute.", nameof(roots));
            if (!Directory.Exists(root)) continue;
            metadata[$"{name}\\<root>"] = new(null, null);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var file = new FileInfo(path);
                metadata[$"{name}\\{Path.GetRelativePath(root, path)}"] = new(file.Length, file.LastWriteTimeUtc);
            }
        }
        return metadata;
    }

    public static IReadOnlyList<ProfileMetadataChange> Compare(
        IReadOnlyDictionary<string, ProfileFileMetadata> before,
        IReadOnlyDictionary<string, ProfileFileMetadata> after) =>
        before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(path => new ProfileMetadataChange(path,
                before.TryGetValue(path, out var old) ? old : null,
                after.TryGetValue(path, out var current) ? current : null))
            .Where(change => change.Before != change.After).ToArray();
}
