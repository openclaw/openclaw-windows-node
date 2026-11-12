using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.FileTransfer;

namespace OpenClaw.FileWorker;

[SupportedOSPlatform("windows")]
internal sealed class UploadStore(RetainedFileSystem fs, FileWorkerAdmission admission, CancellationToken stop)
{
    private const string RootMarker = ".openclaw-root.json";
    private const string LockName = ".openclaw-lock";
    private const string ManifestName = ".openclaw-upload.json";
    private const string Prefix = "openclaw-terminal-upload-";
    private sealed record Manifest(string DirectoryDevice, string DirectoryInode, string FileName,
        string FileDevice, string FileInode, DateTimeOffset Expires);

    internal FileOperationResult Execute(FileCommandRequest request)
    {
        var rootPath = RetainedFileSystem.ValidatePath(admission.UploadRoot);
        fs.Demand(rootPath, true);
        using var root = OpenRoot(rootPath, request.Operation == FileOperation.Upload);
        Native.RequirePrivate(root.Handle);
        var identity = new UploadRootIdentity(root.Path, root.Device, root.Inode);
        if (admission.ExpectedUploadRoot is { } expected && expected != identity)
            throw new FileCommandException("CANONICAL_PATH_CHANGED", "The remembered upload root has been replaced. Cleanup is blocked.");
        using var gate = AcquireLock(root);
        var now = DateTimeOffset.UtcNow;
        var retained = Scan(root, now);
        var bytes = request.Operation == FileOperation.Upload ? StrictFileBase64.Decode(request.ContentBase64!, standardPadded: true) : null;
        if (bytes is null) return FileWorkerEngine.Ok(new { reaped = true }, State(identity, retained));
        if (retained.Count >= FileLimits.RetainedDirectories || retained.Sum(x => x.Size) + bytes.LongLength > FileLimits.RetainedBytes)
            throw new FileCommandException("UPLOAD_QUOTA", "Upload storage is full (64 directories or 256 MiB). Wait for expiry or restore cleanup access.");
        var safeName = SanitizeName(request.Name!);
        stop.ThrowIfCancellationRequested();
        var name = Prefix + Guid.NewGuid().ToString("N");
        var creating = UploadOwnership.Creating(root, name, "upload", safeName);
        using var directory = fs.OpenChild(root, name, write: true, create: true, directory: true, privateAcl: true,
            ownership: true, initialOwnership: creating.Bytes());
        var record = creating.Bind(directory);
        record.Save(directory);
        // The payload stays kernel-owned until its exact identity is durable in
        // the directory EA. An interrupted create with no payload is recoverable.
        using var file = fs.OpenChild(directory, safeName, write: true, create: true, privateAcl: true, deleteOnClose: true);
        var expires = now + FileLimits.Retention;
        record = record with { FileDevice = file.Device, FileInode = file.Inode, Expires = expires };
        record.Save(directory);
        file.CancelKernelDeleteOnClose();
        stop.ThrowIfCancellationRequested();
        file.Write(bytes, 0);
        file.Finish(bytes.Length);
        (record with { State = "retained" }).Save(directory);
        retained.Add((bytes.Length, expires));
        return FileWorkerEngine.Ok(new { path = file.Path, size = bytes.Length }, State(identity, retained));
    }

    private NativeEntry OpenRoot(string path, bool create)
    {
        var target = fs.ResolveWrite(path, createParents: false);
        NativeEntry root;
        if (target.Existing is not null)
        {
            root = fs.OpenChild(target.Anchor, target.Name, ownership: true);
            if (!root.IsDirectory) { root.Dispose(); throw new FileCommandException("PERMISSION_DENIED", "The upload root is not a directory."); }
            try
            {
                Native.RequirePrivate(root.Handle);
                var record = UploadOwnership.Read(root);
                if (record is null)
                {
                    // Complete pre-EA inventories retain their original identity proof.
                    using var marker = fs.OpenChild(root, RootMarker, data: true);
                    Native.RequirePrivate(marker.Handle);
                    var recorded = ReadJson<UploadRootIdentity>(marker);
                    if (recorded != new UploadRootIdentity(root.Path, root.Device, root.Inode))
                        throw new FileCommandException("CANONICAL_PATH_CHANGED", "Upload root ownership does not match its retained identity.");
                }
                else
                {
                    record.Match(target.Anchor, root, "root");
                    if (record.Device is null)
                    {
                        if (admission.ExpectedUploadRoot is not null || fs.Names(root, 1).Any())
                            throw UploadOwnership.Blocked("An interrupted root contains unowned entries.");
                        (record.Bind(root) with { State = "retained" }).Save(root);
                    }
                }
                return root;
            }
            catch { root.Dispose(); throw; }
        }
        if (!create || admission.ExpectedUploadRoot is not null)
            throw new FileCommandException("NOT_FOUND", "The remembered upload root is unavailable. No replacement was created.");
        var initial = UploadOwnership.Creating(target.Anchor, target.Name, "root");
        root = fs.OpenChild(target.Anchor, target.Name, write: true, create: true, directory: true, privateAcl: true,
            ownership: true, initialOwnership: initial.Bytes());
        try
        {
            (initial.Bind(root) with { State = "retained" }).Save(root);
            return root;
        }
        catch { root.Dispose(); throw; }
    }

