using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenClaw.FileWorker;
using OpenClaw.Shared.FileTransfer;

namespace OpenClaw.FileWorker.Tests;

[SupportedOSPlatform("windows")]
public sealed class FileWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
        ".file-worker-test-data", Guid.NewGuid().ToString("N"));
    private const string AbcSha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string EmptySha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private FileWorkerAdmission Host => new("host", "windows_unsupported", 7, "test-policy", null, _root, Path.Combine(_root, "uploads"));
    public FileWorkerTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        var parent = Path.GetDirectoryName(_root)!;
        if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
    }

    private FileOperationResult Run(string command, object args, IWorkerMessages? messages = null, FileWorkerAdmission? admission = null) =>
        new FileWorkerEngine().Execute(admission ?? Host, FileCommandParser.Parse(command, FileJson.Element(args)), messages);
    private string PathOf(string name) => Path.Combine(_root, name);
    private static FileBinding Binding(FileOperationResult result) => result.Payload.GetProperty("binding").Deserialize<FileBinding>(FileJson.Options)!;
    private static void Good(FileOperationResult result) => Assert.True(result.Outcome.Ok, result.Payload.GetRawText());

    [Fact]
    public void ListingUsesNoFollowMetadataWhileArchiveRejectsAnUnsupportedMember()
    {
        var target = Directory.CreateDirectory(PathOf("target")).FullName;
        File.WriteAllText(Path.Combine(target, "secret"), "do not read");
        var listing = Directory.CreateDirectory(PathOf("listing")).FullName;
        var junction = Path.Combine(listing, "junction");
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec")!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = $"/d /c mklink /J \"{junction}\" \"{target}\""
        };
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        }
        try
        {
            var result = Run("dir.list", new { path = listing });
            Good(result);
            var entry = Assert.Single(result.Payload.GetProperty("entries").EnumerateArray());
            Assert.Equal("junction", entry.GetProperty("name").GetString());
            Assert.True(entry.GetProperty("unsupported").GetBoolean());
            Assert.False(entry.GetProperty("isFile").GetBoolean());
            Assert.False(entry.GetProperty("isDir").GetBoolean());
            var directories = Run("fs.listDir", new { path = listing });
            Good(directories);
            Assert.True(Assert.Single(directories.Payload.GetProperty("entries").EnumerateArray()).GetProperty("unsupported").GetBoolean());
            Assert.Equal("SYMLINK_REDIRECT", Run("dir.fetch", new { path = listing }).Outcome.Code);
            Assert.Equal("SYMLINK_REDIRECT", Run("file.stat", new { path = junction }).Outcome.Code);
            Assert.Equal("do not read", File.ReadAllText(Path.Combine(target, "secret")));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void NewDestinationsInheritFinalParentAccessButStagesAndUploadsRemainPrivate()
    {
        var sid = new SecurityIdentifier("S-1-5-21-111111111-222222222-333333333-4444");
        var security = new DirectoryInfo(_root).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_root).SetAccessControl(security);
        using (var fs = new RetainedFileSystem(Host))
        using (var parent = fs.Open(_root))
        using (var stage = fs.CreateStage(parent))
            Native.RequirePrivate(stage.Handle);
        var path = PathOf("parents\\child\\result.txt");
        Good(Run("file.write", new { path, contentBase64 = "YWJj", createParents = true }));
        foreach (var item in new FileSystemInfo[] { new DirectoryInfo(PathOf("parents")), new DirectoryInfo(PathOf("parents\\child")), new FileInfo(path) })
        {
            FileSystemSecurity acl = item is DirectoryInfo directory ? directory.GetAccessControl() : ((FileInfo)item).GetAccessControl();
            Assert.False(acl.AreAccessRulesProtected);
            Assert.Contains(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                rule => rule.IdentityReference == sid && rule.IsInherited && rule.AccessControlType == AccessControlType.Allow);
        }
        var before = new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Good(Run("file.write", new { path, contentBase64 = "eHl6", overwrite = true }));
        Assert.Equal(before, new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        var upload = Run("terminal.upload", new { name = "private", contentBase64 = "YWJj" });
        Good(upload);
        using var check = new RetainedFileSystem(Host);
        Native.RequirePrivate(check.Open(Host.UploadRoot).Handle);
        Native.RequirePrivate(check.Open(upload.Payload.GetProperty("path").GetString()!).Handle);
    }

    [Fact]
    public void PrivateStagesAndUploadStorageAreOwnedByTheCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var expectedOwner = identity.User!;
        using (var fs = new RetainedFileSystem(Host))
        using (var parent = fs.Open(_root))
        using (var stage = fs.CreateStage(parent))
            Assert.Equal(expectedOwner, new FileInfo(stage.Path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));

        var upload = Run("terminal.upload", new { name = "private.txt", contentBase64 = "YWJj" });
        Good(upload);
        var payload = upload.Payload.GetProperty("path").GetString()!;
        FileSystemSecurity[] descriptors =
        [
            new DirectoryInfo(Host.UploadRoot).GetAccessControl(),
            new DirectoryInfo(Path.GetDirectoryName(payload)!).GetAccessControl(),
            new FileInfo(payload).GetAccessControl(),
            new FileInfo(Path.Combine(Host.UploadRoot, ".openclaw-lock")).GetAccessControl()
        ];
        foreach (var descriptor in descriptors)
        {
            Assert.Equal(expectedOwner, descriptor.GetOwner(typeof(SecurityIdentifier)));
            Assert.True(descriptor.AreAccessRulesProtected);
        }
    }

    [Fact]
    public void KernelHandleClosureRollsBackOnlyTheOwnedStageWithoutManagedDeletion()
    {
        File.WriteAllText(PathOf(".openclaw-write-unowned"), "keep");
        using (var fs = new RetainedFileSystem(Host))
        using (var parent = fs.Open(_root))
        {
            string staged;
            using (var stage = fs.CreateStage(parent))
            {
                staged = stage.Path;
                stage.Write("partial"u8, 0);
                Assert.True(File.Exists(staged));
                // Close the OS handle directly. No worker finally or path-based cleanup is involved.
                stage.Handle.Dispose();
            }
            Assert.False(File.Exists(staged));
            using (var stage = fs.CreateStage(parent))
            {
                stage.Write("abc"u8, 0);
                stage.Finish(3);
                stage.Commit(parent, "not-published");
                stage.Handle.Dispose();
            }
            Assert.False(File.Exists(PathOf("not-published")));
            using (var stage = fs.CreateStage(parent))
            {
                stage.Write("abc"u8, 0);
                stage.Finish(3);
                stage.Commit(parent, "complete", publishStage: true);
            }
        }
        Assert.Equal("abc", File.ReadAllText(PathOf("complete")));
        Assert.Equal("keep", File.ReadAllText(PathOf(".openclaw-write-unowned")));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void StagePublicationRetainsTheNewDestinationDirectoryAgainstReplacement()
    {
        var target = Directory.CreateDirectory(PathOf("target")).FullName;
        using (var fs = new RetainedFileSystem(Host))
        using (var anchor = fs.Open(_root))
        using (var parent = fs.Open(target))
        using (var stage = fs.CreateStage(anchor))
        {
            stage.Write("abc"u8, 0);
            stage.Finish(3);
            Assert.Throws<IOException>(() => Directory.Move(target, PathOf("moved")));
            stage.Commit(parent, "result", publishStage: true);
        }
        Assert.Equal("abc", File.ReadAllText(Path.Combine(target, "result")));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void PaxArchivePreservesLongUnicodeNamesAndNestedPaths()
    {
        var leaf = new string('界', 80) + ".txt";
        var directory = Directory.CreateDirectory(PathOf(new string('a', 150))).FullName;
        File.WriteAllText(Path.Combine(directory, leaf), "abc");
        var result = Run("dir.fetch", new { path = _root });
        Good(result);
        using var bytes = new MemoryStream(Convert.FromBase64String(result.Payload.GetProperty("tarBase64").GetString()!));
        using var gzip = new GZipStream(bytes, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var names = new List<string>();
        while (tar.GetNextEntry() is { } entry)
        {
            names.Add(entry.Name);
            if (entry.EntryType == TarEntryType.RegularFile)
            {
                Assert.Equal(TarEntryFormat.Pax, entry.Format);
                using var text = new StreamReader(entry.DataStream!);
                Assert.Equal("abc", text.ReadToEnd());
            }
        }
        Assert.Contains("./" + new string('a', 150) + "/" + leaf, names);
    }

    [Fact]
    public void BoundWriteRejectsReplacementAndNoClobberCommitCannotReplaceARacedName()
    {
        var path = PathOf("target");
        File.WriteAllText(path, "old");
        var preflight = Run("file.write", new { path, contentBase64 = "YWJj", overwrite = true, preflightOnly = true });
        Good(preflight);
        File.Move(path, PathOf("old"));
        File.WriteAllText(path, "replacement");
        Assert.Equal("CANONICAL_PATH_CHANGED", Run("file.write",
            new { path, contentBase64 = "YWJj", overwrite = true, expectedBinding = Binding(preflight) }).Outcome.Code);
        Assert.Equal("replacement", File.ReadAllText(path));
        using var fs = new RetainedFileSystem(Host);
        using var parent = fs.Open(_root);
        using var stage = fs.CreateStage(parent);
        try
        {
            stage.Write("abc"u8, 0);
            stage.Finish(3);
            Assert.Equal("EXISTS_NO_OVERWRITE", Assert.Throws<FileCommandException>(() => stage.Commit(parent, "target")).Code);
            Assert.Equal("replacement", File.ReadAllText(path));
        }
        finally { stage.Delete(); }
    }

    [Fact]
    public void OversizedArchiveEntryAndUnaryFetchFailBeforeReadingContent()
    {
        var path = PathOf("large");
        using (var file = File.Create(path)) file.SetLength(16 * 1024 * 1024 + 1);
        Assert.Equal("FILE_TOO_LARGE", Run("file.fetch", new { path }).Outcome.Code);
        Assert.Equal("TREE_TOO_LARGE", Run("dir.fetch", new { path = _root, preflightOnly = true }).Outcome.Code);
        Assert.Equal("TREE_TOO_LARGE", Run("dir.fetch", new { path = _root }).Outcome.Code);
    }

    [Fact]
    public void FullDefaultCreateStreamsFiftyMiBWithoutBufferingTheWholeTransfer()
    {
        const int chunks = 50;
        var chunk = new byte[1024 * 1024];
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < chunks; i++) expected.AppendData(chunk);
        var digest = Convert.ToHexStringLower(expected.GetHashAndReset());
        var path = PathOf("full-size");
        var preflight = Run("file.create", new { path, sizeBytes = 50L * 1024 * 1024, expectedSha256 = digest, preflightOnly = true });
        Good(preflight);
        var command = FileCommandParser.Parse("file.create", FileJson.Element(new
        {
            path, sizeBytes = 50L * 1024 * 1024, expectedSha256 = digest, expectedBinding = Binding(preflight)
        }));
        var messages = new RepeatingMessages(chunk, chunks);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Good(new FileWorkerEngine().Execute(Host, command, messages));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8L * 1024 * 1024, $"Streaming allocated {allocated} bytes, exceeding eight chunk buffers.");
        Assert.Equal(chunks, messages.Acknowledgements);
        using var actual = File.OpenRead(path);
        Assert.Equal(50L * 1024 * 1024, actual.Length);
        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(actual)));
    }

    private sealed class RepeatingMessages(byte[] chunk, int count) : IWorkerMessages
    {
        private int _remaining = count;
        public int Acknowledgements { get; private set; }
        public void Listen() { }
        public byte[] Receive() => _remaining-- > 0 ? chunk : [];
        public void Send(ReadOnlySpan<byte> message)
        {
            Assert.True(message.SequenceEqual("ack"u8));
            Acknowledgements++;
        }
    }

    [Fact]
    public void CommittedTransferBudgetBoundsBinaryReadsCreatesAndUnaryWritesBeforeMutation()
    {
        File.WriteAllText(PathOf("source"), "abc");
        var limited = Host with { TransferBytes = 2 };
        var binding = Binding(Run("file.stat", new { path = PathOf("source") }));
        Assert.Equal("FILE_TOO_LARGE", Run("file.fetch", new
        {
            path = PathOf("source"), transport = "binary", maxBytes = 9_007_199_254_740_991L, expectedBinding = binding
        }, admission: limited).Outcome.Code);
        Assert.Equal("FILE_TOO_LARGE", Run("file.create", new
        {
            path = PathOf("new"), sizeBytes = 3, maxBytes = 9_007_199_254_740_991L, expectedSha256 = AbcSha, preflightOnly = true
        }, admission: limited).Outcome.Code);
        Assert.Equal("FILE_TOO_LARGE", Run("file.write", new { path = PathOf("new"), contentBase64 = "YWJj" }, admission: limited).Outcome.Code);
        Assert.Equal("FILE_TOO_LARGE", Run("terminal.upload", new { name = "new", contentBase64 = "YWJj" }, admission: limited).Outcome.Code);
        Assert.Equal(["source"], Directory.GetFiles(_root).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Host.UploadRoot));
    }

    [Fact]
    public void FollowSymlinksIsExplicitlyUnsupportedAndRootPathConfinesOrdinaryReads()
    {
        var path = PathOf("file");
        File.WriteAllText(path, "abc");
        Assert.Equal("UNSUPPORTED_FILE_TYPE", Run("file.fetch", new { path, followSymlinks = true }).Outcome.Code);
        Good(Run("file.fetch", new { path, rootPath = _root }));
        Directory.CreateDirectory(PathOf("other"));
        Assert.Equal("PERMISSION_DENIED", Run("file.fetch", new { path, rootPath = PathOf("other") }).Outcome.Code);
    }

    [Fact]
    public void OversizedCreateMessageCannotPublishOrLeaveScratch()
    {
        var path = PathOf("large");
        var preflight = Run("file.create", new { path, sizeBytes = 2 * 1024 * 1024, expectedSha256 = AbcSha, preflightOnly = true });
        Good(preflight);
        var result = Run("file.create", new { path, sizeBytes = 2 * 1024 * 1024, expectedSha256 = AbcSha, expectedBinding = Binding(preflight) },
            new Messages([new byte[1024 * 1024 + 1]]));
        Assert.Equal("FILE_TOO_LARGE", result.Outcome.Code);
        Assert.True(result.InvalidRequest);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void ExistingUnownedUploadRootIsNeverAdoptedOrCleaned()
    {
        var root = Directory.CreateDirectory(Host.UploadRoot).FullName;
        var sentinel = Path.Combine(root, "keep");
        File.WriteAllText(sentinel, "owned by another activity");
        var result = Run("terminal.upload", new { name = "file", contentBase64 = "YWJj" });
        Assert.False(result.Outcome.Ok);
        Assert.Equal("owned by another activity", File.ReadAllText(sentinel));
        Assert.Equal(["keep"], Directory.GetFiles(root).Select(Path.GetFileName));
    }

    [Fact]
    public void UploadLockAllowsMetadataReadersButSerializesWriters()
    {
        Good(Run("terminal.upload", new { name = "first", contentBase64 = "" }));
        using (var scanner = File.Open(Path.Combine(Host.UploadRoot, ".openclaw-lock"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Good(Run("terminal.upload", new { name = "second", contentBase64 = "" }));
        using var fs = new RetainedFileSystem(Host);
        using var root = fs.Open(Host.UploadRoot);
        using var gate = fs.OpenChild(root, ".openclaw-lock", write: true);
        Native.LockUploadRoot(gate.Handle);
        Assert.Equal("BUSY", Run("terminal.upload", new { name = "third", contentBase64 = "" }).Outcome.Code);
    }

    [Fact]
    public void ReadingAnUnexpiredUploadDoesNotRequireGrantingCleanupDeleteSharing()
    {
        var first = Run("terminal.upload", new { name = "first", contentBase64 = "YWJj" });
        Good(first);
        using var consumer = File.Open(first.Payload.GetProperty("path").GetString()!, FileMode.Open, FileAccess.Read, FileShare.Read);
        Good(Run("terminal.upload", new { name = "second", contentBase64 = "YQ==" }));
        Assert.Equal(2, Directory.GetDirectories(Host.UploadRoot).Length);
    }

    [Fact]
    public void UploadCountQuotaIsDiskAuthoritativeAcrossWorkerInstances()
    {
        for (var i = 0; i < 64; i++)
            Good(Run("terminal.upload", new { name = "file", contentBase64 = "" }));
        var full = Run("terminal.upload", new { name = "file", contentBase64 = "" });
        Assert.Equal("UPLOAD_QUOTA", full.Outcome.Code);
        Assert.Equal(64, Directory.GetDirectories(Host.UploadRoot).Length);
    }

    [Fact]
    public void UploadByteQuotaUsesActualRetainedSizeRatherThanClientMetadata()
    {
        var first = Run("terminal.upload", new { name = "file", contentBase64 = "" });
        Good(first);
        var path = first.Payload.GetProperty("path").GetString()!;
        using (var file = File.OpenWrite(path)) file.SetLength(256L * 1024 * 1024);
        var full = Run("terminal.upload", new { name = "next", contentBase64 = "YQ==" });
        Assert.Equal("UPLOAD_QUOTA", full.Outcome.Code);
        Assert.Single(Directory.GetDirectories(Host.UploadRoot));
    }

    [Fact]
    public void StatAndUnaryFetchBindTheActualHandleAndReturnLiteralDigest()
    {
        var path = PathOf("plain");
        File.WriteAllBytes(path, "abc"u8.ToArray());
        var stat = Run("file.stat", new { path });
        Good(stat);
        Assert.Equal("file", stat.Payload.GetProperty("type").GetString());
        Assert.Equal(3, stat.Payload.GetProperty("size").GetInt64());
        var binding = Binding(stat);
        Assert.True(UInt128.TryParse(binding.Inode, out _));
        Assert.True(ulong.TryParse(binding.Device, out _));
        var fetch = Run("file.fetch", new { path, expectedBinding = binding });
        Good(fetch);
        Assert.Equal("YWJj", fetch.Payload.GetProperty("base64").GetString());
        Assert.Equal(AbcSha, fetch.Payload.GetProperty("sha256").GetString());
        Assert.Equal("text/plain", fetch.Payload.GetProperty("mimeType").GetString());
    }

    [Fact]
    public void ChangedReadBindingIsDeniedBeforeReturningBytes()
    {
        var path = PathOf("file");
        File.WriteAllText(path, "abc");
        var old = Binding(Run("file.stat", new { path }));
        File.Move(path, PathOf("old"));
        File.WriteAllText(path, "abc");
        var result = Run("file.fetch", new { path, expectedBinding = old });
        Assert.Equal("CANONICAL_PATH_CHANGED", result.Outcome.Code);
        Assert.False(result.Payload.TryGetProperty("base64", out _));
    }

    [Fact]
    public void WriteHashVerifiesNewBytesAndOverwritePreservesIdentity()
    {
        var path = PathOf("overwrite");
        File.WriteAllText(path, "old content to truncate");
        var before = Binding(Run("file.stat", new { path }));
        var invalid = Run("file.write", new { path, contentBase64 = "YWJj", overwrite = true, expectedSha256 = EmptySha });
        Assert.Equal("INTEGRITY_FAILURE", invalid.Outcome.Code);
        Assert.Equal("old content to truncate", File.ReadAllText(path));
        var result = Run("file.write", new { path, contentBase64 = "YWJj", overwrite = true, expectedSha256 = AbcSha });
        Good(result);
        Assert.Equal(before, Binding(result));
        Assert.Equal("abc", File.ReadAllText(path));
        Assert.Equal("EXISTS_NO_OVERWRITE", Run("file.write", new { path, contentBase64 = "" }).Outcome.Code);
    }

    [Fact]
    public void PreflightDoesNotCreateParentsOrSubscribeAndWriteBindingPinsAnchor()
    {
        var path = PathOf(@"missing\file");
        var preflight = Run("file.create", new { path, sizeBytes = 3, expectedSha256 = AbcSha, createParents = true, preflightOnly = true });
        Good(preflight);
        Assert.False(Directory.Exists(PathOf("missing")));
        var binding = Binding(preflight);
        Assert.Equal(_root, binding.AnchorPath);
        var messages = new Messages(["abc"u8.ToArray(), []]);
        var create = Run("file.create", new { path, sizeBytes = 3, expectedSha256 = AbcSha, createParents = true, expectedBinding = binding }, messages);
        Good(create);
        Assert.Equal("created", create.Payload.GetProperty("status").GetString());
        Assert.Equal("abc", File.ReadAllText(path));
        Assert.Equal(1, messages.Listens);
        Assert.Equal(["ack"], messages.Output.Select(Encoding.UTF8.GetString));
    }

    [Fact]
    public void StreamingMismatchLeavesNoFinalNameOrParentMutation()
    {
        var path = PathOf(@"missing\file");
        var preflight = Run("file.create", new { path, sizeBytes = 3, expectedSha256 = AbcSha, createParents = true, preflightOnly = true });
        Good(preflight);
        var result = Run("file.create", new { path, sizeBytes = 3, expectedSha256 = AbcSha, createParents = true, expectedBinding = Binding(preflight) },
            new Messages(["xyz"u8.ToArray(), []]));
        Assert.Equal("INTEGRITY_FAILURE", result.Outcome.Code);
        Assert.True(result.InvalidRequest);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void CreateExistingConsumesAndVerifiesInputWithoutClaimingExistingHash()
    {
        var path = PathOf("exists");
        File.WriteAllText(path, "different");
        var preflight = Run("file.create", new { path, sizeBytes = 3, expectedSha256 = AbcSha, preflightOnly = true });
        Good(preflight);
        var result = Run("file.create", new { path, sizeBytes = 3, expectedSha256 = AbcSha, expectedBinding = Binding(preflight) },
            new Messages(["abc"u8.ToArray(), []]));
        Good(result);
        Assert.Equal("exists", result.Payload.GetProperty("status").GetString());
        Assert.False(result.Payload.TryGetProperty("sha256", out _));
        Assert.False(result.Payload.TryGetProperty("size", out _));
        Assert.Equal("different", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateExistingRejectsDestinationRemovedOrReplacedDuringReception(bool replace)
    {
        var path = PathOf("exists");
        var moved = PathOf("moved");
        File.WriteAllText(path, "original");
        var preflight = Run("file.create", new { path, sizeBytes = 3, expectedSha256 = AbcSha, preflightOnly = true });
        Good(preflight);
        var messages = new Messages(["abc"u8.ToArray(), []], () =>
        {
            File.Move(path, moved);
            if (replace) File.WriteAllText(path, "replacement");
        });
        var result = Run("file.create", new { path, sizeBytes = 3, expectedSha256 = AbcSha, expectedBinding = Binding(preflight) }, messages);
        Assert.False(result.Outcome.Ok);
        Assert.Equal("CANONICAL_PATH_CHANGED", result.Outcome.Code);
        Assert.Equal("original", File.ReadAllText(moved));
        if (replace) Assert.Equal("replacement", File.ReadAllText(path));
        else Assert.False(File.Exists(path));
        Assert.DoesNotContain(Directory.GetFiles(_root), file => Path.GetFileName(file).StartsWith(".openclaw-write-", StringComparison.Ordinal));
    }

    [Fact]
    public void ContainedWriteToTopLevelDirectoryDoesNotResolveItAsItsOwnParent()
    {
        var volume = Path.GetPathRoot(_root)!;
        var separator = _root.IndexOf('\\', volume.Length);
        var directory = _root[..separator];
        var admission = Host with { ExecutionMode = "sandbox", Reason = null, DataAccess = new([], [directory], [], []) };
        using var fs = new RetainedFileSystem(admission);
        // Inspect resolution only. Never create a child under the shared traversal anchor.
        var error = Assert.Throws<FileCommandException>(() => fs.ResolveWrite(directory, createParents: false));
        Assert.Equal("IS_DIRECTORY", error.Code);
    }

    [Fact]
    public void EmptyCreateRequiresAndAcceptsAnEmptyEofMessage()
    {
        var path = PathOf("empty");
        var preflight = Run("file.create", new { path, sizeBytes = 0, maxBytes = 0, expectedSha256 = EmptySha, preflightOnly = true });
        Good(preflight);
        var result = Run("file.create", new { path, sizeBytes = 0, maxBytes = 0, expectedSha256 = EmptySha, expectedBinding = Binding(preflight) },
            new Messages([[]]));
        Good(result);
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void BinaryFetchWaitsForStartAndEachAck()
    {
        var path = PathOf("binary");
        File.WriteAllBytes(path, "abc"u8.ToArray());
        var stat = Run("file.stat", new { path });
        var messages = new Messages(["start"u8.ToArray(), "ack"u8.ToArray()]);
        var result = Run("file.fetch", new { path, transport = "binary", maxBytes = 3, expectedBinding = Binding(stat) }, messages);
        Good(result);
        Assert.Equal("abc", Encoding.UTF8.GetString(Assert.Single(messages.Output)));
        Assert.Equal(AbcSha, result.Payload.GetProperty("sha256").GetString());
        Assert.Equal("", result.Payload.GetProperty("base64").GetString());
        Assert.Equal("INVALID_PARAMS", Run("file.fetch", new { path, transport = "binary", maxBytes = 3, expectedBinding = Binding(stat) },
            new Messages(["ack"u8.ToArray()])).Outcome.Code);
    }

    [Fact]
    public void DirectoryPreflightAndDecimalPagingUseDistinctWireFields()
    {
        File.WriteAllText(PathOf("a.txt"), "a");
        File.WriteAllText(PathOf("b.txt"), "b");
        Directory.CreateDirectory(PathOf("c"));
        var preflight = Run("dir.list", new { path = _root, preflightOnly = true });
        Good(preflight);
        Assert.True(preflight.Payload.GetProperty("preflight").GetBoolean());
        Assert.False(preflight.Payload.TryGetProperty("preflightOnly", out _));
        var page = Run("dir.list", new { path = _root, maxEntries = 1, pageToken = "1" });
        Good(page);
        Assert.Equal("b.txt", page.Payload.GetProperty("entries")[0].GetProperty("name").GetString());
        Assert.Equal("2", page.Payload.GetProperty("nextPageToken").GetString());
        var admin = Run("fs.listDir", new { path = _root });
        Good(admin);
        Assert.Equal(["entries", "home", "parent", "path"], admin.Payload.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("c", Assert.Single(admin.Payload.GetProperty("entries").EnumerateArray()).GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("dir.list")]
    [InlineData("fs.listDir")]
    public void AllowedDirectoryListingOmitsDeniedAndProtectedChildren(string command)
    {
        var allowed = Directory.CreateDirectory(PathOf("allowed")).FullName;
        var readOnly = Directory.CreateDirectory(PathOf("readonly")).FullName;
        var denied = Directory.CreateDirectory(PathOf(".ssh")).FullName;
        var protectedPath = Directory.CreateDirectory(PathOf(".openclaw")).FullName;
        var deniedFile = PathOf("blocked.txt");
        File.WriteAllText(deniedFile, "keep private");
        File.WriteAllText(PathOf("visible.txt"), "visible");
        var data = new FileDataAccess([readOnly], [_root, protectedPath], [denied, deniedFile], [protectedPath]);
        var admission = Host with { ExecutionMode = "sandbox", Reason = null, DataAccess = data };

        var listing = Run(command, new { path = _root }, admission: admission);
        Good(listing);
        string[] expected = command == "dir.list" ? ["allowed", "readonly", "visible.txt"] : ["allowed", "readonly"];
        Assert.Equal(expected, listing.Payload.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString()).ToArray());
        Good(Run(command, new { path = allowed }, admission: admission));
        Good(Run(command, new { path = readOnly }, admission: admission));
        Assert.Equal("PERMISSION_DENIED", Run(command, new { path = denied }, admission: admission).Outcome.Code);
        Assert.Equal("PERMISSION_DENIED", Run(command, new { path = protectedPath }, admission: admission).Outcome.Code);
        Assert.Equal("PERMISSION_DENIED", Run("file.stat", new { path = deniedFile }, admission: admission).Outcome.Code);
        Assert.Equal("PERMISSION_DENIED", Run(command, new { path = _root }, admission: admission with
            { DataAccess = data with { ProtectedPaths = [_root] } }).Outcome.Code);
        Assert.Equal("keep private", File.ReadAllText(deniedFile));
    }

    [Fact]
    public void DirectoryPagingCountsOnlyVisibleChildren()
    {
        var deniedFile = PathOf("00-denied.txt");
        File.WriteAllText(deniedFile, "private");
        File.WriteAllText(PathOf("01-visible.txt"), "one");
        var deniedDirectory = Directory.CreateDirectory(PathOf("02-denied")).FullName;
        Directory.CreateDirectory(PathOf("03-visible"));
        var protectedDirectory = Directory.CreateDirectory(PathOf("04-protected")).FullName;
        File.WriteAllText(PathOf("05-visible.txt"), "five");
        var admission = Host with { ExecutionMode = "sandbox", Reason = null,
            DataAccess = new([], [_root], [deniedFile, deniedDirectory], [protectedDirectory]) };
        string[] expected = ["01-visible.txt", "03-visible", "05-visible.txt"];
        string? token = null;
        for (var index = 0; index < expected.Length; index++)
        {
            var result = Run("dir.list", new { path = _root, maxEntries = 1, pageToken = token }, admission: admission);
            Good(result);
            Assert.Equal(expected[index], Assert.Single(result.Payload.GetProperty("entries").EnumerateArray())
                .GetProperty("name").GetString());
            var more = index < expected.Length - 1;
            Assert.Equal(more, result.Payload.GetProperty("truncated").GetBoolean());
            if (more)
            {
                token = result.Payload.GetProperty("nextPageToken").GetString();
                Assert.Equal((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), token);
            }
            else Assert.False(result.Payload.TryGetProperty("nextPageToken", out _));
        }
    }

    [Fact]
    public void ArchiveIsGzipTarWithOneRootAndDeclaredContent()
    {
        File.WriteAllText(PathOf("a.txt"), "abc");
        Directory.CreateDirectory(PathOf("folder"));
        var result = Run("dir.fetch", new { path = _root });
        Good(result);
        using var bytes = new MemoryStream(Convert.FromBase64String(result.Payload.GetProperty("tarBase64").GetString()!));
        using var gzip = new GZipStream(bytes, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var entries = new List<string>();
        while (tar.GetNextEntry() is { } entry)
        {
            entries.Add(entry.Name);
            if (entry.EntryType == TarEntryType.RegularFile)
            {
                using var text = new StreamReader(entry.DataStream!);
                Assert.Equal("abc", text.ReadToEnd());
            }
        }
        Assert.Equal(["./", "./a.txt", "./folder/"], entries);
        Assert.Equal(2, result.Payload.GetProperty("fileCount").GetInt32());
    }

    [Fact]
    public void ContainedDataPolicyRejectsDeniedDescendantsAndAllowsExplicitChildren()
    {
        var parent = Directory.CreateDirectory(PathOf("denied")).FullName;
        var allowed = Directory.CreateDirectory(Path.Combine(parent, "allowed")).FullName;
        File.WriteAllText(Path.Combine(allowed, "file"), "abc");
        var data = new FileDataAccess([], [_root, allowed], [parent], []);
        var admission = Host with { ExecutionMode = "sandbox", Reason = null, DataAccess = data };
        Assert.Equal("PERMISSION_DENIED", Run("file.stat", new { path = parent }, admission: admission).Outcome.Code);
        Good(Run("file.fetch", new { path = Path.Combine(allowed, "file") }, admission: admission));
        Assert.Equal("PERMISSION_DENIED", Run("dir.fetch", new { path = _root }, admission: admission).Outcome.Code);
        var protectedAdmission = admission with { DataAccess = data with { ProtectedPaths = [parent] } };
        Assert.Equal("PERMISSION_DENIED", Run("file.fetch", new { path = Path.Combine(allowed, "file") }, admission: protectedAdmission).Outcome.Code);
        Assert.Equal("PERMISSION_DENIED", Run("file.stat", new { path = _root }, admission: admission with
        { DataAccess = new([], [], [], []) }).Outcome.Code);
    }

    [Fact]
    public void HostCompatibilityDoesNotRequireInactiveFolderGrantsAndInvalidModeFailsClosed()
    {
        File.WriteAllText(PathOf("file"), "abc");
        Good(Run("file.fetch", new { path = PathOf("file") }));
        Assert.Equal("SANDBOX_UNAVAILABLE", Run("file.fetch", new { path = PathOf("file") },
            admission: Host with { Reason = null }).Outcome.Code);
        Assert.Equal("SANDBOX_UNAVAILABLE", Run("file.fetch", new { path = PathOf("file") },
            admission: Host with { ExecutionMode = "sandbox" }).Outcome.Code);
    }

    [Fact]
    public void RetainedAncestorsRefuseRenameAndFileHardlinkAliasesAreNotContainedData()
    {
        var path = PathOf("file");
        File.WriteAllText(path, "abc");
        using (var fs = new RetainedFileSystem(Host))
        {
            _ = fs.Open(path, data: true);
            Assert.Throws<IOException>(() => File.Move(path, PathOf("moved")));
            Assert.Throws<IOException>(() => Directory.Move(_root, _root + "-moved"));
        }
        Assert.True(CreateHardLinkW(PathOf("alias"), path, IntPtr.Zero));
        Good(Run("file.fetch", new { path }));
        var admission = Host with { ExecutionMode = "sandbox", Reason = null, DataAccess = new([], [_root], [], []) };
        Assert.Equal("HARDLINK_TARGET_DENIED", Run("file.fetch", new { path }, admission: admission).Outcome.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeEntryRejectsContainedHardlinksButPreservesHostCompatibility(bool useAlias)
    {
        var original = PathOf("entry-original");
        var alias = PathOf("entry-alias");
        File.WriteAllText(original, "abc");
        Assert.True(CreateHardLinkW(alias, original, IntPtr.Zero));
        var path = useAlias ? alias : original;

        using (var handle = OpenEntryFixtureHandle(path))
        using (var entry = new NativeEntry(path, handle, contained: false))
        {
            Assert.False(entry.IsDirectory);
            Assert.Equal(2u, entry.Links);
        }
        using var containedHandle = OpenEntryFixtureHandle(path);
        var error = Assert.Throws<FileCommandException>(() =>
            new NativeEntry(path, containedHandle, contained: true));
        Assert.Equal("HARDLINK_TARGET_DENIED", error.Code);
        Assert.Equal("abc", File.ReadAllText(original));
        Assert.Equal("abc", File.ReadAllText(alias));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeEntryAcceptsContainedSingleLinkFilesAndDirectoryAnchors(bool directory)
    {
        var path = PathOf(directory ? "entry-directory" : "entry-single");
        if (directory) Directory.CreateDirectory(path);
        else File.WriteAllText(path, "abc");

        using var handle = OpenEntryFixtureHandle(path);
        using var entry = new NativeEntry(path, handle, contained: true);
        Assert.Equal(directory, entry.IsDirectory);
        if (!directory) Assert.Equal(1u, entry.Links);
        Assert.Equal(path, entry.Path);
    }

    private static SafeFileHandle OpenEntryFixtureHandle(string path)
    {
        var handle = Native.CreateFileW(@"\\?\" + path,
            Native.ReadAttributes | Native.ReadControl | Native.Synchronize | Native.Traverse,
            FileShare.ReadWrite, IntPtr.Zero, 3, Native.OpenReparsePoint | Native.BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Cannot open the test-owned native entry (Win32 {error}).");
        }
        return handle;
    }

    [Fact]
    public void TrustedBootstrapCreatesOnlyPrivateIdentityBoundMetadataAndWorkerConsumesIt()
    {
        using var lease = ManagedUploadRootLease.Open(Host.UploadRoot);
        Assert.Null(lease.Identity);
        lease.Bootstrap(null, CancellationToken.None);
        Assert.True(lease.Complete);
        Assert.Empty(Directory.GetFileSystemEntries(Host.UploadRoot));
        var acl = new DirectoryInfo(Host.UploadRoot).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        using var owner = WindowsIdentity.GetCurrent();
        Assert.Equal(owner.User, acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule =>
            Assert.True(rule.IdentityReference == owner.User || ((SecurityIdentifier)rule.IdentityReference).IsWellKnown(WellKnownSidType.LocalSystemSid)));
        using (var fs = new RetainedFileSystem(Host))
        using (var parent = fs.Open(_root))
        using (var root = fs.OpenChild(parent, "uploads", ownership: true))
        {
            var record = UploadOwnership.Read(root)!;
            Assert.Equal("root", record.Kind);
            Assert.Equal("retained", record.State);
            Assert.Equal(parent.Device, record.ParentDevice);
            Assert.Equal(parent.Inode, record.ParentInode);
            Assert.Equal(root.Device, record.Device);
            Assert.Equal(root.Inode, record.Inode);
        }
        var upload = Run("terminal.upload", new { name = "payload", contentBase64 = "YWJj" },
            admission: Host with { ExpectedUploadRoot = lease.Identity, ExpectedOperation = FileOperation.Upload });
        Good(upload);
        Assert.Equal(lease.Identity, upload.Retention!.Root);
        Assert.Equal("abc", File.ReadAllText(upload.Payload.GetProperty("path").GetString()!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BootstrapRecoversOnlyBoundEmptyCreatingRoot(bool unbound, bool hasForeignChild)
    {
        UploadRootIdentity identity;
        using (var first = ManagedUploadRootLease.Open(Host.UploadRoot))
        { first.Bootstrap(null, CancellationToken.None); identity = first.Identity!; }
        using (var fs = new RetainedFileSystem(Host))
        using (var parent = fs.Open(_root))
        using (var root = fs.OpenChild(parent, "uploads", ownership: true))
        {
            var record = UploadOwnership.Read(root)! with { State = "creating" };
            if (unbound) record = record with { Device = null, Inode = null };
            record.Save(root);
        }
        if (hasForeignChild) File.WriteAllText(Path.Combine(Host.UploadRoot, "foreign"), "keep");
        using var next = ManagedUploadRootLease.Open(Host.UploadRoot);
        Assert.False(next.Complete);
        if (unbound || hasForeignChild)
        {
            Assert.Throws<FileCommandException>(() => next.Bootstrap(identity, CancellationToken.None));
            Assert.False(next.Complete);
            if (hasForeignChild) Assert.Equal("keep", File.ReadAllText(Path.Combine(Host.UploadRoot, "foreign")));
        }
        else { next.Bootstrap(identity, CancellationToken.None); Assert.True(next.Complete); Assert.Equal(identity, next.Identity); }
    }

    [Fact]
    public void BootstrapCancellationAndMissingRememberedIdentityDoNotCreateAReplacement()
    {
        using var lease = ManagedUploadRootLease.Open(Host.UploadRoot);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => lease.Bootstrap(null, cancel.Token));
        Assert.False(Directory.Exists(Host.UploadRoot));
        Assert.Throws<FileCommandException>(() => lease.Bootstrap(new(Host.UploadRoot, "17", "23"), CancellationToken.None));
        Assert.False(Directory.Exists(Host.UploadRoot));
    }

    [Fact]
    public void ManagedRootReadLeaseRequiresAtomicOwnershipAndPinsAncestryWithoutCreatingStorage()
    {
        using (var missing = ManagedUploadRootLease.Open(Host.UploadRoot))
        {
            Assert.Null(missing.Identity);
            Assert.False(missing.Complete);
            Assert.False(Directory.Exists(Host.UploadRoot));
        }
        var upload = Run("terminal.upload", new { name = "data.txt", contentBase64 = "YWJj" });
        Good(upload);
        using (var lease = ManagedUploadRootLease.Open(Host.UploadRoot))
        {
            Assert.True(lease.Complete);
            Assert.Equal(upload.Retention!.Root, lease.Identity);
            Assert.Throws<IOException>(() => Directory.Move(Host.UploadRoot, PathOf("moved")));
            Assert.Throws<IOException>(() => Directory.Move(_root, _root + "-moved"));
        }
        Directory.Move(Host.UploadRoot, PathOf("moved"));
        using (var fs = new RetainedFileSystem(Host))
        using (var parent = fs.Open(_root))
        using (var replacement = fs.OpenChild(parent, "uploads", write: true, create: true,
            directory: true, privateAcl: true, ownership: true))
        {
            // Establish private ACL and absent ownership independently of the rejection below.
            Native.RequirePrivate(replacement.Handle);
            Assert.Null(UploadOwnership.Read(replacement));
        }
        Assert.Equal("UPLOAD_CLEANUP_BLOCKED", Assert.Throws<FileCommandException>(() =>
            ManagedUploadRootLease.Open(Host.UploadRoot)).Code);
        Assert.True(File.Exists(Path.Combine(PathOf("moved"),
            Path.GetFileName(Path.GetDirectoryName(upload.Payload.GetProperty("path").GetString()!))!, "data.txt")));
    }

    [Fact]
    public void ManagedRootReadLeaseRejectsAReparseAncestorBeforeOpeningItsTarget()
    {
        var target = Directory.CreateDirectory(PathOf("ordinary")).FullName;
        File.WriteAllText(Path.Combine(target, "sentinel"), "keep");
        var alias = PathOf("alias");
        Directory.CreateSymbolicLink(alias, target);
        Assert.Equal("UPLOAD_CLEANUP_BLOCKED", Assert.Throws<FileCommandException>(() =>
            ManagedUploadRootLease.Open(Path.Combine(alias, "uploads"))).Code);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "sentinel")));
        Assert.False(Directory.Exists(Path.Combine(target, "uploads")));
    }

    [Fact]
    public void ManagedRootReadLeaseRejectsOwnershipBoundToAnotherNativeIdentity()
    {
        Good(Run("terminal.upload", new { name = "data", contentBase64 = "YWJj" }));
        using (var fs = new RetainedFileSystem(Host))
        using (var parent = fs.Open(_root))
        using (var writable = fs.OpenChild(parent, "uploads", ownership: true))
            (UploadOwnership.Read(writable)! with { Inode = "1" }).Save(writable);
        Assert.Equal("UPLOAD_CLEANUP_BLOCKED", Assert.Throws<FileCommandException>(() =>
            ManagedUploadRootLease.Open(Host.UploadRoot)).Code);
    }

    [Theory]
    [InlineData(FileOperation.Write)]
    [InlineData(FileOperation.Create)]
    [InlineData(FileOperation.Fetch)]
    [InlineData(FileOperation.ReapUploads)]
    public void UploadAdmissionCannotBeReusedForAnotherWorkerOperation(FileOperation operation)
    {
        var path = PathOf("unchanged");
        File.WriteAllText(path, "keep");
        var result = new FileWorkerEngine().Execute(Host with { ExpectedOperation = FileOperation.Upload },
            new(operation, path, ContentBase64: "YWJj", Overwrite: true));
        Assert.Equal("PERMISSION_DENIED", result.Outcome.Code);
        Assert.Equal("keep", File.ReadAllText(path));
        Assert.False(Directory.Exists(Host.UploadRoot));
    }

    [Fact]
    public void OrdinaryReadAdmissionCannotSwitchToUploadEvenWithBroadDataRights()
    {
        var command = FileCommandParser.Parse("terminal.upload", FileJson.Element(new { name = "data", contentBase64 = "YWJj" }));
        var result = new FileWorkerEngine().Execute(Host with { ExpectedOperation = FileOperation.Fetch }, command);
        Assert.Equal("PERMISSION_DENIED", result.Outcome.Code);
        Assert.False(Directory.Exists(Host.UploadRoot));
    }

    [Fact]
    public void UploadIsPrivateSanitizedRetainedAndReapedThroughCurrentPolicy()
    {
        var result = Run("terminal.upload", new { name = "..\\a!%.txt", contentBase64 = "YWJj" });
        Good(result);
        var path = result.Payload.GetProperty("path").GetString()!;
        Assert.Equal("a__.txt", Path.GetFileName(path));
        Assert.Equal("abc", File.ReadAllText(path));
        Assert.Equal(["path", "size"], result.Payload.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(1, result.Retention!.Count);
        var denied = Host with { ExecutionMode = "sandbox", Reason = null, DataAccess = new([], [], [], []) };
        Assert.Equal("PERMISSION_DENIED", new FileWorkerEngine().Execute(denied, new(FileOperation.ReapUploads, "")).Outcome.Code);
        Assert.True(File.Exists(path));
        using (var fs = new RetainedFileSystem(Host))
        using (var root = fs.Open(Host.UploadRoot))
        using (var directory = fs.OpenChild(root, Path.GetFileName(Path.GetDirectoryName(path)!), ownership: true))
            (UploadOwnership.Read(directory)! with { Expires = DateTimeOffset.UtcNow - TimeSpan.FromDays(2) }).Save(directory);
        var reaped = new FileWorkerEngine().Execute(Host with { ExpectedUploadRoot = result.Retention.Root }, new(FileOperation.ReapUploads, ""));
        Good(reaped);
        Assert.Equal(0, reaped.Retention!.Count);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("root-created")]
    [InlineData("directory-created")]
    [InlineData("payload-created")]
    [InlineData("payload-bound")]
    [InlineData("payload-partial")]
    [InlineData("deleting")]
    [InlineData("payload-deleted")]
    public void AtomicNativeOwnershipRecoversInterruptedUploadStatesWithoutManagedRollback(string phase)
    {
        using (var fs = new RetainedFileSystem(Host))
        using (var parent = fs.Open(_root))
        {
            var rootRecord = UploadOwnership.Creating(parent, "uploads", "root");
            using var root = fs.OpenChild(parent, "uploads", write: true, create: true, directory: true,
                privateAcl: true, ownership: true, initialOwnership: rootRecord.Bytes());
            if (phase != "root-created")
            {
                (rootRecord.Bind(root) with { State = "retained" }).Save(root);
                var name = "openclaw-terminal-upload-" + Guid.NewGuid().ToString("N");
                var creating = UploadOwnership.Creating(root, name, "upload", "data");
                using var directory = fs.OpenChild(root, name, write: true, create: true, directory: true,
                    privateAcl: true, ownership: true, initialOwnership: creating.Bytes());
                if (phase != "directory-created")
                {
                    var record = creating.Bind(directory);
                    record.Save(directory);
                    using var file = fs.OpenChild(directory, "data", write: true, create: true, privateAcl: true, deleteOnClose: true);
                    if (phase != "payload-created")
                    {
                        record = record with { FileDevice = file.Device, FileInode = file.Inode, Expires = DateTimeOffset.UtcNow.AddDays(-1) };
                        record.Save(directory);
                        file.CancelKernelDeleteOnClose();
                        if (phase is "payload-partial" or "deleting" or "payload-deleted") file.Write("partial"u8, 0);
                        if (phase is "deleting" or "payload-deleted") (record with { State = "deleting" }).Save(directory);
                        if (phase == "payload-deleted") file.Delete();
                    }
                    // Closing a parent first reproduces the kernel ordering that
                    // defeats directory DELETE_ON_CLOSE. No UploadStore finally runs.
                    directory.Handle.Dispose();
                    root.Handle.Dispose();
                    file.Handle.Dispose();
                }
            }
        }
        var result = Run("terminal.upload", new { name = "next", contentBase64 = "YWJj" });
        Good(result);
        Assert.Equal(1, result.Retention!.Count);
        Assert.Single(Directory.GetDirectories(Host.UploadRoot));
        Assert.Equal("abc", File.ReadAllText(result.Payload.GetProperty("path").GetString()!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedCleanupPreservesUnknownOrReplacedPayloads(bool replace)
    {
        var upload = Run("terminal.upload", new { name = "data", contentBase64 = "YWJj" });
        Good(upload);
        var path = upload.Payload.GetProperty("path").GetString()!;
        using (var fs = new RetainedFileSystem(Host))
        using (var root = fs.Open(Host.UploadRoot))
        using (var directory = fs.OpenChild(root, Path.GetFileName(Path.GetDirectoryName(path)!), ownership: true))
            (UploadOwnership.Read(directory)! with { State = "deleting" }).Save(directory);
        if (replace)
        {
            File.Move(path, PathOf("original"));
            using var fs = new RetainedFileSystem(Host);
            using var directory = fs.Open(Path.GetDirectoryName(path)!);
            using var replacement = fs.OpenChild(directory, "data", write: true, create: true, privateAcl: true);
            replacement.Write("replacement"u8, 0);
            replacement.Finish(11);
        }
        else File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "unknown"), "keep");
        Assert.Equal("UPLOAD_CLEANUP_BLOCKED", Run("terminal.upload", new { name = "next", contentBase64 = "YWJj" }).Outcome.Code);
        Assert.Equal(replace ? "replacement" : "abc", File.ReadAllText(path));
        if (!replace) Assert.Equal("keep", File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "unknown")));
    }

    [Fact]
    public void CancelledRecoveryLeavesIdentityBackedStateForTheNextAdmission()
    {
        var upload = Run("terminal.upload", new { name = "data", contentBase64 = "YWJj" });
        Good(upload);
        using (var fs = new RetainedFileSystem(Host))
        using (var root = fs.Open(Host.UploadRoot))
        using (var directory = fs.OpenChild(root, Path.GetFileName(Path.GetDirectoryName(upload.Payload.GetProperty("path").GetString()!)!), ownership: true))
            (UploadOwnership.Read(directory)! with { State = "creating" }).Save(directory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new FileWorkerEngine().Execute(Host, new(FileOperation.ReapUploads, ""),
            cancellationToken: cancellation.Token));
        var recovered = new FileWorkerEngine().Execute(Host, new(FileOperation.ReapUploads, ""));
        Good(recovered);
        Assert.Equal(0, recovered.Retention!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AtomicCreationMarkersDoNotAuthorizeUnrelatedOrReplacedDirectories(bool replacedRoot)
    {
        var upload = Run("terminal.upload", new { name = "data", contentBase64 = "YWJj" });
        Good(upload);
        var original = replacedRoot ? Host.UploadRoot : Path.GetDirectoryName(upload.Payload.GetProperty("path").GetString()!)!;
        Directory.Move(original, original + "-owned");
        using (var fs = new RetainedFileSystem(Host))
        using (var parent = fs.Open(Path.GetDirectoryName(original)!))
        using (var unknown = fs.OpenChild(parent, Path.GetFileName(original), write: true, create: true, directory: true, privateAcl: true))
        using (var sentinel = fs.OpenChild(unknown, "keep", write: true, create: true, privateAcl: true))
            sentinel.Write("unknown"u8, 0);
        var result = new FileWorkerEngine().Execute(Host with { ExpectedUploadRoot = upload.Retention!.Root }, new(FileOperation.ReapUploads, ""));
        Assert.False(result.Outcome.Ok);
        Assert.Equal("unknown", File.ReadAllText(Path.Combine(original, "keep")));
        Assert.True(Directory.Exists(original + "-owned"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteLegacyRecordsMigrateBeforeReapingAndSurviveInterruptedMarkerDeletion(bool interrupted)
    {
        string payload;
        using (var fs = new RetainedFileSystem(Host))
        using (var parent = fs.Open(_root))
        using (var root = fs.OpenChild(parent, "uploads", create: true, write: true, directory: true, privateAcl: true))
        {
            using (var marker = fs.OpenChild(root, ".openclaw-root.json", create: true, write: true, privateAcl: true))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new UploadRootIdentity(root.Path, root.Device, root.Inode), FileJson.Options);
                marker.Write(bytes, 0);
                marker.Finish(bytes.Length);
            }
            using var directory = fs.OpenChild(root, "openclaw-terminal-upload-" + Guid.NewGuid().ToString("N"),
                create: true, write: true, directory: true, privateAcl: true);
            using var file = fs.OpenChild(directory, "data", create: true, write: true, privateAcl: true);
            file.Write("abc"u8, 0);
            file.Finish(3);
            payload = file.Path;
            using var manifest = fs.OpenChild(directory, ".openclaw-upload.json", create: true, write: true, privateAcl: true);
            var content = JsonSerializer.SerializeToUtf8Bytes(new
            {
                directoryDevice = directory.Device, directoryInode = directory.Inode, fileName = "data",
                fileDevice = file.Device, fileInode = file.Inode, expires = DateTimeOffset.UtcNow.AddDays(1)
            });
            manifest.Write(content, 0);
            manifest.Finish(content.Length);
        }
        var migrated = new FileWorkerEngine().Execute(Host, new(FileOperation.ReapUploads, ""));
        Good(migrated);
        Assert.Equal(1, migrated.Retention!.Count);
        using (var fs = new RetainedFileSystem(Host))
        using (var root = fs.Open(Host.UploadRoot))
        using (var directory = fs.OpenChild(root, Path.GetFileName(Path.GetDirectoryName(payload)!), ownership: true))
        {
            var ownership = UploadOwnership.Read(directory)!;
            Assert.NotNull(ownership.LegacyMarkerInode);
            (ownership with { State = interrupted ? "deleting" : "retained", Expires = DateTimeOffset.UtcNow.AddDays(-1) }).Save(directory);
        }
        if (interrupted)
        {
            File.Delete(payload);
            File.Delete(Path.Combine(Path.GetDirectoryName(payload)!, ".openclaw-upload.json"));
        }
        var result = new FileWorkerEngine().Execute(Host, new(FileOperation.ReapUploads, ""));
        Good(result);
        Assert.Equal(0, result.Retention!.Count);
        Assert.False(Directory.Exists(Path.GetDirectoryName(payload)!));
    }

    [Theory]
    [InlineData(@"C:\a\b:stream")]
    [InlineData(@"\\server\share\file")]
    [InlineData(@"\\?\C:\file")]
    [InlineData(@"C:\a\..\file")]
    [InlineData(@"C:\a\NUL.txt")]
    [InlineData(@"C:\a\file. ")]
    public void UnsafeNamespacesAreRejectedBeforeOpening(string path) =>
        Assert.Equal("INVALID_PATH", Run("file.stat", new { path }).Outcome.Code);

    private sealed class Messages(IEnumerable<byte[]> incoming, Action? beforeReceive = null) : IWorkerMessages
    {
        private readonly Queue<byte[]> _incoming = new(incoming);
        private Action? _beforeReceive = beforeReceive;
        public readonly List<byte[]> Output = [];
        public int Listens { get; private set; }
        public void Listen() => Listens++;
        public byte[] Receive()
        {
            Assert.Equal(1, Listens);
            Interlocked.Exchange(ref _beforeReceive, null)?.Invoke();
            return _incoming.Dequeue();
        }
        public void Send(ReadOnlySpan<byte> message) { Assert.Equal(1, Listens); Output.Add(message.ToArray()); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string newPath, string existingPath, IntPtr security);
}
