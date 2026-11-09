using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.FileTransfer;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;
using OpenClaw.Shared.Tests.Mxc;

namespace OpenClaw.Shared.Tests.FileTransfer;

[Collection("MxcOwnership")]
public sealed partial class InstalledFileWorkerResolverTests : IDisposable
{
    private static readonly TimeSpan ActiveCopyTimeout = TimeSpan.FromSeconds(15);
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
        ".file-resolver-test-data", Guid.NewGuid().ToString("N"));
    private readonly string _payload;
    private readonly string _product;
    public InstalledFileWorkerResolverTests()
    {
        _product = Path.Combine(_root, "profile", "AppData", "Local", "OpenClawTray");
        var rid = RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64";
        using var embedded = typeof(InstalledFileWorkerResolverTests).Assembly.GetManifestResourceStream("OpenClaw.FileWorker.Manifest." + rid)!;
        using var manifest = new MemoryStream();
        embedded.CopyTo(manifest);
        var payloadId = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(manifest.ToArray()))[..32];
        _payload = Path.Combine(_product, "Workers", "FileWorker", rid, payloadId);
        Directory.CreateDirectory(_payload);
        File.WriteAllBytes(Path.Combine(_payload, "payload-manifest.json"), manifest.ToArray());
        foreach (var name in new[] { "OpenClaw.FileWorker.exe", "OpenClaw.FileWorker.runtimeconfig.json", "coreclr.dll" })
            File.WriteAllBytes(Path.Combine(_payload, name), "abc"u8.ToArray());
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
        var parent = Path.GetDirectoryName(_root)!;
        if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
    }
    private InstalledFileWorkerResolver Resolver => new(typeof(InstalledFileWorkerResolverTests).Assembly, _product);
    private string Cache => Path.Combine(_root, "FileWorkerCache");

    [Fact]
    public void NormalUpgradesBeyondEightVersionsReclaimOnlyOwnedIdlePublishedEntriesAcrossResolvers()
    {
        var first = VersionResolver(0);
        var published = new List<string>();
        using (var lease = first.Resolve(Cache)) published.Add(lease.PayloadDirectory);
        for (var version = 1; version <= 12; version++)
        {
            using var lease = VersionResolver(version).Resolve(Cache);
            published.Add(lease.PayloadDirectory);
            Assert.Equal(4, Directory.GetFiles(lease.PayloadDirectory).Length);
            Assert.InRange(Directory.GetDirectories(Cache).Length, 1, 8);
            Assert.True(Directory.GetFiles(Cache, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length) <= 1024L * 1024 * 1024);
        }
        Assert.Equal(13, published.Distinct().Count());
        Assert.Equal(5, published.Count(path => !Directory.Exists(path)));
    }

    private InstalledFileWorkerResolver VersionResolver(int version)
    {
        var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(_payload, "payload-manifest.json"))
            .Replace("\"fixture\"", "\"upgrade-" + version + "\"", StringComparison.Ordinal));
        var rid = RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64";
        var payload = Directory.CreateDirectory(Path.Combine(_product, "Workers", "FileWorker", rid,
            Convert.ToHexStringLower(SHA256.HashData(bytes))[..32])).FullName;
        if (!File.Exists(Path.Combine(payload, "payload-manifest.json")))
        {
            File.WriteAllBytes(Path.Combine(payload, "payload-manifest.json"), bytes);
            foreach (var name in new[] { "OpenClaw.FileWorker.exe", "OpenClaw.FileWorker.runtimeconfig.json", "coreclr.dll" })
                File.WriteAllText(Path.Combine(payload, name), "abc");
        }
        return new InstalledFileWorkerResolver(new ManifestAssembly(bytes), _product);
    }

    private sealed class ManifestAssembly(byte[] bytes) : Assembly
    {
        public override Stream GetManifestResourceStream(string name) => new MemoryStream(bytes, writable: false);
    }

    [Fact]
    public async Task RepeatedSuccessfulExecutorsReuseCodeButHaveSeparateScratchAndNoOtherPayloadGrant()
    {
        var context = MxcSyntheticContext.Create(_root);
        var runs = Path.Combine(_root, "runs");
        var otherCode = Directory.CreateDirectory(Path.Combine(Cache, "unrelated-version")).FullName;
        var requests = new List<ContainerRequest>();
        NodeSandboxExecutor Executor() => new(new(new SettingsData
        {
            SandboxCustomFolders = [new() { Path = _root, Access = SandboxFolderAccess.ReadWrite }],
        }), () => context.ProtectedPaths[0],
            () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: request =>
            {
                requests.Add(request);
                return Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess());
            }, probe: _ => MxcAvailabilityTests.BaseProbe(), scratchRoot: runs,
            contextProvider: () => context, fileWorker: Resolver);
        using var first = Executor();
        using var second = Executor();
        var observedCode = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var workload = new FixedFileWorkload<bool>(async (io, _) =>
        {
            var resources = io.Resources!;
            var code = Assert.Single(resources.InfrastructureAccess.ReadonlyPaths, p => p.StartsWith(_root));
            observedCode.Add(code);
            Assert.Equal(SandboxFolderAccess.NoAccess, resources.DataAccess.EffectiveAccess(code));
            Assert.Equal(SandboxFolderAccess.NoAccess, resources.DataAccess.EffectiveAccess(otherCode));
            Assert.Equal(SandboxFolderAccess.NoAccess, MxcFolderPermissions.EffectiveAccess(
                otherCode, resources.ReadonlyPaths, resources.ReadwritePaths, resources.DeniedPaths));
            if (Interlocked.Increment(ref calls) == 3)
            {
                entered.SetResult();
                await release.Task;
            }
            return true;
        }, 10_000);
        Assert.True(await first.RunFileAsync(workload));
        Assert.True(await second.RunFileAsync(workload));
        Assert.Equal(observedCode[0], observedCode[1]);
        var overlapping = first.RunFileAsync(workload);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await second.RunFileAsync(workload));
            var held = requests[2];
            var concurrent = requests[3];
            Assert.NotEqual(held.WorkingDirectory, concurrent.WorkingDirectory);
            Assert.Equal(SandboxFolderAccess.NoAccess, MxcFolderPermissions.EffectiveAccess(
                concurrent.WorkingDirectory!, held.Filesystem!.ReadonlyPaths, held.Filesystem.ReadwritePaths, held.Filesystem.DeniedPaths));
        }
        finally { release.TrySetResult(); await overlapping; }
        Assert.Empty(Directory.GetDirectories(runs));
        Assert.Single(observedCode.Distinct());
        Assert.Equal(2, Directory.GetDirectories(Cache).Length);
    }

    [Fact]
    public void RepeatedCallsAndNewResolversReuseTheSamePublishedNativeFilesWithoutCopying()
    {
        string published;
        (uint Volume, ulong Index) identity;
        using (var first = Resolver.Resolve(Cache))
        {
            published = first.PayloadDirectory;
            identity = NativeIdentity(first.ImagePath);
            Assert.False(MxcRequestBuilder.Within(published, _payload));
        }
        for (var call = 0; call < 4; call++)
        {
            using var next = Resolver.Resolve(Cache);
            Assert.Equal(published, next.PayloadDirectory);
            Assert.Equal(identity, NativeIdentity(next.ImagePath));
            Assert.Equal(4, Directory.GetFiles(published).Length);
        }
        Assert.Single(Directory.GetDirectories(Cache));
        Assert.DoesNotContain(Directory.GetDirectories(Cache), p => Path.GetFileName(p).StartsWith(".preparing-"));
    }

    [Fact]
    public async Task ConcurrentResolversPublishExactlyOneEntryAndRetainIndependentLeases()
    {
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return Resolver.Resolve(Cache);
        })).ToArray();
        start.Set();
        var leases = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Single(leases.Select(l => l.PayloadDirectory).Distinct());
            Assert.Single(Directory.GetDirectories(Cache));
            var runtime = Path.Combine(leases[0].PayloadDirectory, "coreclr.dll");
            leases[0].Dispose();
            Assert.Throws<IOException>(() => File.WriteAllText(runtime, "xyz"));
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    [Fact]
    public async Task ConcurrentProcessesAndAbruptParentRestartReusePublishedIdentityWhileNewVersionGetsDistinctCode()
    {
        string previous;
        using (var lease = Resolver.Resolve(Cache)) previous = lease.PayloadDirectory;
        var product = CreateProcessPayload();
        using var first = StartCacheHost(product);
        using var second = StartCacheHost(product);
        Process? restarted = null;
        try
        {
            var firstPath = await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var secondPath = await second.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(firstPath);
            Assert.NotEqual(previous, firstPath);
            Assert.Equal(firstPath, secondPath);
            var identity = NativeIdentity(Path.Combine(firstPath, "OpenClaw.FileWorker.exe"));
            first.Kill();
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(firstPath, "coreclr.dll"), "tamper"));
            second.StandardInput.Close();
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, second.ExitCode);
            restarted = StartCacheHost(product);
            var restartedPath = await restarted.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(firstPath, restartedPath);
            Assert.Equal(identity, NativeIdentity(Path.Combine(restartedPath!, "OpenClaw.FileWorker.exe")));
            Assert.Equal(2, Directory.GetDirectories(Cache).Length);
            using var oldVersion = Resolver.Resolve(Cache);
            Assert.Equal(previous, oldVersion.PayloadDirectory);
        }
        finally
        {
            foreach (var process in new[] { first, second, restarted }.OfType<Process>())
            {
                if (process.HasExited) continue;
                process.StandardInput.Close();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                finally
                {
                    if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
                }
            }
            restarted?.Dispose();
        }
    }

    [Fact]
    public async Task AbruptDeathDuringRealPreparationNeverPublishesPartialCodeOrAllocatesAnotherPreparation()
    {
        var product = CreateProcessPayload();
        Directory.CreateDirectory(Cache);
        using (var preparing = StartCacheHost(product, "crash-preparation"))
        {
            try
            {
                await preparing.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.NotEqual(0, preparing.ExitCode);
                Assert.Empty(await preparing.StandardOutput.ReadToEndAsync());
            }
            finally
            {
                if (!preparing.HasExited) { preparing.Kill(); await preparing.WaitForExitAsync(); }
            }
        }
        var partial = Assert.Single(Directory.GetDirectories(Cache));
        Assert.StartsWith("win-", Path.GetFileName(partial));
        using (var state = System.Text.Json.JsonDocument.Parse(ReadCacheEa(partial)))
            Assert.Equal("preparing", state.RootElement.GetProperty("state").GetString());
        var files = Directory.GetFiles(partial).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        for (var restart = 0; restart < 2; restart++)
        {
            using var retry = StartCacheHost(product);
            await retry.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, retry.ExitCode);
            Assert.Contains("uncertain preparation", await retry.StandardError.ReadToEndAsync());
            Assert.Equal(partial, Assert.Single(Directory.GetDirectories(Cache)));
            Assert.Equal(files.Keys.Order(), Directory.GetFiles(partial).Select(Path.GetFileName).Order());
            foreach (var (name, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(partial, name!)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveCopyCancellationOrReadFailureRemovesOnlyOwnedPreparationAndRetryPublishes(bool readFailure)
    {
        var product = CreateProcessPayload();
        var resolver = ProcessResolver(product);
        var sourceRuntime = Directory.GetFiles(product, "coreclr.dll", SearchOption.AllDirectories).Single();
        var preparation = ProcessPreparation(product);
        var marker = Path.Combine(_root, "unrelated-marker");
        File.WriteAllText(marker, "preserve");
        var markerIdentity = NativeIdentity(marker);
        using var cancel = new CancellationTokenSource();
        FileStream? blockedSource = null;
        var observation = ObserveActiveCopy(preparation, () =>
        {
            if (readFailure)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The read-lock fixture requires Windows.");
                blockedSource = new FileStream(sourceRuntime, FileMode.Open, FileAccess.Read, FileShare.Read);
                blockedSource.Lock(0, 64L * 1024 * 1024);
            }
            else cancel.Cancel();
        });
        Exception? error;
        try
        {
            if (readFailure)
            {
                var context = MxcSyntheticContext.Create(_root);
                using var executor = new NodeSandboxExecutor(new(new SettingsData()), () => context.ProtectedPaths[0],
                    () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
                    spawn: _ => throw new InvalidOperationException("A failed preparation must never launch a process."),
                    probe: _ => MxcAvailabilityTests.BaseProbe(), scratchRoot: Path.Combine(_root, "runs"),
                    contextProvider: () => context, fileWorker: resolver);
                error = await Record.ExceptionAsync(() => executor.RunFileAsync(
                    new FixedFileWorkload<bool>((_, _) => Task.FromResult(true), 10_000)));
            }
            else error = Record.Exception(() => { using var lease = resolver.Resolve(Cache, cancel.Token); });
            await observation;
        }
        finally { blockedSource?.Dispose(); }
        if (readFailure)
        {
            var failure = Assert.IsType<FileCommandException>(error);
            Assert.Equal("FILE_WORKER_CACHE_PREPARATION_FAILED", failure.Code);
            Assert.Contains("Windows error 33", failure.Message);
        }
        else Assert.IsType<OperationCanceledException>(error);
        Assert.False(Directory.Exists(preparation));
        Assert.Empty(Directory.GetDirectories(Cache));
        Assert.Equal(markerIdentity, NativeIdentity(marker));
        Assert.Equal("preserve", File.ReadAllText(marker));
        using var recovered = resolver.Resolve(Cache);
        Assert.Equal(recovered.PayloadDirectory, Assert.Single(Directory.GetDirectories(Cache)));
        Assert.DoesNotContain(".preparing-", recovered.PayloadDirectory);
        Assert.Equal(64L * 1024 * 1024, new FileInfo(Path.Combine(recovered.PayloadDirectory, "coreclr.dll")).Length);
    }

    [Theory]
    [InlineData("added")]
    [InlineData("replaced")]
    [InlineData("hardlink")]
    [InlineData("reparse")]
    [InlineData("create-obstacle")]
    public async Task TamperFailureRetainsUnprovenPreparationInsteadOfDeletingForeignObjects(string obstacle)
    {
        var product = CreateProcessPayload();
        var resolver = ProcessResolver(product);
        var preparation = ProcessPreparation(product);
        var marker = Path.Combine(_root, "foreign-marker");
        File.WriteAllText(marker, "preserve");
        var markerIdentity = NativeIdentity(marker);
        (uint Volume, ulong Index)? obstacleIdentity = null;
        (uint Volume, ulong Index)? originalIdentity = null;
        var image = Path.Combine(preparation, "OpenClaw.FileWorker.exe");
        var observation = ObserveActiveCopy(preparation, () =>
        {
            originalIdentity = NativeIdentity(image);
            switch (obstacle)
            {
                case "added":
                    File.Copy(marker, Path.Combine(preparation, "foreign"));
                    obstacleIdentity = NativeIdentity(Path.Combine(preparation, "foreign"));
                    break;
                case "replaced":
                    File.Move(image, Path.Combine(_root, "original-image"));
                    File.Copy(marker, image);
                    break;
                case "hardlink": CreateHardLink(Path.Combine(_root, "image-alias"), image); break;
                case "reparse":
                    File.Move(image, Path.Combine(_root, "original-image"));
                    File.CreateSymbolicLink(image, marker);
                    break;
                case "create-obstacle":
                    var directory = Directory.CreateDirectory(Path.Combine(preparation, "payload-manifest.json")).FullName;
                    File.Copy(marker, Path.Combine(directory, "marker"));
                    obstacleIdentity = NativeIdentity(Path.Combine(directory, "marker"));
                    break;
            }
            if (obstacle is "replaced" or "hardlink" or "reparse")
                obstacleIdentity = NativeNoFollowIdentity(image);
        });
        // No cancellation competes with verification. An earlier tamper failure
        // must remain a typed cache failure, not become a later cancellation.
        var error = Record.Exception(() => { using var lease = resolver.Resolve(Cache); });
        await observation;
        var failure = Assert.IsType<FileCommandException>(error);
        Assert.Equal("FILE_WORKER_CACHE_CLEANUP_BLOCKED", failure.Code);
        Assert.Contains("cleanup is blocked", failure.Message);
        Assert.Contains("retained", failure.Message);
        if (obstacle == "create-obstacle")
        {
            Assert.Equal(obstacleIdentity, NativeIdentity(Path.Combine(preparation, "payload-manifest.json", "marker")));
            Assert.Equal("preserve", File.ReadAllText(Path.Combine(preparation, "payload-manifest.json", "marker")));
        }
        if (obstacle is "replaced" or "hardlink" or "reparse")
            Assert.Equal(obstacleIdentity, NativeNoFollowIdentity(image));
        Assert.Equal(preparation, Assert.Single(Directory.GetDirectories(Cache)));
        Assert.True(File.Exists(image));
        Assert.Equal(markerIdentity, NativeIdentity(marker));
        Assert.Equal("preserve", File.ReadAllText(marker));
        if (obstacle == "added")
        {
            Assert.Equal(obstacleIdentity, NativeIdentity(Path.Combine(preparation, "foreign")));
            Assert.Equal("preserve", File.ReadAllText(Path.Combine(preparation, "foreign")));
        }
        if (obstacle is "replaced" or "reparse") Assert.Equal("preserve", File.ReadAllText(image));
        if (obstacle is "replaced" or "reparse")
        {
            Assert.Equal(originalIdentity, NativeIdentity(Path.Combine(_root, "original-image")));
            Assert.Equal("abc", File.ReadAllText(Path.Combine(_root, "original-image")));
        }
        if (obstacle == "hardlink")
        {
            Assert.Equal(originalIdentity, NativeIdentity(Path.Combine(_root, "image-alias")));
            Assert.Equal(originalIdentity, NativeIdentity(image));
            Assert.Equal("abc", File.ReadAllText(Path.Combine(_root, "image-alias")));
            Assert.Equal("abc", File.ReadAllText(image));
        }
        using var state = System.Text.Json.JsonDocument.Parse(ReadCacheEa(preparation));
        Assert.Equal("preparing", state.RootElement.GetProperty("state").GetString());
        var expectedNames = new[] { "OpenClaw.FileWorker.exe", "OpenClaw.FileWorker.runtimeconfig.json", "coreclr.dll", "payload-manifest.json" };
        Assert.Equal((obstacle == "added" ? expectedNames.Append("foreign") : expectedNames).Order(),
            Directory.GetFileSystemEntries(preparation).Select(Path.GetFileName).Order());
        var retained = Directory.GetFiles(preparation).ToDictionary(path => Path.GetFileName(path)!, path => (NativeIdentity(path), File.ReadAllBytes(path)));
        var retry = Assert.Throws<NotSupportedException>(() => resolver.Resolve(Cache));
        Assert.Contains("uncertain preparation", retry.Message);
        Assert.Equal(retained.Keys.Order(), Directory.GetFiles(preparation).Select(Path.GetFileName).Order());
        foreach (var (name, snapshot) in retained)
        {
            Assert.Equal(snapshot.Item1, NativeIdentity(Path.Combine(preparation, name!)));
            Assert.Equal(snapshot.Item2, File.ReadAllBytes(Path.Combine(preparation, name!)));
        }
    }

    [Theory]
    [InlineData("added")]
    [InlineData("locked")]
    [InlineData("published-ea")]
    [InlineData("none")]
    public async Task CancellationBeforeVerificationResumesPreservesItsTokenAndOnlyUnprovenObjects(string obstacle)
    {
        var product = CreateProcessPayload();
        var resolver = ProcessResolver(product);
        var preparation = ProcessPreparation(product);
        var image = Path.Combine(preparation, "OpenClaw.FileWorker.exe");
        var marker = Path.Combine(_root, "foreign-marker");
        File.WriteAllText(marker, "preserve");
        var markerIdentity = NativeIdentity(marker);
        (uint Volume, ulong Index)? imageIdentity = null;
        (uint Volume, ulong Index)? foreignIdentity = null;
        byte[]? expectedEa = null;
        using var cancel = new CancellationTokenSource();
        SafeFileHandle? held = null;
        var observation = ObserveActiveCopy(preparation, () =>
        {
            imageIdentity = NativeIdentity(image);
            held = HoldVerificationOpen(image, obstacle == "locked", () =>
            {
                if (obstacle == "added")
                {
                    File.Copy(marker, Path.Combine(preparation, "foreign"));
                    foreignIdentity = NativeIdentity(Path.Combine(preparation, "foreign"));
                }
                expectedEa = ReadCacheEa(preparation);
                if (obstacle == "published-ea")
                {
                    expectedEa = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(expectedEa)
                        .Replace("\"preparing\"", "\"published\"", StringComparison.Ordinal));
                    WriteCacheEa(preparation, expectedEa);
                }
                cancel.Cancel();
            });
        });
        Exception? error;
        try
        {
            error = Record.Exception(() => { using var lease = resolver.Resolve(Cache, cancel.Token); });
            await observation;
        }
        finally { held?.Dispose(); }
        var failure = Assert.IsType<OperationCanceledException>(error);
        Assert.Equal(cancel.Token, failure.CancellationToken);
        Assert.Equal(markerIdentity, NativeIdentity(marker));
        Assert.Equal("preserve", File.ReadAllText(marker));
        if (obstacle == "none")
        {
            Assert.Null(failure.InnerException);
            Assert.False(Directory.Exists(preparation));
            Assert.Empty(Directory.GetDirectories(Cache));
            using var recovered = resolver.Resolve(Cache);
            Assert.Equal(preparation, recovered.PayloadDirectory);
            Assert.Equal(4, Directory.GetFiles(preparation).Length);
            return;
        }
        var causes = Assert.IsType<AggregateException>(failure.InnerException).InnerExceptions;
        Assert.Equal(2, causes.Count);
        Assert.Equal(cancel.Token, Assert.IsType<OperationCanceledException>(causes[0]).CancellationToken);
        var cleanup = Assert.IsType<NotSupportedException>(causes[1]);
        Assert.Contains(obstacle switch
        {
            "added" => "inventory changed",
            "locked" => "cannot be opened for rollback",
            _ => "no longer provably unpublished",
        }, cleanup.Message);
        Assert.Contains("cleanup is blocked", failure.Message);
        Assert.Contains("retained", failure.Message);
        Assert.Equal(preparation, Assert.Single(Directory.GetDirectories(Cache)));
        Assert.Equal(imageIdentity, NativeIdentity(image));
        Assert.Equal("abc", File.ReadAllText(image));
        Assert.Equal(expectedEa, ReadCacheEa(preparation));
        if (obstacle == "added")
        {
            Assert.Equal(foreignIdentity, NativeIdentity(Path.Combine(preparation, "foreign")));
            Assert.Equal("preserve", File.ReadAllText(Path.Combine(preparation, "foreign")));
        }
        var expectedNames = new[] { "OpenClaw.FileWorker.exe", "OpenClaw.FileWorker.runtimeconfig.json", "coreclr.dll", "payload-manifest.json" };
        Assert.Equal((obstacle == "added" ? expectedNames.Append("foreign") : expectedNames).Order(),
            Directory.GetFiles(preparation).Select(Path.GetFileName).Order());
        var retained = Directory.GetFiles(preparation).ToDictionary(path => Path.GetFileName(path)!, path => (NativeIdentity(path), File.ReadAllBytes(path)));
        var retry = Assert.Throws<NotSupportedException>(() => resolver.Resolve(Cache));
        Assert.Contains(obstacle == "published-ea" ? "ownership changed" : "uncertain preparation", retry.Message);
        Assert.Equal(retained.Keys.Order(), Directory.GetFiles(preparation).Select(Path.GetFileName).Order());
        foreach (var (name, snapshot) in retained)
        {
            Assert.Equal(snapshot.Item1, NativeIdentity(Path.Combine(preparation, name!)));
            Assert.Equal(snapshot.Item2, File.ReadAllBytes(Path.Combine(preparation, name!)));
        }
        Assert.Equal(expectedEa, ReadCacheEa(preparation));
    }

    [Fact]
    public async Task PreparationVetoExistsDuringCopyInLegacyRootAndBlocksCompleteButUnpublishedCode()
    {
        Directory.CreateDirectory(Cache);
        var product = CreateProcessPayload();
        var resolver = ProcessResolver(product);
        var path = ProcessPreparation(product);
        var observation = ObserveActiveCopy(path, () =>
        {
            using var record = System.Text.Json.JsonDocument.Parse(ReadCacheEa(path));
            Assert.Equal("preparing", record.RootElement.GetProperty("state").GetString());
            Assert.False(record.RootElement.TryGetProperty("children", out var children) &&
                children.ValueKind == System.Text.Json.JsonValueKind.Object);
        });
        using (var lease = resolver.Resolve(Cache)) Assert.Equal(path, lease.PayloadDirectory);
        await observation;
        Assert.Empty(ReadCacheEa(Cache));
        var published = Encoding.UTF8.GetString(ReadCacheEa(path));
        WriteCacheEa(path, Encoding.UTF8.GetBytes(published.Replace("\"published\"", "\"preparing\"", StringComparison.Ordinal)));
        Assert.Contains("uncertain preparation", Assert.Throws<NotSupportedException>(() => resolver.Resolve(Cache)).Message);
        Assert.Equal(4, Directory.GetFiles(path).Length);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("replaced-identical")]
    public async Task ChangedPreparationCannotPublishEvenWithoutCancellation(string mutation)
    {
        var product = CreateProcessPayload();
        var resolver = ProcessResolver(product);
        var path = ProcessPreparation(product);
        var image = Path.Combine(path, "OpenClaw.FileWorker.exe");
        var observation = ObserveActiveCopy(path, () =>
        {
            if (mutation == "digest") File.WriteAllText(image, "bad");
            else
            {
                File.Move(image, Path.Combine(_root, "original-apphost"));
                File.WriteAllText(image, "abc");
            }
        });
        var error = Record.Exception(() => { using var lease = resolver.Resolve(Cache); });
        await observation;
        Assert.NotNull(error);
        if (mutation == "digest")
        {
            Assert.Contains("digest changed", error.Message);
            Assert.False(Directory.Exists(path));
        }
        else
        {
            Assert.Equal("FILE_WORKER_CACHE_CLEANUP_BLOCKED", Assert.IsType<FileCommandException>(error).Code);
            using var state = System.Text.Json.JsonDocument.Parse(ReadCacheEa(path));
            Assert.Equal("preparing", state.RootElement.GetProperty("state").GetString());
            Assert.Contains("uncertain preparation", Assert.Throws<NotSupportedException>(() => resolver.Resolve(Cache)).Message);
            Assert.Equal("abc", File.ReadAllText(image));
        }
    }

    [Fact]
    public async Task ReadOnlyChildHandleDoesNotBlockCompletePublicationOrRelaxExecutionPins()
    {
        var product = CreateProcessPayload();
        var resolver = ProcessResolver(product);
        FileStream? held = null;
        var observation = ObserveActiveCopy(ProcessPreparation(product), () =>
        {
            held = new FileStream(Path.Combine(ProcessPreparation(product), "OpenClaw.FileWorker.exe"),
                FileMode.Open, FileAccess.Read, FileShare.Read);
        });
        FixedFileWorkerLease? lease = null;
        Exception? error;
        try
        {
            error = Record.Exception(() => lease = resolver.Resolve(Cache));
            await observation;
            Assert.Null(error);
            Assert.NotNull(lease);
            Assert.NotNull(held);
            Assert.Equal(4, Directory.GetFiles(lease.PayloadDirectory).Length);
            using var receipt = System.Text.Json.JsonDocument.Parse(ReadCacheEa(lease.PayloadDirectory));
            Assert.Equal("published", receipt.RootElement.GetProperty("state").GetString());
            Assert.Equal(Path.GetFileName(lease.PayloadDirectory), receipt.RootElement.GetProperty("name").GetString());
            AssertCacheIdentity(Cache, receipt.RootElement.GetProperty("parent"));
            AssertCacheIdentity(lease.PayloadDirectory, receipt.RootElement.GetProperty("identity"));
            var children = receipt.RootElement.GetProperty("children").EnumerateObject().ToArray();
            Assert.Equal(Directory.GetFiles(lease.PayloadDirectory).Select(Path.GetFileName).Order(), children.Select(c => c.Name).Order());
            foreach (var child in children)
                AssertCacheIdentity(Path.Combine(lease.PayloadDirectory, child.Name), child.Value);
            Assert.Throws<IOException>(() => File.WriteAllText(lease.ImagePath, "tamper"));
            Assert.Throws<IOException>(() => File.Delete(lease.ImagePath));
            held.Dispose();
            Assert.Throws<IOException>(() => File.WriteAllText(lease.ImagePath, "tamper"));
            Assert.Throws<IOException>(() => File.Delete(lease.ImagePath));
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(lease.PayloadDirectory, "coreclr.dll"), "tamper"));
            Assert.Throws<IOException>(() => Directory.Move(lease.PayloadDirectory, Path.Combine(_root, "moved-code")));
        }
        finally { held?.Dispose(); lease?.Dispose(); }
    }

    [Fact]
    public Task CancellationAfterPublicationRetainsPublishedIdentityAndRetryReusesIt() =>
        AssertPublishedReadCompletionAsync(existingCache: false, requestCancellation: true);

    [Fact]
    public Task CancellationDuringPublishedCacheReuseRetainsItsCompleteNativeIdentity() =>
        AssertPublishedReadCompletionAsync(existingCache: true, requestCancellation: true);

    [Fact]
    public Task HeldPublishedReadWithoutCancellationReturnsARealVerifiedLease() =>
        AssertPublishedReadCompletionAsync(existingCache: false, requestCancellation: false);

    private async Task AssertPublishedReadCompletionAsync(bool existingCache, bool requestCancellation)
    {
        // Use the ordinary three-byte payload, not a copy-size scheduling window.
        Directory.CreateDirectory(Cache);
        var path = Path.Combine(Cache, PublishedName());
        Assert.False(Directory.Exists(path));
        if (existingCache)
            using (var warm = Resolver.Resolve(Cache)) Assert.Equal(path, warm.PayloadDirectory);

        using var cancel = new CancellationTokenSource();
        using var reader = new PublishedPayloadReadGate(path, "payload-manifest.json");
        var resolver = new InstalledFileWorkerResolver(typeof(InstalledFileWorkerResolverTests).Assembly, _product, reader);
        var resolving = Task.Factory.StartNew(() => Record.Exception(() =>
        {
            using var lease = resolver.Resolve(Cache, cancel.Token);
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        byte[]? receiptBytes = null;
        (ulong Volume, ulong Low, ulong High) entryIdentity = default;
        Dictionary<string, (ulong Volume, ulong Low, ulong High)>? childIdentities = null;
        Exception? error;
        try
        {
            var first = await Task.WhenAny(reader.Reached.Task, resolving).WaitAsync(ActiveCopyTimeout);
            if (!ReferenceEquals(first, reader.Reached.Task))
            {
                var early = await resolving;
                Assert.Fail("Resolve ended before the actual published-read barrier: " +
                    (early?.ToString() ?? "returned a lease"));
            }
            await reader.Reached.Task;
            Assert.False(resolving.IsCompleted, "The verifier must remain blocked until the test releases its real read.");
            Assert.True(reader.ReadBytes > 0);
            receiptBytes = Assert.IsType<byte[]>(reader.PublishedReceipt);
            Assert.Equal(receiptBytes, ReadCacheEa(path));
            using (var receipt = System.Text.Json.JsonDocument.Parse(receiptBytes))
            {
                var record = receipt.RootElement;
                Assert.Equal(1, record.GetProperty("version").GetInt32());
                Assert.Equal(Path.GetFileName(path), record.GetProperty("name").GetString());
                Assert.Equal("published", record.GetProperty("state").GetString());
                AssertCacheIdentity(Cache, record.GetProperty("parent"));
                AssertCacheIdentity(path, record.GetProperty("identity"));
                var children = record.GetProperty("children").EnumerateObject().ToArray();
                string[] expectedNames = ["OpenClaw.FileWorker.exe", "OpenClaw.FileWorker.runtimeconfig.json",
                    "coreclr.dll", "payload-manifest.json"];
                Assert.Equal(expectedNames.Order(), children.Select(child => child.Name).Order());
                Assert.Equal(expectedNames.Order(), Directory.GetFiles(path).Select(Path.GetFileName).Order());
                foreach (var child in children) AssertCacheIdentity(Path.Combine(path, child.Name), child.Value);
                entryIdentity = ReadFullCacheIdentity(path);
                childIdentities = expectedNames.ToDictionary(name => name,
                    name => ReadFullCacheIdentity(Path.Combine(path, name)), StringComparer.Ordinal);
            }
            Assert.Equal(childIdentities!["payload-manifest.json"], reader.StreamIdentity);
            Assert.NotEqual(ReadFullCacheIdentity(Path.Combine(_payload, "payload-manifest.json")), reader.StreamIdentity);
            Assert.Equal(3L, new FileInfo(Path.Combine(path, "coreclr.dll")).Length);
            // The read adapter must not weaken the real publication/verification pins.
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(path, "coreclr.dll"), "tamper"));
            Assert.Throws<IOException>(() => Directory.Move(path, path + ".moved"));
            if (requestCancellation)
            {
                cancel.Cancel();
                Assert.True(cancel.IsCancellationRequested);
            }
            reader.Release();
            error = await resolving.WaitAsync(ActiveCopyTimeout);
        }
        finally
        {
            reader.Release();
            await resolving.WaitAsync(ActiveCopyTimeout);
        }
        if (requestCancellation)
        {
            var cancellation = Assert.IsType<OperationCanceledException>(error);
            Assert.Equal(cancel.Token, cancellation.CancellationToken);
            // A mistaken rollback can itself be vetoed and wrapped as an OCE.
            // That is not the successful published-retention branch under test.
            Assert.Null(cancellation.InnerException);
        }
        else Assert.Null(error);
        Assert.Equal(path, Assert.Single(Directory.GetDirectories(Cache)));
        Assert.Equal(receiptBytes, ReadCacheEa(path));
        Assert.Equal(entryIdentity, ReadFullCacheIdentity(path));
        Assert.NotNull(childIdentities);
        Assert.Equal(childIdentities.Keys.Order(), Directory.GetFiles(path).Select(Path.GetFileName).Order());
        foreach (var (name, identity) in childIdentities)
            Assert.Equal(identity, ReadFullCacheIdentity(Path.Combine(path, name)));
        using var retry = Resolver.Resolve(Cache); // Default production reader, no retained test gate.
        Assert.Equal(path, retry.PayloadDirectory);
        Assert.Equal(entryIdentity, ReadFullCacheIdentity(path));
        foreach (var (name, identity) in childIdentities)
            Assert.Equal(identity, ReadFullCacheIdentity(Path.Combine(path, name)));
    }

    private static (ulong Volume, ulong Low, ulong High) ReadFullCacheIdentity(string path)
    {
        using var handle = CreateFileW(@"\\?\" + path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        Assert.True(GetFileInformationByHandleEx(handle, 18, out CacheFileIdentity identity, 24));
        return (identity.Volume, identity.Low, identity.High);
    }

    private sealed class PublishedPayloadReadGate(string entryPath, string targetName) : IFileWorkerPayloadReader, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _held;
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[]? PublishedReceipt { get; private set; }
        internal (ulong Volume, ulong Low, ulong High) StreamIdentity { get; private set; }
        internal int ReadBytes { get; private set; }
        public int Read(Stream pinnedStream, Span<byte> buffer)
        {
            // Always read the actual pinned stream. Only completion timing is controlled.
            var count = pinnedStream.Read(buffer);
            if (Volatile.Read(ref _held) != 0 || count == 0 || !Directory.Exists(entryPath)) return count;
            var file = Assert.IsType<FileStream>(pinnedStream);
            var bytes = ReadCacheEa(entryPath);
            using var receipt = System.Text.Json.JsonDocument.Parse(bytes);
            if (receipt.RootElement.GetProperty("state").GetString() != "published") return count;
            var target = receipt.RootElement.GetProperty("children").GetProperty(targetName);
            Assert.True(GetFileInformationByHandleEx(file.SafeFileHandle, 18, out CacheFileIdentity identity, 24));
            if (identity.Volume != target.GetProperty("volume").GetUInt64() ||
                identity.Low != target.GetProperty("low").GetUInt64() ||
                identity.High != target.GetProperty("high").GetUInt64()) return count;
            if (Interlocked.CompareExchange(ref _held, 1, 0) != 0) return count;
            PublishedReceipt = bytes;
            StreamIdentity = (identity.Volume, identity.Low, identity.High);
            ReadBytes = count;
            Reached.TrySetResult();
            if (!_release.Wait(ActiveCopyTimeout))
                throw new TimeoutException("The published payload read was not released by its test owner.");
            return count;
        }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private static Task ObserveActiveCopy(string preparation, Action action) =>
        Task.Factory.StartNew(() =>
        {
            var runtime = Path.Combine(preparation, "coreclr.dll");
            long observedBytes = 0;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                if (!File.Exists(runtime)) return false;
                observedBytes = new FileInfo(runtime).Length;
                return observedBytes > 0;
            }, ActiveCopyTimeout), "The resolver must reach a nonempty real runtime copy.");
            Assert.InRange(observedBytes, 1, 64L * 1024 * 1024 - 1);
            action();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static SafeFileHandle HoldVerificationOpen(string image, bool blockDelete, Action beforeResume)
    {
        const uint requestLevelOne = 0x90000;
        const uint acknowledgeWithoutLevelTwo = 0x90050;
        const int ioPending = 997;
        var handle = CreateFileW(@"\\?\" + image, 0x80000000, blockDelete ? 1u : 7u,
            IntPtr.Zero, 3, 0x40200000, IntPtr.Zero);
        using var signal = new ManualResetEvent(false);
        var size = IntPtr.Size == 8 ? 32 : 20;
        var overlapped = Marshal.AllocHGlobal(size);
        try
        {
            Assert.False(handle.IsInvalid);
            Marshal.Copy(new byte[size], 0, overlapped, size);
            Marshal.WriteIntPtr(overlapped, IntPtr.Size == 8 ? 24 : 16, signal.SafeWaitHandle.DangerousGetHandle());
            Assert.False(DeviceIoControl(handle, requestLevelOne, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, overlapped));
            Assert.Equal(ioPending, Marshal.GetLastWin32Error());
            Assert.True(signal.WaitOne(ActiveCopyTimeout), "Verification must break the real apphost oplock.");
            Assert.True(GetOverlappedResult(handle, overlapped, out _, false));
            // The real read open is pending its oplock acknowledgement. It cannot
            // verify/publish or begin rollback until the obstacle and token exist.
            beforeResume();
            if (!blockDelete)
            {
                // Closing acknowledges the break without retaining a file object
                // that could itself keep the preparation directory nonempty.
                handle.Dispose();
                return handle;
            }
            signal.Reset();
            Marshal.Copy(new byte[size], 0, overlapped, size);
            Marshal.WriteIntPtr(overlapped, IntPtr.Size == 8 ? 24 : 16, signal.SafeWaitHandle.DangerousGetHandle());
            if (!DeviceIoControl(handle, acknowledgeWithoutLevelTwo, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, overlapped))
            {
                Assert.Equal(ioPending, Marshal.GetLastWin32Error());
                Assert.True(signal.WaitOne(ActiveCopyTimeout), "The apphost oplock acknowledgement must complete.");
                Assert.True(GetOverlappedResult(handle, overlapped, out _, false));
            }
            return handle;
        }
        catch
        {
            if (!handle.IsInvalid)
            {
                CancelIoEx(handle, overlapped);
                GetOverlappedResult(handle, overlapped, out _, true);
            }
            handle.Dispose();
            throw;
        }
        finally
        {
            if (!handle.IsClosed && !handle.IsInvalid)
            {
                CancelIoEx(handle, overlapped);
                GetOverlappedResult(handle, overlapped, out _, true);
            }
            Marshal.FreeHGlobal(overlapped);
        }
    }

    private string ProcessPreparation(string product)
    {
        var manifest = File.ReadAllBytes(Directory.GetFiles(product, "payload-manifest.json", SearchOption.AllDirectories).Single());
        var rid = RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64";
        return Path.Combine(Cache, rid + "-" + Convert.ToHexStringLower(SHA256.HashData(manifest)));
    }

    private InstalledFileWorkerResolver ProcessResolver(string product)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var host = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!, "tests",
            "OpenClaw.Shared.TestHost", "bin", configuration, "net10.0", "OpenClaw.Shared.TestHost.dll");
        return new InstalledFileWorkerResolver(Assembly.LoadFrom(host), product);
    }

    private string CreateProcessPayload()
    {
        var rid = RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64";
        var repository = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!;
        var manifest = File.ReadAllBytes(Path.Combine(repository, "tests", "OpenClaw.Shared.TestHost", "Fixtures",
            rid == "win-arm64" ? "worker-cache-arm64.json" : "worker-cache-x64.json"));
        var product = Path.Combine(_root, "process-product");
        var payload = Directory.CreateDirectory(Path.Combine(product, "Workers", "FileWorker", rid,
            Convert.ToHexStringLower(SHA256.HashData(manifest))[..32])).FullName;
        File.WriteAllBytes(Path.Combine(payload, "payload-manifest.json"), manifest);
        File.WriteAllText(Path.Combine(payload, "OpenClaw.FileWorker.exe"), "abc");
        File.WriteAllText(Path.Combine(payload, "OpenClaw.FileWorker.runtimeconfig.json"), "abc");
        using var runtime = new FileStream(Path.Combine(payload, "coreclr.dll"), FileMode.CreateNew);
        runtime.SetLength(64L * 1024 * 1024);
        return product;
    }

    private Process StartCacheHost(string product, string mode = "resolve", string? manifest = null)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var host = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!, "tests",
            "OpenClaw.Shared.TestHost", "bin", configuration, "net10.0", "OpenClaw.Shared.TestHost.dll");
        Assert.True(File.Exists(host), "The ordinary process fixture must be built.");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in manifest is null ? new[] { host, "--file-worker-cache", product, Cache, mode } :
            new[] { host, "--file-worker-cache-manifest", product, Cache, manifest, mode }) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("digest")]
    [InlineData("missing")]
    [InlineData("additional")]
    [InlineData("directory")]
    [InlineData("hardlink")]
    [InlineData("reparse")]
    public void CachedTamperFailsClosedWithoutReplacingOrRepairingPublishedStorage(string mutation)
    {
        string published;
        using (var lease = Resolver.Resolve(Cache)) published = lease.PayloadDirectory;
        var runtime = Path.Combine(published, "coreclr.dll");
        switch (mutation)
        {
            case "manifest": File.AppendAllText(Path.Combine(published, "payload-manifest.json"), " "); break;
            case "digest": File.WriteAllText(runtime, "xyz"); break;
            case "missing": File.Delete(runtime); break;
            case "additional": File.WriteAllText(Path.Combine(published, "extra.dll"), "preserve"); break;
            case "directory": Directory.CreateDirectory(Path.Combine(published, "unexpected")); break;
            case "hardlink": CreateHardLink(Path.Combine(_root, "alias"), runtime); break;
            case "reparse":
                File.Move(runtime, runtime + ".original");
                File.CreateSymbolicLink(runtime, runtime + ".original");
                break;
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = Assert.Throws<NotSupportedException>(() => Resolver.Resolve(Cache));
            Assert.Contains("Unproven or active cache entries were retained", error.Message);
        }
        Assert.Single(Directory.GetDirectories(Cache));
        if (mutation == "digest") Assert.Equal("xyz", File.ReadAllText(runtime));
        if (mutation == "missing") Assert.False(File.Exists(runtime));
    }

    [Fact]
    public void UncertainPreparationIsNotExecutableAndCannotAllocateAnotherCopyAcrossRestarts()
    {
        Directory.CreateDirectory(Cache);
        var preparation = Path.Combine(Cache, ".preparing-" + PublishedName());
        Directory.CreateDirectory(preparation);
        File.WriteAllText(Path.Combine(preparation, "partial.dll"), "preserve");
        for (var restart = 0; restart < 4; restart++)
        {
            var error = Assert.Throws<NotSupportedException>(() => Resolver.Resolve(Cache));
            Assert.Contains("uncertain preparation", error.Message);
            Assert.Equal(preparation, Assert.Single(Directory.GetDirectories(Cache)));
            Assert.False(Directory.Exists(Path.Combine(Cache, PublishedName())));
        }
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(preparation, "partial.dll")));
    }

    [Fact]
    public async Task UnknownAndPartialEntriesConsumeCapacityButAFullCacheStillReusesCurrentVerifiedCode()
    {
        using var activeLease = Resolver.Resolve(Cache);
        var published = activeLease.PayloadDirectory;
        for (var entry = 0; entry < 7; entry++)
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(Cache, "unrecognized-" + entry)).FullName, "data"), "preserve");
        using (var reused = Resolver.Resolve(Cache)) Assert.Equal(published, reused.PayloadDirectory);
        using var newVersion = StartCacheHost(CreateProcessPayload());
        await newVersion.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, newVersion.ExitCode);
        Assert.Contains("cache is full", await newVersion.StandardError.ReadToEndAsync());
        Assert.Equal(8, Directory.GetDirectories(Cache).Length);
        using (var afterRestart = Resolver.Resolve(Cache)) Assert.Equal(published, afterRestart.PayloadDirectory);
        Assert.All(Directory.GetFiles(Cache, "data", SearchOption.AllDirectories),
            file => Assert.Equal("preserve", File.ReadAllText(file)));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("reparse")]
    [InlineData("nested")]
    [InlineData("oversized")]
    [InlineData("sparse")]
    public void UnaccountableCacheObjectsBlockAdmissionWithoutAdoptionOrDeletion(string kind)
    {
        Directory.CreateDirectory(Cache);
        var entry = Path.Combine(Cache, "unknown");
        if (kind == "file") File.WriteAllText(entry, "preserve");
        else if (kind == "reparse") Directory.CreateSymbolicLink(entry, _payload);
        else
        {
            Directory.CreateDirectory(entry);
            var file = Path.Combine(entry, "data");
            if (kind == "nested") Directory.CreateDirectory(file);
            else
            {
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite);
                if (kind == "oversized") stream.SetLength(128L * 1024 * 1024 + 1);
                else Assert.True(DeviceIoControl(stream.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero));
            }
        }
        Assert.Throws<NotSupportedException>(() => Resolver.Resolve(Cache));
        Assert.True(Path.Exists(entry));
        Assert.False(Directory.Exists(Path.Combine(Cache, PublishedName())));
        Assert.DoesNotContain(Directory.GetDirectories(Cache), p => Path.GetFileName(p).StartsWith(".preparing-"));
    }

    private string PublishedName()
    {
        var manifest = File.ReadAllBytes(Path.Combine(_payload, "payload-manifest.json"));
        var rid = RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64";
        return rid + "-" + Convert.ToHexStringLower(SHA256.HashData(manifest));
    }

    private static (uint Volume, ulong Index) NativeIdentity(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(GetFileInformationByHandle(file, out var identity));
        return (identity.Volume, ((ulong)identity.IndexHigh << 32) | identity.IndexLow);
    }

    private static (uint Volume, ulong Index) NativeNoFollowIdentity(string path)
    {
        using var file = CreateFileW(@"\\?\" + path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(file.IsInvalid);
        Assert.True(GetFileInformationByHandle(file, out var identity));
        return (identity.Volume, ((ulong)identity.IndexHigh << 32) | identity.IndexLow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedWorkloadAndUploadAdmissionFailuresSurviveExecutorInfrastructure(bool uploadAdmission)
    {
        var context = MxcSyntheticContext.Create(_root);
        var process = new MxcCommandRunnerTests.FakeProcess();
        using var executor = new NodeSandboxExecutor(new(new SettingsData
        {
            SandboxUploadStagingPath = Path.Combine(_root, "uploads")
        }), () => context.ProtectedPaths[0],
            () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: _ => Task.FromResult<IMxcProcess>(process), probe: _ => MxcAvailabilityTests.BaseProbe(),
            scratchRoot: Path.Combine(_root, "runs"), contextProvider: () => context, fileWorker: Resolver);
        var failure = new FileCommandException(uploadAdmission ? "UPLOAD_CLEANUP_BLOCKED" : "INVALID_FRAME",
            uploadAdmission ? "Inventory reservation is unavailable." : "Transfer frame is invalid.", invalidRequest: !uploadAdmission);
        var operations = new FileOperations(executor) { UploadAdmission = _ => throw failure };
        var observed = uploadAdmission
            ? await Assert.ThrowsAsync<FileCommandException>(() => operations.ExecuteAsync(new(FileOperation.Upload, "", Name: "data", ContentBase64: "YWJj")))
            : await Assert.ThrowsAsync<FileCommandException>(() => executor.RunFileAsync(new FixedFileWorkload<bool>((_, _) => throw failure, 10_000)));
        Assert.Same(failure, observed);
        Assert.Equal(!uploadAdmission, observed.InvalidRequest);
        Assert.Equal(!uploadAdmission, process.Finalized);
        Assert.Equal(!uploadAdmission, process.Disposed);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "runs")));
    }

    private string NewDestination()
    {
        var parent = Directory.CreateDirectory(Path.Combine(_root, "operations", Guid.NewGuid().ToString("N"))).FullName;
        return Path.Combine(parent, "worker");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InstalledProtectedPayloadLoadsReadonlyWithoutOpeningItsSourceOrExpandingDataRights(
        bool devBuild, bool userFilesWrite)
    {
        var product = devBuild ? _product + "-Dev" : _product;
        var sourcePayload = Path.Combine(product, Path.GetRelativePath(_product, _payload));
        if (devBuild) Directory.Move(_product, product);
        var context = MxcSyntheticContext.Create(_root);
        context = context with { ProtectedPaths = [.. context.ProtectedPaths, _product, _product + "-Dev"] };
        var selected = Directory.CreateDirectory(Path.Combine(_root, "selected")).FullName;
        var requests = new List<ContainerRequest>();
        var runs = Path.Combine(Path.GetDirectoryName(_product)!, "OpenClawMxc", "Runs");
        using var executor = new NodeSandboxExecutor(new(new SettingsData
        {
            SystemRunFilesystemScope = userFilesWrite ? SystemRunFilesystemScope.UserFilesReadWrite : SystemRunFilesystemScope.SelectedFolders,
            SandboxCustomFolders = [new() { Path = selected, Access = SandboxFolderAccess.ReadWrite }],
        }), () => context.ProtectedPaths[0],
            () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: request =>
            {
                requests.Add(request);
                return Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess());
            }, probe: _ => MxcAvailabilityTests.BaseProbe(), scratchRoot: runs,
            contextProvider: () => context, fileWorker: new InstalledFileWorkerResolver(
                typeof(InstalledFileWorkerResolverTests).Assembly, product));

        await new MxcCommandRunner(executor).RunAsync(new()
        {
            Argv = [Path.Combine(Environment.SystemDirectory, "whoami.exe")], TimeoutMs = 10_000,
        });
        string? prepared = null;
        Assert.True(await executor.RunFileAsync(new FixedFileWorkload<bool>((io, _) =>
        {
            var resources = io.Resources!;
            var payload = Assert.Single(resources.InfrastructureAccess.ReadonlyPaths, p => p.StartsWith(NodeSandboxExecutor.GetPayloadCacheRoot(runs)));
            prepared = payload;
            Assert.False(MxcRequestBuilder.Within(payload, product));
            Assert.False(MxcRequestBuilder.Within(payload, resources.InfrastructureAccess.ReadwritePaths.Single()));
            var expectedData = userFilesWrite ? context.UserFolders.Select(f => f.Path!).Append(selected) : [selected];
            Assert.Equal(expectedData, resources.DataAccess.ReadwritePaths);
            Assert.Empty(resources.DataAccess.ReadonlyPaths);
            Assert.Equal(SandboxFolderAccess.NoAccess, resources.DataAccess.EffectiveAccess(payload));
            var admittedData = FileOperations.CreateAdmission(io, null, resolveUploadRoot: false).DataAccess!;
            var dataError = Assert.Throws<FileCommandException>(() => admittedData.Demand(payload, write: true));
            Assert.Equal("PERMISSION_DENIED", dataError.Code);
            foreach (var denied in context.ProtectedPaths)
            {
                Assert.Contains(denied, resources.DataAccess.ProtectedPaths);
                Assert.Equal(SandboxFolderAccess.NoAccess, resources.DataAccess.EffectiveAccess(Path.Combine(denied, "secret")));
            }
            Assert.Equal("abc", File.ReadAllText(Path.Combine(payload, "coreclr.dll")));
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(payload, "coreclr.dll"), "xyz"));
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(sourcePayload, "coreclr.dll"), "xyz"));
            return Task.FromResult(true);
        }, 10_000)));
        Assert.NotNull(prepared);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].Filesystem!.DeniedPaths, requests[1].Filesystem!.DeniedPaths);
        Assert.Equal(requests[0].Filesystem!.ReadonlyPaths.Order(),
            requests[1].Filesystem!.ReadonlyPaths.Where(p => p != prepared).Order());
        var filesystem = requests[1].Filesystem!;
        Assert.Equal(SandboxFolderAccess.ReadOnly, MxcFolderPermissions.EffectiveAccess(
            Path.Combine(prepared, "OpenClaw.FileWorker.exe"), filesystem.ReadonlyPaths, filesystem.ReadwritePaths, filesystem.DeniedPaths));
        Assert.DoesNotContain(product, filesystem.ReadonlyPaths);
        Assert.DoesNotContain(Path.GetPathRoot(_root)!, filesystem.ReadonlyPaths);
        Assert.Empty(Directory.GetDirectories(runs));
        File.WriteAllText(Path.Combine(sourcePayload, "coreclr.dll"), "xyz");
    }

    [Fact]
    public async Task ConcurrentFileAndSystemRunOperationsHaveDisjointPrivateRightsUnderBroadUserGrant()
    {
        var context = MxcSyntheticContext.Create(_root);
        var runs = Path.Combine(_root, "runs");
        var requests = new System.Collections.Concurrent.ConcurrentQueue<ContainerRequest>();
        var otherPublished = Directory.CreateDirectory(Path.Combine(Cache, "other-published")).FullName;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProtocol = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processes = Enumerable.Range(0, 2).Select(_ => new MxcCommandRunnerTests.FakeProcess
        {
            WaitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        }).ToArray();
        var count = 0;
        using var executor = new NodeSandboxExecutor(new(new SettingsData
        {
            SystemRunFilesystemScope = SystemRunFilesystemScope.UserFilesReadWrite,
            SandboxCustomFolders = [new() { Path = _root, Access = SandboxFolderAccess.ReadWrite }],
        }), () => context.ProtectedPaths[0],
            () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: request =>
            {
                requests.Enqueue(request);
                var index = Interlocked.Increment(ref count) - 1;
                if (index == 1) started.SetResult();
                return Task.FromResult<IMxcProcess>(processes[index]);
            }, probe: _ => MxcAvailabilityTests.BaseProbe(), scratchRoot: runs,
            contextProvider: () => context, fileWorker: Resolver);
        var file = executor.RunFileAsync(new FixedFileWorkload<bool>(async (io, _) =>
        {
            await releaseProtocol.Task;
            var code = Assert.Single(io.Resources!.InfrastructureAccess.ReadonlyPaths, p => p.StartsWith(Cache));
            Assert.Equal(SandboxFolderAccess.NoAccess, io.Resources.DataAccess.EffectiveAccess(code));
            return true;
        }, 10_000));
        var command = new MxcCommandRunner(executor).RunAsync(new()
        {
            Argv = [Path.Combine(Environment.SystemDirectory, "whoami.exe")], TimeoutMs = 10_000,
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Directory.GetDirectories(runs).Length);
            var policies = requests.ToArray();
            foreach (var request in policies)
            {
                var fs = request.Filesystem!;
                Assert.Contains(runs, fs.DeniedPaths);
                Assert.Contains(Cache, fs.DeniedPaths);
                Assert.Equal(SandboxFolderAccess.NoAccess, MxcFolderPermissions.EffectiveAccess(
                    otherPublished, fs.ReadonlyPaths, fs.ReadwritePaths, fs.DeniedPaths));
                Assert.Equal(SandboxFolderAccess.ReadWrite, MxcFolderPermissions.EffectiveAccess(
                    request.WorkingDirectory!, fs.ReadonlyPaths, fs.ReadwritePaths, fs.DeniedPaths));
                var sibling = policies.Single(other => other != request).WorkingDirectory!;
                Assert.Equal(SandboxFolderAccess.NoAccess, MxcFolderPermissions.EffectiveAccess(
                    sibling, fs.ReadonlyPaths, fs.ReadwritePaths, fs.DeniedPaths));
                var worker = Path.Combine(Path.GetDirectoryName(sibling)!, "worker");
                Assert.Equal(SandboxFolderAccess.NoAccess, MxcFolderPermissions.EffectiveAccess(
                    worker, fs.ReadonlyPaths, fs.ReadwritePaths, fs.DeniedPaths));
                Assert.All(context.ProtectedPaths, path => Assert.Contains(path, fs.DeniedPaths));
            }
        }
        finally
        {
            releaseProtocol.TrySetResult();
            foreach (var process in processes) process.WaitCompletion!.TrySetResult(new() { ExitCode = 0 });
            await Task.WhenAll(file, command);
        }
        Assert.Empty(Directory.GetDirectories(runs));
    }

    [Theory]
    [InlineData("namespace")]
    [InlineData("child")]
    [InlineData("cwd")]
    [InlineData("image")]
    public async Task CallerCannotReopenPrivateNamespaceOrSelectItsContents(string attempt)
    {
        var context = MxcSyntheticContext.Create(_root);
        var runs = Directory.CreateDirectory(Path.Combine(_root, "runs")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(runs, "other-operation", "scratch")).FullName;
        var settings = new SettingsData
        {
            SystemRunFilesystemScope = SystemRunFilesystemScope.UserFilesReadWrite,
            SandboxCustomFolders = [new() { Path = _root, Access = SandboxFolderAccess.ReadWrite }],
        };
        if (attempt is "namespace" or "child")
            settings.SandboxCustomFolders.Add(new()
            {
                Path = attempt == "namespace" ? runs : child, Access = SandboxFolderAccess.ReadWrite,
            });
        using var executor = new NodeSandboxExecutor(new(settings), () => context.ProtectedPaths[0],
            () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: _ => throw new InvalidOperationException("Private paths must fail before launch."),
            probe: _ => throw new InvalidOperationException("Private paths must fail before probe."),
            scratchRoot: runs, contextProvider: () => context, fileWorker: Resolver);
        if (attempt is "namespace" or "child")
        {
            var error = await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => executor.RunFileAsync(
                new FixedFileWorkload<bool>((_, _) => Task.FromResult(true), 10_000)));
            Assert.Equal(NodeToolErrorCategory.SandboxDenied, error.Category);
        }
        else
        {
            var result = await new MxcCommandRunner(executor).RunAsync(new()
            {
                Argv = [attempt == "image" ? Path.Combine(child, "tool.exe") : Path.Combine(Environment.SystemDirectory, "whoami.exe")],
                Cwd = attempt == "cwd" ? child : null, TimeoutMs = 10_000,
            });
            Assert.Equal(NodeToolErrorCategory.SandboxDenied, result.ErrorCategory);
        }
        Assert.True(Directory.Exists(child));
        Assert.Single(Directory.GetDirectories(runs));
    }

    [Theory]
    [InlineData("namespace")]
    [InlineData("child")]
    [InlineData("cwd")]
    [InlineData("image")]
    public async Task SystemRunCannotReopenTheCodeCacheEvenWithoutAFileWorkerLease(string attempt)
    {
        var context = MxcSyntheticContext.Create(_root);
        var child = Directory.CreateDirectory(Path.Combine(Cache, "other-published")).FullName;
        var settings = new SettingsData
        {
            SandboxCustomFolders = [new() { Path = _root, Access = SandboxFolderAccess.ReadWrite }],
        };
        if (attempt is "namespace" or "child")
            settings.SandboxCustomFolders.Add(new()
            {
                Path = attempt == "namespace" ? Cache : child, Access = SandboxFolderAccess.ReadWrite,
            });
        using var executor = new NodeSandboxExecutor(new(settings), () => context.ProtectedPaths[0],
            () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: _ => throw new InvalidOperationException("Cache paths must fail before launch."),
            probe: _ => throw new InvalidOperationException("Cache paths must fail before probe."),
            scratchRoot: Path.Combine(_root, "runs"), contextProvider: () => context);
        var result = await new MxcCommandRunner(executor).RunAsync(new()
        {
            Argv = [attempt == "image" ? Path.Combine(child, "tool.exe") : Path.Combine(Environment.SystemDirectory, "whoami.exe")],
            Cwd = attempt == "cwd" ? child : null, TimeoutMs = 10_000,
        });
        Assert.Equal(NodeToolErrorCategory.SandboxDenied, result.ErrorCategory);
        Assert.True(Directory.Exists(child));
    }

    [Fact]
    public void DeepProductPathRetainsPayloadReplacementProtection()
    {
        var productRoot = Path.Combine(_root, new string('x', 150));
        Directory.CreateDirectory(productRoot);
        Directory.Move(Path.Combine(_product, "Workers"), Path.Combine(productRoot, "Workers"));
        var payload = Path.Combine(productRoot, Path.GetRelativePath(_product, _payload));
        var runtime = Path.Combine(payload, "coreclr.dll");
        Assert.True(runtime.Length > 260);
        using (var lease = new InstalledFileWorkerResolver(typeof(InstalledFileWorkerResolverTests).Assembly, productRoot).Resolve())
        {
            Assert.Equal(payload, lease.PayloadDirectory);
            Assert.Throws<IOException>(() => File.WriteAllText(runtime, "xyz"));
            Assert.Throws<IOException>(() => Directory.Move(productRoot, productRoot + ".moved"));
        }
        File.WriteAllText(runtime, "xyz");
    }

    [Fact]
    public void LeasePinsImageDependenciesAndAncestorsUntilDisposed()
    {
        var runtime = Path.Combine(_payload, "coreclr.dll");
        using (var lease = Resolver.Resolve())
        {
            Assert.Equal(Path.Combine(_payload, "OpenClaw.FileWorker.exe"), lease.ImagePath);
            Assert.Throws<IOException>(() => File.WriteAllText(runtime, "xyz"));
            Assert.Throws<IOException>(() => File.Move(runtime, runtime + ".replaced"));
            Assert.Throws<IOException>(() => Directory.Move(_root, _root + ".moved"));
        }
        File.WriteAllText(runtime, "xyz");
        Assert.Throws<NotSupportedException>(() => Resolver.Resolve());
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("digest")]
    [InlineData("missing")]
    [InlineData("additional")]
    [InlineData("directory")]
    [InlineData("file-reparse")]
    [InlineData("directory-reparse")]
    [InlineData("ancestor-reparse")]
    [InlineData("hardlink")]
    [InlineData("hardlink-extended")]
    public void InvalidInstalledSourceCannotMaterializeATrustedPayload(string mutation)
    {
        using (Resolver.Resolve()) { }
        var runtime = Path.Combine(_payload, "coreclr.dll");
        var destination = NewDestination();
        switch (mutation)
        {
            case "manifest": File.AppendAllText(Path.Combine(_payload, "payload-manifest.json"), " "); break;
            case "digest": File.WriteAllText(runtime, "xyz"); break;
            case "missing": File.Delete(runtime); break;
            case "additional": File.WriteAllText(Path.Combine(_payload, "unexpected.dll"), "abc"); break;
            case "directory": Directory.CreateDirectory(Path.Combine(_payload, "unexpected")); break;
            case "file-reparse":
                File.Move(runtime, Path.Combine(_root, "original.dll"));
                File.CreateSymbolicLink(runtime, Path.Combine(_root, "original.dll"));
                break;
            case "directory-reparse":
                Directory.Move(_payload, _payload + ".original");
                Directory.CreateSymbolicLink(_payload, _payload + ".original");
                break;
            case "ancestor-reparse":
                Directory.Move(_product, _product + ".original");
                Directory.CreateSymbolicLink(_product, _product + ".original");
                break;
            case "hardlink":
                CreateHardLink(Path.Combine(_root, "alias.dll"), runtime);
                break;
            case "hardlink-extended":
                CreateHardLink(@"\\?\" + SandboxPathRules.Normalize(Path.Combine(_root, "alias.dll")),
                    @"\\?\" + SandboxPathRules.Normalize(runtime));
                break;
        }
        var error = Assert.Throws<NotSupportedException>(() => Resolver.Resolve(destination));
        Assert.False(Directory.Exists(destination));
        Assert.Contains(mutation switch
        {
            "manifest" => "manifest differs",
            "digest" => "digest changed",
            "missing" or "additional" => "inventory changed",
            "directory" => "dependency directory",
            "hardlink" or "hardlink-extended" => "file aliases",
            _ => "ordinary files and directories",
        }, error.Message);
        File.WriteAllText(runtime, "xyz");
    }

    [Theory]
    [InlineData("file")]
    [InlineData("reparse")]
    [InlineData("ancestor-reparse")]
    [InlineData("trailing-dot-alias")]
    public void AliasedOrNonDirectoryCacheRootCannotMaterializePayload(string kind)
    {
        var destination = NewDestination();
        var sentinel = Path.Combine(_root, "sentinel");
        Directory.CreateDirectory(sentinel);
        File.WriteAllText(Path.Combine(sentinel, "preserve"), "untouched");
        switch (kind)
        {
            case "file": File.WriteAllText(destination, "untouched"); break;
            case "reparse": Directory.CreateSymbolicLink(destination, sentinel); break;
            case "ancestor-reparse":
                Directory.Delete(Path.GetDirectoryName(destination)!);
                Directory.CreateSymbolicLink(Path.GetDirectoryName(destination)!, sentinel);
                break;
            case "trailing-dot-alias": destination += "."; break;
        }
        Assert.Throws<NotSupportedException>(() => Resolver.Resolve(destination));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(sentinel, "preserve")));
        if (kind == "file") Assert.Equal("untouched", File.ReadAllText(destination));
        if (kind == "ancestor-reparse") Assert.False(Directory.Exists(Path.Combine(sentinel, "worker")));
        File.WriteAllText(Path.Combine(_payload, "coreclr.dll"), "xyz");
    }

    [Fact]
    public void PreparedImageAndSourceKeepReplacementAndRenamePinsUntilDisposed()
    {
        string destination;
        using (var lease = Resolver.Resolve(Cache))
        {
            destination = lease.PayloadDirectory;
            Assert.StartsWith(Cache + Path.DirectorySeparatorChar, destination);
            Assert.Equal(Path.Combine(destination, "OpenClaw.FileWorker.exe"), lease.ImagePath);
            foreach (var directory in new[] { _payload, destination })
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    Assert.Throws<IOException>(() => File.WriteAllText(file, "xyz"));
                    Assert.Throws<IOException>(() => File.Move(file, file + ".replaced"));
                }
                Assert.Throws<IOException>(() => Directory.Move(directory, directory + ".moved"));
            }
            Assert.Throws<IOException>(() => Directory.Move(Path.GetDirectoryName(destination)!, destination + ".moved"));
        }
        File.WriteAllText(Path.Combine(destination, "coreclr.dll"), "xyz");
        Assert.Throws<NotSupportedException>(() => Resolver.Resolve(Cache));
        File.WriteAllText(Path.Combine(_payload, "coreclr.dll"), "xyz");
    }

    [Fact]
    public void CancelledPreparationCannotCreateOrRetainPayloadPins()
    {
        var destination = NewDestination();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Resolver.Resolve(destination, cancel.Token));
        Assert.False(Directory.Exists(destination));
        File.WriteAllText(Path.Combine(_payload, "coreclr.dll"), "xyz");
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("spawn")]
    [InlineData("protocol")]
    public async Task PublishedCodePersistsForEveryOutcomeButUncertainLaunchRetainsPinsUntilOwnerShutdown(string failure)
    {
        var context = MxcSyntheticContext.Create(_root);
        var runs = Path.Combine(_root, "runs");
        string? prepared = null;
        using var executor = new NodeSandboxExecutor(new(new SettingsData()), () => context.ProtectedPaths[0],
            () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: _ => failure == "spawn" ? throw new IOException("launch failure") :
                Task.FromResult<IMxcProcess>(new MxcCommandRunnerTests.FakeProcess()),
            probe: request =>
            {
                prepared = Assert.Single(request.Filesystem!.ReadonlyPaths, p => p.StartsWith(Cache));
                Assert.True(File.Exists(Path.Combine(prepared, "coreclr.dll")));
                if (failure == "probe") throw new IOException("admission failure");
                return MxcAvailabilityTests.BaseProbe();
            }, scratchRoot: runs, contextProvider: () => context, fileWorker: Resolver);
        await Assert.ThrowsAsync<NodeSandboxExecutionException>(() => executor.RunFileAsync(
            new FixedFileWorkload<bool>((_, _) => throw new IOException("protocol failure"), 10_000)));
        Assert.NotNull(prepared);
        Assert.True(Directory.Exists(prepared));
        Assert.Equal(failure == "spawn" ? 1 : 0, Directory.GetDirectories(runs).Length);
        if (failure == "spawn")
        {
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(_payload, "coreclr.dll"), "xyz"));
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(prepared, "coreclr.dll"), "xyz"));
        }
        executor.Dispose();
        File.WriteAllText(Path.Combine(_payload, "coreclr.dll"), "xyz");
    }

    [Fact]
    public async Task CancelledLateLaunchRetainsBothPayloadsUntilProcessAndPipeCleanupSettle()
    {
        var context = MxcSyntheticContext.Create(_root);
        var runs = Path.Combine(_root, "runs");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = new TaskCompletionSource<IMxcProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipesRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new MxcCommandRunnerTests.FakeProcess { RootExited = false };
        string? prepared = null;
        using var executor = new NodeSandboxExecutor(new(new SettingsData()), () => context.ProtectedPaths[0],
            () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: request =>
            {
                prepared = Assert.Single(request.Filesystem!.ReadonlyPaths, p => p.StartsWith(Cache));
                entered.SetResult();
                return launched.Task;
            }, probe: _ => MxcAvailabilityTests.BaseProbe(), scratchRoot: runs,
            contextProvider: () => context, fileWorker: Resolver);
        using var cancel = new CancellationTokenSource();
        var run = executor.RunFileAsync(new FixedFileWorkload<bool>((io, _) =>
        {
            io.OwnPipeWorkers(pipeWorker.Task);
            pipesRegistered.SetResult();
            return Task.FromResult(true);
        }, 10_000), cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(_payload, "coreclr.dll"), "xyz"));
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(prepared!, "coreclr.dll"), "xyz"));
            launched.SetResult(process);
            await pipesRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(process.Disposed);
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(prepared!, "coreclr.dll"), "xyz"));
        }
        finally
        {
            cancel.Cancel();
            launched.TrySetResult(process);
            pipeWorker.TrySetResult();
            await process.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cleanup = Stopwatch.StartNew();
            while (Directory.EnumerateDirectories(runs).Any() && cleanup.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10);
        }
        Assert.True(process.Killed);
        Assert.Empty(Directory.GetDirectories(runs));
        File.WriteAllText(Path.Combine(_payload, "coreclr.dll"), "xyz");
    }

    [Fact]
    public void ObsoletePayloadSlotsCannotChangeTheSelectedProductPayload()
    {
        var obsolete = Path.Combine(Path.GetDirectoryName(_payload)!, new string('0', 32));
        Directory.CreateDirectory(obsolete);
        File.WriteAllText(Path.Combine(obsolete, "dependency.dll"), "obsolete build");
        using var lease = Resolver.Resolve();
        Assert.Equal(_payload, lease.PayloadDirectory);
        Assert.Equal("obsolete build", File.ReadAllText(Path.Combine(obsolete, "dependency.dll")));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existing, IntPtr security);

    private static void CreateHardLink(string path, string existing)
    {
        static string NativePath(string value) =>
            value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value :
            value.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + value[2..] :
            @"\\?\" + Path.GetFullPath(value);

        Assert.True(CreateHardLinkW(NativePath(path), NativePath(existing), IntPtr.Zero),
            $"Hard link creation failed: {Marshal.GetLastWin32Error()}");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Identity
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Identity identity);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint control, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out uint transferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);
}
