using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class BootstrapTokenLifecycleTests
{
    [Fact]
    public async Task ClearsBootstrap_OnlyWhenBothRoleTokensDurable()
    {
        using var temp = new TempDirectory("openclaw-bootstrap-owner-");
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://gateway.example",
            BootstrapToken = "bootstrap-secret"
        });
        registry.SetActive("gw-1");
        var identityPath = registry.GetIdentityDirectory("gw-1");
        var identity = new DeviceIdentity(identityPath, NullLogger.Instance);
        identity.Initialize();
        identity.StoreDeviceTokenForRole(
            "operator",
            "operator-device-token",
            ["operator.read"]);

        var lifecycle = new BootstrapTokenLifecycle(
            registry,
            identityStore: null,
            new AlwaysCurrentAttemptLeaseSource(),
            new AllowEndpointCredentialSecurity(),
            new RecordingReconnectScheduler(),
            new RecordingV2SignatureSink(),
            NullLogger.Instance,
            new ConnectionDiagnostics());
        var attempt = new GatewayAttemptStamp(1, "gw-1");

        var clearedWithOnlyOperator =
            await lifecycle.TryClearAfterDurablePairingAsync(
                attempt,
                CancellationToken.None);

        Assert.False(clearedWithOnlyOperator);
        Assert.Equal(
            "bootstrap-secret",
            registry.GetById("gw-1")?.BootstrapToken);

        identity.Initialize();
        identity.StoreDeviceTokenForRole(
            "node",
            "node-device-token");

        var clearedWithBothRoles =
            await lifecycle.TryClearAfterDurablePairingAsync(
                attempt,
                CancellationToken.None);

        Assert.True(clearedWithBothRoles);
        Assert.Null(registry.GetById("gw-1")?.BootstrapToken);
    }

    [Fact]
    public async Task StaleAttempt_DoesNotClearNewerRecord()
    {
        using var temp = new TempDirectory("openclaw-bootstrap-stale-");
        var registry = CreateDurablyPairedRegistry(temp.Path);
        var current = new GatewayAttemptStamp(2, "gw-1");
        var lifecycle = CreateLifecycle(
            registry,
            new CurrentAttemptLeaseSource(current));

        var cleared = await lifecycle.TryClearAfterDurablePairingAsync(
            new GatewayAttemptStamp(1, "gw-1"),
            CancellationToken.None);

        Assert.False(cleared);
        Assert.Equal(
            "bootstrap-secret",
            registry.GetById("gw-1")?.BootstrapToken);
    }

    [Fact]
    public async Task SaveFailure_RestoresBootstrapForRetry()
    {
        using var temp = new TempDirectory("openclaw-bootstrap-save-");
        var registry = CreateDurablyPairedRegistry(
            temp.Path,
            new FailingWriteFileSystem());
        var lifecycle = CreateLifecycle(
            registry,
            new AlwaysCurrentAttemptLeaseSource());

        var cleared = await lifecycle.TryClearAfterDurablePairingAsync(
            new GatewayAttemptStamp(1, "gw-1"),
            CancellationToken.None);

        Assert.False(cleared);
        Assert.Equal(
            "bootstrap-secret",
            registry.GetById("gw-1")?.BootstrapToken);
    }

    [Fact]
    public async Task StaleDeviceTokenEvent_DoesNotWriteIdentity()
    {
        using var temp = new TempDirectory("openclaw-bootstrap-store-");
        var registry = CreateDurablyPairedRegistry(temp.Path);
        var store = new RecordingIdentityStore();
        var lifecycle = new BootstrapTokenLifecycle(
            registry,
            store,
            new CurrentAttemptLeaseSource(
                new GatewayAttemptStamp(2, "gw-1")),
            new AllowEndpointCredentialSecurity(),
            new RecordingReconnectScheduler(),
            new RecordingV2SignatureSink(),
            NullLogger.Instance,
            new ConnectionDiagnostics());

        var result = await lifecycle.HandleDeviceTokenReceivedAsync(
            new GatewayAttemptStamp(1, "gw-1"),
            registry.GetIdentityDirectory("gw-1"),
            new DeviceTokenReceivedEventArgs(
                "new-device-token",
                ["operator.read"],
                "operator"),
            CancellationToken.None);

        Assert.Equal(DeviceTokenHandlingOutcome.IgnoredStale, result.Outcome);
        Assert.Equal(0, store.WriteCount);
    }

    private static BootstrapTokenLifecycle CreateLifecycle(
        GatewayRegistry registry,
        IGatewayAttemptLeaseSource attemptLeases) =>
        new(
            registry,
            identityStore: null,
            attemptLeases,
            new AllowEndpointCredentialSecurity(),
            new RecordingReconnectScheduler(),
            new RecordingV2SignatureSink(),
            NullLogger.Instance,
            new ConnectionDiagnostics());

    private static GatewayRegistry CreateDurablyPairedRegistry(
        string dataPath,
        IFileSystem? fileSystem = null)
    {
        var registry = new GatewayRegistry(dataPath, fileSystem);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://gateway.example",
            BootstrapToken = "bootstrap-secret"
        });
        registry.SetActive("gw-1");
        var identity = new DeviceIdentity(
            registry.GetIdentityDirectory("gw-1"),
            NullLogger.Instance);
        identity.Initialize();
        identity.StoreDeviceTokenForRole(
            "operator",
            "operator-device-token",
            ["operator.read"]);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("node", "node-device-token");
        return registry;
    }

    private sealed class FailingWriteFileSystem : IFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);
        public string ReadAllText(string path) => File.ReadAllText(path);
        public void WriteAllText(string path, string content) =>
            throw new IOException("simulated save failure");
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CopyFile(string source, string destination, bool overwrite) =>
            File.Copy(source, destination, overwrite);
        public void DeleteFile(string path) => File.Delete(path);
    }

    private sealed class RecordingIdentityStore : IDeviceIdentityStore
    {
        public int WriteCount { get; private set; }

        public void StoreToken(
            string identityPath,
            string token,
            string[]? scopes,
            string role) =>
            WriteCount++;
    }
}
