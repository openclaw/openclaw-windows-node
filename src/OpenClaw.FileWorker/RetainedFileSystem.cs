using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared.FileTransfer;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.FileWorker;

[SupportedOSPlatform("windows")]
internal sealed class RetainedFileSystem : IDisposable
{
    private readonly FileWorkerAdmission _admission;
    private readonly List<NativeEntry> _ancestors = [];
    internal RetainedFileSystem(FileWorkerAdmission admission)
    {
        admission.Validate();
        _admission = admission;
    }
    internal bool Contained => _admission.ExecutionMode == "sandbox";

    internal static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_000 || path.Contains('\0') ||
            path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.Contains('/') || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new FileCommandException("INVALID_PATH", "Use an absolute local Windows drive path. Network and device namespaces are not supported.");
        foreach (var component in path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries)) ValidateName(component);
        return SandboxPathRules.Normalize(path);
    }

    internal static void ValidateName(string name)
    {
        var stem = name.Split('.')[0];
        if (name.Length is 0 or > 255 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) ||
            stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            ((stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem.Length == 4 && "123456789¹²³".Contains(stem[3])))
            throw new FileCommandException("INVALID_PATH", "Device names, alternate streams, traversal, and ambiguous Windows names are not supported.");
    }

    internal void Demand(string path, bool write = false)
    {
        if (Contained) _admission.DataAccess!.Demand(path, write);
    }

    internal NativeEntry Open(string rawPath, bool data = false, bool write = false)
    {
        var path = ValidatePath(rawPath);
        Demand(path, write);
        var parts = path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var parent = OpenTraversalStart(path, out var nextComponent, data, write);
        for (var i = nextComponent; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            parent = Retain(OpenChild(parent, parts[i], data: last && data, write: last && write, metadataOnly: !last));
        }
        return parent;
    }

    internal WriteTarget ResolveWrite(string rawPath, bool createParents)
    {
        var path = ValidatePath(rawPath);
        Demand(path, true);
        var parts = path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new FileCommandException("INVALID_PATH", "A file name is required.");
        var parent = OpenTraversalStart(path, out var nextComponent);
        if (nextComponent == parts.Length)
            throw new FileCommandException(parent.IsDirectory ? "IS_DIRECTORY" : "UNSUPPORTED_FILE_TYPE",
                parent.IsDirectory ? "The target is a directory." : "Sandbox writes directly under a drive root are not supported.");
        var missing = new List<string>();
        for (var i = nextComponent; i < parts.Length - 1; i++)
        {
            if (missing.Count > 0) { missing.Add(parts[i]); continue; }
            try
            {
                var next = OpenChild(parent, parts[i], metadataOnly: true);
                if (!next.IsDirectory) { next.Dispose(); throw new FileCommandException("PARENT_NOT_FOUND", "A parent component is not a directory."); }
                parent = Retain(next);
            }
            catch (FileCommandException ex) when (ex.Code == "NOT_FOUND")
            {
                if (!createParents) throw new FileCommandException("PARENT_NOT_FOUND", "The parent directory does not exist. Set createParents explicitly.");
                missing.Add(parts[i]);
            }
        }
        NativeEntry? existing = null;
        if (missing.Count == 0)
        {
            try { existing = Retain(OpenChild(parent, parts[^1])); }
            catch (FileCommandException ex) when (ex.Code == "NOT_FOUND") { }
        }
        return new(path, parent, parts[^1], missing, existing);
    }

    internal NativeEntry MaterializeParents(WriteTarget target)
    {
        var current = target.Anchor;
        foreach (var name in target.MissingParents)
        {
            var path = Path.Combine(current.Path, name);
            Demand(path, true);
            // FILE_CREATE, never OPEN_IF: a concurrent replacement cannot become our parent.
            current = Retain(OpenChild(current, name, write: true, create: true, directory: true));
        }
        return current;
    }

    private NativeEntry OpenTraversalStart(string path, out int nextComponent, bool data = false, bool write = false)
    {
        var start = path[..3];
        nextComponent = 0;
        if (Contained)
        {
            // MXC permits ancestor traversal metadata, not drive-root handles or ancestor ACL reads.
            // Pin the first non-root component; its final path and file ID reject drive aliases.
            var separator = path.IndexOf('\\', 3);
            start = separator < 0 ? path : path[..separator];
            nextComponent = 1;
        }
        var leaf = start.Equals(path, StringComparison.OrdinalIgnoreCase);
        var access = Native.ReadAttributes | Native.Synchronize | Native.Traverse;
        if (leaf) access |= Native.ReadControl;
        if (leaf && data) access |= Native.ReadData;
        if (leaf && write) access |= Native.WriteData;
        var handle = Native.CreateFileW(@"\\?\" + start, access,
            FileShare.ReadWrite, IntPtr.Zero, 3, Native.OpenReparsePoint | Native.BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            Native.ThrowError(error, "traversal anchor open");
        }
        try { return Retain(new NativeEntry(start, handle, Contained)); }
        catch { handle.Dispose(); throw; }
    }

    internal NativeEntry OpenChild(NativeEntry parent, string name, bool data = false, bool write = false,
        bool create = false, bool directory = false, bool metadataOnly = false, bool privateAcl = false,
        bool delete = false, bool exclusive = false, bool shareDelete = false, bool deleteOnClose = false, bool writeDac = false,
        bool ownership = false, byte[]? initialOwnership = null)
    {
        ValidateName(name);
        if (!parent.IsDirectory) throw new FileCommandException("IS_FILE", "The parent is not a directory.");
        var path = Path.Combine(parent.Path, name);
        if (!metadataOnly) Demand(path, write || create || delete || ownership || initialOwnership is not null);
        var access = Native.ReadAttributes | Native.Synchronize;
        if (!metadataOnly) access |= Native.ReadControl;
        if (metadataOnly || directory) access |= Native.Traverse;
        if (data) access |= Native.ReadData;
        if (write) access |= Native.WriteData;
        if (writeDac) access |= Native.WriteDac;
        if (ownership || initialOwnership is not null)
            access |= Native.ReadEa | Native.WriteEa | Native.WriteData | 0x4u | 0x100u;
        // Destination parents must not hold DELETE: MXC's rename target open does not share it.
        // Directory owners request deletion explicitly when they also own rollback or cleanup.
        if (delete || (create && !directory) || deleteOnClose) access |= Native.Delete;
        var handle = Native.OpenRelative(parent.Handle, name, access, create, directory, privateAcl, exclusive, shareDelete, deleteOnClose, initialOwnership);
        try
        {
            if (!shareDelete && (access & (Native.ReadData | Native.WriteData | Native.Delete | Native.Traverse)) == 0 &&
                ((FileAttributes)Native.Info<Native.TagInfo>(handle, 9).Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory)
            {
                // Windows ignores share-delete restrictions for metadata-only handles.
                // Reopen the same directory object with traverse rights before validating its path.
                var pinned = Native.OpenRelative(handle, "", access | Native.Traverse,
                    create: false, directory: true, privateAcl: false, exclusive: false);
                handle.Dispose();
                handle = pinned;
            }
            return new NativeEntry(path, handle, Contained);
        }
        catch { handle.Dispose(); throw; }
    }

    internal IEnumerable<string> Names(NativeEntry directory, int limit = FileLimits.TreeEntries) =>
        Enumerate(directory, limit).Select(entry => entry.Name);

    internal sealed record DirectoryEntry(string Name, FileAttributes Attributes, long Size, DateTimeOffset Modified)
    {
        internal bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);
        internal bool Unsupported => (Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline |
            (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0;
    }

    internal IEnumerable<DirectoryEntry> Enumerate(NativeEntry directory, int limit = FileLimits.TreeEntries)
    {
        Demand(directory.Path);
        // A separate relative handle supplies enumeration rights while the original identity stays pinned.
        using var enumeration = Native.OpenRelative(directory.Handle, "", Native.ReadData | Native.ReadAttributes | Native.Synchronize,
            create: false, directory: true, privateAcl: false, exclusive: false, shareDelete: true);
        var buffer = Marshal.AllocHGlobal(Native.DirectoryBufferBytes);
        try
        {
            var first = true;
            var count = 0;
            while (true)
            {
                if (!Native.GetInfo(enumeration, first ? 11 : 10, buffer, Native.DirectoryBufferBytes))
                {
                    if (Marshal.GetLastWin32Error() == 18) yield break;
                    Native.ThrowLastError("directory enumeration");
                }
                first = false;
                var offset = 0;
                while (true)
                {
                    var item = IntPtr.Add(buffer, offset);
                    var next = Marshal.ReadInt32(item);
                    var bytes = Marshal.ReadInt32(item, 60);
                    if (bytes < 0 || bytes % 2 != 0 || offset + 104L + bytes > Native.DirectoryBufferBytes)
                        throw new IOException("Invalid native directory record.");
                    var name = Marshal.PtrToStringUni(IntPtr.Add(item, 104), bytes / 2)!;
                    if (name is not ("." or ".."))
                    {
                        ValidateName(name);
                        if (++count > limit) throw new FileCommandException("TREE_TOO_LARGE", "The directory exceeds the bounded entry inventory.");
                        yield return new(name, (FileAttributes)Marshal.ReadInt32(item, 56),
                            Marshal.ReadInt64(item, 40), DateTimeOffset.FromFileTime(Marshal.ReadInt64(item, 24)));
                    }
                    if (next == 0) break;
                    if (next < 104 || offset + (long)next >= Native.DirectoryBufferBytes) throw new IOException("Invalid native directory offset.");
                    offset += next;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal NativeEntry CreateStage(NativeEntry parent) =>
        OpenChild(parent, ".openclaw-write-" + Guid.NewGuid().ToString("N"), data: true, write: true,
            create: true, privateAcl: true, exclusive: true, deleteOnClose: true, writeDac: true);

    private NativeEntry Retain(NativeEntry entry) { _ancestors.Add(entry); return entry; }
    public void Dispose()
    {
        for (var i = _ancestors.Count - 1; i >= 0; i--) _ancestors[i].Dispose();
        _ancestors.Clear();
    }
}

[SupportedOSPlatform("windows")]
internal sealed record WriteTarget(string Path, NativeEntry Anchor, string Name, List<string> MissingParents, NativeEntry? Existing)
{
    internal FileBinding Binding => new("write", AnchorPath: Anchor.Path, AnchorDevice: Anchor.Device, AnchorInode: Anchor.Inode,
        TargetDevice: Existing?.Device, TargetInode: Existing?.Inode);
    internal void Match(FileBinding? expected)
    {
        if (expected is not null && expected != Binding)
            throw new FileCommandException("CANONICAL_PATH_CHANGED", "The authorized parent or target identity changed.");
    }
}

[SupportedOSPlatform("windows")]
internal sealed class NativeEntry : IDisposable
{
    internal SafeFileHandle Handle { get; }
    internal string Path { get; private set; }
    internal string Device { get; }
    internal string Inode { get; }
    internal bool IsDirectory { get; }
    internal FileAttributes Attributes { get; }
    internal uint Links => Native.Info<Native.StandardInfo>(Handle, 1).Links;
    internal long Size => Native.Info<Native.StandardInfo>(Handle, 1).EndOfFile;
    internal DateTimeOffset Modified => DateTimeOffset.FromFileTime(Native.Info<Native.BasicInfo>(Handle, 0).LastWrite);
    internal FileBinding Binding => new("existing", Device, Inode);

    internal NativeEntry(string path, SafeFileHandle handle, bool contained)
    {
        Path = path;
        Handle = handle;
        var tag = Native.Info<Native.TagInfo>(handle, 9);
        Attributes = (FileAttributes)tag.Attributes;
        IsDirectory = Attributes.HasFlag(FileAttributes.Directory);
        if (Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new FileCommandException("SYMLINK_REDIRECT", "Reparse points, Cloud Files hydration and aliases are not proven by this native worker. Use an ordinary local path.");
        if ((Attributes & (FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0)
            throw new FileCommandException("UNSUPPORTED_FILE_TYPE", "Offline and recall-on-access files are not supported.");
        var actual = Native.FinalPath(handle);
        if (!SandboxPathRules.Normalize(actual).Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new FileCommandException("CANONICAL_PATH_CHANGED", "The handle resolves to a different physical path.");
        if (IsDirectory && (Native.Info<Native.CaseInfo>(handle, 23).Flags & 1) != 0)
            throw new FileCommandException("UNSUPPORTED_FILE_TYPE", "Case-sensitive directories are not supported.");
        var id = Native.Info<Native.IdInfo>(handle, 18);
        Device = id.Volume.ToString(CultureInfo.InvariantCulture);
        Inode = (((UInt128)id.High << 64) | id.Low).ToString(CultureInfo.InvariantCulture);
        if (id.Low == 0 && id.High == 0) throw new FileCommandException("UNSUPPORTED_FILE_TYPE", "The filesystem did not supply a stable file identity.");
        // Absolute traversal anchors and relative children share the same alias admission.
        if (contained && !IsDirectory && Links > 1)
            throw new FileCommandException("HARDLINK_TARGET_DENIED", "Contained file operations cannot prove access to all hard-link aliases.");
    }

    internal int Read(Span<byte> buffer, long offset) => RandomAccess.Read(Handle, buffer, offset);
    internal void Write(ReadOnlySpan<byte> bytes, long offset) => RandomAccess.Write(Handle, bytes, offset);
    internal void Finish(long length) { RandomAccess.SetLength(Handle, length); RandomAccess.FlushToDisk(Handle); }
    internal void Match(FileBinding? expected)
    {
        if (expected is not null && expected != Binding)
            throw new FileCommandException("CANONICAL_PATH_CHANGED", "The authorized file identity changed.");
    }

    internal void Commit(NativeEntry parent, string name, bool publishStage = false, bool inheritParentAcl = false)
    {
        RetainedFileSystem.ValidateName(name);
        var destination = Native.FinalPath(parent.Handle, nativeDevice: true).TrimEnd('\\') + "\\" + name;
        var encoded = Encoding.Unicode.GetBytes(destination);
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        var size = nameOffset + encoded.Length;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            // MXC cannot transport an embedded RootDirectory handle. Use the pinned parent's
            // native volume path, not a caller/DOS-drive spelling. Every ancestor remains pinned.
            // RootDirectory stays null and ReplaceIfExists stays false.
            Marshal.WriteInt32(buffer, lengthOffset, encoded.Length);
            Marshal.Copy(encoded, 0, IntPtr.Add(buffer, nameOffset), encoded.Length);
            Native.Rename(Handle, buffer, (uint)size);
            Path = System.IO.Path.Combine(parent.Path, name);
            if (inheritParentAcl) Native.InheritParentAcl(Handle);
            // Until the complete file has its final name, kernel handle closure must roll it back,
            // including termination that never executes managed finally blocks.
            if (publishStage)
                CancelKernelDeleteOnClose();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal void CancelKernelDeleteOnClose()
    {
        var flags = Marshal.AllocHGlobal(4);
        try
        {
            // FILE_DISPOSITION_ON_CLOSE without DELETE cancels FILE_DELETE_ON_CLOSE.
            Marshal.WriteInt32(flags, 8);
            if (!Native.SetInfo(Handle, 21, flags, 4)) Native.ThrowLastError("stage publication");
        }
        finally { Marshal.FreeHGlobal(flags); }
    }

    internal void Delete()
    {
        var flag = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(flag, 1);
            if (!Native.SetInfo(Handle, 4, flag, 1)) Native.ThrowLastError("retained-handle deletion");
        }
        finally { Marshal.FreeHGlobal(flag); }
    }

    public void Dispose() => Handle.Dispose();
}

[SupportedOSPlatform("windows")]
internal static class Native
{
    internal const uint ReadData = 1, WriteData = 2, ReadEa = 8, WriteEa = 16, Traverse = 0x20, ReadAttributes = 0x80, Delete = 0x10000, ReadControl = 0x20000, WriteDac = 0x40000, Synchronize = 0x100000;
    internal const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
    internal const int DirectoryBufferBytes = 64 * 1024;
    [StructLayout(LayoutKind.Sequential)] internal struct TagInfo { public uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)] internal struct IdInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] internal struct CaseInfo { public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct BasicInfo { public long Creation, Access, LastWrite, Change; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct StandardInfo { public long Allocation, EndOfFile; public uint Links; public byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Security, Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)] private struct Overlapped { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }

    internal static SafeFileHandle OpenRelative(SafeFileHandle root, string name, uint access, bool create,
        bool directory, bool privateAcl, bool exclusive, bool shareDelete = false, bool deleteOnClose = false, byte[]? initialOwnership = null)
    {
        var text = Marshal.StringToHGlobalUni(name);
        var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        IntPtr security = IntPtr.Zero;
        var ea = initialOwnership is null ? null : OwnershipEa(initialOwnership);
        var eaPointer = IntPtr.Zero;
        try
        {
            var unicode = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = text };
            Marshal.StructureToPtr(unicode, unicodePointer, false);
            if (privateAcl)
            {
                using var identity = WindowsIdentity.GetCurrent();
                var sid = identity.User?.Value ?? throw new IOException("A private file owner could not be resolved.");
                // A token's default owner can be a group rather than its user.
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW($"O:{sid}D:P(A;OICI;FA;;;{sid})(A;OICI;FA;;;SY)", 1, out security, out _))
                    ThrowLastError();
            }
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(), Root = root.DangerousGetHandle(),
                Name = unicodePointer, Attributes = 0x40, Security = security
            };
            // OPEN_REPARSE_POINT + SYNCHRONOUS_IO_NONALERT, with no share-delete.
            if (ea is not null)
            {
                eaPointer = Marshal.AllocHGlobal(ea.Length);
                Marshal.Copy(ea, 0, eaPointer, ea.Length);
            }
            var status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero,
                directory ? 0x10u : 0x80u, exclusive ? 0u : shareDelete ? 7u : 3u, create ? 2u : 1u,
                0x00200000u | 0x20u | (directory ? 1u : 0u) | (deleteOnClose ? 0x1000u : 0u),
                eaPointer, (uint)(ea?.Length ?? 0));
            if (status < 0)
            {
                handle?.Dispose();
                ThrowError((int)RtlNtStatusToDosError(status), create ? "relative create" :
                    (access & Delete) != 0 ? "relative cleanup open" : "relative open");
            }
            return handle!;
        }
        finally
        {
            if (security != IntPtr.Zero) LocalFree(security);
            if (eaPointer != IntPtr.Zero) Marshal.FreeHGlobal(eaPointer);
            Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(text);
        }
    }

    private const string OwnershipEaName = ManagedUploadRootLease.OwnershipEaName;
    private const int OwnershipEaBytes = ManagedUploadRootLease.OwnershipEaBytes;
    private static byte[] OwnershipEa(byte[] value)
    {
        if (value.Length > OwnershipEaBytes) throw new IOException("Upload ownership exceeds its native bound.");
        var name = Encoding.ASCII.GetBytes(OwnershipEaName);
        var bytes = new byte[8 + name.Length + 1 + value.Length];
        bytes[5] = (byte)name.Length;
        BitConverter.TryWriteBytes(bytes.AsSpan(6, 2), checked((ushort)value.Length));
        name.CopyTo(bytes, 8);
        value.CopyTo(bytes, 9 + name.Length);
        return bytes;
    }

    internal static byte[]? ReadOwnership(SafeFileHandle handle)
    {
        var name = Encoding.ASCII.GetBytes(OwnershipEaName);
        var query = new byte[5 + name.Length + 1];
        query[4] = (byte)name.Length;
        name.CopyTo(query, 5);
        var buffer = new byte[OwnershipEaBytes + 8 + name.Length + 1];
        var status = NtQueryEaFile(handle, out _, buffer, (uint)buffer.Length, true, query, (uint)query.Length, IntPtr.Zero, true);
        if (status is unchecked((int)0xC0000052) or unchecked((int)0xC0000051)) return null;
        if (status < 0) ThrowError((int)RtlNtStatusToDosError(status), "upload ownership read");
        var length = BitConverter.ToUInt16(buffer, 6);
        var offset = 9 + buffer[5];
        // A named query can return a successful zero-length value for an absent EA.
        // Native zero-length writes remove an EA, so it cannot be an ownership record.
        if (length == 0) return null;
        if (length > OwnershipEaBytes || offset + length > buffer.Length)
            throw new IOException("Invalid upload ownership attribute.");
        return buffer.AsSpan(offset, length).ToArray();
    }

    internal static void WriteOwnership(SafeFileHandle handle, byte[] value)
    {
        var bytes = OwnershipEa(value);
        var status = NtSetEaFile(handle, out _, bytes, (uint)bytes.Length);
        if (status < 0) ThrowError((int)RtlNtStatusToDosError(status), "upload ownership write");
        if (!FlushFileBuffers(handle)) ThrowLastError("upload ownership flush");
    }

    internal static T Info<T>(SafeFileHandle handle, int kind) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetInfo(handle, kind, buffer, size)) ThrowLastError($"handle information {kind}");
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static string FinalPath(SafeFileHandle handle, bool nativeDevice = false)
    {
        var buffer = new StringBuilder(32_768);
        var count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, nativeDevice ? 2u : 0u);
        if (count == 0) ThrowLastError();
        if (count >= buffer.Capacity) throw new IOException("The physical path exceeds the supported bound.");
        var path = buffer.ToString();
        if (nativeDevice && !path.StartsWith(@"\Device\", StringComparison.Ordinal))
            throw new FileCommandException("UNSUPPORTED_FILE_TYPE", "The retained directory has no stable native volume path.");
        return path;
    }

    internal static void RequirePrivate(SafeFileHandle handle) => ManagedUploadRootLease.RequirePrivate(handle);

    internal static void InheritParentAcl(SafeFileHandle handle)
    {
        // After the retained-handle rename, replace staging's protected ACL with an
        // empty, unprotected DACL. Windows adds the final parent's inheritable ACEs.
        // The non-null empty ACL never grants access by itself.
        var empty = new RawAcl(GenericAcl.AclRevision, 0);
        var bytes = new byte[empty.BinaryLength];
        empty.GetBinaryForm(bytes, 0);
        var acl = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, acl, bytes.Length);
            var error = SetSecurityInfo(handle, 1, 0x20000004, IntPtr.Zero, IntPtr.Zero, acl, IntPtr.Zero);
            if (error != 0) ThrowError((int)error, "destination ACL inheritance");
        }
        finally { Marshal.FreeHGlobal(acl); }
    }

    internal static void ThrowLastError(string operation = "file operation") => ThrowError(Marshal.GetLastWin32Error(), operation);
    internal static void Rename(SafeFileHandle file, IntPtr info, uint size)
    {
        var status = NtSetInformationFile(file, out _, info, size, 10);
        if (status < 0) ThrowError((int)RtlNtStatusToDosError(status), "retained-directory rename");
    }
    internal static void LockUploadRoot(SafeFileHandle file)
    {
        var overlapped = new Overlapped();
        if (!LockFileEx(file, 3, 0, 1, 0, ref overlapped)) ThrowLastError("upload inventory lock");
    }
    internal static void ThrowError(int error, string operation = "file operation") => throw new FileCommandException(error switch
    {
        2 or 3 => "NOT_FOUND", 5 => "PERMISSION_DENIED", 32 or 33 => "BUSY",
        80 or 183 => "EXISTS_NO_OVERWRITE", 4390 or 1920 => "SYMLINK_REDIRECT",
        _ => "IO_ERROR"
    }, $"Windows {operation} failed ({error}): {new Win32Exception(error).Message}");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatus status, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("ntdll.dll")] private static extern int NtQueryEaFile(SafeFileHandle file, out IoStatus status,
        [Out] byte[] buffer, uint length, [MarshalAs(UnmanagedType.U1)] bool singleEntry, byte[] list, uint listLength,
        IntPtr index, [MarshalAs(UnmanagedType.U1)] bool restart);
    [DllImport("ntdll.dll")] private static extern int NtSetEaFile(SafeFileHandle file, out IoStatus status, byte[] buffer, uint length);
    [DllImport("ntdll.dll")] private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatus status, IntPtr info, uint size, int kind);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetInfo(SafeFileHandle handle, int kind, IntPtr info, int size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInfo(SafeFileHandle handle, int kind, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LockFileEx(SafeFileHandle handle, uint flags, uint reserved, uint bytesLow, uint bytesHigh, ref Overlapped overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string descriptor, uint revision, out IntPtr security, out uint size);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(SafeFileHandle handle, int type, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
