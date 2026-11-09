using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// In-process ownership, not a startup sweeper. Only an atomically created directory
/// whose native identity and no-follow ancestry are retained can enter recovery.
/// </summary>
internal sealed class MxcOperationCleanup(IOpenClawLogger logger) : IDisposable
{
    private const int MaximumOutstanding = 16;
    private const int ImmediateRetries = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly object _gate = new();
    private readonly List<MxcOwnedOperation> _pending = [];
    private int _outstanding;
    private bool _disposed;

    internal void Reserve()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RecoverLocked();
            if (_outstanding >= MaximumOutstanding)
                throw NodeSandboxExecutor.Failure(
                    "Command cleanup is blocked: owned runtime folders could not be reclaimed. Release open handles and retry. " +
                    "An uncertain process launch requires diagnosis before recovery.",
                    NodeToolErrorCategory.NodeBusy, null);
            _outstanding++;
        }
    }

    internal void ReleaseUncreated()
    {
        lock (_gate) _outstanding--;
    }

    internal async Task CompleteAsync(MxcOwnedOperation operation, bool settled)
    {
        operation.ProcessSettled = settled;
        if (settled)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    operation.Delete();
                    operation.Dispose();
                    ReleaseUncreated();
                    return;
                }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32 && attempt < ImmediateRetries)
                {
                    if (attempt == 0) logger.Info("[mxc] operation=scratch-cleanup-retry reason=sharing_violation");
                    await Task.Delay(RetryDelay).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    logger.Warn($"[mxc] operation=scratch-cleanup-deferred error={ex.GetType().Name}");
                    break;
                }
            }
        }
        else
        {
            // No process handle was returned, or finalization failed. Keep the payload
            // pins and capacity debit until owner shutdown, never infer safe deletion.
            logger.Warn("[mxc] operation=scratch-cleanup-deferred reason=unsettled_launch");
        }
        lock (_gate)
        {
            if (_disposed) operation.Dispose();
            else _pending.Add(operation);
        }
    }

    // Admission retries only our own retained leases. No directory enumeration confers ownership,
    // and no pending operation occupies the executor's eight process/pipe slots.
    private void RecoverLocked()
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var operation = _pending[i];
            if (!operation.ProcessSettled) continue;
            try
            {
                operation.Delete();
                operation.Dispose();
                _pending.RemoveAt(i);
                _outstanding--;
                logger.Info("[mxc] operation=scratch-cleanup-recovered");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var operation in _pending) operation.Dispose();
            _pending.Clear();
        }
    }
}

internal sealed class MxcOwnedOperation : IDisposable
{
    private const uint ReadAttributes = 0x80, Traverse = 0x20, Synchronize = 0x100000, DeleteAccess = 0x10000;
    private const uint OpenReparsePoint = 0x00200000, Synchronous = 0x20, DirectoryOnly = 1;
    private const int MaximumCleanupEntries = 100_000, MaximumCleanupDepth = 128;
    private readonly List<SafeFileHandle> _ancestors = [];
    private SafeFileHandle? _operation;
    private IDisposable? _payload;
    private SafeFileHandle Parent => _ancestors[^1];
    private FileId _identity;
    internal string Path { get; private set; } = "";
    internal string Scratch => System.IO.Path.Combine(Path, "scratch");
    internal bool ProcessSettled { get; set; }
    internal void RetainPayload(IDisposable? payload) => _payload = payload;

