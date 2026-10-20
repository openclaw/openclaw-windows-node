using System.ComponentModel;

namespace OpenClaw.Shared.Mxc;

/// <summary>Resolves verified physical protection boundaries and runner-owned scratch, never caller grants.</summary>
internal static class MxcPhysicalPaths
{
    internal static IReadOnlyList<string> ProtectedPaths(IEnumerable<string> paths,
        Func<string, MxcFolderAdmission.Metadata?> readMetadata)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var logical = MxcFolderAdmission.NormalizeIdentity(path);
            result.Add(logical);
            result.Add(ResolveDirectory(logical, readMetadata));
        }
        return result.ToArray();
    }

    internal static string ResolveDirectory(string path, Func<string, MxcFolderAdmission.Metadata?> readMetadata,
        bool requireExisting = false)
    {
        var logical = MxcFolderAdmission.NormalizeIdentity(path);
        var components = new Stack<string>();
        for (var current = logical; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            components.Push(current);
        string? physicalParent = null;
        string? logicalParent = null;
        while (components.TryPop(out var component))
        {
            MxcFolderAdmission.Metadata? metadata;
            try { metadata = readMetadata(component); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                throw new NotSupportedException(
                    $"The physical protection or scratch path for '{component}' could not be verified.", ex);
            }
            if (metadata is null)
            {
                if (requireExisting || physicalParent is null || logicalParent is null)
                    throw new NotSupportedException($"The physical protection or scratch path for '{component}' is unavailable.");
                // Carry a prospective protected descendant beneath its verified existing parent.
                // Do not create state, guess package identities, or lose its future deny boundary.
                return MxcFolderAdmission.NormalizeIdentity(
                    Path.Combine(physicalParent, Path.GetRelativePath(logicalParent, logical)));
            }
            var existing = metadata.Value;
            if ((existing.Attributes & FileAttributes.Directory) == 0 ||
                ((existing.Attributes & FileAttributes.ReparsePoint) != 0 &&
                    !MxcFolderAdmission.IsAdmissible(component, existing)))
                throw new NotSupportedException(
                    $"The physical protection or scratch path for '{component}' is not a supported directory.");
            try { physicalParent = MxcFolderAdmission.NormalizeIdentity(existing.FinalPath); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new NotSupportedException(
                    $"The physical protection or scratch path for '{component}' could not be verified.", ex);
            }
            logicalParent = component;
        }
        return physicalParent!;
    }
}
