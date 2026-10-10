using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayDirectConnectServiceTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialCommitCasConflictCleansOnlyUnadoptedNewRealmIdentity(bool adoptCandidate)
    {
        var previous = AddPreviousGateway();
        var previousIdentity = CreateIdentity(previous.Id);
        var beforeIdentity = File.ReadAllBytes(Path.Combine(_registry.GetIdentityDirectory(previous.Id), "device-key-ed25519.json"));
        var fs = new BeforeRegistryReadFileSystem();
        var registry = new GatewayRegistry(_tempDir, fs);
        registry.Load();
        string? candidateId = null;
        fs.BeforeRead = () =>
        {
            var candidate = Directory.GetDirectories(Path.Combine(_tempDir, "gateways"))
                .SingleOrDefault(path => Path.GetFileName(path) != previous.Id);
            if (candidate is null) return;
            Assert.True(File.Exists(Path.Combine(candidate, "device-key-ed25519.json")));
            candidateId = Path.GetFileName(candidate);
            fs.BeforeRead = null;
            var external = new GatewayRegistry(_tempDir);
            external.Load();
            external.AddOrUpdate(new() { Id = adoptCandidate ? candidateId : "newer",
                Url = "wss://newer.example", SharedGatewayToken = "newer-token" });
            external.SetActive(adoptCandidate ? candidateId : "newer");
            external.Save();
        };
        var service = new GatewayDirectConnectService(_manager, registry, _settings,
            () => _tunnelReconcileCount++, NullLogger.Instance, TimeSpan.FromMilliseconds(250));
        var result = await service.ConnectAsync(new("wss://replacement-realm.example", "candidate", null, null,
            EditingGatewayId: previous.Id, NativeSetup: true));
        Assert.NotNull(candidateId);
        Assert.False(result.GatewayCommitted);
        Assert.True(result.RollbackIncomplete);
        Assert.Equal(0, _manager.ConnectCount);
        Assert.Equal(adoptCandidate, Directory.Exists(registry.GetIdentityDirectory(candidateId!)));
        Assert.Equal(adoptCandidate ? candidateId : "newer", registry.ActiveGatewayId);
        Assert.Equal("newer-token", registry.GetActive()!.SharedGatewayToken);
        Assert.Equal(beforeIdentity, File.ReadAllBytes(Path.Combine(registry.GetIdentityDirectory(previous.Id), "device-key-ed25519.json")));
        Assert.Equal(previousIdentity.DeviceId, CreateIdentity(previous.Id).DeviceId);
        registry.UpdateAndSave(registry.ActiveGatewayId!, value => value with { FriendlyName = "still-saveable" });
    }

    private sealed class BeforeRegistryReadFileSystem : IFileSystem
    {
        public Action? BeforeRead { get; set; }
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public string ReadAllText(string path) { BeforeRead?.Invoke(); return File.ReadAllText(path); }
        public void WriteAllText(string path, string content) => File.WriteAllText(path, content);
        public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
        public void DeleteFile(string path) => File.Delete(path);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RollbackConflictAdoptsActualNewerSavedSelectionNotStaleCandidate(bool sameId, bool sameInstance)
    {
        var previous = AddPreviousGateway();
        _manager.SetCurrentSnapshot(Connected(previous.Id));
        _manager.NextSnapshot = Failed("candidate", "rejected");
        string? candidateId = null;
        _manager.BeforeSnapshot = () =>
        {
            candidateId = _manager.LastGatewayId;
            var external = sameInstance ? _registry : new GatewayRegistry(_tempDir);
            if (!sameInstance) external.Load();
            var current = external.GetActive()!;
            var newer = current with { Id = sameId ? current.Id : "external",
                Url = "wss://external.example", SharedGatewayToken = "external-token",
                SshTunnel = new("user", "ssh.example", 18789, 19001) };
            external.AddOrUpdate(newer);
            external.SetActive(newer.Id);
            external.Save();
        };
        var result = await CreateService().ConnectAsync(new("wss://candidate.example", "candidate-token", null, null,
            NativeSetup: true));
        Assert.False(result.GatewayCommitted);
        Assert.True(result.RollbackIncomplete);
        Assert.Equal(sameId ? candidateId : "external", _registry.ActiveGatewayId);
        Assert.Equal("wss://external.example", _registry.GetActive()!.Url);
        Assert.Equal("wss://external.example", _settings.GatewayUrl);
        Assert.Equal("ssh.example", _settings.SshTunnelHost);
        Assert.Equal(1, _manager.ConnectCount);
        _registry.UpdateAndSave(_registry.ActiveGatewayId!, value => value with { FriendlyName = "normal-save-after-failure" });
        Assert.Equal("external-token", _registry.CapturePersistedSnapshot().Records.Single(r => r.Id == _registry.ActiveGatewayId).SharedGatewayToken);
    }

    [Fact]
    public async Task RollbackConflictWithNoSavedActiveDoesNotInventCandidateSettings()
    {
        var previous = AddPreviousGateway();
        _manager.SetCurrentSnapshot(Connected(previous.Id));
        _manager.NextSnapshot = Failed("candidate", "rejected");
        _manager.BeforeSnapshot = () =>
        {
            var external = new GatewayRegistry(_tempDir);
            external.Load();
            external.SetActive(null);
            external.Save();
        };
        var result = await CreateService().ConnectAsync(new("wss://candidate.example", "candidate", null, null, NativeSetup: true));
        Assert.False(result.GatewayCommitted);
        Assert.True(result.RollbackIncomplete);
        Assert.Null(_registry.ActiveGatewayId);
        Assert.Equal("", _settings.GatewayUrl);
        Assert.False(_settings.UseSshTunnel);
        Assert.Equal(1, _manager.ConnectCount);
        _registry.SetActive(previous.Id);
        _registry.Save();
    }

    [Fact]
    public async Task UnreadableRollbackStateReportsUnknownWithoutSettingsOrReconnectGuesses()
    {
        var previous = AddPreviousGateway();
        _manager.SetCurrentSnapshot(Connected(previous.Id));
        _manager.NextSnapshot = Failed("candidate", "rejected");
        FileStream? held = null;
        _manager.BeforeSnapshot = () => held = new FileStream(Path.Combine(_tempDir, "gateways.json"),
            FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var result = await CreateService().ConnectAsync(new("wss://candidate.example", "candidate", null, null, NativeSetup: true));
            Assert.False(result.GatewayCommitted);
            Assert.True(result.RollbackIncomplete);
            Assert.Contains("could not be confirmed", result.Error);
            Assert.Equal(1, _tunnelReconcileCount);
            Assert.Equal(1, _manager.ConnectCount);
        }
        finally { held?.Dispose(); }
        _registry.AdoptPersistedSnapshot(_registry.GetSnapshot());
        _registry.Save();
    }

    [Fact]
    public async Task NativeHostRejectsARepointedRecordInsteadOfReturningANewCommitBinding()
    {
        var previous = AddPreviousGateway();
        CreateIdentity(previous.Id);
        _manager.NextSnapshot = Connected(previous.Id);
        _manager.BeforeSnapshot = () =>
            _registry.Update(previous.Id, record => record with { Url = "wss://changed-during-commit.example" });
        var notifications = 0;
        var host = new SetupNativeConnectionHost(CreateService(), _registry, NullLogger.Instance, () => notifications++);
        var result = await host.ConnectAsync(
            new(previous.Url, EditingGatewayId: previous.Id), CancellationToken.None);
        Assert.False(result.Success);
        Assert.True(result.GatewayCommitted);
        Assert.True(result.RequiresAttention);
        Assert.Null(result.EndpointBinding);
        Assert.Equal(1, notifications);
        Assert.Equal("wss://changed-during-commit.example", _registry.GetActive()!.Url);
    }

    [Fact]
    public async Task NativePreflightIdentityReadFailureIsRetryableAndNotCommitted()
    {
        var previous = AddPreviousGateway();
        CreateIdentity(previous.Id);
        var before = CaptureFiles();
        var host = new SetupNativeConnectionHost(CreateService(), _registry, NullLogger.Instance);
        var request = new OpenClaw.SetupEngine.SetupNativeConnectionRequest(previous.Url, EditingGatewayId: previous.Id);
        using (var blocked = new FileStream(Path.Combine(_registry.GetIdentityDirectory(previous.Id), "device-key-ed25519.json"),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = await host.ConnectAsync(request, CancellationToken.None);
            Assert.False(failed.Success);
            Assert.False(failed.GatewayCommitted);
            Assert.False(failed.RequiresAttention);
            Assert.Contains("preparation", failed.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, _manager.ValidationCount);
            Assert.Equal(0, _manager.ConnectCount);
        }
        Assert.Equal(before, CaptureFiles());
        _manager.NextSnapshot = Connected(previous.Id);
        Assert.True((await host.ConnectAsync(request, CancellationToken.None)).Success);
        await host.DiscardCheckAsync();
    }

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "openclaw-direct-connect-" + Guid.NewGuid().ToString("N"));
    private readonly GatewayRegistry _registry;
    private readonly SettingsManager _settings;
    private readonly FakeConnectionManager _manager = new();
    private int _tunnelReconcileCount;

    public GatewayDirectConnectServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
        _settings = new SettingsManager(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task Connect_NewRecord_CommitsRegistrySettingsAndRuntimeTunnel()
    {
        _manager.NextSnapshot = Connected("gw-new");
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            "wss://gateway.example",
            SharedToken: null,
            FriendlyName: "Remote",
            SshTunnel: null));

        Assert.Equal(GatewayDirectConnectOutcome.Connected, result.Outcome);
        var record = Assert.Single(_registry.GetAll());
        Assert.Equal(record.Id, _registry.ActiveGatewayId);
        Assert.Equal("Remote", record.FriendlyName);
        Assert.Equal("wss://gateway.example", _settings.GatewayUrl);
        Assert.False(_settings.UseSshTunnel);
        Assert.Equal(1, _tunnelReconcileCount);
        Assert.Equal(1, _manager.LeaseCount);
        Assert.Equal(1, _manager.ConnectCount);
    }

    [Fact]
    public async Task Connect_Failure_RestoresRegistrySettingsAndIdentity()
    {
        var previous = AddPreviousGateway();
        var identity = CreateIdentity(previous.Id);
        identity.StoreDeviceTokenForRole("operator", "operator-old");
        identity.StoreDeviceTokenForRole("node", "node-old");
        _manager.NextSnapshot = Failed(previous.Id, "rejected");
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            "wss://replacement.example",
            SharedToken: "replacement-token",
            FriendlyName: "Replacement",
            SshTunnel: null,
            EditingGatewayId: previous.Id));

        Assert.Equal(GatewayDirectConnectOutcome.Failed, result.Outcome);
        Assert.False(result.GatewayCommitted);
        var restored = Assert.IsType<GatewayRecord>(_registry.GetById(previous.Id));
        Assert.Equal(previous.Url, restored.Url);
        Assert.Equal(previous.Id, _registry.ActiveGatewayId);
        Assert.Equal(previous.Url, _settings.GatewayUrl);
        Assert.Equal(
            "operator-old",
            DeviceIdentity.TryReadStoredDeviceTokenForRole(
                _registry.GetIdentityDirectory(previous.Id),
                "operator"));
        Assert.Equal(
            "node-old",
            DeviceIdentity.TryReadStoredDeviceTokenForRole(
                _registry.GetIdentityDirectory(previous.Id),
                "node"));
        Assert.Equal(2, _tunnelReconcileCount);
    }

    [Fact]
    public async Task Connect_Failure_RestoresPreviousLiveConnection()
    {
        var previous = AddPreviousGateway();
        _manager.SetCurrentSnapshot(Connected(previous.Id));
        _manager.NextSnapshot = Failed(previous.Id, "rejected");
        _manager.RestoreSnapshot = Connected(previous.Id);
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            "wss://replacement.example",
            SharedToken: "replacement-token",
            FriendlyName: "Replacement",
            SshTunnel: null,
            EditingGatewayId: previous.Id));

        Assert.Equal(GatewayDirectConnectOutcome.Failed, result.Outcome);
        Assert.Equal(2, _manager.ConnectCount);
        Assert.Equal(previous.Id, _manager.LastGatewayId);
        Assert.Equal(previous.Id, _manager.CurrentSnapshot.GatewayId);
        Assert.Equal(RoleConnectionState.Connected, _manager.CurrentSnapshot.OperatorState);
    }

    [Fact]
    public async Task Connect_LatePairingWriterWinsOverRollback()
    {
        var previous = AddPreviousGateway();
        var identity = CreateIdentity(previous.Id);
        identity.StoreDeviceTokenForRole("operator", "operator-old");
        _manager.BeforeSnapshot = () =>
        {
            var lateWriter = new DeviceIdentity(_registry.GetIdentityDirectory(previous.Id));
            lateWriter.Initialize();
            lateWriter.StoreDeviceTokenForRole("operator", "operator-new");
        };
        _manager.NextSnapshot = Failed(previous.Id, "rejected");
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            previous.Url,
            SharedToken: "replacement-token",
            FriendlyName: null,
            SshTunnel: null,
            EditingGatewayId: previous.Id));

        Assert.Equal(GatewayDirectConnectOutcome.Failed, result.Outcome);
        Assert.Equal(
            "operator-new",
            DeviceIdentity.TryReadStoredDeviceTokenForRole(
                _registry.GetIdentityDirectory(previous.Id),
                "operator"));
    }

    [Fact]
    public async Task Connect_EndpointChange_ReplacesRecordAndIdentityRealmWithoutDuplicate()
    {
        var previous = AddPreviousGateway();
        var previousIdentityDir = _registry.GetIdentityDirectory(previous.Id);
        CreateIdentity(previous.Id).StoreDeviceTokenForRole("operator", "operator-old");
        _manager.NextSnapshot = Connected(previous.Id);
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            "wss://updated.example",
            SharedToken: null,
            FriendlyName: "Updated",
            SshTunnel: null,
            EditingGatewayId: previous.Id));

        Assert.Equal(GatewayDirectConnectOutcome.Connected, result.Outcome);
        var record = Assert.Single(_registry.GetAll());
        Assert.NotEqual(previous.Id, record.Id);
        Assert.Equal("wss://updated.example", record.Url);
        Assert.Equal("Updated", record.FriendlyName);
        Assert.Null(_registry.GetById(previous.Id));
        Assert.False(Directory.Exists(previousIdentityDir));
    }

    [Fact]
    public async Task Connect_MetadataOnlyEdit_UpdatesInPlace()
    {
        var previous = AddPreviousGateway();
        _manager.NextSnapshot = Connected(previous.Id);
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            previous.Url,
            SharedToken: null,
            FriendlyName: "Updated",
            SshTunnel: null,
            EditingGatewayId: previous.Id));

        Assert.Equal(GatewayDirectConnectOutcome.Connected, result.Outcome);
        var record = Assert.Single(_registry.GetAll());
        Assert.Equal(previous.Id, record.Id);
        Assert.Equal("Updated", record.FriendlyName);
    }

    [Fact]
    public void BuildCandidate_UnchangedSharedToken_KeepsStoredBootstrapToken()
    {
        var existing = new GatewayRecord
        {
            Id = "gw-paired",
            Url = "wss://previous.example",
            FriendlyName = "Previous",
            SharedGatewayToken = "existing-token",
            BootstrapToken = "bootstrap-token",
        };
        var request = new GatewayDirectConnectRequest(
            existing.Url,
            SharedToken: null,
            FriendlyName: "Renamed",
            SshTunnel: null,
            EditingGatewayId: existing.Id,
            PreserveExistingSharedTokenWhenMissing: true);

        var candidate = GatewayDirectConnectService.BuildCandidate(
            request,
            existing,
            existing.Id,
            preserveExistingSharedToken: true);

        Assert.Equal("existing-token", candidate.SharedGatewayToken);
        Assert.Equal("bootstrap-token", candidate.BootstrapToken);
        Assert.Equal("Renamed", candidate.FriendlyName);
        Assert.Equal(existing.Id, candidate.Id);
    }

    [Theory]
    [InlineData(false, true, null, null, "bootstrap-old")]
    [InlineData(false, true, "shared-new", null, null)]
    [InlineData(true, true, null, null, "bootstrap-old")]
    [InlineData(true, true, "shared-new", null, "bootstrap-old")]
    [InlineData(true, false, null, null, null)]
    [InlineData(true, true, null, "bootstrap-new", "bootstrap-new")]
    [InlineData(true, false, null, "bootstrap-new", "bootstrap-new")]
    public void BuildCandidate_BootstrapPreservation_CombinesNativeAndExistingEditPolicies(
        bool nativeSetup, bool preserveExisting, string? sharedToken,
        string? bootstrapToken, string? expectedBootstrapToken)
    {
        var existing = new GatewayRecord
        {
            Id = "gw-existing",
            Url = "wss://gateway.example",
            SharedGatewayToken = "shared-old",
            BootstrapToken = "bootstrap-old",
        };
        var request = new GatewayDirectConnectRequest(
            existing.Url, sharedToken, null, null,
            BootstrapToken: bootstrapToken, NativeSetup: nativeSetup);

        var candidate = GatewayDirectConnectService.BuildCandidate(
            request, existing, existing.Id, preserveExisting);

        Assert.Equal(expectedBootstrapToken, candidate.BootstrapToken);
        Assert.Equal(sharedToken ?? (preserveExisting ? "shared-old" : null), candidate.SharedGatewayToken);
    }

    [Fact]
    public async Task Connect_UnchangedSharedToken_KeepsDeviceTokensAndBootstrap()
    {
        var previous = AddPreviousGateway();
        _registry.AddOrUpdate(previous with
        {
            SharedGatewayToken = "existing-token",
            BootstrapToken = "bootstrap-token",
        });
        _registry.Save();
        var identity = CreateIdentity(previous.Id);
        identity.StoreDeviceTokenForRole("operator", "operator-token");
        identity.StoreDeviceTokenForRole("node", "node-token");
        _manager.NextSnapshot = Connected(previous.Id);
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            previous.Url,
            SharedToken: null,
            FriendlyName: previous.FriendlyName,
            SshTunnel: null,
            EditingGatewayId: previous.Id,
            PreserveExistingSharedTokenWhenMissing: true));

        Assert.Equal(GatewayDirectConnectOutcome.Connected, result.Outcome);
        var saved = Assert.IsType<GatewayRecord>(_registry.GetById(previous.Id));
        Assert.Equal(previous.Id, saved.Id);
        Assert.Equal("existing-token", saved.SharedGatewayToken);
        Assert.Equal("bootstrap-token", saved.BootstrapToken);
        Assert.Equal(
            "operator-token",
            DeviceIdentity.TryReadStoredDeviceTokenForRole(
                _registry.GetIdentityDirectory(previous.Id),
                "operator"));
        Assert.Equal(
            "node-token",
            DeviceIdentity.TryReadStoredDeviceTokenForRole(
                _registry.GetIdentityDirectory(previous.Id),
                "node"));
    }

    [Fact]
    public async Task Connect_TokenlessDiagnosticsRequestPreservesSameRealmSharedToken()
    {
        var previous = AddPreviousGateway();
        _registry.AddOrUpdate(previous with { SharedGatewayToken = "existing-token" });
        _registry.Save();
        _manager.NextSnapshot = Connected(previous.Id);
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            previous.Url,
            SharedToken: null,
            FriendlyName: null,
            SshTunnel: null,
            PreserveExistingSharedTokenWhenMissing: true));

        Assert.Equal(GatewayDirectConnectOutcome.Connected, result.Outcome);
        Assert.Equal("existing-token", _registry.GetActive()?.SharedGatewayToken);
    }

    [Fact]
    public async Task Connect_TokenlessDiagnosticsRequestDoesNotCarryTokenAcrossSshRealmChange()
    {
        var previousSsh = new SshTunnelConfig("user", "old.example", 18789, 45678);
        var previous = AddPreviousGateway();
        _registry.AddOrUpdate(previous with
        {
            SharedGatewayToken = "existing-token",
            SshTunnel = previousSsh,
        });
        _registry.Save();
        _manager.NextSnapshot = Connected(previous.Id);
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            previous.Url,
            SharedToken: null,
            FriendlyName: null,
            SshTunnel: previousSsh with { Host = "replacement.example" },
            PreserveExistingSharedTokenWhenMissing: true));

        Assert.Equal(GatewayDirectConnectOutcome.Connected, result.Outcome);
        Assert.Null(_registry.GetActive()?.SharedGatewayToken);
        Assert.NotEqual(previous.Id, _registry.ActiveGatewayId);
    }

    [Fact]
    public void ShouldPreserveUnchangedSharedToken_RequiresSameCredentialRealm()
    {
        var ssh = new SshTunnelConfig("user", "bastion.example", 18789, 45678, SshPort: 22);
        var previous = AddPreviousGateway() with
        {
            SharedGatewayToken = "existing-token",
            SshTunnel = ssh,
        };
        _registry.AddOrUpdate(previous);
        _registry.Save();

        Assert.False(GatewayDirectConnectService.ShouldPreserveUnchangedSharedToken(
            previous,
            "existing-token",
            "wss://updated.example",
            ssh,
            _registry));
        Assert.False(GatewayDirectConnectService.ShouldPreserveUnchangedSharedToken(
            previous,
            "existing-token",
            previous.Url,
            ssh with { SshPort = 2222 },
            _registry));
        Assert.True(GatewayDirectConnectService.ShouldPreserveUnchangedSharedToken(
            previous,
            "existing-token",
            previous.Url,
            ssh,
            _registry));
    }

    [Fact]
    public async Task Connect_EndpointChangeFailure_RestoresOldRealmAndDeletesCandidateIdentity()
    {
        var previous = AddPreviousGateway();
        var previousIdentity = CreateIdentity(previous.Id);
        previousIdentity.StoreDeviceTokenForRole("operator", "operator-old");
        _manager.BeforeSnapshot = () =>
        {
            var candidateId = Assert.IsType<string>(_manager.LastGatewayId);
            var candidateIdentity = CreateIdentity(candidateId);
            candidateIdentity.StoreDeviceTokenForRole("operator", "candidate-token");
        };
        _manager.NextSnapshot = Failed(previous.Id, "rejected");
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            "wss://replacement.example",
            SharedToken: "replacement-token",
            FriendlyName: null,
            SshTunnel: null,
            EditingGatewayId: previous.Id));

        Assert.Equal(GatewayDirectConnectOutcome.Failed, result.Outcome);
        Assert.Equal(previous.Id, _registry.ActiveGatewayId);
        Assert.Equal(previous.Url, Assert.Single(_registry.GetAll()).Url);
        Assert.Equal(
            "operator-old",
            DeviceIdentity.TryReadStoredDeviceTokenForRole(
                _registry.GetIdentityDirectory(previous.Id),
                "operator"));
        Assert.False(Directory.Exists(
            _registry.GetIdentityDirectory(Assert.IsType<string>(_manager.LastGatewayId))));
    }

    [Fact]
    public async Task Connect_NodePairingRequired_CommitsGatewayWithoutTimeoutRollback()
    {
        _manager.NextSnapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.PairingRequired,
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.PairingRequired,
            NodePairingStatus = PairingStatus.Pending,
        };
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            "wss://pairing.example",
            SharedToken: "replacement-token",
            FriendlyName: null,
            SshTunnel: null));

        Assert.Equal(GatewayDirectConnectOutcome.PairingRequired, result.Outcome);
        Assert.True(result.GatewayCommitted);
        Assert.Equal(1, _manager.ConnectCount);
        Assert.Equal(1, _manager.DisconnectCount);
        Assert.Equal("wss://pairing.example", _registry.GetActive()?.Url);
    }

    [Fact]
    public async Task Connect_SettingsSaveFailure_RollsBackBeforeConnecting()
    {
        var previous = AddPreviousGateway();
        var blockedPath = Path.Combine(_tempDir, "blocked-settings");
        File.WriteAllText(blockedPath, "not a directory");
        var blockedSettings = new SettingsManager(blockedPath);
        var service = new GatewayDirectConnectService(
            _manager,
            _registry,
            blockedSettings,
            () => _tunnelReconcileCount++,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(100));

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            "wss://replacement.example",
            SharedToken: null,
            FriendlyName: null,
            SshTunnel: null,
            EditingGatewayId: previous.Id));

        Assert.Equal(GatewayDirectConnectOutcome.Failed, result.Outcome);
        Assert.Equal(0, _manager.ConnectCount);
        Assert.Equal(previous.Url, _registry.GetById(previous.Id)?.Url);
        Assert.Equal(previous.Id, _registry.ActiveGatewayId);
    }

    [Fact]
    public void SynchronizeSettingsWithActiveGateway_PersistsCommittedGateway()
    {
        var active = AddPreviousGateway() with
        {
            Url = "wss://committed.example",
            SshTunnel = new SshTunnelConfig("user", "host.example", 18789, 45678),
        };
        _registry.AddOrUpdate(active);
        _registry.Save();
        var service = CreateService();

        service.SynchronizeSettingsWithCommittedGateway(active);

        Assert.Equal(active.Url, _settings.GatewayUrl);
        Assert.True(_settings.UseSshTunnel);
        Assert.Equal("host.example", _settings.SshTunnelHost);
        Assert.Equal(1, _tunnelReconcileCount);
    }

    [Fact]
    public void SynchronizeSettings_TunnelFailureTwice_RestoresPriorSettings()
    {
        var active = AddPreviousGateway();
        _settings.GatewayUrl = "wss://rejected.example";
        _settings.SaveOrThrow();
        var service = new GatewayDirectConnectService(
            _manager,
            _registry,
            _settings,
            () => throw new InvalidOperationException("tunnel down"),
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(100));

        var error = Assert.Throws<InvalidOperationException>(
            () => service.SynchronizeSettingsWithCommittedGateway(active));

        Assert.Contains("out of sync", error.Message, StringComparison.Ordinal);
        Assert.Equal("wss://rejected.example", _settings.GatewayUrl);
    }

    [Fact]
    public void SynchronizeSettings_SecondOperationRestoresItsOwnSnapshot()
    {
        var prior = AddPreviousGateway();
        var calls = 0;
        var service = new GatewayDirectConnectService(
            _manager,
            _registry,
            _settings,
            () =>
            {
                calls++;
                if (calls > 1)
                    throw new InvalidOperationException("tunnel down");
            },
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(100));
        var committed = prior with { Url = "wss://committed.example" };
        _registry.AddOrUpdate(committed);
        _registry.SetActive(committed.Id);
        _registry.Save();
        service.SynchronizeSettingsWithCommittedGateway(committed);
        Assert.Equal("wss://committed.example", _settings.GatewayUrl);

        _settings.GatewayUrl = "wss://newer.example";
        _settings.SaveOrThrow();
        var second = committed with { Url = "wss://other.example" };
        _registry.AddOrUpdate(second);
        _registry.Save();
        var error = Assert.Throws<InvalidOperationException>(
            () => service.SynchronizeSettingsWithCommittedGateway(second));

        Assert.Contains("out of sync", error.Message, StringComparison.Ordinal);
        Assert.Equal("wss://newer.example", _settings.GatewayUrl);
        Assert.NotEqual(prior.Url, _settings.GatewayUrl);
    }

    [Fact]
    public void SynchronizeSettings_OverlappingAttempts_RestoreTheirOwnSnapshots()
    {
        var prior = AddPreviousGateway();
        var priorUrl = _settings.GatewayUrl;
        var calls = 0;
        var service = new GatewayDirectConnectService(
            _manager,
            _registry,
            _settings,
            () =>
            {
                calls++;
                if (calls > 1)
                    throw new InvalidOperationException("tunnel down");
            },
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(100));
        var first = service.CaptureSharedTokenSettingsAttempt();
        var candidate = prior with { Url = "wss://rejected.example" };
        _registry.AddOrUpdate(candidate);
        _registry.Save();
        service.SynchronizeSettingsWithCommittedGateway(candidate, first);
        Assert.Equal("wss://rejected.example", _settings.GatewayUrl);

        var second = service.CaptureSharedTokenSettingsAttempt();
        _registry.AddOrUpdate(prior);
        _registry.SetActive(prior.Id);
        _registry.Save();
        var error = Assert.Throws<InvalidOperationException>(
            () => service.SynchronizeSettingsWithCommittedGateway(prior, first));

        Assert.Contains("out of sync", error.Message, StringComparison.Ordinal);
        Assert.Equal(priorUrl, _settings.GatewayUrl);
        _ = second;
    }

    [Fact]
    public void SynchronizeSettings_BeginAttempt_RollbackTunnelFailure_KeepsPreAttemptSnapshot()
    {
        var prior = AddPreviousGateway();
        var priorUrl = _settings.GatewayUrl;
        var calls = 0;
        var service = new GatewayDirectConnectService(
            _manager,
            _registry,
            _settings,
            () =>
            {
                calls++;
                if (calls > 1)
                    throw new InvalidOperationException("tunnel down");
            },
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(100));
        var candidate = prior with { Url = "wss://rejected.example" };
        _registry.AddOrUpdate(candidate);
        _registry.Save();
        service.SynchronizeSettingsWithCommittedGateway(candidate);
        Assert.Equal("wss://rejected.example", _settings.GatewayUrl);

        _registry.AddOrUpdate(prior);
        _registry.SetActive(prior.Id);
        _registry.Save();
        var error = Assert.Throws<InvalidOperationException>(
            () => service.SynchronizeSettingsWithCommittedGateway(prior));

        Assert.Contains("out of sync", error.Message, StringComparison.Ordinal);
        Assert.Equal("wss://rejected.example", _settings.GatewayUrl);
        Assert.NotEqual(priorUrl, _settings.GatewayUrl);
    }

    [Fact]
    public void SynchronizeSettings_NoActiveGateway_RestoresSnapshot()
    {
        var active = AddPreviousGateway();
        var before = _settings.GatewayUrl;
        var service = CreateService();
        service.SynchronizeSettingsWithCommittedGateway(active);
        Assert.Equal(active.Url, _settings.GatewayUrl);

        _registry.SetActive(null);
        _registry.Save();
        service.SynchronizeSettingsWithCommittedGateway(active);

        Assert.Equal(before, _settings.GatewayUrl);
        Assert.Null(_registry.ActiveGatewayId);
    }

    [Fact]
    public void SynchronizeSettings_FirstCommitSurvivesSecondRollbackFailure()
    {
        var prior = AddPreviousGateway();
        var calls = 0;
        var service = new GatewayDirectConnectService(
            _manager,
            _registry,
            _settings,
            () =>
            {
                calls++;
                if (calls > 1)
                    throw new InvalidOperationException("tunnel down");
            },
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(100));
        var firstAttempt = service.CaptureSharedTokenSettingsAttempt();
        var committed = prior with { Url = "wss://committed.example" };
        _registry.AddOrUpdate(committed);
        _registry.SetActive(committed.Id);
        _registry.Save();
        service.SynchronizeSettingsWithCommittedGateway(committed, firstAttempt);
        Assert.Equal("wss://committed.example", _settings.GatewayUrl);
        Assert.True(firstAttempt.CandidateSynchronized);

        var secondAttempt = service.CaptureSharedTokenSettingsAttempt();
        var rejected = committed with { Url = "wss://rejected.example" };
        _registry.AddOrUpdate(rejected);
        _registry.SetActive(rejected.Id);
        _registry.Save();
        var error = Assert.Throws<InvalidOperationException>(
            () => service.SynchronizeSettingsWithCommittedGateway(rejected, secondAttempt));

        Assert.Contains("out of sync", error.Message, StringComparison.Ordinal);
        Assert.Equal("wss://committed.example", _settings.GatewayUrl);
        Assert.NotEqual(prior.Url, _settings.GatewayUrl);
    }

    [Fact]
    public async Task NativeCheck_DoesNotChangeSavedStateOrLiveConnection()
    {
        var previous = AddPreviousGateway();
        CreateIdentity(previous.Id).StoreDeviceTokenForRole("operator", "operator-old");
        var before = CaptureFiles();
        _manager.SetCurrentSnapshot(Connected(previous.Id));

        var result = await CreateService().CheckAsync(new(previous.Url, null, null, null,
            EditingGatewayId: previous.Id, BootstrapToken: "bootstrap-new", NativeSetup: true), CancellationToken.None);

        Assert.Equal(SetupCodeOutcome.Success, result.Outcome);
        Assert.Equal(before, CaptureFiles());
        Assert.Equal(previous.Id, _manager.CurrentSnapshot.GatewayId);
        Assert.Equal(0, _manager.LeaseCount);
        Assert.Equal(0, _manager.DisconnectCount);
        Assert.Equal(0, _tunnelReconcileCount);
        Assert.Equal("operator-old", _manager.ValidatedCredential?.Token);
    }

    [Fact]
    public async Task NativeNext_RevalidatesAndPreservesSameRealmPairingInStagedIdentity()
    {
        var previous = AddPreviousGateway();
        var identity = CreateIdentity(previous.Id);
        identity.StoreDeviceTokenForRole("operator", "operator-old");
        identity.StoreDeviceTokenForRole("node", "node-old");
        var originalIdentity = File.ReadAllBytes(Path.Combine(_registry.GetIdentityDirectory(previous.Id), "device-key-ed25519.json"));
        _manager.NextSnapshot = Connected(previous.Id);
        _manager.ValidationRequiresV2 = true;
        var service = CreateService();
        var request = new GatewayDirectConnectRequest(previous.Url, "new-shared", null, null,
            EditingGatewayId: previous.Id, NativeSetup: true);
        await service.CheckAsync(request, CancellationToken.None);
        var result = await service.ConnectAsync(request);

        Assert.Equal(GatewayDirectConnectOutcome.Connected, result.Outcome);
        Assert.Equal(2, _manager.ValidationCount);
        Assert.Equal("operator-old", _manager.ValidatedCredential?.Token);
        var active = Assert.Single(_registry.GetAll());
        Assert.Equal(previous.Id, active.Id);
        Assert.True(active.RequiresV2Signature);
        Assert.Equal(originalIdentity, File.ReadAllBytes(Path.Combine(_registry.GetIdentityDirectory(active.Id), "device-key-ed25519.json")));
        Assert.True(Directory.Exists(_registry.GetIdentityDirectory(previous.Id)));
    }

    [Fact]
    public async Task NativeNext_CancelDuringValidationLeavesEverythingUnchanged()
    {
        var previous = AddPreviousGateway();
        var before = CaptureFiles();
        _manager.SetCurrentSnapshot(Connected(previous.Id));
        using var cancellation = new CancellationTokenSource();
        _manager.DuringValidation = () => cancellation.Cancel();

        var result = await CreateService().ConnectAsync(new(previous.Url, "shared", null, null,
            NativeSetup: true), cancellation.Token);

        Assert.False(result.GatewayCommitted);
        Assert.Equal(GatewayDirectConnectOutcome.Failed, result.Outcome);
        Assert.Equal(before, CaptureFiles());
        Assert.Equal(0, _manager.DisconnectCount);
        Assert.Equal(0, _manager.ConnectCount);
    }

    [Fact]
    public async Task NativeNext_CancelAfterHandshakeRestoresPreviousIdentityAndLiveConnection()
    {
        var previous = AddPreviousGateway();
        CreateIdentity(previous.Id).StoreDeviceTokenForRole("operator", "operator-old");
        var before = CaptureFiles();
        _manager.SetCurrentSnapshot(Connected(previous.Id));
        _manager.NextSnapshot = Connected(previous.Id);
        _manager.RestoreSnapshot = Connected(previous.Id);
        using var cancellation = new CancellationTokenSource();
        _manager.BeforeSnapshot = () =>
        {
            if (_manager.ConnectCount == 1)
            {
                cancellation.Cancel();
            }
        };
        var result = await CreateService().ConnectAsync(new(previous.Url, null, null, null,
            NativeSetup: true), cancellation.Token);

        Assert.False(result.GatewayCommitted);
        Assert.Equal(GatewayDirectConnectOutcome.Failed, result.Outcome);
        Assert.Equal(before, CaptureFiles());
        Assert.Equal(previous.Id, _manager.CurrentSnapshot.GatewayId);
        Assert.Equal(2, _manager.ConnectCount);
    }

    [Fact]
    public async Task NativeHost_CancelledNextWithConfirmedRollbackRetainsStagedIdentity()
    {
        var previous = AddPreviousGateway();
        CreateIdentity(previous.Id).StoreDeviceTokenForRole("operator", "paired");
        _manager.SetCurrentSnapshot(Connected(previous.Id));
        _manager.NextSnapshot = Connected(previous.Id);
        _manager.RestoreSnapshot = Connected(previous.Id);
        using var cancel = new CancellationTokenSource();
        _manager.BeforeSnapshot = () => { if (_manager.ConnectCount == 1) cancel.Cancel(); };
        var host = new SetupNativeConnectionHost(CreateService(), _registry, NullLogger.Instance);
        var request = new OpenClaw.SetupEngine.SetupNativeConnectionRequest(previous.Url, EditingGatewayId: previous.Id);
        var result = await host.ConnectAsync(request, cancel.Token);
        Assert.False(result.Success);
        Assert.False(result.GatewayCommitted);
        Assert.False(result.RequiresAttention);
        Assert.True(Directory.Exists(_manager.ValidationPaths[0]));
        _manager.BeforeSnapshot = null;
        Assert.True((await host.CheckAsync(request, CancellationToken.None)).Success);
        Assert.Single(_manager.ValidationPaths.Distinct());
        Assert.Single(_manager.ValidationDeviceIds.Distinct());
        await host.DiscardCheckAsync();
        Assert.False(Directory.Exists(_manager.ValidationPaths[0]));
    }

    [Fact]
    public async Task NativeNext_PairingPendingIsNotAiReadyAndRollsBack()
    {
        var previous = AddPreviousGateway();
        _manager.NextSnapshot = Connected(previous.Id) with
        {
            OperatorState = RoleConnectionState.PairingRequired,
            OverallState = OverallConnectionState.PairingRequired
        };
        var result = await CreateService().ConnectAsync(new(previous.Url, "shared", null, null,
            NativeSetup: true));
        Assert.Equal(GatewayDirectConnectOutcome.Failed, result.Outcome);
        Assert.False(result.GatewayCommitted);
        Assert.Contains("approval", result.Error);
        Assert.Equal(previous.Id, _registry.ActiveGatewayId);
    }

    [Fact]
    public async Task NativeNext_ValidationFailureDoesNotStartTransaction()
    {
        var previous = AddPreviousGateway();
        _manager.SetCurrentSnapshot(Connected(previous.Id));
        var before = CaptureFiles();
        _manager.ValidationResult = new(SetupCodeOutcome.ConnectionFailed, "rejected");
        var result = await CreateService().ConnectAsync(new(previous.Url, "shared", null, null, NativeSetup: true));
        Assert.False(result.GatewayCommitted);
        Assert.Equal(before, CaptureFiles());
        Assert.Equal(0, _manager.DisconnectCount);
        Assert.Equal(0, _manager.ConnectCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCheck_ChangedRealmDoesNotCarrySavedCredentials(bool changeSsh)
    {
        var previous = AddPreviousGateway();
        CreateIdentity(previous.Id).StoreDeviceTokenForRole("operator", "operator-old");
        _registry.AddOrUpdate(previous with { SharedGatewayToken = "shared-old", BootstrapToken = "bootstrap-old" });
        var ssh = changeSsh ? new SshTunnelConfig("user", "host.example", 18789, 45678) : null;
        await CreateService().CheckAsync(new(changeSsh ? previous.Url : "wss://other.example",
            null, null, ssh, EditingGatewayId: previous.Id, NativeSetup: true), CancellationToken.None);
        Assert.Null(_manager.ValidatedCredential);
    }

    [Fact]
    public async Task NativeNext_BootstrapRemainsBootstrapAndLaterCancellationDoesNotUndoSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        _manager.NextSnapshot = Connected("new");
        var result = await CreateService().ConnectAsync(new("wss://gateway.example", null, null, null,
            BootstrapToken: "bootstrap", NativeSetup: true), cancellation.Token);
        cancellation.Cancel();
        var active = Assert.Single(_registry.GetAll());
        Assert.Equal("bootstrap", active.BootstrapToken);
        Assert.Null(active.SharedGatewayToken);
        Assert.True(_manager.ValidatedCredential!.IsBootstrapToken);
        Assert.True(result.GatewayCommitted);
        Assert.Equal(active.Id, _registry.ActiveGatewayId);
        Assert.False(_manager.LastConnectCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task NativeHost_RollbackPersistenceFailureRemainsExplicitAndCannotAdvance()
    {
        var previous = AddPreviousGateway();
        _manager.NextSnapshot = Failed(previous.Id, "rejected");
        FileStream? registryLock = null;
        _manager.BeforeSnapshot = () => registryLock = new FileStream(
            Path.Combine(_tempDir, "gateways.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var notifications = 0;
            var host = new SetupNativeConnectionHost(CreateService(), _registry, NullLogger.Instance,
                () => notifications++);
            var result = await host.ConnectAsync(new("wss://replacement.example", SharedToken: "shared",
                EditingGatewayId: previous.Id), CancellationToken.None);
            Assert.False(result.Success);
            Assert.True(result.GatewayCommitted);
            Assert.True(result.RequiresAttention);
            Assert.Equal(1, notifications);
            Assert.Contains("rollback failed", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(previous.Id, result.GatewayId);
            Assert.NotNull(_registry.GetById(previous.Id));
        }
        finally { registryLock?.Dispose(); }
    }

    [Fact]
    public async Task NativeHost_PreviousConnectionRestoreFailureRequiresAttentionWithoutNewCommit()
    {
        var previous = AddPreviousGateway();
        _manager.SetCurrentSnapshot(Connected(previous.Id));
        _manager.NextSnapshot = Failed(previous.Id, "rejected");
        _manager.RestoreSnapshot = Failed(previous.Id, "previous connection unavailable");
        var notifications = 0;
        var host = new SetupNativeConnectionHost(CreateService(), _registry, NullLogger.Instance,
            () => notifications++);
        var result = await host.ConnectAsync(new("wss://new.example", SharedToken: "shared"),
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(result.GatewayCommitted);
        Assert.True(result.RequiresAttention);
        Assert.Equal(1, notifications);
        Assert.Equal(previous.Id, _registry.ActiveGatewayId);
        Assert.NotEmpty(_manager.ValidationPaths);
        Assert.All(_manager.ValidationPaths, path => Assert.False(Directory.Exists(path)));
        await host.DiscardCheckAsync();
    }

    [Fact]
    public async Task NativeNext_ManagedGatewayRetainsLogicalIdAndLocalAiOwnership()
    {
        var previous = AddPreviousGateway() with
        {
            Url = "ws://127.0.0.1:18789",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
            BrowserControlPort = 18791
        };
        _registry.AddOrUpdate(previous);
        _registry.Save();
        CreateIdentity(previous.Id).StoreDeviceTokenForRole("operator", "paired");
        var sidecar = Path.Combine(_registry.GetIdentityDirectory(previous.Id), "retained-state.txt");
        File.WriteAllText(sidecar, "existing gateway state");
        _manager.NextSnapshot = Connected(previous.Id);
        var result = await CreateService().ConnectAsync(new(previous.Url, null, null, null,
            EditingGatewayId: previous.Id, NativeSetup: true));
        Assert.Equal(GatewayDirectConnectOutcome.Connected, result.Outcome);
        var active = Assert.Single(_registry.GetAll());
        Assert.Equal(previous.Id, active.Id);
        Assert.Equal(previous.Id, Assert.Single(LocalAiGatewayDistroResolver.FindOwners(_registry.GetAll())).Id);
        Assert.Equal("OpenClawGateway", active.SetupManagedDistroName);
        Assert.Equal(18791, active.BrowserControlPort);
        Assert.Equal("existing gateway state", File.ReadAllText(sidecar));
    }

    [Fact]
    public async Task NativeNext_ChangedRealmAddsGatewayWithoutRemovingPreviousRecordOrIdentity()
    {
        var previous = AddPreviousGateway();
        CreateIdentity(previous.Id).StoreDeviceTokenForRole("operator", "paired");
        var previousIdentity = File.ReadAllBytes(Path.Combine(_registry.GetIdentityDirectory(previous.Id), "device-key-ed25519.json"));
        _manager.NextSnapshot = Connected("new");
        var result = await CreateService().ConnectAsync(new("wss://other.example", "shared", null, null,
            EditingGatewayId: previous.Id, NativeSetup: true));
        Assert.Equal(GatewayDirectConnectOutcome.Connected, result.Outcome);
        Assert.Equal(2, _registry.GetAll().Count);
        Assert.NotEqual(previous.Id, _registry.ActiveGatewayId);
        Assert.Equal(previous, _registry.GetById(previous.Id));
        Assert.Equal(previousIdentity, File.ReadAllBytes(Path.Combine(_registry.GetIdentityDirectory(previous.Id), "device-key-ed25519.json")));
    }

    [Fact]
    public async Task NativeNext_NewerIdentityWriterWinsAndRollbackRequiresAttention()
    {
        var previous = AddPreviousGateway();
        CreateIdentity(previous.Id).StoreDeviceTokenForRole("operator", "paired");
        _manager.BeforeSnapshot = () => CreateIdentity(previous.Id).StoreDeviceTokenForRole("operator", "newer-token");
        _manager.NextSnapshot = Failed(previous.Id, "rejected");
        var notifications = 0;
        var host = new SetupNativeConnectionHost(CreateService(), _registry, NullLogger.Instance,
            () => notifications++);
        var result = await host.ConnectAsync(new(previous.Url, EditingGatewayId: previous.Id),
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(result.GatewayCommitted);
        Assert.True(result.RequiresAttention);
        Assert.Equal(1, notifications);
        Assert.Equal(previous.Id, _registry.ActiveGatewayId);
        Assert.Equal("newer-token", DeviceIdentity.TryReadStoredDeviceToken(_registry.GetIdentityDirectory(previous.Id)));
        await host.DiscardCheckAsync();
    }

    [Fact]
    public async Task NativeNext_SameUrlDifferentSshRealmsRetainsSelectedGatewayIdAndCredentials()
    {
        var first = AddPreviousGateway() with
        {
            Url = "ws://127.0.0.1:18789",
            SshTunnel = new("user", "first.example", 18789, 45678)
        };
        var selected = first with
        {
            Id = "selected-gateway",
            SshTunnel = new("user", "second.example", 18789, 45679)
        };
        _registry.AddOrUpdate(first);
        _registry.AddOrUpdate(selected);
        _registry.Save();
        CreateIdentity(first.Id).StoreDeviceTokenForRole("operator", "first-token");
        CreateIdentity(selected.Id).StoreDeviceTokenForRole("operator", "selected-token");
        _manager.NextSnapshot = Connected(selected.Id);
        var result = await CreateService().ConnectAsync(new(selected.Url, null, null, selected.SshTunnel,
            EditingGatewayId: selected.Id, NativeSetup: true));
        Assert.Equal(GatewayDirectConnectOutcome.Connected, result.Outcome);
        Assert.Equal("selected-token", _manager.ValidatedCredential?.Token);
        Assert.Equal(selected.Id, _registry.ActiveGatewayId);
        Assert.Equal(2, _registry.GetAll().Count);
        Assert.NotNull(_registry.GetById(first.Id));
    }

    [Fact]
    public async Task NativeHost_NoEditingId_CheckAndNextSelectSecondSavedSshRealm()
    {
        var first = AddPreviousGateway() with
        {
            Url = "wss://gateway.example",
            SshTunnel = new("user", "first.example", 18789, 45678)
        };
        var selected = first with
        {
            Id = "selected-gateway",
            SshTunnel = new("user", "second.example", 18789, 45679)
        };
        _registry.AddOrUpdate(first);
        _registry.AddOrUpdate(selected);
        _registry.Save();
        CreateIdentity(first.Id).StoreDeviceTokenForRole("operator", "first-token");
        var selectedIdentity = CreateIdentity(selected.Id);
        selectedIdentity.StoreDeviceTokenForRole("operator", "selected-token");
        _manager.DuringValidation = () => _manager.ValidationResult =
            _manager.ValidatedCredential is null
                ? new(SetupCodeOutcome.ConnectionFailed, "missing credential")
                : new(SetupCodeOutcome.Success);
        _manager.NextSnapshot = Connected(selected.Id);
        var before = CaptureFiles();
        var host = new SetupNativeConnectionHost(CreateService(), _registry, NullLogger.Instance);
        var request = new OpenClaw.SetupEngine.SetupNativeConnectionRequest(
            selected.Url, SshTunnel: selected.SshTunnel);

        try
        {
            var check = await host.CheckAsync(request, CancellationToken.None);

            Assert.True(check.Success, check.Error);
            Assert.Equal("selected-token", _manager.ValidatedCredential?.Token);
            Assert.Equal(before, CaptureFiles());
            Assert.Equal(first.Id, _registry.ActiveGatewayId);
            Assert.Equal(0, _manager.DisconnectCount);
            var connected = await host.ConnectAsync(request, CancellationToken.None);
            Assert.True(connected.Success, connected.Error);
            Assert.Equal(selected.Id, connected.GatewayId);
            Assert.Equal(GatewayDashboardBinding.Capture(_registry.GetActive()!), connected.EndpointBinding);
            Assert.Equal(selected.Id, _registry.ActiveGatewayId);
            Assert.Equal(2, _registry.GetAll().Count);
            Assert.All(_manager.ValidationDeviceIds, id => Assert.Equal(selectedIdentity.DeviceId, id));
            Assert.Equal(2, _manager.ValidationCount);
        }
        finally { await host.DiscardCheckAsync(); }
    }

    private string CaptureFiles() => string.Join("\n",
        Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(_tempDir, path) + ":" +
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))));

    [Fact]
    public async Task NativeHost_CheckRetryNextRetainsPublicKeyAndEphemeralBootstrapUpgrade()
    {
        var host = new SetupNativeConnectionHost(CreateService(), _registry, NullLogger.Instance);
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            """{"url":"wss://gateway.example","bootstrapToken":"bootstrap"}"""));
        var request = new OpenClaw.SetupEngine.SetupNativeConnectionRequest(SetupCode: code);
        _manager.IssueValidationToken = true;
        _manager.NextSnapshot = Connected("new");

        Assert.True((await host.CheckAsync(request, CancellationToken.None)).Success);
        Assert.True((await host.CheckAsync(request, CancellationToken.None)).Success);
        Assert.Empty(_registry.GetAll());
        Assert.Null(DeviceIdentity.TryReadStoredDeviceToken(_manager.ValidationPaths[0]));
        var result = await host.ConnectAsync(request, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(GatewayDashboardBinding.Capture(_registry.GetActive()!), result.EndpointBinding);
        Assert.Single(_manager.ValidationDeviceIds.Distinct());
        Assert.Equal(3, _manager.ValidationCount);
        Assert.Equal(CredentialResolver.SourceDeviceToken, _manager.ValidatedCredential!.Source);
        Assert.Equal("issued-token", DeviceIdentity.TryReadStoredDeviceToken(_registry.GetIdentityDirectory(result.GatewayId!)));
        Assert.False(Directory.Exists(_manager.ValidationPaths[0]));
    }

    [Fact]
    public async Task NativeHost_CancelledCheckRetryKeepsStagedIdentityUntilClose()
    {
        var host = new SetupNativeConnectionHost(CreateService(), _registry, NullLogger.Instance);
        var request = new OpenClaw.SetupEngine.SetupNativeConnectionRequest("wss://gateway.example", SharedToken: "shared");
        using var cancel = new CancellationTokenSource();
        _manager.DuringValidation = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.CheckAsync(request, cancel.Token));
        Assert.True(Directory.Exists(_manager.ValidationPaths[0]));
        _manager.DuringValidation = null;
        Assert.True((await host.CheckAsync(request, CancellationToken.None)).Success);
        Assert.Single(_manager.ValidationDeviceIds.Distinct());
        await host.DiscardCheckAsync();
        Assert.False(Directory.Exists(_manager.ValidationPaths[0]));
    }

    [Fact]
    public async Task NativeHost_DraftChangeAndCloseDiscardStagedIdentity()
    {
        var host = new SetupNativeConnectionHost(CreateService(), _registry, NullLogger.Instance);
        var first = new OpenClaw.SetupEngine.SetupNativeConnectionRequest("wss://one.example", SharedToken: "token");
        Assert.True((await host.CheckAsync(first, CancellationToken.None)).Success);
        var firstPath = _manager.ValidationPaths[0];
        Assert.True(Directory.Exists(firstPath));
        Assert.True((await host.CheckAsync(first with { GatewayUrl = "wss://two.example" }, CancellationToken.None)).Success);
        Assert.False(Directory.Exists(firstPath));
        Assert.Equal(2, _manager.ValidationDeviceIds.Distinct().Count());
        await host.DiscardCheckAsync();
        Assert.All(_manager.ValidationPaths, path => Assert.False(Directory.Exists(path)));
        Assert.Empty(_registry.GetAll());
        Assert.Equal(0, _manager.DisconnectCount);
    }

    private GatewayDirectConnectService CreateService() =>
        new(
            _manager,
            _registry,
            _settings,
            () => _tunnelReconcileCount++,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(250));

    private GatewayRecord AddPreviousGateway()
    {
        var previous = new GatewayRecord
        {
            Id = "gw-previous",
            Url = "wss://previous.example",
            FriendlyName = "Previous",
        };
        _registry.AddOrUpdate(previous);
        _registry.SetActive(previous.Id);
        _registry.Save();
        _settings.GatewayUrl = previous.Url;
        _settings.UseSshTunnel = false;
        _settings.SaveOrThrow();
        return previous;
    }

    private DeviceIdentity CreateIdentity(string gatewayId)
    {
        var identity = new DeviceIdentity(_registry.GetIdentityDirectory(gatewayId));
        identity.Initialize();
        return identity;
    }

    private static GatewayConnectionSnapshot Connected(string gatewayId) =>
        new()
        {
            GatewayId = gatewayId,
            GatewayUrl = "wss://connected.example",
            OverallState = OverallConnectionState.Ready,
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.Connected,
            NodePairingStatus = PairingStatus.Paired,
        };

    private static GatewayConnectionSnapshot Failed(string gatewayId, string error) =>
        new()
        {
            GatewayId = gatewayId,
            GatewayUrl = "wss://failed.example",
            OverallState = OverallConnectionState.Error,
            OperatorState = RoleConnectionState.Error,
            OperatorError = error,
        };

    private sealed class FakeConnectionManager : IGatewayConnectionManager
    {
        public GatewayConnectionSnapshot CurrentSnapshot { get; private set; } =
            GatewayConnectionSnapshot.Idle;
        public string? ActiveGatewayUrl => CurrentSnapshot.GatewayUrl;
        public IOperatorGatewayClient? OperatorClient => null;
        public ConnectionDiagnostics Diagnostics { get; } = new();
        public bool IsManualGatewayLifecycleInProgress => false;
        public int LeaseCount { get; private set; }
        public int ConnectCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public string? LastGatewayId { get; private set; }
        public GatewayConnectionSnapshot NextSnapshot { get; set; } =
            GatewayConnectionSnapshot.Idle;
        public GatewayConnectionSnapshot? RestoreSnapshot { get; set; }
        public Action? BeforeSnapshot { get; set; }
        public Action? DuringValidation { get; set; }
        public int ValidationCount { get; private set; }
        public GatewayCredential? ValidatedCredential { get; private set; }
        public SetupCodeResult ValidationResult { get; set; } = new(SetupCodeOutcome.Success);
        public bool IssueValidationToken { get; set; }
        public bool ValidationRequiresV2 { get; set; }
        public List<string> ValidationDeviceIds { get; } = [];
        public List<string> ValidationPaths { get; } = [];
        public CancellationToken LastConnectCancellation { get; private set; }

        public Task<SetupCodeResult> ValidateConnectionAsync(
            GatewayRecord candidate, GatewayValidationIdentity identity, CancellationToken cancellationToken = default)
        {
            ValidationCount++;
            var device = new DeviceIdentity(identity.DirectoryPath);
            device.Initialize();
            ValidationDeviceIds.Add(device.DeviceId);
            ValidationPaths.Add(identity.DirectoryPath);
            ValidatedCredential = identity.OperatorCredential is { } ephemeral
                ? new GatewayCredential(ephemeral.Token, false, CredentialResolver.SourceDeviceToken)
                : new CredentialResolver(DeviceIdentityFileReader.Instance).ResolveOperator(candidate, identity.DirectoryPath);
            if (IssueValidationToken)
                identity.CaptureToken(new("issued-token", ["operator.read"], "operator"));
            identity.UseV2Signature = ValidationRequiresV2;
            DuringValidation?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ValidationResult);
        }

        public event EventHandler<GatewayConnectionSnapshot>? StateChanged;
#pragma warning disable CS0067
        public event EventHandler<ConnectionDiagnosticEvent>? DiagnosticEvent;
        public event EventHandler<OperatorClientChangedEventArgs>? OperatorClientChanged;
#pragma warning restore CS0067

        public Task ConnectAsync(string? gatewayId = null)
        {
            ConnectCount++;
            LastGatewayId = gatewayId;
            BeforeSnapshot?.Invoke();
            var snapshot = ConnectCount > 1 && RestoreSnapshot is not null
                ? RestoreSnapshot
                : NextSnapshot;
            CurrentSnapshot = snapshot with
            {
                GatewayId = gatewayId ?? snapshot.GatewayId
            };
            StateChanged?.Invoke(this, CurrentSnapshot);
            return Task.CompletedTask;
        }

        public Task ConnectAsync(string? gatewayId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastConnectCancellation = cancellationToken;
            return ConnectAsync(gatewayId);
        }

        public void SetCurrentSnapshot(GatewayConnectionSnapshot snapshot) =>
            CurrentSnapshot = snapshot;

        public Task DisconnectAsync()
        {
            DisconnectCount++;
            CurrentSnapshot = GatewayConnectionSnapshot.Idle;
            StateChanged?.Invoke(this, CurrentSnapshot);
            return Task.CompletedTask;
        }

        public Task<IDisposable> BeginManualGatewayLifecycleOperationAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeaseCount++;
            return Task.FromResult<IDisposable>(new Scope());
        }

        public Task ConnectNodeOnlyAsync(string? gatewayId = null) => Task.CompletedTask;
        public Task DisconnectByUserAsync() => DisconnectAsync();
        public Task ReconnectAsync() => Task.CompletedTask;
        public Task<bool> ReconnectIfCurrentAsync(
            string gatewayId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RecoverSshTunnelAsync(SshTunnelExit tunnelExit) => Task.FromResult(false);
        public Task SwitchGatewayAsync(string gatewayId) => Task.CompletedTask;
        public void SetGatewayConnectionIntent(string gatewayId, bool shouldBeConnected) { }
        public bool IsAutomaticReconnectAllowed(string gatewayId) => true;
        public Task EnsureNodeConnectedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<SetupCodeResult> ApplySetupCodeAsync(
            string setupCode,
            SshTunnelConfig? sshTunnel = null) =>
            Task.FromResult(new SetupCodeResult(SetupCodeOutcome.Success));
        public Task<SetupCodeResult> ConnectWithSharedTokenAsync(
            string gatewayUrl,
            string token,
            SshTunnelConfig? sshTunnel = null) =>
            Task.FromResult(new SetupCodeResult(SetupCodeOutcome.Success));
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
