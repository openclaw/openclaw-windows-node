using System.Text.Json;
using OpenClaw.SetupEngine;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Connection.LocalAi;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class LocalAiGatewayRpcConfigurationTransportTests
{
    [Fact]
    public void NativeAdmissionAcceptsStagedRecordWithoutPublishingRegistryState()
    {
        var rpc = new Transport();
        var target = NativeLocalAiGatewayTarget.Capture(NativeRecord, rpc.Route);
        Assert.Equal(NativeRecord.Id, target.GatewayId);
        Assert.Equal(NativeRecord.NativePackageFamilyName, target.PackageFamilyName);
        target.RequireCurrent(rpc.Route);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("legacy")]
    [InlineData("wsl")]
    [InlineData("package")]
    [InlineData("id")]
    [InlineData("endpoint")]
    [InlineData("identity")]
    [InlineData("session")]
    public void NativeAdmissionRejectsUnsupportedOrReplacedOwner(string change)
    {
        var rpc = new Transport();
        var record = change switch
        {
            "remote" => NativeRecord with { Url = "wss://example.com:443", IsLocal = false },
            "legacy" => NativeRecord with { NativeRuntimeContract = null },
            "wsl" => NativeRecord with { SetupManagedDistroName = "OpenClaw" },
            "package" => NativeRecord with { NativePackageFamilyName = "other-package" },
            "id" => NativeRecord with { Id = "other-id" },
            "endpoint" => NativeRecord with { Url = "ws://127.0.0.1:55061" },
            _ => NativeRecord,
        };
        if (change == "identity") rpc.Route = rpc.Route with { IdentityBinding = null };
        if (change == "session") rpc.Route = rpc.Route with { SessionKey = "agent:other:main" };
        Assert.Throws<InvalidOperationException>(() => NativeLocalAiGatewayTarget.Capture(record, rpc.Route));
    }

    [Fact]
    public async Task SnapshotAndPatch_UseAuthenticatedRpcWithExactBaseHash()
    {
        var rpc = new Transport();
        var configuration = Create(rpc);
        var snapshot = await configuration.CaptureAsync(CancellationToken.None);
        var patch = JsonSerializer.SerializeToElement(new { models = new { providers = new { } } });
        await configuration.ApplyAsync(snapshot, patch, CancellationToken.None);
        Assert.Equal(["config.get", "config.patch"], rpc.Calls.Select(c => c.Method));
        var payload = JsonSerializer.SerializeToElement(rpc.Calls[1].Parameters);
        Assert.Equal("exact-base-hash", payload.GetProperty("baseHash").GetString());
        Assert.Equal(patch.GetRawText(), payload.GetProperty("raw").GetString());
        Assert.False(payload.TryGetProperty("replacePaths", out _));
    }

    [Fact]
    public async Task ProviderWithdrawalNamesOnlyOwnedModelArrayAndRetainsSnapshotGuard()
    {
        var rpc = new Transport();
        var configuration = Create(rpc);
        var snapshot = await configuration.CaptureAsync(CancellationToken.None);
        var patch = JsonDocument.Parse("""{"models":{"providers":{"llamacpp":null}}}""").RootElement.Clone();
        await configuration.ApplyAsync(snapshot, patch, CancellationToken.None,
            [LocalAiGatewayProviderDefinition.ProviderModelsPath]);
        var payload = JsonSerializer.SerializeToElement(rpc.Calls[1].Parameters);
        Assert.Equal("exact-base-hash", payload.GetProperty("baseHash").GetString());
        Assert.Equal([LocalAiGatewayProviderDefinition.ProviderModelsPath],
            payload.GetProperty("replacePaths").EnumerateArray().Select(value => value.GetString()));
    }

    [Theory]
    [InlineData("models.providers")]
    [InlineData("models.providers.other.models")]
    [InlineData("agents.defaults.models")]
    public async Task UnrelatedArrayReplacementIsRejectedBeforeMutation(string path)
    {
        var rpc = new Transport();
        var configuration = Create(rpc);
        var snapshot = await configuration.CaptureAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => configuration.ApplyAsync(
            snapshot, JsonSerializer.SerializeToElement(new { }), CancellationToken.None, [path]));
        Assert.Single(rpc.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"hash":"","config":{}}""")]
    [InlineData("""{"hash":"hash","config":null}""")]
    [InlineData("""{"hash":"hash","config":{},"valid":false}""")]
    public async Task MissingOrInvalidSnapshotNeverAllowsMutation(string response)
    {
        var rpc = new Transport { Response = JsonDocument.Parse(response).RootElement.Clone() };
        var configuration = Create(rpc);
        await Assert.ThrowsAsync<InvalidDataException>(() => configuration.CaptureAsync(CancellationToken.None));
        Assert.Single(rpc.Calls);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("session")]
    [InlineData("identity")]
    [InlineData("generation")]
    [InlineData("offline")]
    public async Task DriftAfterCaptureRejectsMutation(string drift)
    {
        var rpc = new Transport();
        var configuration = Create(rpc);
        var snapshot = await configuration.CaptureAsync(CancellationToken.None);
        switch (drift)
        {
            case "gateway": rpc.Route = rpc.Route with { GatewayId = "other" }; break;
            case "session": rpc.Route = rpc.Route with { SessionKey = "agent:other:main" }; break;
            case "identity": rpc.Route = rpc.Route with { IdentityBinding = "other" }; break;
            case "generation": rpc.Generation++; break;
            case "offline": rpc.IsConnected = false; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            configuration.ApplyAsync(snapshot, JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
        Assert.Single(rpc.Calls);
    }

    [Fact]
    public async Task SnapshotFromAnotherTransportCannotBeApplied()
    {
        var rpc = new Transport();
        var first = Create(rpc);
        var second = Create(rpc);
        var snapshot = await first.CaptureAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            second.ApplyAsync(snapshot, JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
        Assert.Single(rpc.Calls);
    }

    [Fact]
    public async Task PreDispatchFailureDoesNotJournalOrRetainAnEarlierAcknowledgement()
    {
        var rpc = new Transport();
        var configuration = Create(rpc);
        var snapshot = await configuration.CaptureAsync(default);
        var patch = JsonSerializer.SerializeToElement(new { });
        await configuration.ApplyAsync(snapshot, patch, default);
        Assert.NotNull(configuration.LastConfirmedHash);
        var admitted = false;
        configuration.BeforeMutationDispatch = () => admitted = true;
        rpc.Generation++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => configuration.ApplyAsync(snapshot, patch, default));
        Assert.False(admitted);
        Assert.Null(configuration.LastConfirmedHash);
        Assert.Equal(2, rpc.Calls.Count);
    }

    [Fact]
    public async Task CancellationAfterDispatchDrainsBoundedMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rpc = new Transport
        {
            Patch = async ct =>
            {
                Assert.False(ct.CanBeCanceled);
                dispatched.SetResult();
                await released.Task;
            },
        };
        var configuration = Create(rpc);
        var snapshot = await configuration.CaptureAsync(cancellation.Token);
        var mutation = configuration.ApplyAsync(snapshot, JsonSerializer.SerializeToElement(new { }), cancellation.Token);
        await dispatched.Task;
        cancellation.Cancel();
        Assert.False(mutation.IsCompleted);
        released.SetResult();
        await mutation;
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void MissingCapabilityOrWriteScopeFailsBeforeRequest(bool patchAvailable, bool admin)
    {
        var rpc = new Transport
        {
            Methods = patchAvailable ? ["config.get", "config.patch"] : ["config.get"],
            OperatorScopes = admin ? ["operator.admin"] : ["operator.read"],
        };
        Assert.Throws<InvalidOperationException>(() => Create(rpc));
        Assert.Empty(rpc.Calls);
    }

    private static readonly GatewayRecord NativeRecord = new()
    {
        Id = "native",
        Url = "ws://127.0.0.1:55060",
        IsLocal = true,
        NativePackageFamilyName = "OpenClawFoundation.OpenClawGateway_test",
        NativeRuntimeContract = NativeGatewayPackageClient.IsolatedContract,
    };

    private static LocalAiGatewayRpcConfigurationTransport Create(Transport rpc) =>
        new(NativeLocalAiGatewayTarget.Capture(NativeRecord, rpc.Route), rpc);

    private sealed class Transport : IGatewayAiSetupTransport
    {
        public GatewayAiSetupRoute Route { get; set; } = new("native", "main", "authority",
            GatewayDashboardBinding.Capture(NativeRecord), new string('A', 64), "agent:main:main");
        public long Generation { get; set; } = 1;
        public bool IsConnected { get; set; } = true;
        public IReadOnlyCollection<string> Methods { get; init; } = ["config.get", "config.patch"];
        public IReadOnlyCollection<string> OperatorScopes { get; init; } = ["operator.admin"];
        public JsonElement Response { get; init; } = JsonSerializer.SerializeToElement(
            new { hash = "exact-base-hash", config = new { }, valid = true });
        public List<(string Method, object Parameters)> Calls { get; } = [];
        public Func<CancellationToken, Task>? Patch { get; init; }

        public async Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs, CancellationToken ct)
        {
            Assert.Equal(15_000, timeoutMs);
            Calls.Add((method, parameters));
            if (method == "config.patch" && Patch is not null) await Patch(ct);
            return Response;
        }
    }
}
