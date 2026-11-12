using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatToolDetailsProfileTests
{
    public class ProfileProxy : AgentCreationServiceTests.GatewayProxy
    {
        public long? Epoch = 1;
        public string Device = "device";
        public List<long> ReadEpochs = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "get_SessionMutationConnectionEpoch") return Epoch;
            if (method?.Name == "get_AuthenticatedSigningDeviceId") return Device;
            if (method?.Name == "SendWizardRequestOnConnectionAsync")
            {
                ReadEpochs.Add((long)args![2]!);
                return base.Invoke(typeof(IOperatorGatewayClient).GetMethod(nameof(IOperatorGatewayClient.SendWizardRequestAsync)),
                    [args[0], args[1], args[3]]);
            }
            return base.Invoke(method, args);
        }
    }

    private static JsonElement Profile(string id, string name = "Name") =>
        JsonSerializer.SerializeToElement(new { profile = new { id, displayName = name } });

    private static (IOperatorGatewayClient Client, ProfileProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, ProfileProxy>();
        return (client, (ProfileProxy)client);
    }

    [Fact]
    public async Task SameAccountNameChange_RestoresDetailsWithoutReconnect()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("same-person", "Before"));
        using var profile = new ChatToolDetailsProfile(client, () => { }, Assert.Fail);
        Assert.False(profile.IsAvailable);
        await profile.RefreshAsync();
        Assert.True(profile.IsAvailable);
        var refresh = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Response = () => refresh.Task;
        var pending = profile.RefreshAsync();
        Assert.False(profile.IsAvailable);
        refresh.SetResult(Profile("same-person", "After"));
        await pending;
        Assert.True(profile.IsAvailable);
        Assert.Equal([1L, 1L], proxy.ReadEpochs);
        Assert.All(proxy.Requests, request => Assert.Equal("users.self", request.Method));
    }

    [Fact]
    public async Task RevalidationRetiresCapturedReaderRevisionEvenForTheSameAccount()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("same-person"));
        using var profile = new ChatToolDetailsProfile(client, () => { }, Assert.Fail);
        Assert.False(default(ChatToolDetailsProfileState).IsAvailable);
        await profile.RefreshAsync();
        var captured = profile.Snapshot;
        Assert.True(captured.IsAvailable);
        var refresh = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Response = () => refresh.Task;
        var pending = profile.RefreshAsync();
        var verifying = profile.Snapshot;
        Assert.False(verifying.IsAvailable);
        Assert.NotEqual(captured.Revision, verifying.Revision);
        refresh.SetResult(Profile("same-person", "Renamed"));
        await pending;
        var current = profile.Snapshot;
        Assert.True(current.IsAvailable);
        Assert.Equal(verifying.Revision, current.Revision);
        Assert.NotEqual(captured, current);
    }

    [Fact]
    public async Task DifferentAccount_RemainsRetiredUntilNewConnection()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("first"));
        using var profile = new ChatToolDetailsProfile(client, () => { }, Assert.Fail);
        await profile.RefreshAsync();
        proxy.Response = () => Task.FromResult(Profile("second"));
        await profile.RefreshAsync();
        Assert.False(profile.IsAvailable);
        await profile.RefreshAsync();
        Assert.Equal(2, proxy.Requests.Count);
        proxy.Device = "replacement";
        await profile.RefreshAsync();
        Assert.False(profile.IsAvailable);
        Assert.Equal(2, proxy.Requests.Count);
        proxy.Epoch = 2;
        await profile.RefreshAsync();
        Assert.True(profile.IsAvailable);
        Assert.Equal([1L, 1L, 2L], proxy.ReadEpochs);
    }

    [Fact]
    public async Task LateSupersededReply_CannotRetireCurrentProfile()
    {
        var (client, proxy) = Gateway();
        var delayed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Response = () => delayed.Task;
        using var profile = new ChatToolDetailsProfile(client, () => { }, Assert.Fail);
        var old = profile.RefreshAsync();
        proxy.Epoch = 2;
        proxy.Response = () => Task.FromResult(Profile("current"));
        await profile.RefreshAsync();
        await old;
        delayed.SetResult(Profile("old"));
        Assert.True(profile.IsAvailable);
        await profile.RefreshAsync();
        Assert.True(profile.IsAvailable);
    }

    [Fact]
    public async Task FailureIsReportedAndNextInvalidationCanRecoverSameProfile()
    {
        var (client, proxy) = Gateway();
        var failures = new List<string>();
        proxy.Response = () => Task.FromResult(Profile("person"));
        using var profile = new ChatToolDetailsProfile(client, () => { }, failures.Add);
        await profile.RefreshAsync();
        proxy.Response = () => throw new TimeoutException("private server detail");
        await profile.RefreshAsync();
        Assert.False(profile.IsAvailable);
        Assert.Equal(["Timeout"], failures);
        proxy.Response = () => Task.FromResult(Profile("person"));
        await profile.RefreshAsync();
        Assert.True(profile.IsAvailable);
    }

    [Fact]
    public async Task Disposal_BlocksLateIdentity()
    {
        var (client, proxy) = Gateway();
        var delayed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Response = () => delayed.Task;
        var profile = new ChatToolDetailsProfile(client, () => { }, Assert.Fail);
        var read = profile.RefreshAsync();
        profile.Dispose();
        await read;
        delayed.SetResult(Profile("person"));
        Assert.False(profile.IsAvailable);
        await profile.RefreshAsync();
        Assert.Single(proxy.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportIdentityChange_DeniesAvailabilityBeforeRefresh(bool changeDevice)
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("person"));
        using var profile = new ChatToolDetailsProfile(client, () => { }, Assert.Fail);
        await profile.RefreshAsync();
        Assert.True(profile.IsAvailable);
        if (changeDevice) proxy.Device = "replacement";
        else proxy.Epoch = 2;
        Assert.False(profile.IsAvailable);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"profile":{"id":""}}""")]
    public async Task MalformedProfileFailsClosed(string json)
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(JsonSerializer.Deserialize<JsonElement>(json));
        var failures = new List<string>();
        using var profile = new ChatToolDetailsProfile(client, () => { }, failures.Add);
        await profile.RefreshAsync();
        Assert.False(profile.IsAvailable);
        Assert.Equal(["MalformedReply"], failures);
    }
}
