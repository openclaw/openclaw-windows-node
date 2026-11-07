namespace OpenClaw.Shared.Mxc;

/// <summary>One lexical folder boundary shared by the editor and SDK request builder.</summary>
public sealed record MxcFolderPermission(
    string Path,
    SandboxFolderAccess? Access,
    IReadOnlyList<string> KnownFolders,
    bool IsPreset,
    bool HasOverride,
    bool HasCustomGrant,
    bool HasLegacyBlocked,
    bool IsRuntime,
    string? Error);

public static class MxcFolderPermissions
{
    /// <summary>
    /// Explicit paths replace known-folder overrides, which replace preset entries at the
    /// same normalized path. Equal-priority conflicts prefer No access, then read-only.
    /// Parent/child specificity is enforced by the SDK, not flattened here.
    /// </summary>
    public static IReadOnlyList<MxcFolderPermission> Resolve(SettingsData settings, MxcRequestContext? context)
    {
        var entries = new List<(string Path, string? Name, SandboxFolderAccess? Access, int Priority, string? Error)>();
        var broad = settings.SystemRunFilesystemScope is SystemRunFilesystemScope.UserFilesReadOnly or SystemRunFilesystemScope.UserFilesReadWrite;
        if (broad && context is not null)
        {
            var access = settings.SystemRunFilesystemScope == SystemRunFilesystemScope.UserFilesReadWrite
                ? SandboxFolderAccess.ReadWrite : SandboxFolderAccess.ReadOnly;
            foreach (var folder in context.UserFolders)
            {
                if (folder.IsOptional && folder.Path is null && folder.Error is null) continue;
                entries.Add((folder.Path ?? "", folder.Name, access, 0, folder.Error));
            }
        }
        AddKnown("Documents", settings.SandboxDocumentsAccess);
        AddKnown("Desktop", settings.SandboxDesktopAccess);
        AddKnown("Downloads", settings.SandboxDownloadsAccess);
        foreach (var folder in settings.SandboxCustomFolders ?? [])
            entries.Add((folder.Path ?? "", null, folder.Access, 2, null));

        var groups = new Dictionary<string, List<(string Path, string? Name, SandboxFolderAccess? Access, int Priority, string? Error)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var normalized = entry;
            if (context is not null || entry.Name is null || entry.Path.Length > 0)
            {
                try { normalized.Path = NormalizePath(entry.Path); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                { normalized.Error ??= ex.Message; }
            }
            if (entry.Access is { } value && !Enum.IsDefined(value))
                normalized.Error = "A folder has an unsupported access value. Review its permissions.";
            var key = string.IsNullOrWhiteSpace(normalized.Path) && entry.Name is not null
                ? "known:" + entry.Name : normalized.Path;
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
            group.Add(normalized);
        }

        return groups.Values.Select(group =>
        {
            // The SDK's same-path RO rule also applies to its fixed Windows runtime entries.
            var runtime = context is not null && new[] { context.WindowsDirectory, context.SystemDirectory }
                .Any(path => SamePath(path, group[0].Path));
            var active = group.Where(e => e.Access is not null and not SandboxFolderAccess.Blocked).ToArray();
            var priority = active.Length == 0 ? -1 : active.Max(e => e.Priority);
            var selected = active.Where(e => e.Priority == priority).ToArray();
            var access = selected.Length == 0 ? (SandboxFolderAccess?)null :
                selected.Any(e => e.Access == SandboxFolderAccess.NoAccess) ? SandboxFolderAccess.NoAccess :
                selected.Any(e => e.Access == SandboxFolderAccess.ReadOnly) ? SandboxFolderAccess.ReadOnly : selected[0].Access;
            var error = active.Length > 0 ? active.Select(e => e.Error).FirstOrDefault(e => e is not null) :
                runtime ? null : group.Select(e => e.Error).FirstOrDefault(e => e is not null);
            if (runtime && access == SandboxFolderAccess.NoAccess)
                error = "Windows runtime access cannot be denied. Review Node Sandbox settings.";
            return new MxcFolderPermission(group[0].Path, runtime && access != SandboxFolderAccess.NoAccess ? SandboxFolderAccess.ReadOnly : access,
                group.Where(e => e.Name is not null).Select(e => e.Name!).Distinct().Order(StringComparer.Ordinal).ToArray(),
                group.Any(e => e.Priority == 0), group.Any(e => e.Priority > 0),
                group.Any(e => e.Priority == 2), group.Any(e => e.Access == SandboxFolderAccess.Blocked),
                runtime, error);
        }).ToArray();

        void AddKnown(string name, SandboxFolderAccess? access)
        {
            if (access is null) return;
            var folder = context?.UserFolders.FirstOrDefault(f => f.Name == name);
            entries.Add((folder?.Path ?? "", name, access, 1, folder?.Error));
        }
    }

    /// <summary>Edits only represented overrides in a detached snapshot. Null resets/removes an override, never denies a folder.</summary>
    public static SettingsData Edit(SettingsData settings, MxcFolderPermission row, SandboxFolderAccess? access)
    {
        if (access is not null and not SandboxFolderAccess.ReadOnly and not SandboxFolderAccess.ReadWrite and not SandboxFolderAccess.NoAccess)
            throw new ArgumentOutOfRangeException(nameof(access));
        if (row.IsRuntime && access is not null)
            throw new InvalidOperationException("Windows runtime access is read-only.");
        var result = MxcRequestBuilder.Snapshot(settings);
        var hasKnownField = false;
        foreach (var name in row.KnownFolders)
        {
            switch (name)
            {
                case "Documents": result.SandboxDocumentsAccess = access; hasKnownField = true; break;
                case "Desktop": result.SandboxDesktopAccess = access; hasKnownField = true; break;
                case "Downloads": result.SandboxDownloadsAccess = access; hasKnownField = true; break;
            }
        }
        var folders = (result.SandboxCustomFolders ?? []).Where(f => !SamePath(f.Path, row.Path)).ToList();
        if (access is { } value && (row.HasCustomGrant || !hasKnownField))
            folders.Add(new() { Path = NormalizePath(row.Path), Access = value });
        result.SandboxCustomFolders = folders;
        return result;
    }

    /// <summary>SDK path specificity, including user denies. Callers must separately veto immutable protected roots.</summary>
    public static SandboxFolderAccess? EffectiveAccess(string path, IEnumerable<string> readOnly,
        IEnumerable<string> readWrite, IEnumerable<string> userDenied)
    {
        path = NormalizePath(path);
        SandboxFolderAccess? result = null;
        var length = -1;
        var boundaries = readWrite.Select(p => (Path: p, Access: SandboxFolderAccess.ReadWrite))
            .Concat(readOnly.Select(p => (Path: p, Access: SandboxFolderAccess.ReadOnly)))
            .Concat(userDenied.Select(p => (Path: p, Access: SandboxFolderAccess.NoAccess)));
        // Iteration order gives equal paths the SDK's deny > RO > RW precedence.
        foreach (var (boundary, access) in boundaries)
        {
            var normalized = NormalizePath(boundary);
            var prefix = Path.EndsInDirectorySeparator(normalized) ? normalized : normalized + Path.DirectorySeparatorChar;
            if (normalized.Length >= length &&
                (path.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                length = normalized.Length;
                result = access;
            }
        }
        return result;
    }

    public static bool SamePath(string left, string right)
    {
        try { return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

    public static string NormalizePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new NotSupportedException("Folder grants must be fully qualified.");
        return MxcFolderAdmission.NormalizeIdentity(path);
    }
}
