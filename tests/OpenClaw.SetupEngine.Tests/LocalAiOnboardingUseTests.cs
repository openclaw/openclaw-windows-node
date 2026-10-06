using OpenClaw.Connection;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiOnboardingUseTests
{
    private static readonly LocalAiOnboardingSnapshot Selection = new(LocalAiOnboardingState.StartAndUse,
        new("selected", "Managed", 18789, null, null), "provider/model");

    [Fact]
    public async Task CancelledUse_DrainRetainsOwnershipUntilActualRollbackEnds()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trace = new List<string>();
        var host = new UseHost(async ct =>
        {
            trace.Add("mutation");
            using var registration = ct.Register(() => cancelled.SetResult());
            await cancelled.Task;
            trace.Add("rollback-start");
            await release.Task;
            trace.Add("rollback-end");
            ct.ThrowIfCancellationRequested();
            return new("selected", "provider/model");
        });
        using var cancellation = new CancellationTokenSource();
        var use = new LocalAiOnboardingUse(host);
        var operation = use.UseAsync(Selection, cancellation.Token);
        cancellation.Cancel();
        await cancelled.Task;
        var drain = use.DrainAsync();
        Assert.False(drain.IsCompleted);
        Assert.False(operation.IsCompleted);
        release.SetResult();
        await drain;
        trace.Add("owner-released");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(["mutation", "rollback-start", "rollback-end", "owner-released"], trace);
    }

    [Fact]
    public async Task ConfirmedFailureAllowsOnlyAnotherExplicitAdmission()
    {
        var host = new UseHost(_ => Task.FromException<SetupLocalAiUseResult>(
            new LocalAiStartFailedException("Cleaned failed start.")));
        var use = new LocalAiOnboardingUse(host);
        await Assert.ThrowsAsync<LocalAiStartFailedException>(() => use.UseAsync(Selection, default));
        Assert.Null(use.Expected);
        await use.DrainAsync();
        Assert.Equal(1, host.Actions);
        await Assert.ThrowsAsync<LocalAiStartFailedException>(() => use.UseAsync(Selection, default));
        Assert.Equal(2, host.Actions);
    }

    [Fact]
    public async Task UncertainFailureRetainsExactPairAndNeverReplaysUse()
    {
        var host = new UseHost(_ => Task.FromException<SetupLocalAiUseResult>(new IOException("Lost publication reply.")));
        var use = new LocalAiOnboardingUse(host);
        await Assert.ThrowsAsync<IOException>(() => use.UseAsync(Selection, default));
        Assert.Equal(new("selected", "provider/model"), use.Expected);
        Assert.Equal(SetupCompletionIntent.CustodianOnboarding, use.Expected!.CompletionIntent);
        await use.DrainAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => use.UseAsync(Selection, default));
        Assert.Throws<LocalAiSelectionRejectedException>(() =>
            LocalAiOnboardingUse.RequireGateway(use.Expected!.GatewayId, "other"));
        Assert.Equal(1, host.Actions);
    }

    [Fact]
    public async Task ReconnectMismatchRejectsBeforeLookingForCredentialsOrOpeningTransport()
    {
        using var directory = new TempDirectory();
        var registry = new GatewayRegistry(directory.Path);
        registry.AddOrUpdate(new GatewayRecord { Id = "other", Url = "wss://remote.invalid" });
        registry.SetActive("other");
        registry.Save();
        await Assert.ThrowsAsync<LocalAiSelectionRejectedException>(() =>
            SetupGatewaySession.ConnectAsync(directory.Path, expectedGatewayId: "selected"));
    }

    [Fact]
    public void CompletionGatewayGuard_IsReadOnlyAndRejectsSavedGatewaySwitch()
    {
        using var directory = new TempDirectory();
        var registry = new GatewayRegistry(directory.Path);
        var selected = new GatewayRecord { Id = "selected", Url = "wss://selected.invalid/control" };
        registry.AddOrUpdate(selected);
        registry.SetActive(selected.Id);
        registry.Save();
        var identity = new OpenClaw.Shared.DeviceIdentity(registry.GetIdentityDirectory(selected.Id));
        identity.Initialize();
        var receipt = new GatewayAiSetupCompletion(SetupCompletionIntent.CustodianOnboarding,
            selected.Id, GatewayDashboardBinding.Capture(selected), "provider/model", "primary", 1,
            IdentityBinding: SetupCompletionAuthority.CaptureIdentity(registry.GetIdentityDirectory(selected.Id), identity.DeviceId),
            SessionKey: "agent:primary:main");
        var path = Path.Combine(directory.Path, "gateways.json");
        var before = File.ReadAllBytes(path);
        SetupGatewaySession.RequireCompletionGateway(directory.Path, receipt);
        Assert.Equal(before, File.ReadAllBytes(path));
        registry.AddOrUpdate(new GatewayRecord { Id = "other", Url = "wss://other.invalid" });
        registry.SetActive("other");
        registry.Save();
        Assert.Throws<InvalidOperationException>(() =>
            SetupGatewaySession.RequireCompletionGateway(directory.Path, receipt));
    }

    private sealed class UseHost(Func<CancellationToken, Task<SetupLocalAiUseResult>> action) : ISetupLocalAiHost
    {
        public int Actions { get; private set; }
        public GatewayRegistrySnapshot BeginGatewaySetup() => throw new InvalidOperationException();
        public Task ReconcileGatewaySetupAsync(GatewayRegistrySnapshot expectedOutput, string? completedGatewayId) => throw new InvalidOperationException();
        public Task<LocalAiOnboardingSnapshot> ObserveAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<SetupLocalAiTarget> RevalidateReviewAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct) =>
            throw new InvalidOperationException();
        public Task<SetupLocalAiUseResult> UseAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct)
        {
            Actions++;
            return action(ct);
        }
    }
}
