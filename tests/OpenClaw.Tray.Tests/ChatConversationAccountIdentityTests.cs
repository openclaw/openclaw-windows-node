using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatConversationAccountIdentityTests
{
    public class ClientProxy : ChatConversationPanelStateTests.GatewayProxy
    {
        public IReadOnlyList<string> Scopes = ["operator.read"];
        public int Calls;
        public Func<Task<JsonElement>> Response = () => Task.FromResult(Profile("profile-a"));

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "get_GrantedOperatorScopes") return Scopes;
            if (method?.Name == "SendWizardRequestAsync")
            {
                Assert.Equal("users.self", args![0]);
                Assert.Equal("{}", JsonSerializer.Serialize(args[1]));
                Calls++;
                return Response();
            }
            return base.Invoke(method, args);
        }
    }

    private static JsonElement Profile(string id) => JsonSerializer.SerializeToElement(new
    {
        profile = new { id, displayName = "Not the identity", emails = new[] { "not-the-identity@example.test" } }
    });

    private static (IOperatorGatewayClient Client, ClientProxy Proxy) Client()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        return (client, (ClientProxy)client);
    }

    [Fact]
    public async Task CanonicalProfileIsTheOnlyKnownUserIdentityAndReadIsConnectionScoped()
    {
        var (client, proxy) = Client();
        proxy.Response = () => Task.FromResult(Profile("gateway-owner"));
        using var identity = new ChatConversationAccountIdentity(client, () => true, () => true);
        await identity.RefreshAsync();
        Assert.Equal(ChatConversationAccountStatus.Known, identity.Status);
        Assert.Equal("gateway-owner", identity.Current!.UserId);
        Assert.Equal(12, identity.Current.ConnectionEpoch);
        await identity.RefreshAsync();
        Assert.Equal(1, proxy.Calls);
    }

    [Fact]
    public async Task UnknownAccountNeverPublishesAnIdentityBeforeResponse()
    {
        var (client, proxy) = Client();
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Response = () => response.Task;
        using var identity = new ChatConversationAccountIdentity(client, () => true, () => true);
        var pending = identity.RefreshAsync();
        Assert.False(pending.IsCompleted);
        Assert.Equal(ChatConversationAccountStatus.Loading, identity.Status);
        Assert.Null(identity.Current);
        response.SetResult(Profile("profile-a"));
        await pending;
        Assert.Equal("profile-a", identity.Current!.UserId);
    }

    [Theory]
    [InlineData("advertisement")]
    [InlineData("scope")]
    public async Task MissingReadAuthorityOrAdvertisementFailsClosedWithoutRequest(string missing)
    {
        var (client, proxy) = Client();
        if (missing == "scope") proxy.Scopes = ["operator.pairing"];
        using var identity = new ChatConversationAccountIdentity(client, () => true, () => missing != "advertisement");
        await identity.RefreshAsync();
        Assert.Equal(ChatConversationAccountStatus.Unavailable, identity.Status);
        Assert.Null(identity.Current);
        Assert.Equal(0, proxy.Calls);
    }

    [Theory]
    [InlineData("""{"profile":null}""", true)]
    [InlineData("""{"profile":{"displayName":"not an ID"}}""", false)]
    [InlineData("""{}""", false)]
    public async Task ExplicitProfilelessDiffersFromUnknownAndMalformed(string json, bool profileless)
    {
        var (client, proxy) = Client();
        proxy.Response = () => Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
        using var identity = new ChatConversationAccountIdentity(client, () => true, () => true);
        await identity.RefreshAsync();
        Assert.Equal(profileless ? ChatConversationAccountStatus.Profileless : ChatConversationAccountStatus.Failed, identity.Status);
        Assert.Null(identity.Current);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("profile-revision")]
    public async Task LateReplyCannotPublishAcrossEpochOrSameEpochIdentityChange(string change)
    {
        var (client, proxy) = Client();
        var oldResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Response = () => oldResponse.Task;
        using var identity = new ChatConversationAccountIdentity(client, () => true, () => true);
        var oldRead = identity.RefreshAsync();
        if (change == "epoch") proxy.Epoch++;
        proxy.Response = () => newResponse.Task;
        var newRead = identity.RefreshAsync(invalidate: change == "profile-revision");
        oldResponse.SetResult(Profile("wrong-old-account"));
        await oldRead;
        Assert.Null(identity.Current);
        Assert.Equal(ChatConversationAccountStatus.Loading, identity.Status);
        newResponse.SetResult(Profile("profile-b"));
        await newRead;
        Assert.Equal("profile-b", identity.Current!.UserId);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("disconnect")]
    [InlineData("dispose")]
    public async Task RemovedAuthorityDropsAnAdmittedRead(string change)
    {
        var (client, proxy) = Client();
        var current = true;
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Response = () => response.Task;
        using var identity = new ChatConversationAccountIdentity(client, () => current, () => true);
        var pending = identity.RefreshAsync();
        if (change == "client") current = false;
        if (change == "disconnect") proxy.Connected = false;
        if (change == "dispose") identity.Dispose();
        response.SetResult(Profile("wrong-old-account"));
        await pending;
        Assert.Null(identity.Current);
        Assert.Equal(ChatConversationAccountStatus.Waiting, identity.Status);
    }

    [Fact]
    public async Task KnownAccountChangeRequiresFreshConnectionInsteadOfAdoptingOldProviderData()
    {
        var (client, proxy) = Client();
        using var identity = new ChatConversationAccountIdentity(client, () => true, () => true);
        await identity.RefreshAsync();
        Assert.Equal("profile-a", identity.Current!.UserId);
        proxy.Response = () => Task.FromResult(Profile("profile-b"));
        await identity.RefreshAsync(invalidate: true);
        Assert.Null(identity.Current);
        Assert.Equal(ChatConversationAccountStatus.RequiresReconnect, identity.Status);
        proxy.Response = () => Task.FromResult(Profile("profile-a"));
        await identity.RefreshAsync(invalidate: true);
        Assert.Null(identity.Current);
        proxy.Epoch++;
        await identity.RefreshAsync();
        Assert.Equal("profile-a", identity.Current!.UserId);
        Assert.Equal(13, identity.Current.ConnectionEpoch);
        Assert.Equal(3, proxy.Calls);
    }
}
