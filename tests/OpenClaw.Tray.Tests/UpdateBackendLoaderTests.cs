using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class UpdateBackendLoaderTests
{
    public static TheoryData<Exception> DependencyFailures => new()
    {
        new BlockedAssemblyException(),
        new FileNotFoundException("Updater missing", "Updatum.dll"),
        new FileLoadException("Windows blocked the updater", @"C:\App\Updatum.dll"),
        new FileNotFoundException("Updater missing", "Updatum, Version=1.3.4.0, Culture=neutral, PublicKeyToken=null"),
        new BadImageFormatException("Invalid updater"),
        new TypeLoadException("Incompatible updater"),
        new MissingMethodException("Incompatible updater method"),
        new MissingFieldException("Incompatible updater field"),
        new DllNotFoundException("Native dependency missing"),
        new TypeInitializationException("Updater", new BlockedAssemblyException()),
        new TypeInitializationException("Updater",
            new TypeInitializationException("Dependency", new FileNotFoundException()))
    };

    [Theory]
    [MemberData(nameof(DependencyFailures))]
    public void Load_DependencyFailureIsContainedAndCached(Exception failure)
    {
        var attempts = 0;
        var loader = new UpdateBackendLoader(() =>
        {
            attempts++;
            throw failure;
        });

        Assert.Equal(0, attempts);
        Assert.Null(loader.Failure);
        Assert.Null(loader.Load());
        Assert.Same(failure, loader.Failure);
        Assert.Null(loader.Load());
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Load_ActualMissingAssemblyAcrossNonInlinedFactoryIsContained()
    {
        var loader = new UpdateBackendLoader(LoadMissingAssembly);

        Assert.Null(loader.Load());
        Assert.IsType<FileNotFoundException>(loader.Failure);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IUpdateBackend LoadMissingAssembly()
    {
        Assembly.Load(new AssemblyName("OpenClaw.Updater.IntentionallyMissing.TestDependency"));
        throw new InvalidOperationException("The missing dependency unexpectedly loaded.");
    }

    [Fact]
    public async Task Load_SuccessReusesBackendAndPreservesCheckAndInstall()
    {
        var backend = new FakeBackend();
        var attempts = 0;
        var loader = new UpdateBackendLoader(() =>
        {
            attempts++;
            return backend;
        });

        Assert.Equal(0, attempts);
        var loaded = loader.Load();
        Assert.Same(backend, loaded);
        Assert.Same(loaded, loader.Load());
        Assert.Equal(1, attempts);
        var result = await UpdateCheckPipeline.CheckAsync(loaded!, "2026.7.1");
        Assert.True(result.UpdateFound);
        Assert.Equal("v2026.7.1-2", result.SelectedRelease?.TagName);
        Assert.Equal("Release notes", loaded!.GetChangelog());
        Assert.True(await loaded.DownloadAndInstallAsync(() => backend.Events.Add("download-completed")));
        Assert.Equal(["download", "download-completed", "install"], backend.Events);
        Assert.Null(loader.Failure);
    }

    [Fact]
    public void LateDependencyFailureDisablesAlreadyCreatedBackend()
    {
        var backend = new FakeBackend();
        var loader = new UpdateBackendLoader(() => backend);
        Assert.Same(backend, loader.Load());
        var failure = new BlockedAssemblyException();

        loader.MarkUnavailable(failure);

        Assert.Null(loader.Load());
        Assert.Same(failure, loader.Failure);
        Assert.NotNull(loader.Failure);
        Assert.Equal(unchecked((int)0x800711C7), loader.Failure.HResult);
    }

    public static TheoryData<Exception> UnrelatedFailures => new()
    {
        new InvalidOperationException("Programming error"),
        new IOException("Disk full"),
        new UnauthorizedAccessException("Access denied"),
        new OperationCanceledException(),
        new TypeInitializationException("Unrelated", new InvalidOperationException()),
        new AggregateException(new FileLoadException()),
        new FileNotFoundException("Downloaded installer missing", @"C:\Downloads\update.exe"),
        new FileLoadException("Downloaded archive unavailable", "update.zip")
    };

    [Theory]
    [MemberData(nameof(UnrelatedFailures))]
    public void Load_DoesNotReclassifyUnrelatedFailures(Exception failure)
    {
        var loader = new UpdateBackendLoader(() => throw failure);

        Assert.False(UpdateBackendLoader.IsDependencyFailure(failure));
        Assert.Same(failure, Assert.ThrowsAny<Exception>(() => loader.Load()));
        Assert.Null(loader.Failure);
    }

    [Fact]
    public void StartupFacingOwners_DoNotReferenceOptionalAssembly()
    {
        // Retirement: replace with process-level blocked-assembly startup coverage
        // for packaged, Dev, Debug and production Release app entry points.
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var tray = Path.Combine(root, "src", "OpenClaw.Tray.WinUI");
        foreach (var path in Directory.GetFiles(tray, "App*.cs")
                     .Concat(new[]
                     {
                         Path.Combine(tray, "Services", "UpdateCoordinator.cs"),
                         Path.Combine(tray, "Dialogs", "DownloadProgressDialog.cs")
                     }))
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("Updatum", source);
        }

        var coordinator = File.ReadAllText(Path.Combine(tray, "Services", "UpdateCoordinator.cs"));
        var load = coordinator.IndexOf("_updaterLoader.Load()", StringComparison.Ordinal);
        Assert.True(load > 0);
        foreach (var skip in new[] { "if (PackageHelper.IsPackaged)", "if (AppIdentity.IsDev)", "#if DEBUG" })
        {
            var start = coordinator.IndexOf(skip, StringComparison.Ordinal);
            Assert.InRange(start, 0, load - 1);
            Assert.Contains("return true;", coordinator[start..load]);
        }
        var factory = File.ReadAllText(Path.Combine(tray, "Services", "ReleaseUpdateBackendFactory.cs"));
        Assert.Contains("[MethodImpl(MethodImplOptions.NoInlining)]", factory);
        Assert.Contains("IUpdateBackend Create()", factory);
    }

    [Fact]
    public void DependencyFailureRetainsVisibleRecoveryAndOfficialLink()
    {
        // Retirement: replace with mounted failure-dialog and AppState lifecycle tests.
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var coordinator = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "UpdateCoordinator.cs"));
        Assert.Contains("if (!installed && _updaterLoader.Failure is null)", coordinator);
        Assert.Contains("catch (Exception ex) when (UpdateBackendLoader.IsDependencyFailure(ex))", coordinator);
        Assert.Contains("ReportUpdaterUnavailable(ex);", coordinator);
        Assert.Contains("NavigateUri = new Uri(UpdateBackendLoader.ReleasesUrl)", coordinator);
        Assert.Equal("https://github.com/openclaw/openclaw-windows-node/releases/latest",
            UpdateBackendLoader.ReleasesUrl);

        var resources = Directory.GetFiles(Path.Combine(root, "src"), "Resources.resw", SearchOption.AllDirectories);
        Assert.NotEmpty(resources);
        foreach (var key in new[] { "Update_Message_Unavailable", "Update_ManualDownload" })
        {
            var values = resources.Select(path => XDocument.Load(path).Root!.Elements("data")
                .Single(data => (string?)data.Attribute("name") == key).Element("value")!.Value).ToArray();
            Assert.All(values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        }
    }

    private sealed class BlockedAssemblyException : FileLoadException
    {
        public BlockedAssemblyException() : base("Windows blocked the updater", "Updatum.dll") =>
            HResult = unchecked((int)0x800711C7);
    }

    private sealed class FakeBackend : IUpdateBackend
    {
        public List<string> Events { get; } = [];
        public Task<bool> CheckForUpdatesAsync() => Task.FromResult(true);
        public UpdateReleaseCandidate GetSelectedRelease() =>
            new("v2026.7.1-2", "Release notes", true, false, false, true, () => true, () => { });
        public IEnumerable<UpdateReleaseCandidate> GetReleaseCandidates() =>
            throw new InvalidOperationException("Successful checks do not enumerate fallbacks.");
        public string GetChangelog() => "Release notes";
        public Task<bool> DownloadAndInstallAsync(Action downloadCompleted)
        {
            Events.Add("download");
            downloadCompleted();
            Events.Add("install");
            return Task.FromResult(true);
        }
    }
}