    private NativeEntry AcquireLock(NativeEntry root)
    {
        NativeEntry handle;
        try { handle = fs.OpenChild(root, LockName, write: true, create: true, privateAcl: true); }
        catch (FileCommandException ex) when (ex.Code == "EXISTS_NO_OVERWRITE")
        {
            handle = fs.OpenChild(root, LockName, write: true);
        }
        try
        {
            Native.RequirePrivate(handle.Handle);
            Native.LockUploadRoot(handle.Handle);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private List<(long Size, DateTimeOffset Expiry)> Scan(NativeEntry root, DateTimeOffset now)
    {
        var retained = new List<(long, DateTimeOffset)>();
        foreach (var name in fs.Names(root, FileLimits.RetainedDirectories + 2))
        {
            stop.ThrowIfCancellationRequested();
            if (name == LockName || (name == RootMarker && UploadOwnership.Read(root) is null)) continue;
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                throw new FileCommandException("UPLOAD_CLEANUP_BLOCKED", "Upload storage contains an unrecognized entry. Manual review is required.");
            using var directory = fs.OpenChild(root, name, shareDelete: true, ownership: true);
            if (!directory.IsDirectory) throw new FileCommandException("UPLOAD_CLEANUP_BLOCKED", "An upload directory was replaced.");
            Native.RequirePrivate(directory.Handle);
            var record = UploadOwnership.Read(directory) ?? MigrateLegacy(root, directory);
            record.Match(root, directory, "upload");
            if (record.FileName is null) throw UploadOwnership.Blocked("An upload ownership record has no payload name.");
            RetainedFileSystem.ValidateName(record.FileName);
            if (record.FileName.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
                (record.FileDevice is null) != (record.FileInode is null) ||
                (record.State == "retained" && (record.Expires is null || record.FileDevice is null)))
                throw UploadOwnership.Blocked("An upload ownership record is incomplete.");
            var names = fs.Names(directory, 2).ToArray();
            if (names.Any(n => n != record.FileName && (n != ManifestName || record.LegacyMarkerDevice is null)))
                throw UploadOwnership.Blocked("Upload storage contains unowned files.");
            using var file = OpenOwned(directory, record.FileName, record.FileDevice, record.FileInode,
                missingAllowed: record.State != "retained");
            using var marker = record.LegacyMarkerDevice is null ? null :
                OpenOwned(directory, ManifestName, record.LegacyMarkerDevice, record.LegacyMarkerInode,
                    missingAllowed: record.State == "deleting");
            if (record.Device is null && names.Length != 0)
                throw UploadOwnership.Blocked("An interrupted directory contains unbound objects.");
            if (record.State != "retained" || record.Expires <= now)
            {
                // Persist deletion before the first unlink. Missing children are then
                // expected after termination, but replacements never confer ownership.
                (record.Bind(directory) with { State = "deleting" }).Save(directory);
                using var deleteDirectory = fs.OpenChild(root, name, delete: true);
                deleteDirectory.Match(directory.Binding);
                Native.RequirePrivate(deleteDirectory.Handle);
                DeleteOwned(deleteDirectory, file);
                DeleteOwned(deleteDirectory, marker);
                if (fs.Names(deleteDirectory, 1).Any())
                    throw UploadOwnership.Blocked("An upload acquired unowned entries during cleanup.");
                deleteDirectory.Delete();
            }
            else retained.Add((file!.Size, record.Expires!.Value));
        }
        return retained;
    }

    private NativeEntry? OpenOwned(NativeEntry directory, string name, string? device, string? inode, bool missingAllowed)
    {
        NativeEntry entry;
        try { entry = fs.OpenChild(directory, name, shareDelete: true); }
        catch (FileCommandException ex) when (ex.Code == "NOT_FOUND" && missingAllowed) { return null; }
        try
        {
            Native.RequirePrivate(entry.Handle);
            if (device is null || inode is null || entry.IsDirectory || entry.Links != 1 ||
                entry.Device != device || entry.Inode != inode)
                throw UploadOwnership.Blocked("A retained upload was replaced or linked.");
            return entry;
        }
        catch { entry.Dispose(); throw; }
    }

    private void DeleteOwned(NativeEntry directory, NativeEntry? entry)
    {
        if (entry is null) return;
        using (var deletion = fs.OpenChild(directory, Path.GetFileName(entry.Path), delete: true))
        {
            deletion.Match(entry.Binding);
            Native.RequirePrivate(deletion.Handle);
            if (deletion.Links != 1) throw UploadOwnership.Blocked("An expired upload gained a hard link.");
            deletion.Delete();
            entry.Dispose();
        }
    }

    private UploadOwnership MigrateLegacy(NativeEntry root, NativeEntry directory)
    {
        using var marker = fs.OpenChild(directory, ManifestName, data: true, shareDelete: true);
        Native.RequirePrivate(marker.Handle);
        var old = ReadJson<Manifest>(marker);
        RetainedFileSystem.ValidateName(old.FileName);
        if (old.DirectoryDevice != directory.Device || old.DirectoryInode != directory.Inode)
            throw UploadOwnership.Blocked("A legacy upload directory was replaced.");
        if (old.FileName.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
            fs.Names(directory, 2).Count() != 2)
            throw UploadOwnership.Blocked("A legacy upload contains unowned entries.");
        using var file = OpenOwned(directory, old.FileName, old.FileDevice, old.FileInode, missingAllowed: false);
        var record = UploadOwnership.Creating(root, Path.GetFileName(directory.Path), "upload", old.FileName).Bind(directory) with
        {
            FileDevice = old.FileDevice, FileInode = old.FileInode, Expires = old.Expires, State = "retained",
            LegacyMarkerDevice = marker.Device, LegacyMarkerInode = marker.Inode
        };
        // Validation of both entries occurs before any deletion. The atomic EA
        // retains the marker identity if later cleanup is interrupted.
        record.Save(directory);
        return record;
    }

    private static UploadRetentionState State(UploadRootIdentity root, List<(long Size, DateTimeOffset Expiry)> files) =>
        new(root, files.Count, files.Sum(f => f.Size), files.Count == 0 ? null : files.Min(f => f.Expiry));

    internal static string SanitizeName(string value)
    {
        var name = value.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        var builder = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            var text = rune.ToString();
            if (rune.Value < 32 || "<>:\"/\\|?*!%".Contains(text, StringComparison.Ordinal)) text = "_";
            if (Encoding.UTF8.GetByteCount(builder.ToString()) + Encoding.UTF8.GetByteCount(text) > 180) break;
            builder.Append(text);
        }
        name = builder.ToString().Trim().TrimEnd('.');
        if (name.Length == 0 || name is "." or "..") name = "upload";
        try { RetainedFileSystem.ValidateName(name); }
        catch (FileCommandException) { name = "_" + name; RetainedFileSystem.ValidateName(name); }
        if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) name = "upload-" + name;
        return name;
    }

    private static T ReadJson<T>(NativeEntry file)
    {
        if (file.Size < 0 || file.Size > FileLimits.MetadataBytes) throw new FileCommandException("UPLOAD_CLEANUP_BLOCKED", "Upload ownership metadata exceeds its bound.");
        var bytes = new byte[(int)file.Size];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = file.Read(bytes.AsSpan(offset), offset);
            if (count == 0) throw new IOException("Upload ownership metadata was truncated.");
            offset += count;
        }
        try { return JsonSerializer.Deserialize<T>(bytes, FileJson.Options) ?? throw new JsonException(); }
        catch (JsonException) { throw new FileCommandException("UPLOAD_CLEANUP_BLOCKED", "Upload ownership metadata is invalid."); }
    }
}
