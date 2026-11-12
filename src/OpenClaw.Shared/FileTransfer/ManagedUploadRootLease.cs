using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.FileTransfer;

/// <summary>
/// Native root metadata ownership and read admission for automatic upload storage. Open retains
/// no-follow directory ancestry until disposal. The executor may bootstrap only its derived
/// root under committed policy admission; payload IO remains in UploadStore.
/// </summary>
internal sealed class ManagedUploadRootLease : IDisposable
{
    internal const string OwnershipEaName = "OPENCLAW.UPLOAD.V1";
    internal const int OwnershipEaBytes = 4096;
    private readonly List<SafeFileHandle> _pins = [];
    internal UploadRootIdentity? Identity { get; private set; }
    internal bool Complete { get; private set; }
    private string _path = "";
    private SafeFileHandle? _parent;
    private SafeFileHandle? _root;
    private UploadRootIdentity? _parentIdentity;

    /// <summary>Returns a pinned missing leaf or a verified owned root. Unknown existing roots fail closed.</summary>
    internal static ManagedUploadRootLease Open(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("Upload ownership requires Windows.");
        path = SandboxPathRules.Normalize(path);
        var volume = Path.GetPathRoot(path)!;
        if (volume.Length != 3 || volume[1] != ':' || path == Path.TrimEndingDirectorySeparator(volume))
            throw new NotSupportedException("Managed uploads require an ordinary local directory.");
        var lease = new ManagedUploadRootLease { _path = path };
        try
        {
            var chain = new Stack<string>();
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current)) chain.Push(current);
            UploadRootIdentity? parent = null;
            while (chain.TryPop(out var component))
            {
                var leaf = chain.Count == 0;
                if (leaf)
                {
                    lease._parentIdentity = parent;
                    lease._parent = lease._pins.Count == 0 ? null : lease._pins[^1];
                }
                // Traverse is needed: Windows ignores rename pins on metadata-only directory handles.
                var handle = CreateFileW(@"\\?\" + component, 0x1000A0u | (leaf ? 0x20008u : 0),
                    FileShare.ReadWrite, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (leaf && error is 2 or 3) return lease;
                    throw new IOException($"Managed upload ancestry could not be pinned (Windows error {error}).");
                }
                lease._pins.Add(handle);
                var identity = ValidateDirectory(handle, component);
                if (leaf)
                {
                    RequirePrivate(handle);
                    var record = ReadRoot(handle);
                    if (parent is null) throw Blocked("Managed upload storage has no parent identity.");
                    ValidateRecord(record, parent, identity, path);
                    lease._root = handle;
                    lease.Identity = identity;
                    lease.Complete = record.Device is not null && record.State == "retained";
                }
                parent = identity;
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>
    /// Establishes only the exact already-pinned root leaf. Call only for Upload,
    /// after durable reservation, in the policy store's synchronous commit prefix.
    /// Failures retain owned metadata; this owner never deletes a root or payload.
    /// </summary>
    internal void Bootstrap(UploadRootIdentity? expected, CancellationToken stop)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("Upload ownership requires Windows.");
        stop.ThrowIfCancellationRequested();
        if (_parent is null || _parentIdentity is null)
            throw Blocked("Managed upload storage has no pinned parent.");
        if (expected is not null && expected != Identity)
            throw Blocked("The remembered managed upload root is missing or replaced.");
        if (Identity is not null)
        {
            var existing = ReadRoot(_root!);
            ValidateRecord(existing, _parentIdentity, Identity, _path);
            if (existing.State == "retained" && existing.Device is not null) { Complete = true; return; }
            // A crash before self identity was durably bound is not proof that a
            // later object with the same parent/name is the original creation.
            if (existing.Device is null || existing.Inode is null)
                throw Blocked("An unbound managed upload creation requires manual review.");
        }
        var record = new ManagedUploadRootRecord(1, "root", _parentIdentity.Device,
            _parentIdentity.Inode, Path.GetFileName(_path));
        stop.ThrowIfCancellationRequested();
        foreach (var pin in _pins) RequireOrdinaryDirectory(pin);
        var handle = OpenMetadataRoot(create: Identity is null, record: record, stop: stop);
        _pins.Add(handle);
        var actual = ValidateDirectory(handle, _path);
        RequirePrivate(handle);
        if (Identity is not null && Identity != actual)
            throw Blocked("Managed upload root identity changed during metadata admission.");
        if (HasChildren(handle))
            throw Blocked("An incomplete managed upload root contains unknown children.");
        Identity = actual;
        _root = handle;
        // Finish the small ownership transaction after FILE_CREATE even if the
        // caller cancels concurrently. The empty owned root is safe to retain.
        record = record with { Device = actual.Device, Inode = actual.Inode };
        WriteRoot(handle, record);
        record = record with { State = "retained" };
        WriteRoot(handle, record);
        if (ReadRoot(handle) != record)
            throw Blocked("Managed upload root publication has no matching native receipt.");
        // Keep a read/traverse identity pin before shedding the metadata writer.
        // Its read-only sharing excluded concurrent child/EA writers during publication.
        var readPin = OpenMetadataRoot(create: false, record: record, stop: CancellationToken.None, readOnly: true);
        _pins.Add(readPin);
        if (ValidateDirectory(readPin, _path) != actual || ReadRoot(readPin) != record)
            throw Blocked("Managed upload root changed during publication handoff.");
        _root = readPin;
        _pins.Remove(handle);
        handle.Dispose();
        Complete = true;
        stop.ThrowIfCancellationRequested();
    }

