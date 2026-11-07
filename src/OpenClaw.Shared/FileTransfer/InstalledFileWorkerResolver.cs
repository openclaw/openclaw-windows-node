using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.FileTransfer;

/// <summary>Fixed product payload, compared with the build manifest embedded in Companion, not a caller-owned manifest.</summary>
public sealed partial class InstalledFileWorkerResolver : IFixedFileWorkerResolver
{
    private readonly Assembly _productAssembly;
    private readonly string _productDirectory;
    public InstalledFileWorkerResolver(Assembly productAssembly) : this(productAssembly, AppContext.BaseDirectory) { }
    internal InstalledFileWorkerResolver(Assembly productAssembly, string productDirectory)
    {
        _productAssembly = productAssembly;
        _productDirectory = productDirectory;
    }
    private const int MaximumPayloadFiles = 512;
    private const long MaximumPayloadFileBytes = 256L * FileLimits.MessageBytes;
    private const int MaximumCachedPayloads = 8;
    private const long MaximumPayloadBytes = 128L * 1024 * 1024;
    private const long MaximumCacheBytes = MaximumCachedPayloads * MaximumPayloadBytes;
    private static readonly TimeSpan CacheLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CacheLockRetry = TimeSpan.FromMilliseconds(25);
    private const string CacheRepair = "Unproven or active cache entries were retained. Diagnose cache ownership and active workers before retrying.";
    private sealed record PayloadManifest(int Version, string Rid, string ProductVersion, Dictionary<string, string> Files);

