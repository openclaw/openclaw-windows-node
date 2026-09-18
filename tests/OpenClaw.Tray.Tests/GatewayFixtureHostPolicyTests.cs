using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GatewayFixtureEnvironmentCollection
{
    public const string Name = "Gateway fixture environment";
}

// These tests never construct settings/registry services or call Windows. All effects
// are counters; environment changes are serialized and restored by TestSupport.
[Collection(GatewayFixtureEnvironmentCollection.Name)]
public sealed class GatewayFixtureHostPolicyTests
{
    [Fact]
    public void FixtureWslPolicy_RejectsLoopbackAndEveryDistroFallback()
    {
        using var temp = new TempDirectory();
        using var environment = SetEnvironment(temp, "1");
        var record = new GatewayRecord
        {
            Id = "fixture",
            Url = "ws://127.0.0.1:49152",
            IsLocal = true,
            SetupManagedDistroName = "SyntheticInstalledDistro",
        };

        Assert.True(GatewayFixtureIsolation.IsEnabled);
        Assert.False(WslKeepAlivePolicy.ShouldStart(record, "ws://localhost:18789"));
        Assert.False(WslKeepAlivePolicy.ShouldStart(null, "ws://127.0.0.1:49152"));
        Assert.Null(WslKeepAlivePolicy.ResolveDistroName(record, "SyntheticSetupDistro", "SyntheticOverride"));
        Assert.Null(WslKeepAlivePolicy.ResolveDistroName(null, null, null));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixtureAutoStart_ReconciliationRefusesBeforeAnyHostDelegate(bool configured)
    {
        using var temp = new TempDirectory();
        using var environment = SetEnvironment(temp, "1");
        var reads = 0;
        var writes = 0;

        var error = await Assert.ThrowsAsync<AutoStartRefusedException>(() =>
            AutoStartReconciliation.ReconcileAsync(
                configured,
                () => { reads++; return Task.FromResult(AutoStartState.Disabled); },
                _ => { writes++; return Task.CompletedTask; }));

        Assert.Contains("Gateway fixture mode", error.Message);
        Assert.Equal(0, reads);
        Assert.Equal(0, writes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixtureAutoStart_FailedToggleReportsDisabledWithoutInstalledStateQuery(bool requested)
    {
        using var temp = new TempDirectory();
        using var environment = SetEnvironment(temp, "1");
        var failure = Assert.Throws<AutoStartRefusedException>(AutoStartReconciliation.ThrowIfFixtureMutation);
        var reads = 0;

        var result = await AutoStartReconciliation.ResolveAfterFailedChangeAsync(
            requested,
            failure,
            () => { reads++; return Task.FromResult(AutoStartState.Enabled); });

        Assert.False(result);
        Assert.Equal(0, reads);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task FixtureAutoStart_BackgroundPreferenceRefreshSkipsGateAndHostEffects()
    {
        using var temp = new TempDirectory();
        using var environment = SetEnvironment(temp, "1");
        using var gate = new SemaphoreSlim(0, 1);
        var reads = 0;
        var writes = 0;

        await AutoStartSettingsApplier.ApplyLatestAsync(
            gate,
            () => { reads++; return true; },
            _ => { writes++; return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, reads);
        Assert.Equal(0, writes);
        Assert.Equal(0, gate.CurrentCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
        Assert.Throws<AutoStartRefusedException>(AutoStartReconciliation.ThrowIfFixtureMutation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative-local-data")]
    public async Task InvalidFixtureContext_BackgroundRefreshThrowsBeforeGateOrHostEffects(string? root)
    {
        using var temp = new TempDirectory();
        using var environment = SetEnvironment(temp, "1")
            .Set(GatewayFixtureIsolation.LocalDataDirectoryEnvironmentVariable, root);
        using var gate = new SemaphoreSlim(0, 1);
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AutoStartSettingsApplier.ApplyLatestAsync(
                gate,
                () => { calls++; return true; },
                _ => { calls++; return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(0, calls);
        Assert.Equal(0, gate.CurrentCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task OrdinaryIsolatedMode_BackgroundRefreshStillReadsLatestPreferenceAfterGate()
    {
        using var temp = new TempDirectory();
        using var environment = SetEnvironment(temp, null);
        using var gate = new SemaphoreSlim(0, 1);
        var preference = true;
        var reads = 0;
        var writes = new List<bool>();

        var pending = AutoStartSettingsApplier.ApplyLatestAsync(
            gate,
            () => { reads++; return preference; },
            enabled => { writes.Add(enabled); return Task.CompletedTask; });
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, reads);
        Assert.Empty(writes);
        preference = false;
        gate.Release();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, reads);
        Assert.Equal(new[] { false }, writes);
        Assert.Equal(1, gate.CurrentCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative-profile")]
    public async Task InvalidFixtureContext_FailsBeforeWslOrAutoStartPolicyEffects(string? root)
    {
        using var temp = new TempDirectory();
        using var environment = SetEnvironment(temp, "1")
            .Set(GatewayFixtureIsolation.DataDirectoryEnvironmentVariable, root);
        var calls = 0;

        Assert.Throws<InvalidOperationException>(() => GatewayFixtureIsolation.IsEnabled);
        Assert.Throws<InvalidOperationException>(() => WslKeepAlivePolicy.ShouldStart(null, "ws://localhost:18789"));
        Assert.Throws<InvalidOperationException>(() => WslKeepAlivePolicy.ResolveDistroName(null, null, null));
        Assert.Throws<InvalidOperationException>(AutoStartReconciliation.ThrowIfFixtureMutation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AutoStartReconciliation.ReconcileAsync(
            true,
            () => { calls++; return Task.FromResult(AutoStartState.Disabled); },
            _ => { calls++; return Task.CompletedTask; }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AutoStartReconciliation.ResolveAfterFailedChangeAsync(
            true,
            new InvalidOperationException("Synthetic failure"),
            () => { calls++; return Task.FromResult(AutoStartState.Enabled); }));

        Assert.Equal(0, calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryIsolatedMode_PreservesWslFallbackAndAutoStartEffects(bool configured)
    {
        using var temp = new TempDirectory();
        using var environment = SetEnvironment(temp, null);
        var reads = 0;
        var writes = 0;

        Assert.False(GatewayFixtureIsolation.IsEnabled);
        Assert.True(WslKeepAlivePolicy.ShouldStart(null, "ws://127.0.0.1:49152"));
        Assert.Equal(AppIdentity.SetupDistroName, WslKeepAlivePolicy.ResolveDistroName(null, null, null));
        AutoStartReconciliation.ThrowIfFixtureMutation();
        var result = await AutoStartReconciliation.ReconcileAsync(
            configured,
            () => { reads++; return Task.FromResult(AutoStartState.Disabled); },
            enabled => { Assert.True(enabled); writes++; return Task.CompletedTask; });

        Assert.Equal(configured, result);
        Assert.Equal(1, reads);
        Assert.Equal(configured ? 1 : 0, writes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    private static EnvironmentScope SetEnvironment(TempDirectory temp, string? mode) => new EnvironmentScope()
        .Set(GatewayFixtureIsolation.ModeEnvironmentVariable, mode)
        .Set(GatewayFixtureIsolation.DataDirectoryEnvironmentVariable, temp.Combine("profile"))
        .Set(GatewayFixtureIsolation.LocalDataDirectoryEnvironmentVariable, temp.Combine("setup-local"))
        .Set(GatewayFixtureIsolation.LocalAppDataDirectoryEnvironmentVariable, null);
}