    [SupportedOSPlatform("windows")]
    private SafeFileHandle OpenMetadataRoot(bool create, ManagedUploadRootRecord record, CancellationToken stop, bool readOnly = false)
    {
        var name = Marshal.StringToHGlobalUni(Path.GetFileName(_path));
        var unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var ea = create ? EncodeOwnership(record.Bytes()) : null;
        var eaPointer = IntPtr.Zero;
        var security = IntPtr.Zero;
        try
        {
            var bytes = checked((ushort)(Path.GetFileName(_path).Length * 2));
            Marshal.StructureToPtr(new UnicodeString { Length = bytes, MaximumLength = bytes, Buffer = name }, unicode, false);
            if (create)
            {
                using var user = WindowsIdentity.GetCurrent();
                var sid = user.User?.Value ?? throw Blocked("Managed upload owner is unavailable.");
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    $"O:{sid}D:P(A;OICI;FA;;;{sid})(A;OICI;FA;;;SY)", 1, out security, out _))
                    throw Blocked("Managed upload private ACL could not be constructed.");
                var initialEa = ea ?? throw Blocked("Managed root creation requires an atomic ownership EA.");
                eaPointer = Marshal.AllocHGlobal(initialEa.Length);
                Marshal.Copy(initialEa, 0, eaPointer, initialEa.Length);
            }
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(),
                Root = _parent!.DangerousGetHandle(), Name = unicode, Attributes = 0x40, Security = security };
            // Exact retained-parent child, FILE_CREATE for new roots. The short
            // metadata writer shares reads only; the retained read pin shares RW.
            // Neither handle requests DELETE or inherits into a child process.
            stop.ThrowIfCancellationRequested();
            var status = NtCreateFile(out var handle, readOnly ? 0x001200A8u : 0x001201BFu, ref attributes, out var io,
                IntPtr.Zero, 0x10, readOnly ? 3u : 1u, create ? 2u : 1u, 0x00200021, eaPointer, (uint)(ea?.Length ?? 0));
            GC.KeepAlive(_parent);
            if (status < 0 || create && io.Information.ToInt64() != 2)
            {
                handle?.Dispose();
                throw Blocked($"Managed upload root creation/open was refused (NTSTATUS 0x{status:X8}). Existing objects were not adopted.");
            }
            return handle!;
        }
        finally
        {
            if (security != IntPtr.Zero) LocalFree(security);
            if (eaPointer != IntPtr.Zero) Marshal.FreeHGlobal(eaPointer);
            Marshal.FreeHGlobal(unicode);
            Marshal.FreeHGlobal(name);
        }
    }

    private static bool HasChildren(SafeFileHandle root)
    {
        const int size = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var first = true;
            while (true)
            {
                if (!GetInfo(root, first ? 11 : 10, buffer, size))
                {
                    if (Marshal.GetLastWin32Error() == 18) return false;
                    throw Blocked("Incomplete managed root inventory could not be verified.");
                }
                first = false;
                var offset = 0;
                while (true)
                {
                    var entry = IntPtr.Add(buffer, offset);
                    var next = Marshal.ReadInt32(entry);
                    var length = Marshal.ReadInt32(entry, 60);
                    if (length < 0 || length % 2 != 0 || offset + 104L + length > size)
                        throw Blocked("Incomplete managed root inventory is invalid.");
                    var name = Marshal.PtrToStringUni(IntPtr.Add(entry, 104), length / 2);
                    if (name is not ("." or "..")) return true;
                    if (next == 0) break;
                    if (next < 104 || offset + (long)next >= size) throw Blocked("Invalid managed root inventory offset.");
                    offset += next;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static byte[] EncodeOwnership(byte[] value)
    {
        if (value.Length > OwnershipEaBytes) throw Blocked("Managed root ownership exceeds its bound.");
        var name = Encoding.ASCII.GetBytes(OwnershipEaName);
        var buffer = new byte[9 + name.Length + value.Length];
        buffer[5] = (byte)name.Length;
        BitConverter.TryWriteBytes(buffer.AsSpan(6, 2), checked((ushort)value.Length));
        name.CopyTo(buffer, 8);
        value.CopyTo(buffer, 9 + name.Length);
        return buffer;
    }

    private static void WriteRoot(SafeFileHandle root, ManagedUploadRootRecord record)
    {
        var bytes = EncodeOwnership(record.Bytes());
        if (NtSetEaFile(root, out _, bytes, (uint)bytes.Length) < 0 || !FlushFileBuffers(root))
            throw Blocked("Managed root ownership could not be durably recorded. The owned metadata was retained.");
    }

    private static void ValidateRecord(ManagedUploadRootRecord record, UploadRootIdentity parent,
        UploadRootIdentity entry, string path)
    {
        if (record.Version != 1 || record.Kind != "root" || record.ParentDevice != parent.Device ||
            record.ParentInode != parent.Inode || record.Name != Path.GetFileName(path) ||
            (record.Device is null) != (record.Inode is null) ||
            (record.Device is not null && (record.Device != entry.Device || record.Inode != entry.Inode)) ||
            (record.Device is null && record.State != "creating") || record.State is not ("creating" or "retained"))
            throw Blocked("Managed upload ownership no longer matches its native object.");
    }

    private static void RequireOrdinaryDirectory(SafeFileHandle handle)
    {
        var tag = Info<TagInfo>(handle, 9);
        const uint unsupported = 0x400 | 0x1000 | 0x40000 | 0x400000;
        if ((tag.Attributes & 0x10) == 0 || (tag.Attributes & unsupported) != 0 ||
            (Info<CaseInfo>(handle, 23).Flags & 1) != 0)
            throw Blocked("Managed upload ancestry must contain ordinary non-reparse directories.");
    }

    private static UploadRootIdentity ValidateDirectory(SafeFileHandle handle, string path)
    {
        RequireOrdinaryDirectory(handle);
        var final = new StringBuilder(32_768);
        var length = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity ||
            !SandboxPathRules.Normalize(final.ToString()).Equals(SandboxPathRules.Normalize(path), StringComparison.OrdinalIgnoreCase))
            throw Blocked("Managed upload ancestry resolves to a different physical path.");
        var id = Info<IdInfo>(handle, 18);
        if (id.Low == 0 && id.High == 0) throw Blocked("Managed upload storage has no stable native identity.");
        return new(SandboxPathRules.Normalize(path), id.Volume.ToString(CultureInfo.InvariantCulture),
            (((UInt128)id.High << 64) | id.Low).ToString(CultureInfo.InvariantCulture));
    }

    private static ManagedUploadRootRecord ReadRoot(SafeFileHandle handle)
    {
        var name = Encoding.ASCII.GetBytes(OwnershipEaName);
        var query = new byte[6 + name.Length];
        query[4] = (byte)name.Length;
        name.CopyTo(query, 5);
        var buffer = new byte[OwnershipEaBytes + 9 + name.Length];
        var status = NtQueryEaFile(handle, out _, buffer, (uint)buffer.Length, true, query, (uint)query.Length, IntPtr.Zero, true);
        if (status < 0) throw Blocked("Managed upload ownership could not be read.");
        var length = BitConverter.ToUInt16(buffer, 6);
        var offset = 9 + buffer[5];
        if (length == 0 || length > OwnershipEaBytes || offset + length > buffer.Length ||
            buffer[5] != name.Length || !buffer.AsSpan(8, name.Length).SequenceEqual(name))
            throw Blocked("Managed upload storage has no valid ownership attribute.");
        try { return JsonSerializer.Deserialize<ManagedUploadRootRecord>(buffer.AsSpan(offset, length), FileJson.Options) ?? throw new JsonException(); }
        catch (JsonException) { throw Blocked("Managed upload ownership metadata is invalid."); }
    }

    /// <summary>Shared owner-only ACL check used by the root lease and the fixed worker.</summary>
    [SupportedOSPlatform("windows")]
    internal static void RequirePrivate(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("Upload ownership requires Windows.");
        if (GetSecurityInfo(handle, 1, 5, out var owner, out _, out _, out _, out var descriptor) != 0)
            throw new FileCommandException("PERMISSION_DENIED", "Upload storage security could not be read.");
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw new IOException("Missing Windows owner identity.");
            var length = GetSecurityDescriptorLength(descriptor);
            if (new SecurityIdentifier(owner) != sid || length > FileLimits.MetadataBytes)
                throw new FileCommandException("PERMISSION_DENIED", "Upload storage has a different owner or invalid security descriptor.");
            var bytes = new byte[length];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            if (!security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) ||
                security.DiscretionaryAcl is null || security.DiscretionaryAcl.Count == 0 ||
                security.DiscretionaryAcl.Cast<GenericAce>().Any(ace => ace is not CommonAce common ||
                    common.AceQualifier != AceQualifier.AccessAllowed ||
                    (common.SecurityIdentifier != sid && !common.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid))))
                throw new FileCommandException("PERMISSION_DENIED", "Upload storage must have a private owner-only access list.");
        }
        finally { LocalFree(descriptor); }
    }

    private static T Info<T>(SafeFileHandle handle, int kind) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetInfo(handle, kind, buffer, size)) throw Blocked("Upload storage native metadata could not be read.");
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static FileCommandException Blocked(string message) => new("UPLOAD_CLEANUP_BLOCKED", message);
    public void Dispose() { for (var i = _pins.Count - 1; i >= 0; i--) _pins[i].Dispose(); _pins.Clear(); }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Security, Quality; }
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle handle, uint access,
        ref ObjectAttributes attributes, out IoStatus io, IntPtr allocation, uint fileAttributes, uint share,
        uint disposition, uint options, IntPtr ea, uint eaBytes);
    [DllImport("ntdll.dll")] private static extern int NtSetEaFile(SafeFileHandle handle, out IoStatus io, byte[] bytes, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string value, uint version, out IntPtr security, out uint size);
    [StructLayout(LayoutKind.Sequential)] private struct TagInfo { public uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct IdInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct CaseInfo { public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public IntPtr Status, Information; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetInfo(SafeFileHandle handle, int kind, IntPtr info, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("ntdll.dll")] private static extern int NtQueryEaFile(SafeFileHandle file, out IoStatus status,
        [Out] byte[] buffer, uint length, [MarshalAs(UnmanagedType.U1)] bool singleEntry, byte[] list, uint listLength,
        IntPtr index, [MarshalAs(UnmanagedType.U1)] bool restart);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
