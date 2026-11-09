using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.FileTransfer;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.FileWorker;

internal interface IWorkerMessages
{
    void Listen();
    byte[] Receive();
    void Send(ReadOnlySpan<byte> message);
}

[SupportedOSPlatform("windows")]
internal sealed class FileWorkerEngine
{
    internal FileOperationResult Execute(FileWorkerAdmission admission, FileCommandRequest request, IWorkerMessages? messages = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            admission.Validate();
            if (admission.ExpectedOperation is { } operation && operation != request.Operation)
                throw new FileCommandException("PERMISSION_DENIED", "The file command differs from its admitted operation.");
            if (request.FollowSymlinks)
                throw new FileCommandException("UNSUPPORTED_FILE_TYPE", "followSymlinks is not supported. Use an ordinary local Windows path.");
            request = ApplyTransferBudget(admission, request);
            using var fs = new RetainedFileSystem(admission);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Operation is FileOperation.Upload or FileOperation.ReapUploads)
                return new UploadStore(fs, admission, cancellationToken).Execute(request);
            if (request.Operation is FileOperation.Write or FileOperation.Create)
                return Write(fs, request, messages, cancellationToken);
            var path = request.Operation == FileOperation.ListDirectories && string.IsNullOrEmpty(request.Path) ? admission.Home : request.Path;
            using var target = fs.Open(path, data: request.Operation is FileOperation.Fetch or FileOperation.Archive);
            CheckCanonical(request, target.Path);
            target.Match(request.ExpectedBinding);
            if (request.RootPath is { } root)
            {
                using var rootEntry = fs.Open(root);
                if (!rootEntry.IsDirectory || !SandboxPathRules.Within(target.Path, rootEntry.Path))
                    throw new FileCommandException("PERMISSION_DENIED", "The target is outside rootPath.");
            }
            return request.Operation switch
            {
                FileOperation.Stat => Ok(new
                {
                    ok = true, path = target.Path, type = target.IsDirectory ? "directory" : "file",
                    size = target.Size, mtimeMs = target.Modified.ToUnixTimeMilliseconds(), binding = target.Binding,
                    preflightOnly = request.PreflightOnly ? true : (bool?)null
                }),
                FileOperation.Fetch => Fetch(target, request, messages, cancellationToken),
                FileOperation.List => List(fs, target, request, cancellationToken),
                FileOperation.ListDirectories => ListDirectories(fs, target, admission.Home, cancellationToken),
                FileOperation.Archive => Archive(fs, target, request, admission.TransferBytes, cancellationToken),
                _ => throw new FileCommandException("INVALID_PARAMS", "Unsupported file operation.")
            };
        }
        catch (FileCommandException ex) { return Failure(ex.Code, ex.Message, ex.InvalidRequest); }
        catch (UnauthorizedAccessException) { return Failure("PERMISSION_DENIED", "Windows denied this file operation."); }
        catch (IOException ex) { return Failure("IO_ERROR", ex.Message); }
        catch (NotSupportedException ex) { return Failure("UNSUPPORTED_FILE_TYPE", ex.Message); }
    }

    private static FileCommandRequest ApplyTransferBudget(FileWorkerAdmission admission, FileCommandRequest request)
    {
        var limit = admission.TransferBytes;
        if (request.Operation == FileOperation.Create && request.SizeBytes > limit ||
            request.ContentBase64 is { } content &&
            StrictFileBase64.Inspect(content, FileLimits.UnaryBytes, request.Operation == FileOperation.Upload) > limit)
            throw new FileCommandException("FILE_TOO_LARGE", "The transfer exceeds the committed Sandbox byte budget.");
        return request with { MaxBytes = Math.Min(request.MaxBytes, limit) };
    }

    private static FileOperationResult Fetch(NativeEntry file, FileCommandRequest request, IWorkerMessages? messages, CancellationToken stop)
    {
        if (file.IsDirectory) throw new FileCommandException("IS_DIRECTORY", "The fetch target is a directory.");
        if (file.Size > request.MaxBytes) throw new FileCommandException("FILE_TOO_LARGE", "The file exceeds maxBytes.");
        if (request.PreflightOnly)
            return Ok(new { ok = true, path = file.Path, size = file.Size, mimeType = "", base64 = "", sha256 = "", preflightOnly = true, binding = file.Binding });
        if (request.Binary && (messages is null || request.ExpectedBinding?.Kind != "existing"))
            throw new FileCommandException("INVALID_PARAMS", "Binary fetch requires framed IO and an authorized existing binding.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var unary = request.Binary ? null : new MemoryStream();
        if (request.Binary)
        {
            messages!.Listen();
            Expect(messages, "start");
        }
        var buffer = new byte[FileLimits.MessageBytes];
        long offset = 0;
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            var read = file.Read(buffer, offset);
            if (read == 0) break;
            offset += read;
            if (offset > request.MaxBytes) throw new FileCommandException("FILE_TOO_LARGE", "The file grew beyond maxBytes.");
            hash.AppendData(buffer.AsSpan(0, read));
            if (request.Binary)
            {
                messages!.Send(buffer.AsSpan(0, read));
                Expect(messages, "ack");
            }
            else unary!.Write(buffer, 0, read);
        }
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        var bytes = unary?.ToArray();
        return Ok(new
        {
            ok = true, path = file.Path, size = offset, mimeType = bytes is null ? "" : Mime(file.Path, bytes),
            base64 = bytes is null ? "" : Convert.ToBase64String(bytes), sha256 = digest,
            transport = request.Binary ? "binary" : null, binding = request.Binary ? null : file.Binding
        });
    }

    private static FileOperationResult List(RetainedFileSystem fs, NativeEntry directory, FileCommandRequest request, CancellationToken stop)
    {
        RequireDirectory(directory);
        if (request.PreflightOnly)
            return Ok(new { ok = true, path = directory.Path, entries = Array.Empty<object>(), truncated = false, preflight = true, binding = directory.Binding });
        var names = VisibleEntries(fs, directory, FileLimits.DirectoryInventoryEntries, stop)
            .OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
        var entries = new List<object>();
        var metadataBytes = Encoding.UTF8.GetByteCount(directory.Path) * 2L;
        foreach (var entry in names.Skip((int)Math.Min(request.Offset, names.Length)).Take(request.MaxEntries))
        {
            stop.ThrowIfCancellationRequested();
            var name = entry.Name;
            var path = Path.Combine(directory.Path, name);
            var item = new
            {
                name, path, size = entry.IsDirectory ? 0 : entry.Size,
                mimeType = entry.Unsupported ? "application/octet-stream" : entry.IsDirectory ? "inode/directory" : Mime(name, null),
                isDir = !entry.Unsupported && entry.IsDirectory, isFile = !entry.Unsupported && !entry.IsDirectory,
                mtime = entry.Modified.ToUnixTimeMilliseconds(), unsupported = entry.Unsupported ? true : (bool?)null
            };
            ReserveMetadata(item, ref metadataBytes);
            entries.Add(item);
        }
        var truncated = request.Offset + request.MaxEntries < names.Length;
        return Metadata(new
        {
            ok = true, path = directory.Path, entries,
            nextPageToken = truncated ? (request.Offset + request.MaxEntries).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            truncated, binding = directory.Binding
        });
    }

    private static FileOperationResult ListDirectories(RetainedFileSystem fs, NativeEntry directory, string home, CancellationToken stop)
    {
        RequireDirectory(directory);
        var entries = new List<object>();
        var metadataBytes = Encoding.UTF8.GetByteCount(directory.Path) * 2L + Encoding.UTF8.GetByteCount(home);
        foreach (var entry in VisibleEntries(fs, directory, FileLimits.TreeEntries, stop)
            .OrderBy(e => e.Name.StartsWith('.')).ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            stop.ThrowIfCancellationRequested();
            var name = entry.Name;
            var path = Path.Combine(directory.Path, name);
            if (entry.IsDirectory)
            {
                var item = new { name, path, hidden = name.StartsWith('.') ? true : (bool?)null,
                    unsupported = entry.Unsupported ? true : (bool?)null };
                ReserveMetadata(item, ref metadataBytes);
                entries.Add(item);
            }
        }
        var parent = Path.GetDirectoryName(directory.Path);
        return Metadata(new { path = directory.Path, parent, home, entries });
    }

    private static IEnumerable<RetainedFileSystem.DirectoryEntry> VisibleEntries(
        RetainedFileSystem fs, NativeEntry directory, int limit, CancellationToken stop)
    {
        foreach (var entry in fs.Enumerate(directory, limit))
        {
            stop.ThrowIfCancellationRequested();
            // A readable directory can contain protected children. Filter policy
            // denials before pagination without hiding IO or metadata failures.
            try { fs.Demand(Path.Combine(directory.Path, entry.Name)); }
            catch (FileCommandException ex) when (ex.Code == "PERMISSION_DENIED") { continue; }
            yield return entry;
        }
    }

    private static FileOperationResult Archive(RetainedFileSystem fs, NativeEntry directory, FileCommandRequest request,
        long transferBudget, CancellationToken stop)
    {
        RequireDirectory(directory);
        using var output = new BoundedMemoryStream(request.MaxBytes);
        var entries = new List<string>();
        long metadataBytes = 0;
        long expanded = 0;
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
            Walk(directory, "", 0);
            void Walk(NativeEntry parent, string prefix, int depth)
            {
                if (depth > FileLimits.TreeDepth) throw new FileCommandException("TREE_TOO_LARGE", "The archive exceeds its traversal depth bound.");
                foreach (var name in fs.Names(parent).Order(StringComparer.Ordinal))
                {
                    stop.ThrowIfCancellationRequested();
                    if (entries.Count == FileLimits.TreeEntries) throw new FileCommandException("TREE_TOO_LARGE", "The archive exceeds 5000 descendants.");
                    using var entry = fs.OpenChild(parent, name, data: true);
                    var relative = prefix + name;
                    if (Encoding.UTF8.GetByteCount(relative) > FileLimits.ArchivePathBytes)
                        throw new FileCommandException("TREE_TOO_LARGE", "An archive member exceeds the bounded PAX path representation.");
                    ReserveMetadata(relative, ref metadataBytes);
                    entries.Add(relative);
                    if (entry.IsDirectory)
                    {
                        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./" + relative + "/"));
                        Walk(entry, relative + "/", depth + 1);
                    }
                    else
                    {
                        var size = entry.Size;
                        if (size > FileLimits.UnaryBytes || expanded + size > Math.Min(FileLimits.ExpandedArchiveBytes, transferBudget))
                            throw new FileCommandException("TREE_TOO_LARGE", "The archive exceeds its entry/expanded byte bounds.");
                        expanded += size;
                        using var data = new NativeReadStream(entry, size, stop);
                        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./" + relative) { DataStream = data });
                        if (entry.Size != size) throw new FileCommandException("READ_ERROR", "An archive file changed size during the read.");
                    }
                }
            }
        }
        var bytes = output.ToArray();
        return Ok(new
        {
            ok = true, path = directory.Path, tarBase64 = request.PreflightOnly ? "" : Convert.ToBase64String(bytes),
            tarBytes = request.PreflightOnly ? 0 : bytes.Length, sha256 = request.PreflightOnly ? "" : StrictFileBase64.Sha256(bytes),
            fileCount = entries.Count, entries, binding = directory.Binding, preflightOnly = request.PreflightOnly ? true : (bool?)null
        });
    }

    private static FileOperationResult Write(RetainedFileSystem fs, FileCommandRequest request, IWorkerMessages? messages, CancellationToken stop)
    {
        var streaming = request.Operation == FileOperation.Create;
        var bytes = streaming ? null : StrictFileBase64.Decode(request.ContentBase64!);
        var digest = bytes is null ? request.ExpectedSha256! : StrictFileBase64.Sha256(bytes);
        if (request.ExpectedSha256 is not null && request.ExpectedSha256 != digest)
            throw new FileCommandException("INTEGRITY_FAILURE", "expectedSha256 does not match the submitted bytes.");
        var target = fs.ResolveWrite(request.Path, request.CreateParents);
        CheckCanonical(request, target.Path);
        target.Match(request.ExpectedBinding);
        if (target.Existing is { } exists)
        {
            if (exists.IsDirectory) throw new FileCommandException("IS_DIRECTORY", "The target is a directory.");
            if (!streaming && !request.Overwrite) throw new FileCommandException("EXISTS_NO_OVERWRITE", "The file exists. Set overwrite explicitly.");
            if (request.RejectHardlinks && exists.Links > 1) throw new FileCommandException("HARDLINK_TARGET_DENIED", "The target has multiple hard links.");
        }
        var size = streaming ? request.SizeBytes : bytes!.Length;
        if (request.PreflightOnly)
        {
            if (streaming) return Ok(new { ok = true, path = target.Path, size, sha256 = digest, binding = target.Binding });
            return Ok(new { ok = true, path = target.Path, size, sha256 = digest, overwritten = target.Existing is not null,
                binding = target.Binding, rejectHardlinks = request.RejectHardlinks ? true : (bool?)null });
        }
        if (streaming && (messages is null || request.ExpectedBinding?.Kind != "write"))
            throw new FileCommandException("CANONICAL_PATH_CHANGED", "file.create requires framed IO and its authorized write binding.");

        // Stage under the retained existing anchor. Parent creation and final writes follow hash verification.
        using var stage = fs.CreateStage(target.Anchor);
        {
            if (streaming) ReceiveCreate(stage, request, messages!, stop);
            else { stop.ThrowIfCancellationRequested(); stage.Write(bytes!, 0); stage.Finish(size); }
            stop.ThrowIfCancellationRequested();
            if (streaming && target.Existing is not null)
            {
                NativeEntry current;
                try { current = fs.OpenChild(target.Anchor, target.Name); }
                catch (FileCommandException ex) when (ex.Code == "NOT_FOUND")
                {
                    throw new FileCommandException("CANONICAL_PATH_CHANGED", "The authorized file no longer exists.");
                }
                using (current)
                {
                    current.Match(target.Existing.Binding);
                    if (current.IsDirectory) throw new FileCommandException("CANONICAL_PATH_CHANGED", "The authorized file became a directory.");
                    return Ok(new { ok = true, path = target.Path, status = "exists", binding = current.Binding });
                }
            }
            var parent = fs.MaterializeParents(target);
            if (target.Existing is not null)
            {
                using var file = fs.OpenChild(parent, target.Name, data: true, write: true);
                file.Match(target.Existing.Binding);
                if (request.RejectHardlinks && file.Links > 1) throw new FileCommandException("HARDLINK_TARGET_DENIED", "The target gained another hard link.");
                try
                {
                    file.Write(bytes!, 0);
                    file.Finish(size);
                }
                catch (Exception ex) when (ex is IOException or FileCommandException or UnauthorizedAccessException)
                {
                    throw new FileCommandException("WRITE_UNCERTAIN", "The in-place overwrite failed after it began. The original file identity remains, but its contents may be partial. Do not retry automatically.");
                }
                return Ok(new { ok = true, path = target.Path, size, sha256 = digest, overwritten = true, binding = file.Binding });
            }
            try {             stage.Commit(parent, target.Name, publishStage: true, inheritParentAcl: true); }
            catch (FileCommandException ex) when (ex.Code == "EXISTS_NO_OVERWRITE" && streaming)
            {
                using var raced = fs.OpenChild(parent, target.Name);
                if (raced.IsDirectory) throw new FileCommandException("NOT_FILE", "The target became a directory.");
                return Ok(new { ok = true, path = target.Path, status = "exists", binding = raced.Binding });
            }
            return streaming
                ? Ok(new { ok = true, path = target.Path, size, sha256 = digest, status = "created", binding = stage.Binding })
                : Ok(new { ok = true, path = target.Path, size, sha256 = digest, overwritten = false, binding = stage.Binding });
        }
    }

    private static void ReceiveCreate(NativeEntry stage, FileCommandRequest request, IWorkerMessages messages, CancellationToken stop)
    {
        messages.Listen();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = messages.Receive(); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                throw new FileCommandException("INVALID_PARAMS", "The incoming file.create stream contains a malformed message.", invalidRequest: true);
            }
            if (bytes.Length == 0) break;
            if (bytes.Length > FileLimits.MessageBytes || length + bytes.Length > request.SizeBytes || length + bytes.Length > request.MaxBytes)
                throw new FileCommandException("FILE_TOO_LARGE", "The incoming stream exceeds its declared size or message bound.", invalidRequest: true);
            stage.Write(bytes, length);
            length += bytes.Length;
            hash.AppendData(bytes);
            messages.Send("ack"u8);
        }
        if (length != request.SizeBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != request.ExpectedSha256)
            throw new FileCommandException("INTEGRITY_FAILURE", "The incoming stream does not match sizeBytes and expectedSha256.", invalidRequest: true);
        stage.Finish(length);
    }

    private static void Expect(IWorkerMessages messages, string value)
    {
        if (!messages.Receive().AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(value)))
            throw new FileCommandException("INVALID_PARAMS", $"Expected the {value} control message.");
    }
    private static void RequireDirectory(NativeEntry entry)
    {
        if (!entry.IsDirectory) throw new FileCommandException("IS_FILE", "The target is not a directory.");
    }
    private static void CheckCanonical(FileCommandRequest request, string path)
    {
        if (request.ExpectedCanonicalPath is { } expected &&
            !RetainedFileSystem.ValidatePath(expected).Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new FileCommandException("CANONICAL_PATH_CHANGED", "The authorized canonical path changed.");
    }
    internal static FileOperationResult Ok(object payload, UploadRetentionState? retention = null) =>
        new(FileJson.Element(payload), new(true), retention);
    internal static FileOperationResult Failure(string code, string message, bool invalidRequest = false) =>
        new(FileJson.Element(new { ok = false, code, message }), new(false, code, message), InvalidRequest: invalidRequest);
    private static FileOperationResult Metadata(object payload)
    {
        var result = Ok(payload);
        if (Encoding.UTF8.GetByteCount(result.Payload.GetRawText()) > FileLimits.MetadataBytes)
            throw new FileCommandException("RESULT_TOO_LARGE", "The directory metadata exceeds the encoded result budget.");
        return result;
    }
    private static void ReserveMetadata(object entry, ref long total)
    {
        total += JsonSerializer.SerializeToUtf8Bytes(entry, FileJson.Options).Length;
        if (total > FileLimits.MetadataBytes)
            throw new FileCommandException("RESULT_TOO_LARGE", "The directory metadata exceeds the encoded result budget.");
    }
    private static string Mime(string path, byte[]? bytes)
    {
        var known = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".csv" or ".log" => "text/plain", ".json" => "application/json",
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".pdf" => "application/pdf",
            ".html" => "text/html", ".gz" => "application/gzip", ".zip" => "application/zip", _ => null
        };
        if (known is not null) return known;
        if (bytes is null) return "application/octet-stream";
        var sample = bytes.AsSpan(0, Math.Min(bytes.Length, 8192));
        if (sample.Contains((byte)0)) return "application/octet-stream";
        try { _ = new UTF8Encoding(false, true).GetString(sample); }
        catch (DecoderFallbackException) { return "application/octet-stream"; }
        var controls = 0;
        foreach (var b in sample) if (b < 32 && b is not (9 or 10 or 13)) controls++;
        return controls * 100 < Math.Max(1, sample.Length) ? "text/plain" : "application/octet-stream";
    }
}

[SupportedOSPlatform("windows")]
internal sealed class NativeReadStream(NativeEntry file, long length, CancellationToken stop) : Stream
{
    private long _position;
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        stop.ThrowIfCancellationRequested();
        if (_position == length) return 0;
        var read = file.Read(buffer[..(int)Math.Min(buffer.Length, length - _position)], _position);
        if (read == 0) throw new IOException("An archive file shrank during the read.");
        _position += read;
        return read;
    }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class BoundedMemoryStream(long maximum) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Length + count > maximum) throw new FileCommandException("TREE_TOO_LARGE", "The compressed archive exceeds maxBytes.");
        base.Write(buffer, offset, count);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Length + buffer.Length > maximum) throw new FileCommandException("TREE_TOO_LARGE", "The compressed archive exceeds maxBytes.");
        var copy = buffer.ToArray();
        base.Write(copy, 0, copy.Length);
    }
}
