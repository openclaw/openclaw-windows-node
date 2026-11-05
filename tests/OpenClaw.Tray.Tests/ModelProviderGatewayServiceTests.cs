using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class ModelProviderGatewayServiceTests
{
    private const string KeyStep = """{"done":false,"status":"running","step":{"id":"key","type":"text","sensitive":true}}""";
    private static readonly JsonElement Secret = JsonSerializer.SerializeToElement("secret");

    public class GatewayProxy : DispatchProxy
    {
        public long Epoch = 7;
        public bool Connected = true;
        public List<string> Calls = [];
        public Func<Task<JsonElement>> Reply = () => Task.FromResult(Parse(KeyStep));

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_IsConnectedToGateway":
                    return Connected;
                case "get_HasHandshakeSnapshot":
                    return true;
                case "get_GrantedOperatorScopes":
                    return (IReadOnlyList<string>)["operator.admin"];
                case "get_SessionMutationConnectionEpoch":
                    return (long?)Epoch;
                case "SendWizardRequestOnConnectionAsync":
                    Calls.Add($"{args![0]}@{args[2]}:{JsonSerializer.Serialize(args[1])}");
                    return Reply();
                case "SendWizardRequestAsync":
                    Calls.Add("UNBOUND:" + args![0]);
                    return Reply();
                case "RequestModelsListAsync":
                case "RequestConfigAsync":
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }

    private sealed class Fixture
    {
        public readonly IOperatorGatewayClient Client;
        public readonly GatewayProxy Proxy;
        public readonly (IOperatorGatewayClient Client, GatewayProxy Proxy) Replacement = Gateway();
        public IOperatorGatewayClient? Current;
        public string? Active = "gw-a";
        public string? Connected = "gw-a";

        public Fixture()
        {
            (Client, Proxy) = Gateway();
            Current = Client;
        }

        public ModelProviderGatewayService Service() => new(() => Current, () => Active, () => Connected);
    }

    private static (IOperatorGatewayClient Client, GatewayProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        return (client, (GatewayProxy)client);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void Change(Fixture fixture, string change)
    {
        switch (change)
        {
            case "client": fixture.Current = fixture.Replacement.Client; break;
            case "reconnect": fixture.Proxy.Epoch++; break;
            case "disconnect": fixture.Proxy.Connected = false; break;
            case "registry": fixture.Active = "gw-b"; break;
            case "connection": fixture.Connected = "gw-b"; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    private static void Undo(Fixture fixture)
    {
        fixture.Current = fixture.Client;
        fixture.Proxy.Epoch = 7;
        fixture.Proxy.Connected = true;
        fixture.Active = "gw-a";
        fixture.Connected = "gw-a";
    }

    [Fact]
    public async Task SignIn_SendsEveryRequestOnTheStartingConnection()
    {
        var fixture = new Fixture();
        IModelProviderLogin login = fixture.Service().BeginLogin();

        await login.StartAsync("example/browser");
        await login.NextAsync("key", Secret);
        fixture.Proxy.Reply = () => Task.FromResult(Parse("""{"status":"cancelled"}"""));
        Assert.Equal("cancelled", await login.CancelAsync());

        Assert.Collection(fixture.Proxy.Calls,
            call => Assert.StartsWith("models.authLogin@7:", call),
            call => Assert.StartsWith("wizard.next@7:", call),
            call => Assert.StartsWith("wizard.cancel@7:", call));
        string[] sessionIds = fixture.Proxy.Calls
            .Select(call => JsonDocument.Parse(call[(call.IndexOf(':') + 1)..]).RootElement.GetProperty("sessionId").GetString()!)
            .ToArray();
        Assert.Single(sessionIds.Distinct());
    }

    [Theory]
    [InlineData("client")]
    [InlineData("reconnect")]
    [InlineData("disconnect")]
    [InlineData("registry")]
    [InlineData("connection")]
    public async Task ConnectionChangeBeforeAnswer_RetiresAndSendsNothing(string change)
    {
        var fixture = new Fixture();
        IModelProviderLogin login = fixture.Service().BeginLogin();
        await login.StartAsync("example/browser");

        Change(fixture, change);

        await Assert.ThrowsAsync<ModelProviderConnectionChangedException>(() => login.NextAsync("key", Secret));
        Assert.Single(fixture.Proxy.Calls);
        Assert.Empty(fixture.Replacement.Proxy.Calls);
        Assert.DoesNotContain(fixture.Proxy.Calls, call => call.Contains("secret", StringComparison.Ordinal));

        // Returning to the original binding never revives a retired login.
        Undo(fixture);
        Assert.False(login.IsCurrent);
        await Assert.ThrowsAsync<ModelProviderConnectionChangedException>(() => login.NextAsync("key", Secret));
        await Assert.ThrowsAsync<ModelProviderConnectionChangedException>(login.CancelAsync);
        Assert.Single(fixture.Proxy.Calls);
        Assert.Empty(fixture.Replacement.Proxy.Calls);
    }

    [Fact]
    public async Task ChangeWhileStepPending_RetiresBeforeTheNextAnswer()
    {
        var fixture = new Fixture();
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.Reply = () => reply.Task;
        IModelProviderLogin login = fixture.Service().BeginLogin();
        Task start = login.StartAsync("example/browser");

        fixture.Current = fixture.Replacement.Client;
        reply.SetResult(Parse(KeyStep));
        await start;

        await Assert.ThrowsAsync<ModelProviderConnectionChangedException>(() => login.NextAsync("key", Secret));
        Assert.Single(fixture.Proxy.Calls);
        Assert.Empty(fixture.Replacement.Proxy.Calls);
    }

    [Fact]
    public void BeginLogin_RefusesWhenConnectedGatewayIsNotActive()
    {
        var fixture = new Fixture { Connected = "gw-b" };

        Assert.Throws<ModelProviderConnectionChangedException>(() => fixture.Service().BeginLogin());
        Assert.Empty(fixture.Proxy.Calls);
    }
}
