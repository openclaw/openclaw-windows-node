// Direct toast-decision entry -> production owner/client -> authenticated loopback WebSocket.
// This does not register Windows notifications or claim OS-delivered toast activation.
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using Xunit.Abstractions;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class OperatorApprovalTransportTests(ITestOutputHelper output)
{
    // Match the existing GatewayFixtureProtocolTests socket budget, not a new longer CI timeout.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ExpiringRequestLifetime = TimeSpan.FromSeconds(2);
    private const string ApprovalId = "fixture-wire-authority";
    private const string ControlId = "fixture-wire-control";
    private const string Command = "echo fixture";

    [Fact]
    public Task IssuedToastToken_WritesOneAcceptedResolveAndReplayWritesNothing() => RunAsync(async h =>
    {
        var pending = await h.PublishAsync(ApprovalId);
        var token = h.IssueToken(pending);
        Assert.Equal(ApprovalDecisionOutcome.Sent,
            await h.Activate(ApprovalId, "allow-once", token).WaitAsync(Deadline));
        await h.AssertOneResolveAsync(ApprovalId, "allow-once");
        Assert.False(h.Source.TryGetPendingApproval(ApprovalId, out _));

        Assert.Equal(ApprovalDecisionOutcome.NotPending,
            await h.Activate(ApprovalId, "allow-once", token).WaitAsync(Deadline));
        await h.ReadBarrierAsync();
        Assert.Single(h.ResolveFrames);
    });

    [Theory]
    [InlineData("forged", "NotPending")]
    [InlineData("wrong-id", "NotPending")]
    [InlineData("not-offered", "NotAllowed")]
    public Task InvalidToastAuthority_WritesNoFrameAndLeavesLiveControlUsable(string change, string expected) =>
        RunAsync(async h =>
        {
            var pending = await h.PublishAsync(ApprovalId);
            const string otherId = "fixture-wire-other";
            await h.PublishAsync(otherId);
            var token = h.IssueToken(pending);
            var result = await h.Activate(change == "wrong-id" ? otherId : ApprovalId,
                change == "not-offered" ? "allow-always" : "allow-once",
                change == "forged" ? "not-an-issued-token" : token).WaitAsync(Deadline);
            Assert.Equal(expected, result.ToString());
            if (change != "forged")
                Assert.Equal(ApprovalDecisionOutcome.NotPending,
                    await h.Activate(ApprovalId, "deny", token).WaitAsync(Deadline));

            await h.ReadBarrierAsync();
            Assert.Empty(h.ResolveFrames); // Includes rejected/pending frames, not just accepted effects.
            Assert.True(h.Source.TryGetPendingApproval(ApprovalId, out _));
            Assert.True(h.Source.TryGetPendingApproval(otherId, out _));
            await h.AcceptControlAsync(ApprovalId);
            Assert.True(h.Source.TryGetPendingApproval(otherId, out _));
        });

    [Fact]
    public Task ReadOnlyHandshake_CannotUseIssuedTokenOrDirectDecisionAndStillAnswersReads() =>
        RunAsync(async h =>
        {
            Assert.Equal(["operator.read"], h.Client.GrantedOperatorScopes);
            Assert.False(h.Source.CanDecideApprovals);
            // Existing browse fixture can deliver a canonical event without granting approval authority.
            // IssueToastToken is not the permission check: both decision entry points must revalidate it.
            var pending = await h.PublishAsync(ApprovalId);
            var token = h.IssueToken(pending);
            Assert.Equal(ApprovalDecisionOutcome.NotAllowed,
                await h.Activate(ApprovalId, "deny", token).WaitAsync(Deadline));
            Assert.Equal(ApprovalDecisionOutcome.NotAllowed,
                await h.Decisions.DecideAsync(ApprovalId, "deny", pending).WaitAsync(Deadline));
            await h.ReadBarrierAsync();
            Assert.Empty(h.ResolveFrames);
            Assert.True(h.Source.TryGetPendingApproval(ApprovalId, out _));
        }, approvalGrant: false);

    [Theory]
    [InlineData("token-revoked")]
    [InlineData("request-replaced")]
    [InlineData("resolved")]
    [InlineData("expired")]
    public Task QueuedToast_RevalidatesActualOwnerStateBeforeTheProductionWrite(string change) =>
        RunAsync(async h =>
        {
            var pending = await h.PublishAsync(ApprovalId,
                lifetime: change == "expired" ? ExpiringRequestLifetime : null);
            var token = h.IssueToken(pending);
            await h.HoldSendQueueAsync();
            Task<ApprovalDecisionOutcome> sending;
            try
            {
                sending = h.Activate(ApprovalId, "allow-once", token);
                Assert.False(sending.IsCompleted);
                Assert.True(pending.Deadline > DateTimeOffset.UtcNow);
                Assert.Empty(h.ResolveFrames);
                switch (change)
                {
                    case "token-revoked":
                        h.Decisions.RevokeToastTokens(ApprovalId);
                        break;
                    case "request-replaced":
                        var replacement = await h.PublishAsync(ApprovalId, "echo changed");
                        Assert.NotSame(pending, replacement);
                        Assert.Equal(pending.Deadline, replacement.Deadline);
                        break;
                    case "resolved":
                        await h.ObserveAsync(() => !h.Source.TryGetPendingApproval(ApprovalId, out _),
                            () => h.Server.PublishExecApprovalResolvedAsync(GatewayScenario.MainSessionKey, ApprovalId, "deny"));
                        break;
                    case "expired":
                        // The real source timer and wall clock retire this wire lifetime. No fake clock or Tick call.
                        await h.ObserveAsync(() => !h.Source.TryGetPendingApproval(ApprovalId, out _),
                            () => Task.CompletedTask);
                        Assert.True(DateTimeOffset.UtcNow >= pending.Deadline);
                        break;
                }
                Assert.False(sending.IsCompleted); // Still behind the actual transport semaphore.
                Assert.Empty(h.ResolveFrames);
            }
            finally { h.ReleaseSendQueue(); }

            Assert.Equal(ApprovalDecisionOutcome.NotPending, await sending.WaitAsync(Deadline));
            await h.ReadBarrierAsync();
            Assert.Empty(h.ResolveFrames);
            await h.PublishAsync(ControlId);
            await h.AcceptControlAsync(ControlId);
        });

    [Fact]
    public Task QueuedToast_ClientReplacementCannotWriteOnEitherAuthenticatedConnection() => RunAsync(async h =>
    {
        var pending = await h.PublishAsync(ApprovalId);
        var token = h.IssueToken(pending);
        var originalClient = h.Client;
        await h.HoldSendQueueAsync();
        Task<ApprovalDecisionOutcome> sending;
        try
        {
            sending = h.Activate(ApprovalId, "allow-once", token);
            Assert.False(sending.IsCompleted);
            var replacement = await h.ConnectClientAsync();
            Assert.NotSame(originalClient, replacement);
            h.Attach(replacement);
            await h.PublishAsync(ApprovalId);
            Assert.Same(replacement, h.Source.Client);
            Assert.False(sending.IsCompleted);
            Assert.Empty(h.ResolveFrames);
        }
        finally { h.ReleaseSendQueue(); }

        Assert.Equal(ApprovalDecisionOutcome.NotPending, await sending.WaitAsync(Deadline));
        await h.ReadBarrierAsync(originalClient);
        await h.ReadBarrierAsync();
        Assert.Empty(h.ResolveFrames); // Server ledger includes both connections.
        await h.AcceptControlAsync(ApprovalId);
    });

    [Fact]
    public Task QueuedToast_EpochReplacementCannotCarryItsGrantIntoTheNewHandshake() => RunAsync(async h =>
    {
        var pending = await h.PublishAsync(ApprovalId);
        var token = h.IssueToken(pending);
        var oldEpoch = h.Client.SessionMutationConnectionEpoch;
        var handshake = Signal();
        void OnHandshake(object? sender, EventArgs args) => handshake.TrySetResult(true);
        h.Client.HandshakeSucceeded += OnHandshake;
        try
        {
            await h.HoldSendQueueAsync();
            Task<ApprovalDecisionOutcome> sending;
            try
            {
                sending = h.Activate(ApprovalId, "allow-once", token);
                Assert.False(sending.IsCompleted);
                await h.ObserveAsync(() => !h.Source.TryGetPendingApproval(ApprovalId, out _),
                    () => h.Server.CloseConnectionsAsync());
                // Connect publishes a new socket generation; its hello frame also waits for this send lock.
                await h.Track(h.Client.ConnectAsync()).WaitAsync(Deadline);
                Assert.Null(h.Client.SessionMutationConnectionEpoch);
                Assert.False(sending.IsCompleted);
                Assert.Empty(h.ResolveFrames);
            }
            finally { h.ReleaseSendQueue(); }

            Assert.Equal(ApprovalDecisionOutcome.Failed, await sending.WaitAsync(Deadline));
            await handshake.Task.WaitAsync(Deadline);
            Assert.NotNull(h.Client.SessionMutationConnectionEpoch);
            Assert.NotEqual(oldEpoch, h.Client.SessionMutationConnectionEpoch);
            await h.ObserveAsync(() => h.Source.CanDecideApprovals, () => Task.CompletedTask);
            await h.ReadBarrierAsync();
            Assert.Empty(h.ResolveFrames);
            await h.PublishAsync(ApprovalId);
            Assert.Equal(ApprovalDecisionOutcome.NotPending,
                await h.Activate(ApprovalId, "deny", token).WaitAsync(Deadline));
            await h.ReadBarrierAsync();
            Assert.Empty(h.ResolveFrames);
            await h.AcceptControlAsync(ApprovalId);
        }
        finally { h.Client.HandshakeSucceeded -= OnHandshake; }
    });

    [Fact]
    [Trait("Category", "FixtureFailurePolicy")]
    public async Task CleanupPolicy_BodyCleanupAndDiagnosticFailuresPreserveOriginalIdentityAndStack()
    {
        var primary = new InvalidOperationException("Body failure sentinel.");
        var stages = new List<string>();
        string? originalStack = null;
        Task Body()
        {
            stages.Add("body");
            try { throw primary; }
            catch
            {
                originalStack = primary.StackTrace;
                throw;
            }
        }
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => RunWithCleanupAsync(
            Body,
            () =>
            {
                stages.Add("cleanup");
                throw new IOException("Cleanup failure sentinel.");
            },
            _ =>
            {
                stages.Add("diagnostic");
                throw new ApplicationException("Diagnostic failure sentinel.");
            }));

        Assert.Same(primary, observed);
        Assert.NotNull(originalStack);
        Assert.NotNull(observed.StackTrace);
        Assert.StartsWith(originalStack, observed.StackTrace);
        Assert.Equal(["body", "cleanup", "diagnostic"], stages);
    }

    [Fact]
    [Trait("Category", "FixtureFailurePolicy")]
    public async Task CleanupPolicy_CleanupOnlyFailureRemainsTheFailure()
    {
        var cleanup = new IOException("Cleanup-only failure sentinel.");
        var stages = new List<string>();
        var observed = await Assert.ThrowsAsync<IOException>(() => RunWithCleanupAsync(
            () =>
            {
                stages.Add("body");
                return Task.CompletedTask;
            },
            () =>
            {
                stages.Add("cleanup");
                throw cleanup;
            },
            _ => stages.Add("diagnostic")));

        Assert.Same(cleanup, observed);
        Assert.Equal(["body", "cleanup"], stages);
    }

    private Task RunAsync(Func<WireHarness, Task> test, bool approvalGrant = true)
    {
        var harness = new WireHarness();
        return RunWithCleanupAsync(async () =>
        {
            await harness.StartAsync(approvalGrant);
            await test(harness);
        }, harness.DisposeAsync, message => output.WriteLine(message));
    }

    // Local harness policy only: diagnostics are never allowed to choose the test's failure.
    private static async Task RunWithCleanupAsync(Func<Task> body, Func<ValueTask> cleanup, Action<string> writeDiagnostic)
    {
        ExceptionDispatchInfo? primary = null;
        try { await body(); }
        catch (Exception error) { primary = ExceptionDispatchInfo.Capture(error); }
        try { await cleanup(); }
        catch (Exception error)
        {
            if (primary is null) primary = ExceptionDispatchInfo.Capture(error);
            else
            {
                try { writeDiagnostic($"Owned wire fixture cleanup also failed: {error.GetType().Name}"); }
                // Secondary diagnostic delivery cannot replace the captured body/start failure.
                catch (Exception) { }
            }
        }
        primary?.Throw();
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class WireHarness : IAsyncDisposable
    {
        private readonly SerializedDispatcher _dispatcher = new();
        private readonly List<(OpenClawGatewayClient Client, TempDirectory Identity)> _clients = [];
        private readonly List<Task> _operations = [];
        private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        private FixtureGatewayServer? _server;
        private SemaphoreSlim? _heldSendLock;
        public FixtureGatewayServer Server => _server!;
        public OpenClawGatewayClient Client { get; private set; } = null!;
        public InboxGatewaySource Source { get; }
        public OperatorApprovalDecisions Decisions { get; }
        public IEnumerable<GatewayFixtureRequest> ResolveFrames => Server.Requests.Where(request =>
            request.Method is "exec.approval.resolve" or "plugin.approval.resolve" or "approval.resolve");

        public WireHarness()
        {
            Source = new InboxGatewaySource(_dispatcher);
            Decisions = new OperatorApprovalDecisions(Source);
        }

        public async Task StartAsync(bool approvalGrant)
        {
            _server = await FixtureGatewayServer.StartAsync(approvalGrant
                ? GatewayScenario.CreateApprovalReview() : GatewayScenario.CreateBrowse(), _token);
            Attach(await ConnectClientAsync());
            Assert.Equal(approvalGrant ? ["operator.read", "operator.approvals"] : ["operator.read"],
                Client.GrantedOperatorScopes);
            await ReadBarrierAsync();
        }

        public async Task<OpenClawGatewayClient> ConnectClientAsync()
        {
            var identity = new TempDirectory();
            OpenClawGatewayClient client;
            try
            {
                client = new OpenClawGatewayClient(Server.Endpoint.AbsoluteUri, _token, NullLogger.Instance,
                    identityPath: identity.Path, ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
            }
            catch
            {
                identity.Dispose();
                throw;
            }
            _clients.Add((client, identity));
            var handshake = Signal();
            var sessions = Signal();
            void OnHandshake(object? sender, EventArgs args) => handshake.TrySetResult(true);
            void OnSessions(object? sender, SessionInfo[] data)
            {
                if (data.Length == 5) sessions.TrySetResult(true);
            }
            client.HandshakeSucceeded += OnHandshake;
            client.SessionsUpdated += OnSessions;
            try
            {
                await Track(client.ConnectAsync()).WaitAsync(Deadline);
                await Task.WhenAll(handshake.Task, sessions.Task).WaitAsync(Deadline);
                Assert.NotNull(client.SessionMutationConnectionEpoch);
                return client;
            }
            finally
            {
                client.HandshakeSucceeded -= OnHandshake;
                client.SessionsUpdated -= OnSessions;
            }
        }

        public void Attach(OpenClawGatewayClient client)
        {
            Client = client;
            _dispatcher.Run(() => Source.AttachClient(client));
        }

        public async Task<InboxGatewaySource.PendingApproval> PublishAsync(string id, string command = Command, TimeSpan? lifetime = null)
        {
            await ObserveAsync(() => Source.TryGetPendingApproval(id, out var pending) && pending.Approval.Command == command,
                () => Server.PublishExecApprovalRequestedAsync(GatewayScenario.MainSessionKey, id, command, lifetime));
            Assert.True(Source.TryGetPendingApproval(id, out var observed));
            Assert.Equal(GatewayApprovalKind.Exec, observed.Approval.Kind);
            Assert.Equal(Client.SessionMutationConnectionEpoch, observed.ConnectionEpoch);
            return observed;
        }

        public string IssueToken(InboxGatewaySource.PendingApproval pending)
        {
            // The fixture endpoint is direct loopback and the canonical event names host=gateway.
            Assert.True(Server.Endpoint.IsLoopback);
            Assert.Equal("gateway", pending.Approval.Host);
            var offered = ApprovalToastPlan.Decisions(pending.Approval, hostConfirmedLocal: true);
            Assert.Equal(["allow-once", "deny"], offered);
            var token = Decisions.IssueToastToken(pending.Approval, offered);
            Assert.NotNull(token);
            return token;
        }

        public Task<ApprovalDecisionOutcome> Activate(string id, string decision, string token)
        {
            var task = Decisions.DecideFromToastAsync(id, decision, token);
            _operations.Add(task);
            return task;
        }

        public Task Track(Task task)
        {
            _operations.Add(task);
            return task;
        }

        public async Task ObserveAsync(Func<bool> ready, Func<Task> trigger)
        {
            var changed = Signal();
            void Check(object? sender, EventArgs args)
            {
                if (ready()) changed.TrySetResult(true);
            }
            _dispatcher.Run(() => Source.Changed += Check);
            try
            {
                await Track(trigger()).WaitAsync(Deadline);
                _dispatcher.Run(() => Check(null, EventArgs.Empty));
                await changed.Task.WaitAsync(Deadline);
            }
            finally { _dispatcher.Run(() => Source.Changed -= Check); }
        }

        public async Task HoldSendQueueAsync()
        {
            Assert.Null(_heldSendLock);
            // Same existing test-only seam as GatewayFixtureProtocolTests.RealClient_QueuedApprovalRechecksAdmissionBeforeAnyResolveFrame.
            var sendLock = Assert.IsType<SemaphoreSlim>(typeof(WebSocketClientBase)
                .GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client));
            using var deadline = new CancellationTokenSource(Deadline);
            await sendLock.WaitAsync(deadline.Token);
            _heldSendLock = sendLock;
        }

        public void ReleaseSendQueue() => Interlocked.Exchange(ref _heldSendLock, null)?.Release();

        public async Task ReadBarrierAsync(OpenClawGatewayClient? client = null)
        {
            var occurrence = Server.Requests.Count(request => request.Method == "health") + 1;
            var reading = (client ?? Client).SendWizardRequestAsync("health", new { }, (int)Deadline.TotalMilliseconds);
            _operations.Add(reading);
            var health = await reading.WaitAsync(Deadline);
            Assert.True(health.GetProperty("ok").GetBoolean());
            var receipt = await Server.WaitForCompletedRequestAsync("health", occurrence: occurrence).WaitAsync(Deadline);
            Assert.Equal("ok", receipt.Outcome);
            Assert.Empty(Server.UnexpectedRequests);
        }

        public async Task AcceptControlAsync(string id)
        {
            Assert.True(Source.TryGetPendingApproval(id, out var pending));
            Assert.Equal(ApprovalDecisionOutcome.Sent,
                await Activate(id, "deny", IssueToken(pending)).WaitAsync(Deadline));
            await AssertOneResolveAsync(id, "deny");
        }

        public async Task AssertOneResolveAsync(string id, string decision)
        {
            await Server.WaitForCompletedRequestAsync("exec.approval.resolve").WaitAsync(Deadline);
            await ReadBarrierAsync();
            var sent = Assert.Single(ResolveFrames);
            Assert.Equal(("exec.approval.resolve", id, decision, "ok"),
                (sent.Method, sent.ApprovalId, sent.Decision, sent.Outcome));
        }

        public async ValueTask DisposeAsync()
        {
            // Cancel only these clients before releasing a failed test's gate; join every started operation.
            var failures = new List<Exception>();
            void Clean(Action action)
            {
                try { action(); }
                catch (Exception error) { failures.Add(error); }
            }
            Clean(() => _dispatcher.Run(Source.Dispose));
            foreach (var (client, _) in _clients) Clean(client.Dispose);
            Clean(ReleaseSendQueue);
            try { await Task.WhenAll(_operations).WaitAsync(Deadline); }
            catch (Exception error) { failures.Add(error); }
            try
            {
                if (_server is not null) await _server.DisposeAsync().AsTask().WaitAsync(Deadline);
            }
            catch (Exception error) { failures.Add(error); }
            foreach (var (_, identity) in _clients) Clean(identity.Dispose);
            if (failures.Count > 0) throw new AggregateException("Owned approval wire fixture cleanup failed.", failures);
        }
    }

    // Source dictionaries have one UI owner in production. Serialize real socket/timer callbacks
    // through that existing adapter boundary here; it supplies no grant, clock or policy outcome.
    private sealed class SerializedDispatcher : IUiDispatcher
    {
        private readonly object _gate = new();
        public bool HasThreadAccess => Monitor.IsEntered(_gate);
        public void Run(Action action) { lock (_gate) action(); }
        public bool TryEnqueue(Action action) { Run(action); return true; }
    }
}
