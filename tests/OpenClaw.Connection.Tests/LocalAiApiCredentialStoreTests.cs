using OpenClaw.Connection.LocalAi;
using System.Text;

namespace OpenClaw.Connection.Tests;

public sealed class LocalAiApiCredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
        "artifacts", "local-ai-credentials", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CredentialSurvivesRecreationWithoutPlaintextAtRest()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new LocalAiPaths(_directory);
        var first = new LocalAiApiCredentialStore(paths).GetOrCreate();
        var second = new LocalAiApiCredentialStore(paths).GetOrCreate();
        Assert.Equal(64, first.Length);
        Assert.All(first, c => Assert.True(Uri.IsHexDigit(c)));
        Assert.Equal(first, second);
        Assert.DoesNotContain(first, Encoding.UTF8.GetString(File.ReadAllBytes(
            paths.ResolveContainedPath("api-credential.dpapi", "credential"))));
        Assert.Empty(Directory.GetFiles(paths.RootDirectory, "*.pending"));
    }

    [Fact]
    public void CorruptCredentialIsNotSilentlyRotated()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new LocalAiPaths(_directory);
        paths.EnsureDirectories();
        var path = paths.ResolveContainedPath("api-credential.dpapi", "credential");
        File.WriteAllBytes(path, []);
        Assert.Throws<InvalidDataException>(() => new LocalAiApiCredentialStore(paths).GetOrCreate());
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ConcurrentCreationConvergesOnOneCredential()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new LocalAiPaths(_directory);
        var keys = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => new LocalAiApiCredentialStore(paths).GetOrCreate())));
        Assert.Single(keys.Distinct());
        Assert.Empty(Directory.GetFiles(paths.RootDirectory, "*.pending"));
    }

    [Fact]
    public async Task ExistingCredential_RetriesTransientSharingViolationWithoutRotation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new LocalAiPaths(_directory);
        var store = new LocalAiApiCredentialStore(paths);
        var expected = store.GetOrCreate();
        var path = paths.ResolveContainedPath("api-credential.dpapi", "credential");
        using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        using var started = new ManualResetEventSlim();
        var read = Task.Run(() =>
        {
            started.Set();
            return store.GetOrCreate();
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(100);
        lease.Dispose();
        Assert.Equal(expected, await read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(paths.RootDirectory, "*.pending"));
    }

    [Fact]
    public void ExistingCredential_StopsRetryingWhenTheSharingLeasePersists()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new LocalAiPaths(_directory);
        var store = new LocalAiApiCredentialStore(paths);
        _ = store.GetOrCreate();
        var path = paths.ResolveContainedPath("api-credential.dpapi", "credential");
        using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<IOException>(() => store.GetOrCreate());
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
        Assert.Empty(Directory.GetFiles(paths.RootDirectory, "*.pending"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
