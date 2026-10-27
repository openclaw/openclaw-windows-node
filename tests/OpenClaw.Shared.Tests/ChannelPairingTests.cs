using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class ChannelPairingTests
{
    internal const string PendingJson = """
        {
          "accounts":[{"channel":"telegram","channelLabel":"Telegram","accountId":"work","accountLabel":"Work bot","notifySupported":true}],
          "requests":[{"requestId":"req-1","channel":"telegram","channelLabel":"Telegram","accountId":"work","accountLabel":"Work bot",
            "senderId":"sender-7","senderLabel":"Synthetic sender","createdAt":"2026-10-23T12:00:00Z",
            "lastSeenAt":"2026-10-23T12:01:00Z","expiresAt":"2026-10-23T13:00:00Z","notifySupported":true}],
          "commandOwnerConfigured":false,"limits":{"pendingPerAccount":3,"ttlMs":3600000}
        }
        """;

    internal static ChannelPairingSnapshot Snapshot() =>
        GatewayChannelPairingClient.ParseSnapshot(JsonSerializer.Deserialize<JsonElement>(PendingJson));

    [Fact]
    public void Snapshot_PreservesGatewayAccountAndSenderIdentity_WithoutCodes()
    {
        var snapshot = Snapshot();
        var row = Assert.Single(snapshot.Requests);
        Assert.Equal("work", Assert.Single(snapshot.Accounts).AccountId);
        Assert.Equal("sender-7", row.SenderId);
        Assert.Equal("req-1", row.RequestId);
        Assert.Equal("2026-10-23T13:00:00Z", row.ExpiresAt);
        Assert.DoesNotContain("code", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"accounts":[],"requests":null}""")]
    public void MalformedList_IsNotAnEmptySuccess(string json) =>
        Assert.Throws<InvalidDataException>(() =>
            GatewayChannelPairingClient.ParseSnapshot(JsonSerializer.Deserialize<JsonElement>(json)));

    [Fact]
    public void RequestForUnknownAccount_IsRejected()
    {
        var node = JsonNode.Parse(PendingJson)!;
        node["requests"]![0]!["accountId"] = "other";
        Assert.Throws<InvalidDataException>(() =>
            GatewayChannelPairingClient.ParseSnapshot(JsonSerializer.SerializeToElement(node)));
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("requests")]
    public void DuplicateAccountOrRequestIdentity_IsRejected(string collection)
    {
        var node = JsonNode.Parse(PendingJson)!;
        node[collection]!.AsArray().Add(node[collection]![0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() =>
            GatewayChannelPairingClient.ParseSnapshot(JsonSerializer.SerializeToElement(node)));
    }

    [Theory]
    [InlineData("notifySupported")]
    [InlineData("lastSeenAt")]
    [InlineData("senderId")]
    public void IncompleteRequest_IsRejected(string property)
    {
        var node = JsonNode.Parse(PendingJson)!;
        node["requests"]![0]!.AsObject().Remove(property);
        Assert.Throws<InvalidDataException>(() =>
            GatewayChannelPairingClient.ParseSnapshot(JsonSerializer.SerializeToElement(node)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resolve_OnlyRemovesTheAcknowledgedObservedRow_ThenRequiresReconciliation(bool approve)
    {
        var gateway = new FakeGateway();
        var session = new ChannelPairingSession(gateway, () => true);
        await session.RefreshAsync();
        var row = Assert.Single(session.Snapshot!.Requests);
        await session.ResolveAsync(row, approve);
        Assert.Equal((row, approve, 1L), Assert.Single(gateway.Mutations));
        Assert.Empty(session.Snapshot.Requests);
        Assert.True(session.RequiresRefresh);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ResolveAsync(row, approve));
        Assert.Single(gateway.Mutations);
    }

    [Fact]
    public async Task FabricatedOrReplacedRequest_CannotInheritDialogConsent()
    {
        var gateway = new FakeGateway();
        var session = new ChannelPairingSession(gateway, () => true);
        await session.RefreshAsync();
        var row = Assert.Single(session.Snapshot!.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ResolveAsync(row with { AccountId = "personal" }, true));
        await session.RefreshAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ResolveAsync(row, true));
        Assert.Empty(gateway.Mutations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconnectOrClientReplacement_BlocksMutationBeforeDispatch(bool reconnect)
    {
        var current = true;
        var gateway = new FakeGateway();
        var session = new ChannelPairingSession(gateway, () => current);
        await session.RefreshAsync();
        var row = Assert.Single(session.Snapshot!.Requests);
        if (reconnect) gateway.ConnectionEpoch++;
        else current = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ResolveAsync(row, true));
        Assert.Empty(gateway.Mutations);
    }

    [Fact]
    public async Task LateMutationAck_CannotRemoveOrConfirmReplacementContext()
    {
        var current = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway { Resolution = completion.Task };
        var session = new ChannelPairingSession(gateway, () => current);
        await session.RefreshAsync();
        var row = Assert.Single(session.Snapshot!.Requests);
        var pending = session.ResolveAsync(row, true);
        Assert.True(session.Busy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RefreshAsync());
        current = false;
        completion.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Same(row, Assert.Single(session.Snapshot.Requests));
        Assert.True(session.RequiresRefresh);
    }

    [Fact]
    public async Task LateList_CannotPopulateReplacementContext()
    {
        var current = true;
        var completion = new TaskCompletionSource<ChannelPairingSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway { Listing = completion.Task };
        var session = new ChannelPairingSession(gateway, () => current);
        var pending = session.RefreshAsync();
        current = false;
        completion.SetResult(Snapshot());
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Null(session.Snapshot);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("denied")]
    [InlineData("malformed")]
    [InlineData("cancelled")]
    public async Task UnconfirmedMutation_RetainsPendingStateAndNeverRetries(string outcome)
    {
        Exception failure = outcome switch
        {
            "timeout" => new TimeoutException(),
            "denied" => new UnauthorizedAccessException(),
            "malformed" => new InvalidDataException(),
            _ => new OperationCanceledException()
        };
        var gateway = new FakeGateway { Resolution = Task.FromException(failure) };
        var session = new ChannelPairingSession(gateway, () => true);
        await session.RefreshAsync();
        var row = Assert.Single(session.Snapshot!.Requests);
        var error = await Record.ExceptionAsync(() => session.ResolveAsync(row, true));
        Assert.Same(failure, error);
        Assert.Same(row, Assert.Single(session.Snapshot.Requests));
        Assert.True(session.RequiresRefresh);
        Assert.False(session.Busy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ResolveAsync(row, false));
        Assert.Single(gateway.Mutations);
        await session.RefreshAsync();
        Assert.False(session.RequiresRefresh);
        Assert.True(session.CanResolve(Assert.Single(session.Snapshot.Requests)));
    }

    private sealed class FakeGateway : IChannelPairingGateway
    {
        public long? ConnectionEpoch { get; set; } = 1;
        public Task Resolution { get; init; } = Task.CompletedTask;
        public Task<ChannelPairingSnapshot>? Listing { get; init; }
        public List<(ChannelPairingRequest, bool, long)> Mutations { get; } = [];
        public Task<ChannelPairingSnapshot> ListAsync(long epoch) => Listing ?? Task.FromResult(Snapshot());
        public Task ResolveAsync(ChannelPairingRequest request, bool approve, long epoch)
        {
            Mutations.Add((request, approve, epoch));
            return Resolution;
        }
    }
}
