using OpenClaw.Connection.NativeGateway;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class NativeGatewayRemovalTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly GatewayRegistry _registry;
    private readonly GatewayRecord _record = LocalGatewaySettingsTests.Native();
    private readonly Resolver _resolver = new();
    private readonly List<string> _calls = [];
    private string _response = Success("removed");
    private Action? _onTeardown;
    private Action? _onDisconnect;
    private int _exitCode;

    public NativeGatewayRemovalTests()
    {
        _registry = new(_directory.Path);
        _registry.AddOrUpdate(_record);
        _registry.AddOrUpdate(new() { Id = "wsl", Url = "ws://localhost:18790", IsLocal = true, SetupManagedDistroName = "AnotherGateway" });
        _registry.AddOrUpdate(new() { Id = "remote", Url = "wss://remote.test" });
        _registry.SetActive(_record.Id);
        _registry.Save();
        Directory.CreateDirectory(_registry.GetIdentityDirectory(_record.Id));
        File.WriteAllText(Path.Combine(_registry.GetIdentityDirectory(_record.Id), "device-key-ed25519.json"), "fixture");
        File.WriteAllText(_directory.Combine("mcp-token.txt"), "fixture-mcp");
        File.WriteAllText(_directory.Combine("settings.json"), "fixture-settings");
    }

    private static string Success(string state) =>
        """{"ok":true,"schemaVersion":1,"command":"teardown","integration":{"kind":"isolated-session","version":1},"session":{"state":"STATE"},"gateway":{"state":"records-removed"}}""".Replace("STATE", state);

    private Task Remove(GatewayRecord? target = null, CancellationToken ct = default) =>
        new NativeGatewayRemoval(_registry, _resolver, new NativeGatewayPackageClient((package, args, _) =>
        {
            Assert.Equal(_record.NativePackageFamilyName, package.PackageFamilyName);
            if (args.SequenceEqual(["status", "--json"]))
                return Task.FromResult(new NativeGatewayCommandResult(0, _resolver.Contract == NativeGatewayContract.Legacy
                    ? "{}" : """{"schemaVersion":1,"command":"status","integration":{"kind":"isolated-session","version":1}}"""));
            Assert.Equal(["teardown", "--force", "--json"], args);
            _calls.Add("teardown");
            _onTeardown?.Invoke();
            return Task.FromResult(new NativeGatewayCommandResult(_exitCode, _response));
        })).RemoveAsync(target ?? _record, () =>
        {
            _calls.Add("disconnect");
            _onDisconnect?.Invoke();
            return Task.CompletedTask;
        }, ct);

    [Theory]
    [InlineData("removed")]
    [InlineData("not-configured")]
    public async Task RemovalRequiresAcknowledgementAndPreservesOtherInstallations(string state)
    {
        _response = Success(state);
        await Remove();
        Assert.Equal(["disconnect", "teardown"], _calls);
        Assert.Null(_registry.GetById(_record.Id));
        Assert.Null(_registry.ActiveGatewayId);
        Assert.NotNull(_registry.GetById("wsl"));
        Assert.NotNull(_registry.GetById("remote"));
        Assert.False(Directory.Exists(_registry.GetIdentityDirectory(_record.Id)));
        Assert.Equal("fixture-mcp", File.ReadAllText(_directory.Combine("mcp-token.txt")));
        Assert.Equal("fixture-settings", File.ReadAllText(_directory.Combine("settings.json")));
        var reopened = new GatewayRegistry(_directory.Path);
        reopened.Load();
        Assert.Null(reopened.GetById(_record.Id));
        Assert.Equal(2, reopened.GetAll().Count);
    }

    [Theory]
    [InlineData(0, "{}")]
    [InlineData(1, """{"ok":false,"schemaVersion":1,"command":"teardown","integration":{"kind":"isolated-session","version":1},"error":{"message":"fixture failure"}}""")]
    [InlineData(0, """{"ok":true,"schemaVersion":1,"command":"setup","integration":{"kind":"isolated-session","version":1},"session":{"state":"removed"},"gateway":{"state":"records-removed"}}""")]
    [InlineData(0, """{"ok":true,"schemaVersion":1,"command":"teardown","integration":{"kind":"isolated-session","version":1},"session":{"state":"ready"},"gateway":{"state":"records-removed"}}""")]
    [InlineData(0, """{"ok":true,"schemaVersion":1,"command":"teardown","integration":{"kind":"isolated-session","version":1},"session":{"state":"removed"},"gateway":{"state":"running"}}""")]
    public async Task FailureOrIncompleteResponseRetainsRetryState(int exitCode, string response)
    {
        _exitCode = exitCode;
        _response = response;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Remove());
        AssertRetained();
    }

    [Fact]
    public async Task ChangedTargetDuringDisconnectDoesNotTeardown()
    {
        _onDisconnect = () => { _registry.SetActive("remote"); _registry.Save(); };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Remove());
        Assert.Equal(["disconnect"], _calls);
        AssertRetained();
        Assert.Equal("remote", _registry.ActiveGatewayId);
    }

    [Fact]
    public async Task UnrelatedRegistryChangeDuringTeardownIsPreservedWithoutBlockingRemoval()
    {
        _onTeardown = () => _registry.UpdateAndSave("remote", record => record with { FriendlyName = "Concurrent" });
        await Remove();
        Assert.Null(_registry.GetById(_record.Id));
        Assert.Equal("Concurrent", _registry.GetById("remote")!.FriendlyName);
    }

    [Fact]
    public async Task TargetChangeAfterAcknowledgedTeardownExplainsPartialOutcome()
    {
        _onTeardown = () => _registry.UpdateAndSave(_record.Id, record => record with { Url = "ws://localhost:19000" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Remove());
        Assert.Contains("installation was removed", error.Message);
        AssertRetained();
    }

    [Fact]
    public async Task DuplicatePackageConnectionsBlockPackageWideDeletion()
    {
        _registry.AddOrUpdate(_record with { Id = "duplicate" });
        _registry.Save();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Remove());
        Assert.Empty(_calls);
        AssertRetained();
    }

    [Fact]
    public async Task CancelBeforeDispatchDoesNotDisconnectOrRemove()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Remove(ct: cts.Token));
        Assert.Empty(_calls);
        AssertRetained();
    }

    [Fact]
    public async Task PackageFamilyOrContractChangeCannotChooseAnotherInstallation()
    {
        _resolver.Contract = NativeGatewayContract.Legacy;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Remove());
        Assert.Empty(_calls);
        AssertRetained();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyProfileRemovalDoesNotNeedInstalledOrLegacyPackage(bool missing)
    {
        var legacy = _record with { NativeRuntimeContract = null };
        _registry.AddOrUpdate(legacy);
        _registry.Save();
        _resolver.Missing = missing;
        await Remove(legacy);
        Assert.Equal(0, _resolver.Calls);
        Assert.Equal(["disconnect"], _calls);
        Assert.Null(_registry.GetById(legacy.Id));
    }

    private void AssertRetained()
    {
        Assert.NotNull(_registry.GetById(_record.Id));
        Assert.True(File.Exists(Path.Combine(_registry.GetIdentityDirectory(_record.Id), "device-key-ed25519.json")));
    }

    public void Dispose() => _directory.Dispose();

    private sealed class Resolver : INativeGatewayPackageResolver
    {
        public NativeGatewayContract Contract { get; set; } = NativeGatewayContract.IsolatedSessionV1;
        public int Calls { get; private set; }
        public bool Missing { get; set; }
        public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Missing) throw new NativeGatewayPackageNotInstalledException();
            cancellationToken.ThrowIfCancellationRequested();
            var family = LocalGatewaySettingsTests.Native().NativePackageFamilyName!;
            var aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", family);
            return Task.FromResult(new NativeGatewayPackage(family,
                Contract == NativeGatewayContract.Legacy ? "0.0.0.1" : "1.0.0.0",
                Path.Combine(aliases, "openclaw.exe"), Path.Combine(aliases, "clawctl.exe")));
        }
    }
}
