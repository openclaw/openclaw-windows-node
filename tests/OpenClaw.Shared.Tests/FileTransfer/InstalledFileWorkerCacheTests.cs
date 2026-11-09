using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared.FileTransfer;

namespace OpenClaw.Shared.Tests.FileTransfer;

public sealed partial class InstalledFileWorkerResolverTests
{
    [Fact]
    public void EightLiveLeasesBlockNewAdmissionButReuseCurrentAndReleasedEntryCanBeReclaimed()
    {
        var resolvers = Enumerable.Range(0, 9).Select(VersionResolver).ToArray();
        var leases = resolvers.Take(8).Select(r => r.Resolve(Cache)).ToArray();
        try
        {
            var identities = leases.Select(l => NativeIdentity(l.ImagePath)).ToArray();
            Assert.Contains("cache is full", Assert.Throws<NotSupportedException>(() => resolvers[8].Resolve(Cache)).Message);
            using (var current = resolvers[0].Resolve(Cache)) Assert.Equal(identities[0], NativeIdentity(current.ImagePath));
            var released = leases[3].PayloadDirectory;
            leases[3].Dispose();
            using var next = resolvers[8].Resolve(Cache);
            Assert.False(Directory.Exists(released));
            for (var index = 0; index < 8; index++)
                if (index != 3) Assert.Equal(identities[index], NativeIdentity(leases[index].ImagePath));
            Assert.Equal(8, Directory.GetDirectories(Cache).Length);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    [Fact]
    public async Task SimultaneousNewVersionProcessesSerializeEvictionWithoutBreakingAnOldLease()
    {
        var old = VersionResolver(0);
        using var active = old.Resolve(Cache);
        var identity = NativeIdentity(active.ImagePath);
        for (var version = 1; version < 8; version++) using (VersionResolver(version).Resolve(Cache)) { }
        var manifests = Enumerable.Range(8, 2).Select(version =>
        {
            var resolver = VersionResolver(version);
            using var source = resolver.Resolve();
            return Path.Combine(source.PayloadDirectory, "payload-manifest.json");
        }).ToArray();
        using var first = StartCacheHost(_product, manifest: manifests[0]);
        using var second = StartCacheHost(_product, manifest: manifests[1]);
        try
        {
            var paths = await Task.WhenAll(first.StandardOutput.ReadLineAsync(), second.StandardOutput.ReadLineAsync())
                .WaitAsync(TimeSpan.FromSeconds(15));
            if (paths[0] is null) Assert.Fail(await first.StandardError.ReadToEndAsync());
            if (paths[1] is null) Assert.Fail(await second.StandardError.ReadToEndAsync());
            Assert.NotEqual(paths[0], paths[1]);
            Assert.Equal(identity, NativeIdentity(active.ImagePath));
            using var reused = old.Resolve(Cache);
            Assert.Equal(identity, NativeIdentity(reused.ImagePath));
            Assert.Equal(8, Directory.GetDirectories(Cache).Length);
        }
        finally
        {
            foreach (var process in new[] { first, second })
            {
                process.StandardInput.Close();
                if (!await WaitForOwnedExit(process)) { process.Kill(); await process.WaitForExitAsync(); }
            }
        }
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("replaced")]
    [InlineData("hardlink")]
    [InlineData("reparse")]
    [InlineData("missing")]
    [InlineData("sparse")]
    [InlineData("json-ledger")]
    [InlineData("corrupt-ea")]
    [InlineData("transplanted-ea")]
    [InlineData("entry-replaced")]
    [InlineData("locked")]
    public void UnprovenPublishedInventoryOrOwnershipCannotAuthorizeAnyDeletion(string mutation)
    {
        string entry;
        using (var lease = VersionResolver(0).Resolve(Cache)) entry = lease.PayloadDirectory;
        FillUnknownSlots();
        var imageIdentity = NativeIdentity(Path.Combine(entry, "OpenClaw.FileWorker.exe"));
        var runtime = Path.Combine(entry, "coreclr.dll");
        var marker = Path.Combine(_root, "outside");
        File.WriteAllText(marker, "foreign");
        var markerIdentity = NativeIdentity(marker);
        using var locked = mutation == "locked" ? new FileStream(runtime, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        switch (mutation)
        {
            case "extra": File.WriteAllText(Path.Combine(entry, "extra"), "foreign"); break;
            case "json-ledger": File.WriteAllText(Path.Combine(entry, "ownership.json"), "{\"state\":\"published\",\"owned\":true}"); break;
            case "replaced":
                File.Move(runtime, Path.Combine(_root, "retained-runtime"));
                File.Copy(marker, runtime);
                break;
            case "hardlink": CreateHardLink(Path.Combine(_root, "alias"), runtime); break;
            case "reparse":
                File.Delete(runtime);
                File.CreateSymbolicLink(runtime, marker);
                break;
            case "missing": File.Delete(runtime); break;
            case "sparse":
                using (var file = File.OpenHandle(runtime, FileMode.Open, FileAccess.ReadWrite))
                    Assert.True(DeviceIoControl(file, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero));
                break;
            case "corrupt-ea": WriteCacheEa(entry, "{"u8.ToArray()); break;
            case "transplanted-ea":
                var otherCache = Cache + "-other";
                string otherEntry;
                using (var lease = VersionResolver(1).Resolve(otherCache)) otherEntry = lease.PayloadDirectory;
                WriteCacheEa(entry, ReadCacheEa(otherEntry));
                break;
            case "entry-replaced":
                var previousEa = ReadCacheEa(entry);
                var moved = Path.Combine(_root, "retained-entry");
                Directory.Move(entry, moved);
                Directory.CreateDirectory(entry);
                foreach (var file in Directory.GetFiles(moved)) File.Copy(file, Path.Combine(entry, Path.GetFileName(file)));
                WriteCacheEa(entry, previousEa);
                imageIdentity = NativeIdentity(Path.Combine(entry, "OpenClaw.FileWorker.exe"));
                break;
        }
        var names = Directory.GetFileSystemEntries(entry).Select(Path.GetFileName).Order().ToArray();
        var replacementIdentity = mutation == "missing" ? ((uint, ulong)?)null : NativeIdentity(runtime);
        for (var restart = 0; restart < 2; restart++)
            Assert.Throws<NotSupportedException>(() => VersionResolver(2).Resolve(Cache));
        Assert.Equal(imageIdentity, NativeIdentity(Path.Combine(entry, "OpenClaw.FileWorker.exe")));
        Assert.Equal(names, Directory.GetFileSystemEntries(entry).Select(Path.GetFileName).Order());
        if (replacementIdentity is not null) Assert.Equal(replacementIdentity, NativeIdentity(runtime));
        Assert.Equal(markerIdentity, NativeIdentity(marker));
        Assert.Equal("foreign", File.ReadAllText(marker));
    }

    [Fact]
    public void ExistingUnownedRootAndCopiedNativeRootRecordAreNeverAdopted()
    {
        Directory.CreateDirectory(Cache);
        for (var version = 0; version < 8; version++) using (VersionResolver(version).Resolve(Cache)) { }
        Assert.Contains("cache is full", Assert.Throws<NotSupportedException>(() => VersionResolver(8).Resolve(Cache)).Message);
        Assert.Empty(ReadCacheEa(Cache));
        var ownedRoot = Cache + "-owned";
        using (VersionResolver(9).Resolve(ownedRoot)) { }
        WriteCacheEa(Cache, ReadCacheEa(ownedRoot));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var privateAcl = new DirectoryInfo(ownedRoot).GetAccessControl(AccessControlSections.Access);
        privateAcl.SetAccessRuleProtection(true, false);
        // Elevated Directory.CreateDirectory can select Administrators instead of the current user as owner.
        using var identity = WindowsIdentity.GetCurrent();
        var cache = new DirectoryInfo(Cache);
        var currentOwner = cache.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        // An already-correct owner need not request WRITE_OWNER merely to update the DACL.
        if (currentOwner != identity.User)
            privateAcl.SetOwner(identity.User!);
        cache.SetAccessControl(privateAcl);
        Assert.Contains("root ownership changed", Assert.Throws<NotSupportedException>(() => VersionResolver(8).Resolve(Cache)).Message);
        Assert.Equal(8, Directory.GetDirectories(Cache).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KernelDeletionFailureBeforeOrAfterFirstDispositionPreservesDurableRecovery(bool afterImage)
    {
        var originalResolver = VersionResolver(0);
        string entry;
        using (var lease = originalResolver.Resolve(Cache)) entry = lease.PayloadDirectory;
        FillUnknownSlots();
        var originalEa = ReadCacheEa(entry);
        var image = Path.Combine(entry, "OpenClaw.FileWorker.exe");
        var blocked = Path.Combine(entry, afterImage ? "coreclr.dll" : "OpenClaw.FileWorker.exe");
        File.SetAttributes(blocked, FileAttributes.ReadOnly);
        try
        {
            using var attempt = StartCacheHost(CreateProcessPayload());
            await attempt.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, attempt.ExitCode);
            Assert.Contains("cache is full", await attempt.StandardError.ReadToEndAsync());
            Assert.Equal(!afterImage, File.Exists(image));
            if (afterImage)
            {
                Assert.Contains("\"state\":\"deleting\"", Encoding.UTF8.GetString(ReadCacheEa(entry)));
                Assert.Contains("interrupted deletion", Assert.Throws<NotSupportedException>(() => originalResolver.Resolve(Cache)).Message);
            }
            else
            {
                Assert.Equal(originalEa, ReadCacheEa(entry));
                Assert.Equal(4, Directory.GetFiles(entry).Length);
                using var reusable = originalResolver.Resolve(Cache);
            }
        }
        finally { File.SetAttributes(blocked, FileAttributes.Normal); }
        using var recovered = VersionResolver(1).Resolve(Cache);
        Assert.False(Directory.Exists(entry));
        Assert.Equal(8, Directory.GetDirectories(Cache).Length);
        Assert.All(Directory.GetFiles(Cache, "foreign", SearchOption.AllDirectories),
            file => Assert.Equal("preserve", File.ReadAllText(file)));
    }

    [Fact]
    public void SameVersionRestartFinishesOnlyOwnedInterruptedDeletionBeforeRepublishing()
    {
        string entry;
        using (var lease = VersionResolver(0).Resolve(Cache)) entry = lease.PayloadDirectory;
        FillUnknownSlots();
        var runtime = Path.Combine(entry, "coreclr.dll");
        File.SetAttributes(runtime, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<NotSupportedException>(() => VersionResolver(1).Resolve(Cache));
            Assert.False(File.Exists(Path.Combine(entry, "OpenClaw.FileWorker.exe")));
            var remaining = Directory.GetFiles(entry).ToDictionary(path => Path.GetFileName(path)!, NativeIdentity);
            Assert.Contains("interrupted deletion", Assert.Throws<NotSupportedException>(() => VersionResolver(0).Resolve(Cache)).Message);
            Assert.Equal(remaining.Keys.Order(), Directory.GetFiles(entry).Select(Path.GetFileName).Order());
            foreach (var (name, identity) in remaining) Assert.Equal(identity, NativeIdentity(Path.Combine(entry, name!)));
        }
        finally { File.SetAttributes(runtime, FileAttributes.Normal); }

        using (var activeChild = new FileStream(runtime, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var remaining = Directory.GetFiles(entry).ToDictionary(path => Path.GetFileName(path)!, NativeIdentity);
            Assert.Contains("recovery is blocked", Assert.Throws<NotSupportedException>(() => VersionResolver(0).Resolve(Cache)).Message);
            Assert.Equal(remaining.Keys.Order(), Directory.GetFiles(entry).Select(Path.GetFileName).Order());
            foreach (var (name, identity) in remaining) Assert.Equal(identity, NativeIdentity(Path.Combine(entry, name)));
            Assert.False(File.Exists(Path.Combine(entry, "OpenClaw.FileWorker.exe")));
        }
        using var recovered = VersionResolver(0).Resolve(Cache);
        Assert.Equal(entry, recovered.PayloadDirectory);
        Assert.Equal(4, Directory.GetFiles(entry).Length);
        Assert.Equal("abc", File.ReadAllText(recovered.ImagePath));
        Assert.Contains("\"state\":\"published\"", Encoding.UTF8.GetString(ReadCacheEa(entry)));
        Assert.Equal(8, Directory.GetDirectories(Cache).Length);
        Assert.All(Directory.GetFiles(Cache, "foreign", SearchOption.AllDirectories),
            file => Assert.Equal("preserve", File.ReadAllText(file)));
    }

    [Fact]
    public void InterruptedDeletionCannotAdoptAReplacementRemainingChild()
    {
        string entry;
        using (var lease = VersionResolver(0).Resolve(Cache)) entry = lease.PayloadDirectory;
        FillUnknownSlots();
        var runtime = Path.Combine(entry, "coreclr.dll");
        File.SetAttributes(runtime, FileAttributes.ReadOnly);
        try { Assert.Throws<NotSupportedException>(() => VersionResolver(1).Resolve(Cache)); }
        finally { File.SetAttributes(runtime, FileAttributes.Normal); }
        Assert.False(File.Exists(Path.Combine(entry, "OpenClaw.FileWorker.exe")));
        File.Move(runtime, Path.Combine(_root, "original-runtime"));
        File.WriteAllText(runtime, "foreign");
        var replacement = NativeIdentity(runtime);
        var names = Directory.GetFiles(entry).Select(Path.GetFileName).Order().ToArray();
        for (var restart = 0; restart < 2; restart++)
        {
            Assert.Throws<NotSupportedException>(() => VersionResolver(1).Resolve(Cache));
            Assert.Throws<NotSupportedException>(() => VersionResolver(0).Resolve(Cache));
        }
        Assert.Equal(replacement, NativeIdentity(runtime));
        Assert.Equal("foreign", File.ReadAllText(runtime));
        Assert.Equal(names, Directory.GetFiles(entry).Select(Path.GetFileName).Order());
    }

    private void FillUnknownSlots()
    {
        for (var index = 0; index < 7; index++)
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(Cache, "unknown-" + index)).FullName, "foreign"), "preserve");
    }

    private static async Task<bool> WaitForOwnedExit(Process process)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); return true; }
        catch (TimeoutException) { return false; }
    }

    // Independent native EA fixture: no product record serializer or identity
    // helper supplies the expected result or manufactures an idle-worker receipt.
    private const string CacheEaName = "OPENCLAW.WORKER.CACHE.V1";
    private static void AssertCacheIdentity(string path, System.Text.Json.JsonElement value)
    {
        using var file = CreateFileW(@"\\?\" + path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(file.IsInvalid);
        Assert.True(GetFileInformationByHandleEx(file, 18, out CacheFileIdentity identity, 24));
        Assert.Equal(identity.Volume, value.GetProperty("volume").GetUInt64());
        Assert.Equal(identity.Low, value.GetProperty("low").GetUInt64());
        Assert.Equal(identity.High, value.GetProperty("high").GetUInt64());
    }

    private static byte[] ReadCacheEa(string path)
    {
        using var handle = OpenEa(path);
        var name = Encoding.ASCII.GetBytes(CacheEaName);
        var query = new byte[6 + name.Length];
        query[4] = (byte)name.Length;
        name.CopyTo(query, 5);
        var buffer = new byte[65_536];
        var status = NtQueryEaFile(handle, out _, buffer, (uint)buffer.Length, true, query, (uint)query.Length, IntPtr.Zero, true);
        if (status == unchecked((int)0xC0000052)) return [];
        Assert.True(status >= 0, $"EA query NTSTATUS {status:X8}");
        return buffer.AsSpan(9 + buffer[5], BitConverter.ToUInt16(buffer, 6)).ToArray();
    }
    private static void WriteCacheEa(string path, byte[] value)
    {
        using var handle = OpenEa(path);
        var name = Encoding.ASCII.GetBytes(CacheEaName);
        var bytes = new byte[9 + name.Length + value.Length];
        bytes[5] = (byte)name.Length;
        BitConverter.TryWriteBytes(bytes.AsSpan(6, 2), checked((ushort)value.Length));
        name.CopyTo(bytes, 8);
        value.CopyTo(bytes, 9 + name.Length);
        Assert.True(NtSetEaFile(handle, out _, bytes, (uint)bytes.Length) >= 0);
    }
    private static SafeFileHandle OpenEa(string path)
    {
        var handle = CreateFileW(@"\\?\" + path, 0x18, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        return handle;
    }
    [StructLayout(LayoutKind.Sequential)] private struct EaIoStatus { public IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)] private struct CacheFileIdentity { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out CacheFileIdentity identity, uint size);
    [DllImport("ntdll.dll")] private static extern int NtQueryEaFile(SafeFileHandle handle, out EaIoStatus io,
        [Out] byte[] buffer, uint size, [MarshalAs(UnmanagedType.U1)] bool singleEntry, byte[] query, uint querySize,
        IntPtr index, [MarshalAs(UnmanagedType.U1)] bool restart);
    [DllImport("ntdll.dll")] private static extern int NtSetEaFile(SafeFileHandle handle, out EaIoStatus io, byte[] buffer, uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
