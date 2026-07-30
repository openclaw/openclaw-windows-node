using System.Diagnostics;
using System.Text.Json;
using OpenClaw.TestSupport;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace OpenClaw.Shared.Tests;

public sealed class DeviceIdentityFailClosedTests
{
    private const string IdentityFileName = "device-key-ed25519.json";

    public static TheoryData<string> InvalidIdentityJson => new()
    {
        "{",
        """{"PrivateKeyBase64":""}""",
        """{"DeviceId":"missing-private-key"}""",
        """{"PrivateKeyBase64":"not-base64","DeviceId":"invalid-base64"}""",
        JsonSerializer.Serialize(new
        {
            PrivateKeyBase64 = Convert.ToBase64String(new byte[Ed25519.SecretKeySize - 1]),
            DeviceId = new string('0', 64),
            Algorithm = "Ed25519"
        })
    };

    [Fact]
    public void Initialize_WhenIdentityIsAbsent_CreatesValidIdentityWithoutTempFiles()
    {
        using var temp = new TempDirectory("identity-create-");

        var identity = new DeviceIdentity(temp.Path);
        identity.Initialize();

        Assert.Equal(64, identity.DeviceId.Length);
        Assert.True(File.Exists(temp.Combine(IdentityFileName)));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public void Initialize_WhenFirstWriteFails_ThrowsTypedFailureWithoutTempFiles()
    {
        using var temp = new TempDirectory("identity-create-failure-");
        var fileSystem = new WriteFailureFileSystem(
            new UnauthorizedAccessException("simulated write denial"));

        var error = Assert.Throws<DeviceIdentityLoadException>(
            () => new DeviceIdentity(temp.Path, NullLogger.Instance, fileSystem).Initialize());

        Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        Assert.Equal(DeviceIdentityLoadException.RecoveryMessage, error.Message);
        Assert.False(File.Exists(temp.Combine(IdentityFileName)));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public void Initialize_WhenIdentityIsValid_ReloadsSameIdentity()
    {
        using var temp = new TempDirectory("identity-reload-");
        var original = new DeviceIdentity(temp.Path);
        original.Initialize();
        var originalBytes = File.ReadAllBytes(temp.Combine(IdentityFileName));

        var reloaded = new DeviceIdentity(temp.Path);
        reloaded.Initialize();

        Assert.Equal(original.DeviceId, reloaded.DeviceId);
        Assert.Equal(original.PublicKeyBase64Url, reloaded.PublicKeyBase64Url);
        Assert.Equal(originalBytes, File.ReadAllBytes(temp.Combine(IdentityFileName)));
        AssertNoTempFiles(temp.Path);
    }

    [Theory]
    [MemberData(nameof(InvalidIdentityJson))]
    public void Initialize_WhenIdentityIsInvalid_FailsClosedWithoutMutation(string json)
    {
        using var temp = new TempDirectory("identity-invalid-");
        var path = temp.Combine(IdentityFileName);
        File.WriteAllText(path, json);
        var originalBytes = File.ReadAllBytes(path);

        var error = Assert.Throws<DeviceIdentityLoadException>(
            () => new DeviceIdentity(temp.Path).Initialize());

        Assert.Equal(DeviceIdentityLoadException.RecoveryMessage, error.Message);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public void Initialize_WhenExistingIdentityIsLocked_FailsClosedWithoutMutation()
    {
        using var temp = new TempDirectory("identity-lock-");
        var path = CreateValidIdentity(temp.Path);
        var originalBytes = File.ReadAllBytes(path);

        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<DeviceIdentityLoadException>(
                () => new DeviceIdentity(temp.Path).Initialize());
            Assert.IsType<IOException>(error.InnerException);
        }

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public async Task Initialize_WhenTransientLockClearsAfterFailure_DoesNotOverwriteLater()
    {
        using var temp = new TempDirectory("identity-transient-lock-");
        var path = CreateValidIdentity(temp.Path);
        var originalBytes = File.ReadAllBytes(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var attempt = Task.Run(
            () => Record.Exception(() => new DeviceIdentity(temp.Path).Initialize()));
        var error = await attempt.WaitAsync(TimeSpan.FromSeconds(5));
        held.Dispose();

        Assert.IsType<DeviceIdentityLoadException>(error);
        await Task.Delay(100);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public void Initialize_WhenReadAccessIsDenied_FailsClosedWithoutMutation()
    {
        using var temp = new TempDirectory("identity-access-");
        var path = CreateValidIdentity(temp.Path);
        var originalBytes = File.ReadAllBytes(path);
        var fileSystem = new ReadFailureFileSystem(
            new UnauthorizedAccessException("simulated access denial"));

        var error = Assert.Throws<DeviceIdentityLoadException>(
            () => new DeviceIdentity(temp.Path, NullLogger.Instance, fileSystem).Initialize());

        Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public void Initialize_WhenIdentityDisappearsAfterObservation_DoesNotRecreateIt()
    {
        using var temp = new TempDirectory("identity-delete-race-");
        var path = CreateValidIdentity(temp.Path);
        var fileSystem = new DeleteAfterObservationFileSystem();

        var error = Assert.Throws<DeviceIdentityLoadException>(
            () => new DeviceIdentity(temp.Path, NullLogger.Instance, fileSystem).Initialize());

        Assert.IsType<FileNotFoundException>(error.InnerException);
        Assert.False(File.Exists(path));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public async Task Initialize_ConcurrentFirstCreation_ConvergesOnPersistedIdentity()
    {
        using var temp = new TempDirectory("identity-concurrent-");
        using var fileSystem = new ConcurrentAbsenceFileSystem(participantCount: 2);
        var tasks = Enumerable.Range(0, 2)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    var identity = new DeviceIdentity(temp.Path, NullLogger.Instance, fileSystem);
                    identity.Initialize();
                    return identity.DeviceId;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        var ids = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        var persisted = new DeviceIdentity(temp.Path);
        persisted.Initialize();

        Assert.Equal(2, fileSystem.AbsenceObservationCount);
        Assert.All(ids, id => Assert.Equal(persisted.DeviceId, id));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public async Task Initialize_AcrossProcessRestarts_PreservesIdentity()
    {
        using var temp = new TempDirectory("identity-process-");
        var hostPath = FindTestHost();

        var first = await RunHostAsync(hostPath, temp.Path);
        var second = await RunHostAsync(hostPath, temp.Path);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.StandardOutput, second.StandardOutput);
        Assert.Equal(64, first.StandardOutput.Length);
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public async Task Initialize_AcrossProcessRestartsWithCorruptIdentity_FailsWithoutMutation()
    {
        using var temp = new TempDirectory("identity-process-corrupt-");
        var path = temp.Combine(IdentityFileName);
        File.WriteAllText(path, "{");
        var originalBytes = File.ReadAllBytes(path);
        var hostPath = FindTestHost();

        var first = await RunHostAsync(hostPath, temp.Path);
        var second = await RunHostAsync(hostPath, temp.Path);

        Assert.Equal(2, first.ExitCode);
        Assert.Equal(2, second.ExitCode);
        Assert.Contains(DeviceIdentityLoadException.RecoveryMessage, first.StandardError);
        Assert.Contains(DeviceIdentityLoadException.RecoveryMessage, second.StandardError);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public void GatewayClient_WhenPersistedIdentityIsCorrupt_ThrowsTypedFailure()
    {
        using var temp = new TempDirectory("identity-operator-client-");
        var path = temp.Combine(IdentityFileName);
        File.WriteAllText(path, "{");
        var originalBytes = File.ReadAllBytes(path);

        Assert.Throws<DeviceIdentityLoadException>(
            () => new OpenClawGatewayClient(
                "ws://127.0.0.1:18789",
                "test-token",
                identityPath: temp.Path));

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public void WindowsNodeClient_WhenPersistedIdentityIsCorrupt_ThrowsTypedFailure()
    {
        using var temp = new TempDirectory("identity-node-client-");
        var path = temp.Combine(IdentityFileName);
        File.WriteAllText(path, "{");
        var originalBytes = File.ReadAllBytes(path);

        Assert.Throws<DeviceIdentityLoadException>(
            () => new WindowsNodeClient(
                "ws://127.0.0.1:18789",
                "test-token",
                temp.Path));

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public void ReadStoredDeviceToken_WhenKeyMaterialIsInvalid_ReturnsCorrupt()
    {
        using var temp = new TempDirectory("identity-token-reader-invalid-");
        var path = temp.Combine(IdentityFileName);
        File.WriteAllText(path, CreateParseableInvalidIdentityJson());
        var originalBytes = File.ReadAllBytes(path);

        var result = DeviceIdentity.ReadStoredDeviceToken(temp.Path);

        Assert.Equal(DeviceTokenReadStatus.Corrupt, result.Status);
        Assert.Null(result.Token);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public void TryClearAllDeviceTokens_WhenKeyMaterialIsInvalid_DoesNotMutate()
    {
        using var temp = new TempDirectory("identity-clear-invalid-");
        var path = temp.Combine(IdentityFileName);
        var json = JsonSerializer.Serialize(new
        {
            PrivateKeyBase64 = Convert.ToBase64String(new byte[Ed25519.SecretKeySize - 1]),
            DeviceId = new string('0', 64),
            Algorithm = "Ed25519",
            DeviceToken = "operator-token"
        });
        File.WriteAllText(path, json);
        var originalBytes = File.ReadAllBytes(path);

        Assert.False(DeviceIdentity.TryClearAllDeviceTokens(temp.Path));

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("node")]
    public void StoreDeviceTokenForRole_WhenAtomicReplaceFails_PreservesDurableAndInMemoryToken(string role)
    {
        using var temp = new TempDirectory("identity-token-write-failure-");
        var identity = new DeviceIdentity(temp.Path, NullLogger.Instance);
        identity.Initialize();
        identity.StoreDeviceTokenForRole(role, "old-token", ["old.scope"]);
        var path = temp.Combine(IdentityFileName);
        var originalBytes = File.ReadAllBytes(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.Throws<DeviceIdentityLoadException>(
                () => identity.StoreDeviceTokenForRole(role, "new-token", ["new.scope"]));
            Assert.True(error.InnerException is IOException or UnauthorizedAccessException);
        }

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.Equal("old-token", role == "operator" ? identity.DeviceToken : identity.NodeDeviceToken);
        Assert.Equal(
            ["old.scope"],
            role == "operator" ? identity.DeviceTokenScopes : identity.NodeDeviceTokenScopes);
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public void ReadStoredDeviceToken_WhenReadAccessIsDenied_ReturnsUnreadable()
    {
        using var temp = new TempDirectory("identity-token-reader-access-");
        var path = CreateValidIdentity(temp.Path);
        var originalBytes = File.ReadAllBytes(path);
        var fileSystem = new ReadFailureFileSystem(
            new UnauthorizedAccessException("simulated access denial"));

        var result = DeviceIdentity.ReadStoredDeviceTokenForRole(
            temp.Path,
            "operator",
            NullLogger.Instance,
            fileSystem);

        Assert.Equal(DeviceTokenReadStatus.Unreadable, result.Status);
        Assert.Null(result.Token);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public void WindowsNodeClient_WhenStoredCredentialProbeFindsInvalidKeyMaterial_ThrowsTypedFailure()
    {
        using var temp = new TempDirectory("identity-node-probe-invalid-");
        var path = temp.Combine(IdentityFileName);
        File.WriteAllText(path, CreateParseableInvalidIdentityJson());
        var originalBytes = File.ReadAllBytes(path);

        Assert.Throws<DeviceIdentityLoadException>(
            () => new WindowsNodeClient(
                "ws://127.0.0.1:18789",
                string.Empty,
                temp.Path));

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    [Fact]
    public void ConvenienceTokenReaders_WhenKeyMaterialIsInvalid_ThrowTypedFailure()
    {
        using var temp = new TempDirectory("identity-token-convenience-invalid-");
        var path = temp.Combine(IdentityFileName);
        File.WriteAllText(path, CreateParseableInvalidIdentityJson());
        var originalBytes = File.ReadAllBytes(path);

        Assert.Throws<DeviceIdentityLoadException>(
            () => DeviceIdentity.TryReadStoredDeviceToken(temp.Path));
        Assert.Throws<DeviceIdentityLoadException>(
            () => DeviceIdentity.TryReadStoredDeviceTokenForRole(temp.Path, "node"));
        Assert.Throws<DeviceIdentityLoadException>(
            () => DeviceIdentity.HasStoredDeviceToken(temp.Path));
        Assert.Throws<DeviceIdentityLoadException>(
            () => DeviceIdentity.HasStoredDeviceTokenForRole(temp.Path, "operator"));

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperatorCli_WhenPersistedIdentityIsCorrupt_ReturnsSafeFailureWithoutStackTrace(
        bool verbose)
    {
        using var temp = new TempDirectory("identity-operator-cli-invalid-");
        var path = temp.Combine(IdentityFileName);
        File.WriteAllText(path, "{");
        File.WriteAllText(temp.Combine("settings.json"), "{}");
        var originalBytes = File.ReadAllBytes(path);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(FindOperatorCli());
        startInfo.ArgumentList.Add("--settings");
        startInfo.ArgumentList.Add(temp.Combine("settings.json"));
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add("ws://127.0.0.1:18789");
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add("test-token");
        if (verbose)
            startInfo.ArgumentList.Add("--verbose");
        startInfo.Environment["OPENCLAW_TRAY_DATA_DIR"] = temp.Path;

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = await process!.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, process.ExitCode);
        Assert.Equal(string.Empty, standardOutput.Trim());
        Assert.Equal(DeviceIdentityLoadException.RecoveryMessage, standardError.Trim());
        Assert.DoesNotContain(nameof(DeviceIdentityLoadException) + ":", standardError);
        Assert.DoesNotContain(" at ", standardError);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        AssertNoTempFiles(temp.Path);
    }

    private static string CreateParseableInvalidIdentityJson() =>
        JsonSerializer.Serialize(new
        {
            PrivateKeyBase64 = Convert.ToBase64String(new byte[Ed25519.SecretKeySize - 1]),
            PublicKeyBase64 = Convert.ToBase64String(new byte[Ed25519.PublicKeySize]),
            DeviceId = new string('0', 64),
            Algorithm = "Ed25519"
        });

    private static string CreateValidIdentity(string directory)
    {
        var identity = new DeviceIdentity(directory);
        identity.Initialize();
        return Path.Combine(directory, IdentityFileName);
    }

    private static void AssertNoTempFiles(string directory)
    {
        Assert.Empty(Directory.GetFiles(directory, $".{IdentityFileName}.*.tmp"));
    }

    private static string FindTestHost()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null &&
               !File.Exists(Path.Combine(current.FullName, "openclaw-windows-node.slnx")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        var frameworkDirectory = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = frameworkDirectory.Parent?.Name;
        Assert.False(string.IsNullOrWhiteSpace(configuration));
        var hostPath = Path.Combine(
            current!.FullName,
            "tests",
            "OpenClaw.Shared.TestHost",
            "bin",
            configuration!,
            "net10.0",
            "OpenClaw.Shared.TestHost.dll");
        Assert.True(File.Exists(hostPath), $"Identity test host was not built: {hostPath}");
        return hostPath;
    }

    private static string FindOperatorCli()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null &&
               !File.Exists(Path.Combine(current.FullName, "openclaw-windows-node.slnx")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        var frameworkDirectory = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = frameworkDirectory.Parent?.Name;
        Assert.False(string.IsNullOrWhiteSpace(configuration));
        var cliPath = Path.Combine(
            current!.FullName,
            "src",
            "OpenClaw.Cli",
            "bin",
            configuration!,
            "net10.0",
            "OpenClaw.Cli.dll");
        Assert.True(File.Exists(cliPath), $"Operator CLI was not built: {cliPath}");
        return cliPath;
    }

    private static async Task<ProcessResult> RunHostAsync(string hostPath, string identityDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(hostPath);
        startInfo.ArgumentList.Add(identityDirectory);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = await process!.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return new ProcessResult(process.ExitCode, standardOutput.Trim(), standardError.Trim());
    }

    private sealed class ReadFailureFileSystem(Exception failure) : DelegatingFileSystem
    {
        public override string ReadAllText(string path) => throw failure;
    }

    private sealed class DeleteAfterObservationFileSystem : DelegatingFileSystem
    {
        public override bool IdentityFileExists(string path)
        {
            var exists = base.IdentityFileExists(path);
            if (exists)
                File.Delete(path);
            return exists;
        }
    }

    private sealed class ConcurrentAbsenceFileSystem : DelegatingFileSystem, IDisposable
    {
        private readonly Barrier _barrier;
        private int _absenceObservationCount;

        public ConcurrentAbsenceFileSystem(int participantCount)
        {
            _barrier = new Barrier(participantCount);
        }

        public int AbsenceObservationCount => Volatile.Read(ref _absenceObservationCount);

        public override bool IdentityFileExists(string path)
        {
            var exists = base.IdentityFileExists(path);
            if (!exists)
            {
                Interlocked.Increment(ref _absenceObservationCount);
                if (!_barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Concurrent identity initialization did not reach the absence barrier.");
            }
            return exists;
        }

        public void Dispose() => _barrier.Dispose();
    }

    private abstract class DelegatingFileSystem : IDeviceIdentityFileSystem
    {
        public virtual bool IdentityFileExists(string path) =>
            DeviceIdentityFileSystem.Instance.IdentityFileExists(path);

        public bool DirectoryExists(string path) =>
            DeviceIdentityFileSystem.Instance.DirectoryExists(path);

        public void CreateDirectory(string path) =>
            DeviceIdentityFileSystem.Instance.CreateDirectory(path);

        public virtual string ReadAllText(string path) =>
            DeviceIdentityFileSystem.Instance.ReadAllText(path);

        public virtual void WriteAllText(string path, string content) =>
            DeviceIdentityFileSystem.Instance.WriteAllText(path, content);

        public void MoveFileNoOverwrite(string source, string destination) =>
            DeviceIdentityFileSystem.Instance.MoveFileNoOverwrite(source, destination);

        public bool FileExists(string path) =>
            DeviceIdentityFileSystem.Instance.FileExists(path);

        public void DeleteFile(string path) =>
            DeviceIdentityFileSystem.Instance.DeleteFile(path);
    }

    private sealed class WriteFailureFileSystem(Exception failure) : DelegatingFileSystem
    {
        public override void WriteAllText(string path, string content) => throw failure;
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
