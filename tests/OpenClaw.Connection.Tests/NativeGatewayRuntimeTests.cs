using System.Net;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class NativeGatewayRuntimeTests : IAsyncDisposable
{
    private const string Family = "OpenClaw.Gateway_123456789abcd";
    private readonly TempDirectory _directory = new(Path.Combine(Directory.GetCurrentDirectory(), "native-gateway-test-"));
    private readonly GatewayRegistry _registry;
    private readonly FakeResolver _resolver = new();
    private readonly FakeHost _host = new();
    private readonly NativeGatewayRuntime _runtime;
    private readonly GatewayRecord _record = new GatewayRecordBuilder()
        .WithId("gw-native").WithUrl("ws://127.0.0.1:18789").Local().Build()
        with { NativePackageFamilyName = Family };
    private Func<WindowsTcpListenerSnapshotResult>? _snapshot;

    public NativeGatewayRuntimeTests()
    {
        _registry = new GatewayRegistry(_directory.Path);
        Prepare(_record);
        _runtime = new NativeGatewayRuntime(_registry, _resolver, NullLogger.Instance, _host,
            () => _snapshot?.Invoke() ?? Snapshot(), TimeSpan.FromMilliseconds(300));
    }

    private void Prepare(GatewayRecord record)
    {
        Directory.CreateDirectory(NativeGatewayPaths.GetStateDirectory(_registry, record.Id));
        File.WriteAllText(NativeGatewayPaths.GetConfigPath(_registry, record.Id), "{}");
    }

    private WindowsTcpListenerSnapshotResult Snapshot() => new(
        _host.Current is { HasExited: false, Listening: true } process
            ? [process.Listener]
            : [], true, true);

    [Fact]
    public async Task HttpCredentials_RequireFreshOwnedListenerWithoutWslFallback()
    {
        var record = _record with { SharedGatewayToken = "fixture-shared-token" };
        _registry.AddOrUpdate(record);
        _registry.SetActive(record.Id);
        var authorizer = new InteractiveGatewayEndpointAuthorizer(_runtime,
            (_, _) => throw new InvalidOperationException("Native HTTP must not use WSL authorization."),
            NullLogger.Instance);

        bool Resolve(out InteractiveGatewayCredential? credential) =>
            InteractiveGatewayCredentialResolver.TryResolve(
                _registry, _directory.Path, DeviceIdentityFileReader.Instance, record.Url,
                "legacy-must-not-leak", null, authorizer.IsCredentialAllowed, out credential);

        Assert.False(Resolve(out var credential));
        Assert.Null(credential);
        Assert.Empty(_host.Processes);
        await _runtime.EnsureRunningAsync(record, default);
        Assert.True(Resolve(out credential));
        Assert.Equal(record.SharedGatewayToken, credential!.Token);
        Assert.Equal(record.Url, credential.GatewayUrl);

        _snapshot = () => new([UnknownListener()], true, true);
        Assert.False(Resolve(out credential));
        Assert.Null(credential);
        Assert.Single(_host.Processes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HttpCredentials_NonNativePreservesExistingAuthorization(bool allowed)
    {
        var called = false;
        var credential = new GatewayCredential("fixture", false, CredentialResolver.SourceSharedGatewayToken);
        var nonNative = _record with { NativePackageFamilyName = null };
        var authorizer = new InteractiveGatewayEndpointAuthorizer(_runtime, (record, candidate) =>
        {
            Assert.Same(nonNative, record);
            Assert.Same(credential, candidate);
            called = true;
            return allowed;
        }, NullLogger.Instance);

        Assert.Equal(allowed, authorizer.IsCredentialAllowed(nonNative, credential));
        Assert.True(called);
        Assert.Empty(_host.Processes);
    }

    [Fact]
    public async Task HttpInspection_DeniesBusyRuntimeWithoutWaitingOrStarting()
    {
        _host.AfterStart = _ =>
        {
            var result = _runtime.Inspect(_record);
            Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
            Assert.Contains("in progress", result.Detail);
        };
        await _runtime.EnsureRunningAsync(_record, default);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, _runtime.Inspect(_record).Kind);
        Assert.Single(_host.Processes);
    }

    [Fact]
    public async Task HttpInspection_DeniesDifferentRecordAndChangedSecondSnapshot()
    {
        await _runtime.EnsureRunningAsync(_record, default);
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener,
            _runtime.Inspect(_record with { Id = "different-profile" }).Kind);
        var snapshots = 0;
        _snapshot = () => ++snapshots == 1 ? Snapshot() : new([UnknownListener()], true, true);
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, _runtime.Inspect(_record).Kind);
        Assert.Equal(2, snapshots);
    }

    [Fact]
    public async Task HttpCredentials_DenyDisposedRuntime()
    {
        await _runtime.EnsureRunningAsync(_record, default);
        await _runtime.DisposeAsync();
        var authorizer = new InteractiveGatewayEndpointAuthorizer(_runtime, (_, _) => true, NullLogger.Instance);
        Assert.False(authorizer.IsCredentialAllowed(_record,
            new GatewayCredential("fixture", false, CredentialResolver.SourceDeviceToken)));
    }

    [Fact]
    public async Task Ensure_IsIdempotentAndResolvesPackageEveryTime()
    {
        await _runtime.EnsureRunningAsync(_record, default);
        await _runtime.EnsureRunningAsync(_record with { FriendlyName = "Edited" }, default);

        Assert.Single(_host.Processes);
        Assert.Equal(2, _resolver.Calls);
        Assert.Equal(Family, _resolver.ExpectedFamily);
        var spec = Assert.Single(_host.Specs);
        Assert.Equal(_resolver.Package.OpenClawAliasPath, spec.ExecutablePath);
        Assert.Equal(Family, spec.PackageFamilyName);
        Assert.Equal(18789, spec.Port);
        Assert.Equal(NativeGatewayPaths.GetStateDirectory(_registry, _record.Id), spec.WorkingDirectory);
        Assert.Equal(spec.WorkingDirectory, spec.Environment["OPENCLAW_STATE_DIR"]);
        Assert.Equal(NativeGatewayPaths.GetConfigPath(_registry, _record.Id), spec.Environment["OPENCLAW_CONFIG_PATH"]);
        Assert.Equal("external", spec.Environment["OPENCLAW_SUPERVISOR_MODE"]);
        Assert.Equal("external", spec.Environment["OPENCLAW_SERVICE_REPAIR_POLICY"]);
        Assert.Equal("1", spec.Environment["OPENCLAW_NO_AUTO_UPDATE"]);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway,
            (await _runtime.InspectAsync(_record, default)).Kind);
    }

    [Fact]
    public async Task StorePackage_UsesQualifiedAliasesAndPreservesOwnedListenerVerification()
    {
        const string storeFamily = "OpenClawFoundation.OpenClawGateway_123456789abcd";
        var aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", storeFamily);
        _resolver.Package = new(storeFamily, "1.0.0.0",
            Path.Combine(aliases, "openclaw.exe"), Path.Combine(aliases, "clawctl.exe"));
        var record = _record with { NativePackageFamilyName = storeFamily };

        await _runtime.EnsureRunningAsync(record, default);

        var spec = Assert.Single(_host.Specs);
        Assert.Equal(storeFamily, spec.PackageFamilyName);
        Assert.Equal(storeFamily, _resolver.ExpectedFamily);
        Assert.Equal(_resolver.Package.OpenClawAliasPath, spec.ExecutablePath);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway,
            (await _runtime.InspectAsync(record, default)).Kind);
        await _runtime.StopAsync(default);
        Assert.True(Assert.Single(_host.Processes).Disposed);
    }

    [Fact]
    public async Task StoreRecord_CannotSilentlySwitchToDevelopmentPackage()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runtime.EnsureRunningAsync(
            _record with { NativePackageFamilyName = "OpenClawFoundation.OpenClawGateway_123456789abcd" }, default));
        Assert.Empty(_host.Processes);
    }

    [Fact]
    public async Task LaunchMapsCrossPackagePaths_WithoutChangingLogicalStateOrSavingRecord()
    {
        var physicalRoot = Path.Combine(_directory.Path, "physical");
        _resolver.DataPathResolver = path => Path.Combine(physicalRoot, Path.GetRelativePath(_directory.Path, path));

        await _runtime.EnsureRunningAsync(_record, default);

        var spec = Assert.Single(_host.Specs);
        var logicalState = NativeGatewayPaths.GetStateDirectory(_registry, _record.Id);
        var logicalConfig = NativeGatewayPaths.GetConfigPath(_registry, _record.Id);
        Assert.Equal(_resolver.ResolveDataPath(logicalState), spec.WorkingDirectory);
        Assert.Equal(spec.WorkingDirectory, spec.Environment["OPENCLAW_STATE_DIR"]);
        Assert.Equal(_resolver.ResolveDataPath(logicalConfig), spec.Environment["OPENCLAW_CONFIG_PATH"]);
        Assert.Equal("external", spec.Environment["OPENCLAW_SUPERVISOR_MODE"]);
        Assert.True(File.Exists(logicalConfig));
        Assert.False(Directory.Exists(physicalRoot));
        Assert.Empty(_registry.GetAll());
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway,
            (await _runtime.InspectAsync(_record, default)).Kind);
        await _runtime.StopAsync(default);
        Assert.Equal("{}", File.ReadAllText(logicalConfig));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-state")]
    public async Task InvalidMappedPath_IsRejectedBeforeLaunch(string mappedPath)
    {
        _resolver.DataPathResolver = _ => mappedPath;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runtime.EnsureRunningAsync(_record, default));
        Assert.Empty(_host.Processes);
    }

    [Fact]
    public void ResolverDefaultDataPathMapping_IsIdentity()
    {
        INativeGatewayPackageResolver resolver = new PackageOnlyResolver();
        var logical = NativeGatewayPaths.GetConfigPath(_registry, _record.Id);
        Assert.Equal(logical, resolver.ResolveDataPath(logical));
    }

    [Fact]
    public async Task CrashedChild_RestartsOnlyOnNextEnsure()
    {
        await _runtime.EnsureRunningAsync(_record, default);
        var first = _host.Current!;
        first.HasExited = true;
        Assert.Equal(GatewayEndpointProvenanceKind.NoListener, (await _runtime.InspectAsync(_record, default)).Kind);
        Assert.Single(_host.Processes);

        await _runtime.EnsureRunningAsync(_record, default);
        Assert.True(first.Disposed);
        Assert.Equal(2, _host.Processes.Count);
    }

    [Fact]
    public async Task SwitchingRecords_StopsOldChildBeforeStartingNew()
    {
        await _runtime.EnsureRunningAsync(_record, default);
        var first = _host.Current!;
        var next = _record with { Id = "gw-next" };
        Prepare(next);
        _host.BeforeStart = () => Assert.True(first.Disposed);
        await _runtime.EnsureRunningAsync(next, default);

        Assert.Equal(2, _host.Processes.Count);
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener,
            (await _runtime.InspectAsync(_record, default)).Kind);
    }

    [Fact]
    public async Task OccupiedPort_IsNeverAdoptedOrKilled()
    {
        _snapshot = () => new([UnknownListener()], true, true);
        var failure = await Assert.ThrowsAsync<NativeGatewayListenerException>(
            () => _runtime.EnsureRunningAsync(_record, default));
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, failure.Provenance.Kind);
        Assert.Equal(18789, failure.Provenance.Port);
        Assert.Empty(_host.Processes);
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener,
            (await _runtime.InspectAsync(_record, default)).Kind);
    }

    [Fact]
    public async Task UnverifiedPortAfterStart_CleansUpOnlyOwnedChild()
    {
        _snapshot = () => _host.Current is null ? new([], true, true) : new([UnknownListener()], true, true);
        var failure = await Assert.ThrowsAsync<NativeGatewayListenerException>(
            () => _runtime.EnsureRunningAsync(_record, default));
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, failure.Provenance.Kind);
        Assert.True(Assert.Single(_host.Processes).Disposed);
    }

    [Fact]
    public async Task ChangedPackage_IsRejectedAndExistingChildStopped()
    {
        await _runtime.EnsureRunningAsync(_record, default);
        _resolver.Package = _resolver.Package with { PackageFamilyName = "OpenClaw.Gateway_0000000000000" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runtime.EnsureRunningAsync(_record, default));
        Assert.True(Assert.Single(_host.Processes).Disposed);
    }

    [Fact]
    public async Task UnqualifiedExecutable_IsRejectedBeforeStart()
    {
        _resolver.Package = _resolver.Package with { OpenClawAliasPath = "openclaw.exe" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runtime.EnsureRunningAsync(_record, default));
        Assert.Empty(_host.Processes);
    }

    [Fact]
    public async Task MissingConfig_IsNotCreatedByRuntime()
    {
        File.Delete(NativeGatewayPaths.GetConfigPath(_registry, _record.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runtime.EnsureRunningAsync(_record, default));
        Assert.Empty(_host.Processes);
        Assert.False(File.Exists(NativeGatewayPaths.GetConfigPath(_registry, _record.Id)));
    }

    [Fact]
    public async Task CancellationAfterSpawn_CleansUpOwnedChild()
    {
        using var cancellation = new CancellationTokenSource();
        _host.AfterStart = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runtime.EnsureRunningAsync(_record, cancellation.Token));
        Assert.True(Assert.Single(_host.Processes).Disposed);
    }

    [Fact]
    public async Task CancellationBeforeStart_DoesNotResolveOrSpawn()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runtime.EnsureRunningAsync(_record, cancellation.Token));
        Assert.Empty(_host.Processes);
        Assert.Equal(0, _resolver.Calls);
    }

    [Fact]
    public async Task Timeout_CleansUpChild()
    {
        _host.AfterStart = process => process.Listening = false;
        await Assert.ThrowsAsync<TimeoutException>(() => _runtime.EnsureRunningAsync(_record, default));
        Assert.True(Assert.Single(_host.Processes).Disposed);
    }

    [Fact]
    public async Task Stop_IsIdempotentAndPreservesConfig()
    {
        await _runtime.EnsureRunningAsync(_record, default);
        await _runtime.StopAsync(default);
        await _runtime.StopAsync(default);
        Assert.True(Assert.Single(_host.Processes).Disposed);
        Assert.Equal("{}", File.ReadAllText(NativeGatewayPaths.GetConfigPath(_registry, _record.Id)));
        Assert.Equal(GatewayEndpointProvenanceKind.NoListener, (await _runtime.InspectAsync(_record, default)).Kind);
        Assert.Single(_host.Processes);

        // An explicit later connection can start again; no automatic restart exists.
        await _runtime.EnsureRunningAsync(_record, default);
        Assert.Equal(2, _host.Processes.Count);
    }

    [Fact]
    public async Task StopDuringStartup_CancelsPendingEnsure()
    {
        var spawned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.AfterStart = process => { process.Listening = false; spawned.SetResult(); };
        var ensure = _runtime.EnsureRunningAsync(_record, default);
        await spawned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await _runtime.StopAsync(default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ensure);
        Assert.True(Assert.Single(_host.Processes).Disposed);
    }

    [Fact]
    public async Task DisposeDuringStartup_CancelsAndPreventsRestart()
    {
        var spawned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.AfterStart = process => { process.Listening = false; spawned.SetResult(); };
        var ensure = _runtime.EnsureRunningAsync(_record, default);
        await spawned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await _runtime.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ensure);
        Assert.True(Assert.Single(_host.Processes).Disposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runtime.EnsureRunningAsync(_record, default));
    }

    [Fact]
    public async Task ConcurrentEnsures_CreateOneProcess()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _runtime.EnsureRunningAsync(_record, default)));
        Assert.Single(_host.Processes);
    }

    [Fact]
    public async Task IncompleteSnapshot_FailsClosedBeforeStart()
    {
        _snapshot = () => new([], true, false);
        await Assert.ThrowsAsync<NativeGatewayListenerException>(() => _runtime.EnsureRunningAsync(_record, default));
        Assert.Empty(_host.Processes);
    }

    [Fact]
    public async Task ListenerLifetimeMismatch_IsNotTrusted()
    {
        await _runtime.EnsureRunningAsync(_record, default);
        _snapshot = () => new([_host.Current!.Listener with { ProcessStartTimeUtc = DateTime.UtcNow.AddDays(-1) }], true, true);
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, (await _runtime.InspectAsync(_record, default)).Kind);
    }

    [Fact]
    public async Task SnapshotReplacement_IsNotTrusted()
    {
        await _runtime.EnsureRunningAsync(_record, default);
        var count = 0;
        _snapshot = () => ++count == 1 ? Snapshot() : new([UnknownListener()], true, true);
        var result = await _runtime.InspectAsync(_record, default);
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
        Assert.Equal(GatewayEndpointProvenanceFailureReason.ListenerSnapshotChanged, result.FailureReason);
    }

    [Fact]
    public async Task WildcardListener_IsNotTrustedEvenIfJobOwned()
    {
        _host.AfterStart = process => process.Listener = process.Listener with { Address = IPAddress.Any };
        await Assert.ThrowsAsync<NativeGatewayListenerException>(() => _runtime.EnsureRunningAsync(_record, default));
        Assert.True(Assert.Single(_host.Processes).Disposed);
    }

    [Theory]
    [InlineData("wss://127.0.0.1:18789")]
    [InlineData("ws://example.com:18789")]
    [InlineData("ws://user@127.0.0.1:18789")]
    [InlineData("ws://127.0.0.1:18789?token=secret")]
    [InlineData("ws://127.0.0.1:18789#fragment")]
    [InlineData("ws://127.0.0.1:18789/other")]
    [InlineData("file:///gateway")]
    public async Task InvalidEndpoint_IsRejected(string url)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _runtime.EnsureRunningAsync(_record with { Url = url }, default));
        Assert.Empty(_host.Processes);
        Assert.Equal(0, _resolver.Calls);
    }

    [Fact]
    public async Task TunnelAndDistroConflicts_AreRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _runtime.EnsureRunningAsync(
            _record with { SshTunnel = new("user", "host", 18789, 18789) }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _runtime.EnsureRunningAsync(
            _record with { SetupManagedDistroName = "OpenClawGateway" }, default));
        Assert.Empty(_host.Processes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("C:\\escape")]
    [InlineData("id:stream")]
    [InlineData("id.")]
    [InlineData("id ")]
    [InlineData("NUL")]
    [InlineData("com1")]
    public void UnsafeIds_AreRejected(string id) =>
        Assert.Throws<ArgumentException>(() => NativeGatewayPaths.GetStateDirectory(_registry, id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Other.Gateway_123456789abcd")]
    [InlineData("OpenClaw.Gateway_..\\escape")]
    [InlineData("OpenClaw.Gateway_short")]
    [InlineData("OpenClawFoundation.OpenClawGateway_..\\escape")]
    [InlineData("OpenClawFoundation.OpenClawGateway_short")]
    [InlineData("Other.OpenClawFoundation.OpenClawGateway_123456789abcd")]
    public async Task InvalidFamily_IsRejected(string? family)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _runtime.EnsureRunningAsync(
            _record with { NativePackageFamilyName = family }, default));
        Assert.Empty(_host.Processes);
    }

    private static WindowsTcpListenerInfo UnknownListener() =>
        new(IPAddress.Loopback, 18789, 9999, null, null, DateTime.UnixEpoch);

    public async ValueTask DisposeAsync()
    {
        await _runtime.DisposeAsync();
        _directory.Dispose();
    }

    private sealed class FakeResolver : INativeGatewayPackageResolver
    {
        public NativeGatewayPackage Package { get; set; } = new(Family, "1.2.3.4", Alias("openclaw.exe"), Alias("clawctl.exe"));
        public Func<string, string>? DataPathResolver { get; set; }
        public string ResolveDataPath(string path) => DataPathResolver?.Invoke(path) ?? path;
        public int Calls { get; private set; }
        public string? ExpectedFamily { get; private set; }
        public Task<NativeGatewayPackage> ResolveAsync(string expectedFamily, CancellationToken cancellationToken)
        {
            ExpectedFamily = expectedFamily;
            return ResolveAsync(cancellationToken);
        }
        public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Package);
        }
        private static string Alias(string name) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", Family, name);
    }

    private sealed class PackageOnlyResolver : INativeGatewayPackageResolver
    {
        public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHost : INativeGatewayProcessHost
    {
        public List<FakeProcess> Processes { get; } = [];
        public List<NativeGatewayStartSpec> Specs { get; } = [];
        public FakeProcess? Current => Processes.LastOrDefault();
        public Action? BeforeStart { get; set; }
        public Action<FakeProcess>? AfterStart { get; set; }
        public Task<INativeGatewayProcess> StartAsync(NativeGatewayStartSpec spec, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeStart?.Invoke();
            var process = new FakeProcess(100 + Processes.Count, spec.Port);
            Processes.Add(process);
            Specs.Add(spec);
            AfterStart?.Invoke(process);
            return Task.FromResult<INativeGatewayProcess>(process);
        }
    }

    private sealed class FakeProcess(int pid, int port) : INativeGatewayProcess
    {
        public bool HasExited { get; set; }
        public bool Listening { get; set; } = true;
        public bool Disposed { get; private set; }
        public WindowsTcpListenerInfo Listener { get; set; } =
            new(IPAddress.Loopback, port, pid, "fake-gateway", null, DateTime.UnixEpoch.AddSeconds(pid));
        public bool Owns(WindowsTcpListenerInfo listener) =>
            !HasExited && listener.ProcessId == Listener.ProcessId &&
            listener.ProcessStartTimeUtc == Listener.ProcessStartTimeUtc;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            HasExited = true;
            return ValueTask.CompletedTask;
        }
    }
}