    public FixedFileWorkerLease Resolve(string? payloadCacheRoot = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64",
            _ => throw new NotSupportedException("No fixed file worker is packaged for this architecture.")
        };
        using var embedded = _productAssembly.GetManifestResourceStream("OpenClaw.FileWorker.Manifest." + rid)
            ?? throw new NotSupportedException("The product file-worker manifest is missing. Repair the installation.");
        using var manifestBytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = embedded.Read(buffer)) != 0)
        {
            if (manifestBytes.Length + read > FileLimits.MetadataBytes) throw new NotSupportedException("The product manifest exceeds its bound.");
            manifestBytes.Write(buffer, 0, read);
        }
        var productManifest = manifestBytes.ToArray();
        var manifest = JsonSerializer.Deserialize<PayloadManifest>(productManifest, FileJson.Options)
            ?? throw new NotSupportedException("The product manifest is invalid.");
        if (manifest.Version != 1 || manifest.Rid != rid || manifest.Files.Count is 0 or > MaximumPayloadFiles ||
            !manifest.Files.ContainsKey("OpenClaw.FileWorker.exe") || !manifest.Files.ContainsKey("coreclr.dll") ||
            !manifest.Files.ContainsKey("OpenClaw.FileWorker.runtimeconfig.json"))
            throw new NotSupportedException("A complete self-contained fixed-worker payload is required.");
        // A manifest-addressed slot prevents obsolete dependencies from an older build entering this payload.
        // Directory naming is not the trust check: every file is still pinned and verified with its full digest.
        var digest = Convert.ToHexStringLower(SHA256.HashData(productManifest));
        var directory = Path.Combine(_productDirectory, "Workers", "FileWorker", rid, digest[..32]);
        var lease = new PayloadLease(directory);
        try
        {
            lease.PinDirectoryChain(directory);
            var source = VerifyPayload(lease, directory, manifest, productManifest, ct);
            if (payloadCacheRoot is not null)
            {
                ct.ThrowIfCancellationRequested();
                var root = MxcRequestBuilder.ValidateExecutable(payloadCacheRoot);
                var rootHandle = CacheOwnership.OpenRoot(lease, root);
                using var cacheLock = AcquireCacheLock(root, ct);
                var ownership = CacheOwnership.ReadRoot(rootHandle, root, ct);
                var destination = Path.Combine(root, rid + "-" + digest);
                var preparation = Path.Combine(root, ".preparing-" + rid + "-" + digest);
                if (Path.Exists(preparation))
                    throw new NotSupportedException("The fixed-worker payload cache has an uncertain preparation.");
                if (Path.Exists(destination) &&
                    (ownership is null || !CacheOwnership.RecoverInterruptedDeletion(rootHandle, destination)))
                {
                    // Published entries are never repaired or replaced. Only an owned
                    // interrupted deletion may finish before a fresh publication.
                    var entryHandle = lease.PinDirectoryChain(destination);
                    CacheOwnership.DemandExecutable(entryHandle, rootHandle, destination);
                    VerifyPayload(lease, destination, manifest, productManifest, ct);
                    lease.SelectExecutableDirectory(destination);
                    return lease;
                }
                AdmitCachePayload(root, rootHandle, ownership, destination, source.Values.Sum(s => s.Length), ct);
                // The final manifest-addressed name starts with an atomic native
                // execution veto. No rename or closing verification pins is needed.
                preparation = destination;
                using var preparing = new PayloadLease(preparation);
                var preparationHandle = CacheOwnership.CreatePreparing(preparing, rootHandle, preparation);
                var created = new Dictionary<string, NativeFileId>(StringComparer.OrdinalIgnoreCase);
                var published = false;
                try
                {
                    var copyBuffer = new byte[81_920];
                    foreach (var (name, stream) in source)
                    {
                        ct.ThrowIfCancellationRequested();
                        stream.Position = 0;
                        var path = Path.Combine(preparation, name);
                        using var copy = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        created.Add(name, PayloadLease.ReadIdentity(copy.SafeFileHandle));
                        while ((read = stream.Read(copyBuffer)) != 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            copy.Write(copyBuffer, 0, read);
                        }
                        copy.Flush(flushToDisk: true);
                    }
                    using var contents = new PayloadLease(preparation);
                    var verified = VerifyPayload(contents, preparation, manifest, productManifest, ct);
                    foreach (var (name, stream) in verified)
                        if (PayloadLease.ReadIdentity(stream.SafeFileHandle) != created[name])
                            throw new NotSupportedException("A fixed-worker preparation child was replaced before publication.");
                    ct.ThrowIfCancellationRequested();
                    CacheOwnership.Publish(preparationHandle, rootHandle, destination, created, ref published);
                    // Shed the creator's DELETE access so independent read leases
                    // can coexist. Verified child pins stay held across this handoff.
                    using var entryIdentity = PayloadLease.OpenPublicationIdentity(destination);
                    preparationHandle.Dispose();
                    var selectedEntry = lease.PinDirectoryChain(destination);
                    if (PayloadLease.ReadIdentity(selectedEntry) != PayloadLease.ReadIdentity(entryIdentity))
                        throw new NotSupportedException("The fixed-worker published entry identity changed.");
                    VerifyPayload(lease, destination, manifest, productManifest, ct);
                    lease.SelectExecutableDirectory(destination);
                }
                catch (Exception failure)
                {
                    if (!published)
                    {
                        try { preparing.Rollback(preparationHandle, rootHandle, preparation, created); }
                        catch (Exception cleanupFailure)
                        {
                            const string reason = "The fixed-worker payload cache preparation cleanup is blocked; unproven or locked objects were retained. ";
                            if (failure is OperationCanceledException)
                                throw new OperationCanceledException(reason + CacheRepair,
                                    new AggregateException(failure, cleanupFailure), ct);
                            throw new FileCommandException("FILE_WORKER_CACHE_CLEANUP_BLOCKED", reason + CacheRepair);
                        }
                    }
                    if (failure is IOException or UnauthorizedAccessException)
                        throw new FileCommandException("FILE_WORKER_CACHE_PREPARATION_FAILED",
                            $"The fixed-worker payload cache preparation failed (Windows error {failure.HResult & 0xFFFF}). " +
                            (published ? "The published entry was retained; diagnose cache access before retrying." :
                                "Its owned unpublished preparation was removed. Retry when file access is available."));
                    throw;
                }
            }
            return lease;
        }
        catch (NotSupportedException ex) when (payloadCacheRoot is not null)
        {
            lease.Dispose();
            throw new NotSupportedException(ex.Message + " " + CacheRepair, ex);
        }
        catch { lease.Dispose(); throw; }
    }

    private static FileStream AcquireCacheLock(string root, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("The fixed-worker payload cache requires Windows.");
        var stream = PayloadLease.OpenCacheLock(Path.Combine(root, ".cache-lock"));
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { stream.Lock(0, 1); return stream; }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) == 33)
                {
                    if (elapsed.Elapsed >= CacheLockTimeout)
                        throw new NotSupportedException("The fixed-worker payload cache is busy. Wait for its current preparation to finish.");
                    if (ct.WaitHandle.WaitOne(CacheLockRetry)) ct.ThrowIfCancellationRequested();
                }
            }
        }
        catch { stream.Dispose(); throw; }
    }

    private static void AdmitCachePayload(string root, SafeFileHandle rootHandle, CacheOwnership.Record? ownership,
        string destination, long payloadBytes, CancellationToken ct)
    {
        if (payloadBytes > MaximumPayloadBytes - CacheOwnership.MetadataReservationBytes)
            throw new NotSupportedException("The fixed-worker payload exceeds the 128 MiB cache entry bound, including ownership metadata.");
        var entries = Directory.EnumerateFileSystemEntries(root).Where(p => Path.GetFileName(p) != ".cache-lock")
            .Take(MaximumCachedPayloads + 1).ToList();
        if (entries.Count > MaximumCachedPayloads)
            throw new NotSupportedException("The fixed-worker payload cache is full (unaccountable entry count).");
        // Account the entire inventory before any eviction. Unknown objects cannot
        // be made invisible to admission by reclaiming a different, owned entry.
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            using var inventory = new PayloadLease(root);
            // Unknown entries are never adopted or deleted. Only a bounded, ordinary flat
            // inventory can be charged a full entry reservation for new-version admission.
            inventory.PinDirectoryChain(entry);
            long bytes = 0;
            var files = 0;
            foreach (var file in Directory.EnumerateFileSystemEntries(entry))
            {
                ct.ThrowIfCancellationRequested();
                if (++files > MaximumPayloadFiles + 1)
                    throw new NotSupportedException("The fixed-worker payload cache inventory exceeds its file bound.");
                var pinned = inventory.PinFile(file);
                bytes = checked(bytes + pinned.Length);
                if (bytes > MaximumPayloadBytes)
                    throw new NotSupportedException("The fixed-worker payload cache inventory exceeds its byte bound.");
            }
        }
        if (checked((entries.Count + 1L) * MaximumPayloadBytes) <= MaximumCacheBytes) return;
        var refusals = new List<string>();
        if (ownership is not null)
            foreach (var entry in entries.Order(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(entry).StartsWith(".preparing-", StringComparison.Ordinal)) continue;
                if (CacheOwnership.TryEvict(rootHandle, entry, out var reason)) return;
                refusals.Add(reason);
            }
        throw new NotSupportedException("The fixed-worker payload cache is full (eight entries / 1 GiB, including active, unknown and deleting entries). " +
            string.Join(" ", refusals.Distinct()));
    }

    private static Dictionary<string, FileStream> VerifyPayload(PayloadLease lease, string directory,
        PayloadManifest manifest, byte[] productManifest, CancellationToken ct)
    {
        if (Directory.EnumerateDirectories(directory).Any())
            throw new NotSupportedException("Unexpected fixed-worker dependency directory.");
        var names = Directory.EnumerateFiles(directory).Take(manifest.Files.Count + 2).Select(Path.GetFileName).ToArray();
        if (names.Length != manifest.Files.Count + 1 ||
            !names.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(manifest.Files.Keys.Append("payload-manifest.json")))
            throw new NotSupportedException("The fixed-worker payload inventory changed.");
        var files = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = productManifest.Length;
        foreach (var (name, expected) in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(name) != name || name.Contains(':') || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                throw new NotSupportedException("The fixed-worker manifest contains an invalid entry.");
            var stream = lease.PinFile(Path.Combine(directory, name));
            totalBytes = checked(totalBytes + stream.Length);
            if (totalBytes > MaximumPayloadBytes)
                throw new NotSupportedException("The fixed-worker payload exceeds the 128 MiB entry bound.");
            if (stream.Length > MaximumPayloadFileBytes || !Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("A fixed-worker payload digest changed. Do not execute this payload.");
            files.Add(name, stream);
        }
        var installedManifest = lease.PinFile(Path.Combine(directory, "payload-manifest.json"));
        if (installedManifest.Length != productManifest.Length ||
            !SHA256.HashData(installedManifest).AsSpan().SequenceEqual(SHA256.HashData(productManifest)))
            throw new NotSupportedException("The worker payload manifest differs from this product build.");
        files.Add("payload-manifest.json", installedManifest);
        return files;
    }

    private sealed class PayloadLease(string directory) : FixedFileWorkerLease
    {
        // Metadata-only directory handles do not enforce deletion sharing on Windows.
        private const uint DirectoryPinAccess = 0x80 | 0x20 | 0x20008;
        private const uint OpenReparsePoint = 0x00200000;
        private readonly List<IDisposable> _pins = [];
        private string _executableDirectory = directory;
        public override string ImagePath => Path.Combine(_executableDirectory, "OpenClaw.FileWorker.exe");
        public override string PayloadDirectory => _executableDirectory;
        public void SelectExecutableDirectory(string path) => _executableDirectory = path;
        public static SafeFileHandle OpenPublicationIdentity(string path) =>
            OpenPinned(path, directory: true, shareDelete: true);
        public SafeFileHandle PinDirectoryChain(string path, bool createMissing = false)
        {
            var chain = new Stack<string>();
            for (var component = path; !string.IsNullOrEmpty(component); component = Path.GetDirectoryName(component))
                chain.Push(component);
            SafeFileHandle? last = null;
            while (chain.TryPop(out var component))
            {
                if (createMissing && last is not null)
                {
                    var handle = CreateDirectoryRelative(last, component, disposition: 3, publishable: false);
                    _pins.Add(handle);
                    last = handle;
                }
                else last = PinDirectory(component);
            }
            return last!;
        }
        private SafeFileHandle PinDirectory(string path)
        {
            var handle = OpenPinned(path, directory: true);
            _pins.Add(handle);
            return handle;
        }
        public SafeFileHandle CreatePayloadDirectory(string path, byte[] ownership, IntPtr security)
        {
            var parent = PinDirectoryChain(Path.GetDirectoryName(path)!);
            var handle = CreateDirectoryRelative(parent, path, disposition: 2, publishable: true, ownership, security);
            _pins.Add(handle);
            return handle;
        }
        internal static SafeFileHandle CreateDirectoryRelative(SafeFileHandle parent, string path, uint disposition, bool publishable,
            byte[]? ownership = null, IntPtr security = default)
        {
            var name = Path.GetFileName(path);
            var text = Marshal.StringToHGlobalUni(name);
            var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                var unicode = new UnicodeString
                {
                    Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = text,
                };
                Marshal.StructureToPtr(unicode, unicodePointer, false);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = unicodePointer, Attributes = 0x40,
                    Security = security,
                };
                var ea = ownership is null ? null : CacheOwnership.EncodeEa(ownership);
                var eaPointer = ea is null ? IntPtr.Zero : Marshal.AllocHGlobal(ea.Length);
                int status;
                SafeFileHandle handle;
                try
                {
                    if (ea is not null) Marshal.Copy(ea, 0, eaPointer, ea.Length);
                    status = NtCreateFile(out handle, DirectoryPinAccess | 0x100000u | 0x20008u |
                        (publishable ? 0x10116u : ownership is not null ? 0x116u : 0),
                        ref attributes, out _, IntPtr.Zero, 0x10, 3, disposition, OpenReparsePoint | 0x20 | 1,
                        eaPointer, (uint)(ea?.Length ?? 0));
                }
                finally { if (eaPointer != IntPtr.Zero) Marshal.FreeHGlobal(eaPointer); }
                GC.KeepAlive(parent);
                if (status < 0)
                {
                    handle?.Dispose();
                    throw new NotSupportedException("The fixed-worker cache directory cannot be created or opened safely.");
                }
                try { ValidatePinned(handle, path, directory: true); }
                catch { handle.Dispose(); throw; }
                return handle;
            }
            finally
            {
                Marshal.FreeHGlobal(unicodePointer);
                Marshal.FreeHGlobal(text);
            }
        }
        public static NativeFileId ReadIdentity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandleEx(handle, 18, out NativeFileId identity, 24))
                throw new NotSupportedException("The fixed-worker preparation identity cannot be established.");
            return identity;
        }
        public void Rollback(SafeFileHandle directoryHandle, SafeFileHandle rootHandle, string preparation, Dictionary<string, NativeFileId> created)
        {
            // Only this call's create-new children are candidates. Builder exclusion alone
            // never grants deletion authority over an orphan, replacement or added file.
            ValidatePinned(directoryHandle, preparation, directory: true);
            CacheOwnership.DemandUnpublished(directoryHandle, rootHandle, preparation);
            var names = Directory.EnumerateFileSystemEntries(preparation).Take(created.Count + 1).Select(path => Path.GetFileName(path)!).ToArray();
            if (names.Length != created.Count || !created.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(names))
                throw new NotSupportedException("The fixed-worker preparation inventory changed before rollback.");
            var children = new List<SafeFileHandle>();
            try
            {
                foreach (var (name, expected) in created)
                {
                    var path = Path.Combine(preparation, name);
                    var child = OpenRollbackChild(directoryHandle, path);
                    children.Add(child);
                    ValidatePinned(child, path, directory: false);
                    if (ReadIdentity(child) != expected)
                        throw new NotSupportedException("A fixed-worker preparation child was replaced before rollback.");
                }
                // Preflight every child before deleting any. These handles deny new writers
                // and deleters, and remain held until disposition has been set by handle.
                foreach (var child in children) DeleteOpened(child);
                foreach (var child in children) child.Dispose();
                DeleteOpened(directoryHandle);
            }
            finally { foreach (var child in children) child.Dispose(); }
        }
        internal static SafeFileHandle OpenRollbackChild(SafeFileHandle parent, string path, bool eviction = false, bool directory = false)
        {
            var name = Path.GetFileName(path);
            var text = Marshal.StringToHGlobalUni(name);
            var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.StructureToPtr(new UnicodeString
                {
                    Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = text,
                }, unicodePointer, false);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = unicodePointer, Attributes = 0x40,
                };
                var access = eviction ? 0x120089u | 0x10000u | (directory ? 0x136u : 0) : 0x10000u | 0x80u | 0x100000u;
                var status = NtCreateFile(out var handle, access,
                    ref attributes, out _, IntPtr.Zero, 0, directory ? 3u : eviction ? 0u : 1u, 1,
                    OpenReparsePoint | 0x20 | (directory ? 1u : 0x40u), IntPtr.Zero, 0);
                GC.KeepAlive(parent);
                if (status >= 0) return handle;
                handle?.Dispose();
                throw new NotSupportedException($"The fixed-worker preparation child cannot be opened for rollback (NTSTATUS 0x{status:X8}).");
            }
            finally
            {
                Marshal.FreeHGlobal(unicodePointer);
                Marshal.FreeHGlobal(text);
            }
        }
        private static void DeleteOpened(SafeFileHandle handle)
        {
            var disposition = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(disposition, 1);
                var status = NtSetInformationFile(handle, out _, disposition, 1, 13);
                if (status < 0)
                    throw new NotSupportedException($"The fixed-worker preparation cannot be removed safely (NTSTATUS 0x{status:X8}).");
            }
            finally { Marshal.FreeHGlobal(disposition); }
        }
        public static FileStream OpenCacheLock(string path)
        {
            var handle = CreateFileW(NativePath(path), 0xC0000000, FileShare.ReadWrite, IntPtr.Zero, 4,
                OpenReparsePoint | 0x02000000u, IntPtr.Zero);
            try
            {
                if (handle.IsInvalid) throw new NotSupportedException("The fixed-worker payload cache lock is unavailable.");
                ValidatePinned(handle, path, directory: false);
                var stream = new FileStream(handle, FileAccess.ReadWrite);
                if (stream.Length == 0) return stream;
                stream.Dispose();
                throw new NotSupportedException("The fixed-worker payload cache lock changed.");
            }
            catch { handle.Dispose(); throw; }
        }
        public FileStream PinFile(string path)
        {
            var handle = OpenPinned(path, directory: false);
            try
            {
                var stream = new FileStream(handle, FileAccess.Read);
                _pins.Add(stream);
                return stream;
            }
            catch { handle.Dispose(); throw; }
        }
        private static SafeFileHandle OpenPinned(string path, bool directory, bool shareDelete = false)
        {
            // Native calls do not inherit .NET's long-path handling or the test host's manifest.
            var handle = CreateFileW(NativePath(path), directory ? DirectoryPinAccess : 0x80000000u,
                directory ? FileShare.ReadWrite | (shareDelete ? FileShare.Delete : 0) : FileShare.Read,
                IntPtr.Zero, 3, OpenReparsePoint | 0x02000000u, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new NotSupportedException($"The fixed-worker payload cannot be pinned against replacement (Windows error {error}).");
            }
            try
            {
                ValidatePinned(handle, path, directory);
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        internal static void ValidatePinned(SafeFileHandle handle, string path, bool directory)
        {
            if (!GetFileInformationByHandleEx(handle, 9, out TagInfo tag, 8) ||
                ((FileAttributes)tag.Attributes).HasFlag(FileAttributes.ReparsePoint) ||
                ((FileAttributes)tag.Attributes).HasFlag(FileAttributes.SparseFile) ||
                ((FileAttributes)tag.Attributes).HasFlag(FileAttributes.Directory) != directory)
                throw new NotSupportedException("The fixed-worker payload must use ordinary files and directories.");
            if (!directory && (!GetFileInformationByHandle(handle, out var identity) || identity.NumberOfLinks != 1))
                throw new NotSupportedException("The fixed-worker payload must not use file aliases.");
            var final = new StringBuilder(32_768);
            var size = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
            if (size == 0 || size >= final.Capacity || !SandboxPathRules.Normalize(final.ToString()).Equals(
                SandboxPathRules.Normalize(path), StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("The fixed-worker payload path is aliased.");
        }
        public override void Dispose()
        {
            for (var i = _pins.Count - 1; i >= 0; i--) _pins[i].Dispose();
            _pins.Clear();
        }
    }

    private static string NativePath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] :
        @"\\?\" + Path.GetFullPath(path);

    [StructLayout(LayoutKind.Sequential)] private struct TagInfo { public uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativeFileId(ulong Volume, ulong Low, ulong High);
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Security, Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatus io, IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatus io, IntPtr info, uint size, int kind);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out TagInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out NativeFileId info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