    internal static MxcOwnedOperation Create(string root)
    {
        var owned = new MxcOwnedOperation();
        try
        {
            root = MxcRequestBuilder.ValidateExecutable(root);
            var volume = System.IO.Path.GetPathRoot(root)!;
            if (volume.Length != 3 || volume[1] != ':')
                throw new NotSupportedException("Executor runtime storage requires an ordinary local drive path.");
            var handle = CreateFileW(NativePath(volume), ReadAttributes | Traverse,
                FileShare.ReadWrite, IntPtr.Zero, 3, OpenReparsePoint | 0x02000000, IntPtr.Zero);
            CheckHandle(handle);
            owned._ancestors.Add(handle);
            Validate(handle, volume, directory: true);
            var current = volume;
            foreach (var name in root[volume.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = System.IO.Path.Combine(current, name);
                handle = OpenRelative(owned._ancestors[^1], name, ReadAttributes | Traverse, disposition: 3, directory: true);
                owned._ancestors.Add(handle);
                Validate(handle, current, directory: true);
            }
            var operationName = Guid.NewGuid().ToString("N");
            owned.Path = System.IO.Path.Combine(root, operationName);
            owned._operation = OpenRelative(owned.Parent, operationName, ReadAttributes | Traverse, disposition: 2, directory: true);
            Validate(owned._operation, owned.Path, directory: true);
            owned._identity = Identity(owned._operation);
            return owned;
        }
        catch
        {
            // Creation may have materialized a partial operation, but never adopt a colliding name.
            if (owned._operation is not null)
            {
                try { owned.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
            }
            owned.Dispose();
            throw;
        }
    }

    internal void CreateScratch()
    {
        using var scratch = OpenRelative(_operation!, "scratch", ReadAttributes | Traverse, disposition: 2, directory: true);
        Validate(scratch, Scratch, directory: true);
    }

    internal void Delete()
    {
        foreach (var ancestor in _ancestors)
            if (((FileAttributes)Attributes(ancestor).Attributes).HasFlag(FileAttributes.ReparsePoint))
                throw new NotSupportedException("An owned runtime ancestor changed to a reparse point. Recovery is blocked.");
        // Release the runtime rename pin, then reacquire DELETE through the still-pinned parent.
        // The returned identity, not the name, decides whether deletion is authorized.
        _operation?.Dispose();
        _operation = null;
        using var root = OpenRelative(Parent, System.IO.Path.GetFileName(Path),
            ReadAttributes | Traverse | DeleteAccess | 1, disposition: 1, directory: true);
        Validate(root, Path, directory: true);
        if (Identity(root) != _identity)
            throw new NotSupportedException("The owned operation directory was replaced. Recovery is blocked.");
        var remaining = MaximumCleanupEntries;
        DeleteTree(root, 0, ref remaining);
    }

    private static void DeleteTree(SafeFileHandle directory, int depth, ref int remaining)
    {
        if (depth > MaximumCleanupDepth)
            throw new NotSupportedException("Owned runtime cleanup exceeds its traversal bound.");
        foreach (var name in Names(directory))
        {
            if (--remaining < 0) throw new NotSupportedException("Owned runtime cleanup exceeds its entry bound.");
            using var child = OpenRelative(directory, name, ReadAttributes | Traverse | DeleteAccess | 1,
                disposition: 1, directory: false);
            var tag = Attributes(child);
            if ((tag.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("A reparse entry blocks owned runtime cleanup. Its target was not followed.");
            if ((tag.Attributes & (uint)FileAttributes.Directory) != 0) DeleteTree(child, depth + 1, ref remaining);
            else MarkDelete(child);
        }
        MarkDelete(directory);
    }

    private static IEnumerable<string> Names(SafeFileHandle directory)
    {
        // FILE_ID_BOTH_DIR_INFO, restart for each pass so successful partial recovery needs no cursor.
        const int bufferBytes = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(bufferBytes);
        try
        {
            var restart = true;
            while (true)
            {
                if (!GetFileInformationByHandleEx(directory, restart ? 11 : 10, buffer, bufferBytes))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 18) yield break;
                    throw IoError(error);
                }
                restart = false;
                var offset = 0;
                while (true)
                {
                    var entry = IntPtr.Add(buffer, offset);
                    var next = Marshal.ReadInt32(entry);
                    var bytes = Marshal.ReadInt32(entry, 60);
                    if (bytes < 0 || bytes % 2 != 0 || offset + 104L + bytes > bufferBytes)
                        throw new IOException("Owned runtime directory information is invalid.");
                    var name = Marshal.PtrToStringUni(IntPtr.Add(entry, 104), bytes / 2)!;
                    if (name is not ("." or ".."))
                    {
                        if (name.Length == 0 || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
                            throw new IOException("Owned runtime directory information is invalid.");
                        yield return name;
                    }
                    if (next == 0) break;
                    if (next < 104 || offset + (long)next >= bufferBytes)
                        throw new IOException("Owned runtime directory information is invalid.");
                    offset += next;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void MarkDelete(SafeFileHandle handle)
    {
        // FILE_DISPOSITION_INFO_EX: delete this opened object, ignoring its readonly attribute.
        // Never change attributes/ACLs through a possibly aliased path.
        uint flags = 1 | 0x10;
        if (!SetFileInformationByHandle(handle, 21, ref flags, 4))
            throw IoError(Marshal.GetLastWin32Error());
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, uint access, uint disposition, bool directory)
    {
        var text = Marshal.StringToHGlobalUni(name);
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var unicode = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = text };
            Marshal.StructureToPtr(unicode, pointer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = pointer, Attributes = 0x40,
            };
            var status = NtCreateFile(out var handle, access | Synchronize, ref attributes, out _, IntPtr.Zero,
                directory ? 0x10u : 0u, 3, disposition, OpenReparsePoint | Synchronous | (directory ? DirectoryOnly : 0), IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status < 0) { handle?.Dispose(); throw IoError((int)RtlNtStatusToDosError(status)); }
            return handle;
        }
        finally { Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(text); }
    }

    private static TagInfo Attributes(SafeFileHandle handle) =>
        GetTag(handle, 9, out var info, 8) ? info : throw IoError(Marshal.GetLastWin32Error());

    private static FileId Identity(SafeFileHandle handle) =>
        GetId(handle, 18, out var id, 24) ? id : throw IoError(Marshal.GetLastWin32Error());

    private static void Validate(SafeFileHandle handle, string path, bool directory)
    {
        var attributes = (FileAttributes)Attributes(handle).Attributes;
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new NotSupportedException("Executor runtime storage requires ordinary no-follow directories.");
        var final = new StringBuilder(32_768);
        var length = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity || !MxcFolderAdmission.NormalizeIdentity(final.ToString())
                .Equals(MxcFolderAdmission.NormalizeIdentity(path), StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Executor runtime storage has an unverified physical alias.");
    }

    private static void CheckHandle(SafeFileHandle handle)
    {
        if (!handle.IsInvalid) return;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw IoError(error);
    }
    private static IOException IoError(int error) => new(new Win32Exception(error).Message, unchecked((int)(0x80070000u | (uint)error)));
    private static string NativePath(string path) => @"\\?\" + path;
    public void Dispose()
    {
        _payload?.Dispose();
        _payload = null;
        _operation?.Dispose();
        for (var i = _ancestors.Count - 1; i >= 0; i--) _ancestors[i].Dispose();
        _ancestors.Clear();
    }

    [StructLayout(LayoutKind.Sequential)] private struct TagInfo { public uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct FileId(ulong Volume, ulong Low, ulong High);
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Security, Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public IntPtr Status, Information; }
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatus io, IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTag(SafeFileHandle handle, int kind, out TagInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetId(SafeFileHandle handle, int kind, out FileId info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, IntPtr info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref uint info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
