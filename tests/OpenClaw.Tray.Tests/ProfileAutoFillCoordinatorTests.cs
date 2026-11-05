using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class ProfileAutoFillCoordinatorTests
{
    private static AccountIdentityResult Account => new(AccountFieldStatus.Available, "Ada",
        AccountFieldStatus.Available, new AccountPhotoDraft(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], AccountPhotoPolicy.Png));

    private sealed class Provider : IWindowsAccountIdentityProvider
    {
        public int Reads;
        public Func<int, Task<AccountIdentityResult>> Read = _ => Task.FromResult(Account);
        public Task<AccountIdentityResult> ReadAsync(CancellationToken cancellationToken) => Read(++Reads);
    }

    private sealed class ErrorLogger : IOpenClawLogger
    {
        public readonly TaskCompletionSource<Exception> ErrorReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<Exception> Errors = [];
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null)
        {
            if (ex is null) return;
            Errors.Add(ex);
            ErrorReported.TrySetResult(ex);
        }
    }

    public class ManagerProxy : DispatchProxy
    {
        public GatewayConnectionSnapshot Snapshot = GatewayConnectionSnapshot.Idle;
        public IOperatorGatewayClient? Client;
        public Action? OnClientRead;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "get_CurrentSnapshot") return Snapshot;
            if (method?.Name == "get_OperatorClient")
            {
                OnClientRead?.Invoke();
                return Client;
            }
            throw new NotSupportedException(method?.Name);
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly TempDirectory _directory = new();
        public readonly IGatewayConnectionManager Manager = DispatchProxy.Create<IGatewayConnectionManager, ManagerProxy>();
        public readonly IOperatorGatewayClient A = DispatchProxy.Create<IOperatorGatewayClient, ProfileCompletionTests.Proxy>();
        public readonly IOperatorGatewayClient B = DispatchProxy.Create<IOperatorGatewayClient, ProfileCompletionTests.Proxy>();
        public readonly Provider Provider = new();
        public readonly ErrorLogger Logger = new();
        public readonly List<ProfileAutoFillResult> Outcomes = [];
        public readonly GatewayRegistry Registry;
        public readonly ProfileAutoFillCoordinator Coordinator;
        public readonly ProfileCompletionService Service;
        public ManagerProxy State => (ManagerProxy)Manager;
        public ProfileCompletionTests.Proxy RpcA => (ProfileCompletionTests.Proxy)A;
        public ProfileCompletionTests.Proxy RpcB => (ProfileCompletionTests.Proxy)B;

        public Harness()
        {
            Registry = new GatewayRegistry(_directory.Path);
            Registry.AddOrUpdate(new GatewayRecord { Id = "A", Url = "ws://localhost:1", SetupManagedDistroName = "fake-a" });
            Registry.AddOrUpdate(new GatewayRecord { Id = "B", Url = "ws://localhost:2", NativePackageFamilyName = "fake-b" });
            Connect("A", A);
            Coordinator = new(Capture, Provider, Outcomes.Add, Logger);
            Service = new(Capture, Provider);
        }

        public ProfileCompletionContext? Capture() => ProfileCompletionContext.Capture(Manager, Registry);
        public void Connect(string id, IOperatorGatewayClient client)
        {
            Registry.SetActive(id);
            State.Snapshot = new GatewayConnectionSnapshot { GatewayId = id, OperatorState = RoleConnectionState.Connected };
            State.Client = client;
        }

        public void Dispose() => _directory.Dispose();
    }

    [Theory]
    [InlineData("saved")]
    [InlineData("uncertain")]
    [InlineData("denied")]
    [InlineData("failed")]
    [InlineData("empty")]
    public async Task OncePerGateway_SurvivesObserversAndGatewayRoundTrip(string outcome)
    {
        using var h = new Harness();
        if (outcome == "empty")
            h.Provider.Read = _ => Task.FromResult(AccountIdentityResult.Unavailable(AccountFieldStatus.Empty));
        else if (outcome != "saved")
            h.RpcA.OnWrite = _ => Task.FromException<JsonElement>(outcome switch
            {
                "uncertain" => new TimeoutException(),
                "denied" => GatewayRequestErrors.Create("denied", "FORBIDDEN", null),
                _ => GatewayRequestErrors.Create("invalid", "INVALID_REQUEST", null),
            });

        var oldRefreshes = 0;
        EventHandler oldObserver = (_, _) => oldRefreshes++;
        h.Coordinator.ProfileChanged += oldObserver;
        await h.Coordinator.ObserveAsync();
        h.Coordinator.ProfileChanged -= oldObserver;
        var firstCalls = h.RpcA.Methods.ToArray();
        var newRefreshes = 0;
        EventHandler newObserver = (_, _) => newRefreshes++;
        h.Coordinator.ProfileChanged += newObserver;

        await h.Coordinator.ObserveAsync();
        h.Connect("B", h.B);
        await h.Coordinator.ObserveAsync();
        h.Connect("A", h.A);
        await h.Coordinator.ObserveAsync();

        Assert.Equal(firstCalls, h.RpcA.Methods);
        Assert.Equal(2, h.Provider.Reads);
        Assert.Equal(2, h.Outcomes.Count);
        Assert.Equal(outcome is "saved" or "uncertain" ? 1 : 0, oldRefreshes);
        Assert.Equal(outcome == "empty" ? 0 : 1, newRefreshes);
        h.Coordinator.ProfileChanged -= newObserver;
    }

    [Fact]
    public async Task ReopenedObserver_DuringWrite_SharesInFlightAndReceivesRefresh()
    {
        using var h = new Harness();
        var write = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.RpcA.OnWrite = method => method == "users.setDisplayName"
            ? write.Task : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        var oldRefreshes = 0;
        var newRefreshes = 0;
        EventHandler oldObserver = (_, _) => oldRefreshes++;
        EventHandler newObserver = (_, _) => newRefreshes++;
        h.Coordinator.ProfileChanged += oldObserver;
        var run = h.Coordinator.ObserveAsync();
        Assert.False(run.IsCompleted);
        h.Coordinator.ProfileChanged -= oldObserver;
        h.Coordinator.ProfileChanged += newObserver;
        await h.Coordinator.ObserveAsync();

        Assert.Equal(1, h.Provider.Reads);
        Assert.Equal(1, h.RpcA.Methods.Count(m => m == "users.setDisplayName"));
        write.SetResult(JsonSerializer.SerializeToElement(new { }));
        await run;

        Assert.Single(h.Outcomes);
        Assert.Equal(0, oldRefreshes);
        Assert.Equal(1, newRefreshes);
        Assert.Equal(1, h.RpcA.Methods.Count(m => m == "users.setAvatar"));
        h.Coordinator.ProfileChanged -= newObserver;
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("stale")]
    [InlineData("none")]
    public async Task RpcFault_PreservesException_AndDrainsOnlyCurrentPendingGateway(string observation)
    {
        using var h = new Harness();
        var write = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.RpcA.OnWrite = _ => write.Task;
        var run = h.Coordinator.ObserveAsync();
        if (observation != "none")
        {
            h.Connect("B", h.B);
            await h.Coordinator.ObserveAsync();
            if (observation == "stale")
                h.Registry.SetActive(null);
        }

        var error = new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        write.SetException(error);
        Assert.Same(error, await Assert.ThrowsAsync<WebSocketException>(() => run));

        Assert.Equal(observation == "pending" ? 1 : 0, h.Outcomes.Count);
        Assert.Equal(observation == "pending" ? 2 : 1, h.Provider.Reads);
        Assert.Equal(observation == "pending" ? 2 : 0, h.RpcB.Payloads.Count);
        var aCalls = h.RpcA.Methods.ToArray();
        h.Connect("A", h.A);
        await h.Coordinator.ObserveAsync();
        Assert.Equal(aCalls, h.RpcA.Methods);
        Assert.Empty(h.Logger.Errors);
    }

    [Fact]
    public async Task FaultedPendingWrite_IsReportedSeparately_WithoutReplacingOriginalFaultOrRetrying()
    {
        using var h = new Harness();
        var firstWrite = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingWrite = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.RpcA.OnWrite = _ => firstWrite.Task;
        h.RpcB.OnWrite = _ => pendingWrite.Task;
        var run = h.Coordinator.ObserveAsync();
        h.Connect("B", h.B);
        await h.Coordinator.ObserveAsync();
        var firstError = new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        firstWrite.SetException(firstError);
        Assert.Same(firstError, await Assert.ThrowsAsync<WebSocketException>(() => run));
        await h.Coordinator.ObserveAsync();
        Assert.Equal(2, h.Provider.Reads);
        Assert.Equal(1, h.RpcB.Methods.Count(m => m == "users.setDisplayName"));
        Assert.Empty(h.Logger.Errors);

        var pendingError = new WebSocketException(WebSocketError.NativeError);
        pendingWrite.SetException(pendingError);
        Assert.Same(pendingError, await h.Logger.ErrorReported.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(h.Logger.Errors);
        var bCalls = h.RpcB.Methods.ToArray();
        await h.Coordinator.ObserveAsync();
        Assert.Equal(bCalls, h.RpcB.Methods);
        h.Connect("A", h.A);
        var aCalls = h.RpcA.Methods.ToArray();
        await h.Coordinator.ObserveAsync();
        Assert.Equal(aCalls, h.RpcA.Methods);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompletedWrite_RefreshesReplacementOnlyOnSameGateway_WithoutWriteRetry(
        bool uncertain, bool sameGateway)
    {
        using var h = new Harness();
        h.Provider.Read = _ => Task.FromResult(new AccountIdentityResult(
            AccountFieldStatus.Available, "Ada", AccountFieldStatus.Empty, null));
        var write = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.RpcA.OnWrite = _ => write.Task;
        var refreshes = 0;
        h.Coordinator.ProfileChanged += (_, _) =>
        {
            refreshes++;
            _ = h.Coordinator.ObserveAsync();
        };
        var run = h.Coordinator.ObserveAsync();
        h.Connect(sameGateway ? "A" : "B", h.B);
        if (uncertain)
            write.SetException(new TimeoutException());
        else
            write.SetResult(JsonSerializer.SerializeToElement(new { }));
        await run;

        Assert.Equal(sameGateway ? 1 : 0, refreshes);
        Assert.Single(h.Outcomes);
        Assert.Equal(uncertain ? ProfileStep.Uncertain : ProfileStep.Saved, h.Outcomes[0].Save!.Name);
        Assert.Equal(1, h.Provider.Reads);
        Assert.Empty(h.RpcB.Methods);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewContextObservedWhileBusy_IsFilledWithoutAnotherEvent(bool sameGateway)
    {
        using var h = new Harness();
        var account = new TaskCompletionSource<AccountIdentityResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Provider.Read = read => read == 1 ? account.Task : Task.FromResult(Account);
        var run = h.Coordinator.ObserveAsync();
        Assert.False(run.IsCompleted);
        h.Connect(sameGateway ? "A" : "B", h.B);
        await h.Coordinator.ObserveAsync();
        account.SetResult(Account);
        await run;

        Assert.Equal(2, h.Provider.Reads);
        Assert.Equal(ProfileOffer.Unavailable, h.Outcomes[0].Offer);
        Assert.Equal(new ProfileSaveResult(ProfileStep.Saved, ProfileStep.Saved), h.Outcomes[1].Save);
        Assert.Empty(h.RpcA.Payloads);
        Assert.Equal(["users.setDisplayName", "users.setAvatar"], h.RpcB.Payloads.Keys);
    }

    [Fact]
    public async Task UnavailableSameContext_DoesNotSpin_AndRetriesOnlyOnLaterObservation()
    {
        using var h = new Harness();
        h.RpcA.OnSelfRead = () =>
        {
            _ = h.Coordinator.ObserveAsync();
            throw new TimeoutException();
        };
        await h.Coordinator.ObserveAsync();
        Assert.Equal(["users.self"], h.RpcA.Methods);
        Assert.Single(h.Outcomes);
        Assert.Equal(0, h.Provider.Reads);

        h.RpcA.OnSelfRead = null;
        await h.Coordinator.ObserveAsync();
        Assert.Equal(2, h.Outcomes.Count);
        Assert.Equal(1, h.Provider.Reads);
    }

    [Fact]
    public async Task RetryableContext_DoesNotForgetOtherGatewayAttempts()
    {
        using var h = new Harness();
        await h.Coordinator.ObserveAsync();
        var aCalls = h.RpcA.Methods.ToArray();
        h.Connect("B", h.B);
        h.RpcB.OnSelfRead = () => throw new TimeoutException();
        await h.Coordinator.ObserveAsync();
        h.Connect("A", h.A);
        await h.Coordinator.ObserveAsync();
        Assert.Equal(aCalls, h.RpcA.Methods);

        h.Connect("B", h.B);
        h.RpcB.OnSelfRead = null;
        await h.Coordinator.ObserveAsync();
        Assert.Equal(3, h.Outcomes.Count);
        Assert.Equal(2, h.Provider.Reads);
    }

    [Fact]
    public async Task UncertainWrite_WithPendingReplacementClient_DoesNotRetry()
    {
        using var h = new Harness();
        var write = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.RpcA.OnWrite = _ => write.Task;
        var run = h.Coordinator.ObserveAsync();
        h.Connect("A", h.B);
        await h.Coordinator.ObserveAsync();
        write.SetException(new TimeoutException());
        await run;
        await h.Coordinator.ObserveAsync();

        Assert.Single(h.Outcomes);
        Assert.Equal(new ProfileSaveResult(ProfileStep.Uncertain, ProfileStep.NotAttempted), h.Outcomes[0].Save);
        Assert.Empty(h.RpcB.Methods);
        Assert.DoesNotContain("users.setAvatar", h.RpcA.Methods);
    }

    [Fact]
    public async Task PendingContextNoLongerSelected_IsNotReplayed()
    {
        using var h = new Harness();
        var account = new TaskCompletionSource<AccountIdentityResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Provider.Read = _ => account.Task;
        var run = h.Coordinator.ObserveAsync();
        h.Connect("B", h.B);
        await h.Coordinator.ObserveAsync();
        h.Registry.SetActive(null);
        account.SetResult(Account);
        await run;

        Assert.Single(h.Outcomes);
        Assert.Empty(h.RpcB.Methods);
        Assert.Empty(h.RpcA.Payloads);
    }

    [Fact]
    public async Task SelectionMovesBeforeClientRetirement_RejectsOldClient_ThenAllowsCoherentTarget()
    {
        using var h = new Harness();
        h.Registry.AddOrUpdate(new GatewayRecord { Id = "A", Url = "wss://remote.example.test" });
        h.Registry.SetActive("B");

        await h.Coordinator.ObserveAsync();
        var preparation = await h.Service.PrepareAsync(CancellationToken.None);
        Assert.Equal(ProfileOffer.Unavailable, preparation.Offer);
        Assert.Empty(h.RpcA.Methods);
        Assert.Empty(h.RpcB.Methods);
        Assert.Equal(0, h.Provider.Reads);

        h.Connect("B", h.B);
        await h.Coordinator.ObserveAsync();
        Assert.Equal(1, h.Provider.Reads);
        Assert.Empty(h.RpcA.Methods);
        Assert.Equal(["users.setDisplayName", "users.setAvatar"], h.RpcB.Payloads.Keys);
    }

    [Theory]
    [InlineData("unmanaged")]
    [InlineData("tunnel")]
    [InlineData("disconnectedClient")]
    [InlineData("disconnectedSnapshot")]
    [InlineData("missingGatewayId")]
    public async Task ContextAdmission_RequiresConnectedMatchingManagedRecord(string kind)
    {
        using var h = new Harness();
        switch (kind)
        {
            case "unmanaged":
                h.Registry.AddOrUpdate(new GatewayRecord { Id = "A", Url = "ws://localhost:1", IsLocal = true });
                break;
            case "tunnel":
                h.Registry.AddOrUpdate(h.Registry.GetActive()! with
                {
                    SshTunnel = new SshTunnelConfig("fake", "host.example.test", 18789, 18790, false, 22),
                });
                break;
            case "disconnectedClient":
                h.RpcA.Connected = false;
                break;
            case "disconnectedSnapshot":
                h.State.Snapshot = h.State.Snapshot with { OperatorState = RoleConnectionState.Idle };
                break;
            default:
                h.State.Snapshot = h.State.Snapshot with { GatewayId = null };
                break;
        }

        await h.Coordinator.ObserveAsync();
        Assert.Equal(0, h.Provider.Reads);
        Assert.Empty(h.RpcA.Methods);
    }

    [Fact]
    public void Capture_RejectsSnapshotReplacementDuringClientRead()
    {
        using var h = new Harness();
        h.State.OnClientRead = () => h.State.Snapshot = h.State.Snapshot with { GatewayId = "B" };
        Assert.Null(h.Capture());
    }

    [Fact]
    public async Task SelectionChangesAfterPreparation_SaveRejectsBeforeAnyRpc()
    {
        using var h = new Harness();
        var preparation = await h.Service.PrepareAsync(CancellationToken.None);
        h.RpcA.Methods.Clear();
        h.Registry.SetActive("B");
        var result = await h.Service.SaveAsync(preparation, "Ada", Account.Photo, CancellationToken.None);
        Assert.Equal(new ProfileSaveResult(ProfileStep.ContextChanged, ProfileStep.ContextChanged), result);
        Assert.Empty(h.RpcA.Methods);
        Assert.Empty(h.RpcB.Methods);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SelectionChangesDuringProfileReads_StopBeforeAccountOrNextWrite(int readNumber)
    {
        using var h = new Harness();
        var reads = 0;
        h.RpcA.OnSelfRead = () => { if (++reads == readNumber) h.Registry.SetActive("B"); };
        var result = await h.Service.AutoFillAsync(CancellationToken.None);

        Assert.Equal(readNumber == 1 ? 0 : 1, h.Provider.Reads);
        Assert.Equal(readNumber == 3 ? 1 : 0, h.RpcA.Methods.Count(m => m == "users.setDisplayName"));
        Assert.DoesNotContain("users.setAvatar", h.RpcA.Methods);
        Assert.Empty(h.RpcB.Methods);
        if (readNumber == 1)
            Assert.Equal(ProfileOffer.Unavailable, result.Offer);
        else
            Assert.Equal(new ProfileSaveResult(
                readNumber == 3 ? ProfileStep.Saved : ProfileStep.ContextChanged, ProfileStep.ContextChanged), result.Save);
    }

    [Fact]
    public async Task EligibilityChangesDuringAccountRead_DoNotWrite()
    {
        using var h = new Harness();
        h.Provider.Read = _ =>
        {
            h.Registry.AddOrUpdate(new GatewayRecord { Id = "A", Url = "wss://remote.example.test" });
            return Task.FromResult(Account);
        };
        var result = await h.Service.AutoFillAsync(CancellationToken.None);
        Assert.Equal(ProfileOffer.Unavailable, result.Offer);
        Assert.Empty(h.RpcA.Payloads);
    }
}
