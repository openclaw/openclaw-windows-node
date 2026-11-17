using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Shared.FileTransfer;

public sealed partial class InstalledFileWorkerResolver
{
    /// <summary>
    /// Native ownership is separate from the embedded execution manifest. Only the
    /// trusted publisher writes EAs, under the builder lock and private root ACL.
    /// Contained commands have no write or EA-write authority in this namespace.
    /// </summary>
    private static class CacheOwnership
    {
        private const string EaName = "OPENCLAW.WORKER.CACHE.V1";
        private const int MaximumEaBytes = 60 * 1024;
        internal const int MetadataReservationBytes = 64 * 1024;
        internal sealed record Record(int Version, string Name, NativeFileId Parent, NativeFileId Identity,
            string State, Dictionary<string, NativeFileId>? Children = null);

        internal static SafeFileHandle OpenRoot(PayloadLease lease, string root)
        {
            if (!OperatingSystem.IsWindows()) throw new NotSupportedException("The fixed-worker payload cache requires Windows.");
            var parent = lease.PinDirectoryChain(Path.GetDirectoryName(root)!, createMissing: true);
            // FILE_CREATE is the only path establishing ownership. Existing roots,
            // even empty ones, are never silently adopted or given a private ACL.
            if (!Path.Exists(root))
            {
                using var identity = WindowsIdentity.GetCurrent();
                var sid = identity.User?.Value ?? throw new NotSupportedException("Missing cache owner identity.");
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    $"O:{sid}D:P(A;OICI;FA;;;{sid})(A;OICI;FA;;;SY)", 1, out var security, out _))
                    throw new NotSupportedException("The private cache ACL cannot be created.");
                try
                {
                    var creating = new Record(1, Path.GetFileName(root), PayloadLease.ReadIdentity(parent), default, "creating");
                    var madeRoot = false;
                    try
                    {
                        using var created = PayloadLease.CreateDirectoryRelative(parent, root, 2, false, Bytes(creating), security);
                        madeRoot = true;
                        Save(created, creating with { Identity = PayloadLease.ReadIdentity(created), State = "owned" });
                    }
                    catch (NotSupportedException) when (!madeRoot && Path.Exists(root))
                    {
                        // Another creator can win FILE_CREATE. Its native record is
                        // verified below; a name collision never authorizes adoption.
                    }
                }
                finally { LocalFree(security); }
            }
            return lease.PinDirectoryChain(root);
        }

        internal static Record? ReadRoot(SafeFileHandle rootHandle, string root, CancellationToken ct)
        {
            var record = Read(rootHandle);
            if (record is null) return null; // Legacy roots remain finite, unowned storage.
            RequirePrivate(rootHandle, protectedRoot: true);
            using var parent = new PayloadLease(root);
            var parentHandle = parent.PinDirectoryChain(Path.GetDirectoryName(root)!);
            var elapsed = Stopwatch.StartNew();
            // A simultaneous FILE_CREATE winner binds its ID outside this lock.
            // Wait for that writer, never finish its ownership record ourselves.
            while (record.State == "creating" && record.Version == 1 && record.Name == Path.GetFileName(root) &&
                record.Parent == PayloadLease.ReadIdentity(parentHandle))
            {
                if (elapsed.Elapsed >= CacheLockTimeout)
                    throw new NotSupportedException("The fixed-worker cache root has uncertain ownership.");
                if (ct.WaitHandle.WaitOne(CacheLockRetry)) ct.ThrowIfCancellationRequested();
                record = Read(rootHandle) ?? throw new NotSupportedException("The fixed-worker cache root ownership disappeared.");
            }
            if (record.Version != 1 || record.State != "owned" || record.Name != Path.GetFileName(root) ||
                record.Identity != PayloadLease.ReadIdentity(rootHandle) ||
                record.Parent != PayloadLease.ReadIdentity(parentHandle) || record.Children is not null)
                throw new NotSupportedException("The fixed-worker cache root ownership changed.");
            return record;
        }

        internal static SafeFileHandle CreatePreparing(PayloadLease lease, SafeFileHandle root, string destination)
        {
            if (!OperatingSystem.IsWindows()) throw new NotSupportedException("The fixed-worker payload cache requires Windows.");
            // FILE_CREATE attaches the veto in the same native operation as the
            // final name. Even a legacy root cannot expose a new recordless entry.
            var record = new Record(1, Path.GetFileName(destination), PayloadLease.ReadIdentity(root), default, "preparing");
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new NotSupportedException("Missing cache owner identity.");
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                $"O:{sid}D:P(A;OICI;FA;;;{sid})(A;OICI;FA;;;SY)", 1, out var security, out _))
                throw new NotSupportedException("The private cache entry ACL cannot be created.");
            try { return lease.CreatePayloadDirectory(destination, Bytes(record), security); }
            finally { LocalFree(security); }
        }

        internal static void Publish(SafeFileHandle entry, SafeFileHandle root, string destination,
            Dictionary<string, NativeFileId> children, ref bool published)
        {
            RequirePrivate(entry);
            var record = new Record(1, Path.GetFileName(destination), PayloadLease.ReadIdentity(root),
                PayloadLease.ReadIdentity(entry), "published", children);
            var bytes = EncodeEa(Bytes(record));
            if (NtSetEaFile(entry, out _, bytes, (uint)bytes.Length) < 0)
                throw new NotSupportedException("Cache publication could not be recorded.");
            // This is the atomic commit, not the later durability receipt. Once
            // committed, flush failure or cancellation must never select rollback.
            published = true;
            if (!FlushFileBuffers(entry))
                throw new NotSupportedException("Cache publication could not be durably flushed; the complete entry was retained.");
            var receipt = DemandExecutable(entry, root, destination);
            if (receipt?.Children is null || receipt.Children.Count != children.Count ||
                children.Any(child => !receipt.Children.TryGetValue(child.Key, out var identity) || identity != child.Value))
                throw new NotSupportedException("Cache publication has no matching native receipt; the complete entry was retained.");
        }

        internal static Record? DemandExecutable(SafeFileHandle entry, SafeFileHandle root, string path)
        {
            // A missing legacy record is not execution authority: full embedded-
            // manifest verification still follows. Every nonpublished record vetoes reuse.
            if (Read(entry) is not { } record) return null;
            if (record.State != "published")
                throw new NotSupportedException("The fixed-worker cache entry has an uncertain preparation or interrupted deletion; execution is blocked.");
            if (record.Version != 1 || record.Parent != PayloadLease.ReadIdentity(root) ||
                record.Identity != PayloadLease.ReadIdentity(entry) || record.Name != Path.GetFileName(path))
                throw new NotSupportedException("The fixed-worker cache entry ownership changed; execution is blocked.");
            return record;
        }

        internal static void DemandUnpublished(SafeFileHandle entry, SafeFileHandle root, string path)
        {
            // A failed native write can have an uncertain outcome. Only the exact
            // original veto, read back under the lock, still authorizes rollback.
            if (Read(entry) != new Record(1, Path.GetFileName(path), PayloadLease.ReadIdentity(root), default, "preparing"))
                throw new NotSupportedException("The fixed-worker preparation is no longer provably unpublished; its contents were retained.");
        }

        internal static bool RecoverInterruptedDeletion(SafeFileHandle root, string path)
        {
            using (var inspection = new PayloadLease(path))
            {
                var entry = inspection.PinDirectoryChain(path);
                if (Read(entry)?.State != "deleting") return false;
            }
            // Release the inspection pin first. The builder lock still excludes
            // other builders; eviction rechecks every native identity and guard.
            if (!TryEvict(root, path, out var reason))
                throw new NotSupportedException("The fixed-worker cache entry has an interrupted deletion; recovery is blocked. " + reason);
            return true;
        }

        internal static bool TryEvict(SafeFileHandle root, string path, out string reason)
        {
            reason = "Reclamation ownership or inventory is unproven.";
            var children = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var entry = PayloadLease.OpenRollbackChild(root, path, eviction: true, directory: true);
                PayloadLease.ValidatePinned(entry, path, directory: true);
                RequirePrivate(entry);
                var record = Read(entry);
                if (record is null) return false;
                if (record.Version != 1 || record.Parent != PayloadLease.ReadIdentity(root) ||
                    record.Identity != PayloadLease.ReadIdentity(entry) || record.Name != Path.GetFileName(path) ||
                    record.State is not ("published" or "deleting") || record.Children is null ||
                    record.Children.Count is 0 or > MaximumPayloadFiles + 1 ||
                    record.Children.Keys.Any(name => name != Path.GetFileName(name) || name.Contains(':')) ||
                    !record.Children.ContainsKey("OpenClaw.FileWorker.exe") ||
                    !record.Children.ContainsKey("payload-manifest.json"))
                    return false;
                var names = Directory.EnumerateFileSystemEntries(path).Take(MaximumPayloadFiles + 2)
                    .Select(p => Path.GetFileName(p)!).ToArray();
                if (names.Length > record.Children.Count ||
                    (record.State == "published" && names.Length != record.Children.Count) ||
                    names.Any(name => !record.Children.ContainsKey(name))) return false;
                foreach (var name in names)
                {
                    if (name != Path.GetFileName(name) || name.Contains(':')) return false;
                    var childPath = Path.Combine(path, name);
                    var child = PayloadLease.OpenRollbackChild(entry, childPath, eviction: true);
                    children.Add(name, child);
                    PayloadLease.ValidatePinned(child, childPath, directory: false);
                    if (PayloadLease.ReadIdentity(child) != record.Children[name]) return false;
                }
                // Persist the entire original identity inventory BEFORE the first
                // disposition. Process death can remove any subset, never authority.
                // An ordinary guard rejection restores the published record exactly.
                Save(entry, record with { State = "deleting" });
                if (children.TryGetValue("OpenClaw.FileWorker.exe", out var image) && !TryDelete(image))
                {
                    reason = $"Apphost deletion was vetoed (Windows error {Marshal.GetLastWin32Error()}).";
                    Save(entry, record);
                    return false;
                }
                // Non-POSIX FileDispositionInfo checks the apphost's real kernel
                // image section. Exclusive opens alone do NOT prove it is unmapped.
                // Keep every handle, including the image, until all disposition is
                // set: no late loader can reopen this payload during reclamation.
                foreach (var (name, child) in children)
                    if (name != "OpenClaw.FileWorker.exe" && !TryDelete(child))
                    {
                        reason = $"Dependency deletion was vetoed (Windows error {Marshal.GetLastWin32Error()}).";
                        return false;
                    }
                foreach (var child in children.Values) child.Dispose();
                return TryDelete(entry);
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or JsonException)
            {
                reason = ex is NotSupportedException ? ex.Message : $"Native reclamation was blocked (Windows error {ex.HResult & 0xFFFF}).";
                return false;
            }
            finally { foreach (var child in children.Values) child.Dispose(); }
        }

        private static bool TryDelete(SafeFileHandle handle)
        {
            var delete = 1;
            return SetFileInformationByHandle(handle, 4, ref delete, 4);
        }

        private static byte[] Bytes(Record record)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, FileJson.Options);
            if (bytes.Length > MaximumEaBytes) throw new NotSupportedException("Cache ownership exceeds its native bound.");
            return bytes;
        }

        internal static byte[] EncodeEa(byte[] value)
        {
            var name = Encoding.ASCII.GetBytes(EaName);
            var bytes = new byte[8 + name.Length + 1 + value.Length];
            bytes[5] = (byte)name.Length;
            BitConverter.TryWriteBytes(bytes.AsSpan(6, 2), checked((ushort)value.Length));
            name.CopyTo(bytes, 8);
            value.CopyTo(bytes, 9 + name.Length);
            return bytes;
        }

        private static void Save(SafeFileHandle handle, Record record)
        {
            var bytes = EncodeEa(Bytes(record));
            if (NtSetEaFile(handle, out _, bytes, (uint)bytes.Length) < 0 || !FlushFileBuffers(handle))
                throw new NotSupportedException("Cache ownership could not be durably recorded.");
        }

        private static Record? Read(SafeFileHandle handle)
        {
            var name = Encoding.ASCII.GetBytes(EaName);
            var query = new byte[6 + name.Length];
            query[4] = (byte)name.Length;
            name.CopyTo(query, 5);
            var buffer = new byte[MaximumEaBytes + 9 + name.Length];
            var status = NtQueryEaFile(handle, out _, buffer, (uint)buffer.Length, true, query, (uint)query.Length, IntPtr.Zero, true);
            if (status is unchecked((int)0xC0000052) or unchecked((int)0xC0000011)) return null;
            if (status < 0) throw new NotSupportedException("Cache ownership cannot be read safely.");
            var length = BitConverter.ToUInt16(buffer, 6);
            if (length == 0) return null;
            var offset = 9 + buffer[5];
            if (length > MaximumEaBytes || offset + length > buffer.Length)
                throw new NotSupportedException("Cache ownership exceeds its native bound.");
            try
            {
                return JsonSerializer.Deserialize<Record>(buffer.AsSpan(offset, length), FileJson.Options)
                    ?? throw new NotSupportedException("Cache ownership is invalid.");
            }
            catch (JsonException ex) { throw new NotSupportedException("Cache ownership is invalid.", ex); }
        }

        private static void RequirePrivate(SafeFileHandle handle, bool protectedRoot = false)
        {
            if (!OperatingSystem.IsWindows()) throw new NotSupportedException("The fixed-worker payload cache requires Windows.");
            if (GetSecurityInfo(handle, 1, 5, out var owner, out _, out _, out _, out var descriptor) != 0)
                throw new NotSupportedException("Cache ownership ACL cannot be read.");
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var sid = identity.User!;
                var length = GetSecurityDescriptorLength(descriptor);
                if (new SecurityIdentifier(owner) != sid || length > FileLimits.MetadataBytes)
                    throw new NotSupportedException("The cache has a different native owner.");
                var bytes = new byte[length];
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                var acl = new RawSecurityDescriptor(bytes, 0);
                // Children may inherit this private ACL; the root must be protected.
                if (acl.DiscretionaryAcl is null || acl.DiscretionaryAcl.Count == 0 ||
                    (protectedRoot && !acl.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)))
                    throw new NotSupportedException("The cache requires a private owner-only access list.");
                foreach (GenericAce ace in acl.DiscretionaryAcl)
                    if (ace is not CommonAce common || common.AceQualifier != AceQualifier.AccessAllowed ||
                        (common.SecurityIdentifier != sid && !common.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid)))
                        throw new NotSupportedException("The cache requires a private owner-only access list.");
            }
            finally { LocalFree(descriptor); }
        }
    }

    [DllImport("ntdll.dll")] private static extern int NtQueryEaFile(SafeFileHandle handle, out IoStatus io,
        [Out] byte[] buffer, uint size, [MarshalAs(UnmanagedType.U1)] bool singleEntry, byte[] query, uint querySize,
        IntPtr index, [MarshalAs(UnmanagedType.U1)] bool restart);
    [DllImport("ntdll.dll")] private static extern int NtSetEaFile(SafeFileHandle handle, out IoStatus io, byte[] buffer, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref int value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string descriptor, uint revision, out IntPtr security, out uint size);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
