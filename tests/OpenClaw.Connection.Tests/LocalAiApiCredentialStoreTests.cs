using Microsoft.Win32.SafeHandles;
using OpenClaw.Connection.LocalAi;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void InvalidCiphertextLengthIsNotSilentlyRotated(int length)
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new LocalAiPaths(_directory);
        paths.EnsureDirectories();
        var path = paths.ResolveContainedPath("api-credential.dpapi", "credential");
        byte[] corrupt = new byte[length];
        File.WriteAllBytes(path, corrupt);
        Assert.Throws<InvalidDataException>(() => new LocalAiApiCredentialStore(paths).GetOrCreate());
        Assert.Equal(corrupt, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(paths.RootDirectory, "*.pending"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public void CorruptCredentialIsNotSilentlyRotated(int length)
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new LocalAiPaths(_directory);
        paths.EnsureDirectories();
        var path = paths.ResolveContainedPath("api-credential.dpapi", "credential");
        byte[] corrupt = new byte[length];
        File.WriteAllBytes(path, corrupt);
        Assert.Throws<CryptographicException>(() => new LocalAiApiCredentialStore(paths).GetOrCreate());
        Assert.Equal(corrupt, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(paths.RootDirectory, "*.pending"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void InvalidPlaintextLengthIsNotSilentlyRotated(int length)
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new LocalAiPaths(_directory);
        paths.EnsureDirectories();
        var path = paths.ResolveContainedPath("api-credential.dpapi", "credential");
        byte[] protectedBytes = ProtectedData.Protect(new byte[length],
            Encoding.UTF8.GetBytes("OpenClaw.LocalAI.ApiCredential.v1"), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, protectedBytes);
        Assert.Throws<InvalidDataException>(() => new LocalAiApiCredentialStore(paths).GetOrCreate());
        Assert.Equal(protectedBytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(paths.RootDirectory, "*.pending"));
    }

    [Fact]
    public void PublishedCredentialCanBeReadBeforeRenameHandleCloses()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new LocalAiPaths(_directory);
        var expected = new LocalAiApiCredentialStore(paths).GetOrCreate();
        var path = paths.ResolveContainedPath("api-credential.dpapi", "credential");
        var staging = paths.ResolveContainedPath("held-credential.pending", "credential");
        File.Move(path, staging, overwrite: false);

        // Stretch the publisher's DELETE-handle lifetime after the final name becomes visible.
        using (var publisher = CreateFileW(staging, 0x00010000,
                   FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, 0x00000080, IntPtr.Zero))
        {
            if (publisher.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            File.Move(staging, path, overwrite: false);
            Assert.True(File.Exists(path));
            Assert.InRange(new FileInfo(path).Length, 1L, 4096L);
            var blocked = Assert.Throws<IOException>(() => File.ReadAllBytes(path));
            Assert.Equal(32, blocked.HResult & 0xFFFF);
            Assert.Equal(expected, new LocalAiApiCredentialStore(paths).GetOrCreate());
        }

        Assert.Equal(expected, new LocalAiApiCredentialStore(paths).GetOrCreate());
        Assert.Empty(Directory.GetFiles(paths.RootDirectory, "*.pending"));
    }

    [Fact]
    public void CredentialReadDoesNotShareConcurrentWriteAccess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new LocalAiPaths(_directory);
        var expected = new LocalAiApiCredentialStore(paths).GetOrCreate();
        var path = paths.ResolveContainedPath("api-credential.dpapi", "credential");
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var blocked = Assert.Throws<IOException>(() => new LocalAiApiCredentialStore(paths).GetOrCreate());
            Assert.Equal(32, blocked.HResult & 0xFFFF);
        }
        Assert.Equal(expected, new LocalAiApiCredentialStore(paths).GetOrCreate());
        Assert.Empty(Directory.GetFiles(paths.RootDirectory, "*.pending"));
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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string path, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
