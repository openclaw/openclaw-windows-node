using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class AgentCreationServiceTests
{
    public class GatewayProxy : DispatchProxy
    {
        public bool Connected = true;
        public IReadOnlyList<string> Scopes = ["operator.admin"];
        public List<(string Method, JsonElement Payload)> Requests = [];
        public Func<Task<JsonElement>> Response = () => Task.FromResult(
            JsonSerializer.SerializeToElement(new { ok = true, agentId = "research" }));

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_IsConnectedToGateway" => Connected,
            "get_GrantedOperatorScopes" => Scopes,
            "SendWizardRequestAsync" => Send(args!),
            _ => throw new NotSupportedException(method?.Name)
        };

        private Task<JsonElement> Send(object?[] args)
        {
            Requests.Add(((string)args[0]!, JsonSerializer.SerializeToElement(args[1])));
            return Response();
        }
    }

    private static (IOperatorGatewayClient Client, GatewayProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        return (client, (GatewayProxy)client);
    }

    [Fact]
    public async Task CreatesAgent_WithExactGatewayPayload_NotSessionOrLocalPath()
    {
        var (client, proxy) = Gateway();
        var service = new AgentCreationService(() => client);
        Assert.True(service.CanCreate);
        Assert.Equal("research", await service.CreateAsync(" Research ", " ~/.openclaw/research "));
        var request = Assert.Single(proxy.Requests);
        Assert.Equal("agents.create", request.Method);
        Assert.Equal("""{"name":"Research","workspace":"~/.openclaw/research"}""", request.Payload.GetRawText());
    }

    [Theory]
    [InlineData(false, "operator.admin")]
    [InlineData(true, "operator.write")]
    [InlineData(true, "operator.read")]
    public async Task RequiresConnectedAdministrator(bool connected, string scope)
    {
        var (client, proxy) = Gateway();
        proxy.Connected = connected;
        proxy.Scopes = [scope];
        var service = new AgentCreationService(() => client);
        Assert.False(service.CanCreate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("Research", "~/research"));
        Assert.Empty(proxy.Requests);
    }

    [Theory]
    [InlineData("", "~/research")]
    [InlineData("Research", " ")]
    public async Task RejectsMissingFieldsBeforeSending(string name, string workspace)
    {
        var (client, proxy) = Gateway();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new AgentCreationService(() => client).CreateAsync(name, workspace));
        Assert.Empty(proxy.Requests);
    }

    [Theory]
    [InlineData("""{"ok":false,"agentId":"research"}""")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"agentId":""}""")]
    [InlineData("""[]""")]
    public async Task DoesNotInventSuccessFromInvalidResponse(string json)
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentCreationService(() => client).CreateAsync("Research", "~/research"));
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task PropagatesTimeoutWithoutRetry()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => throw new TimeoutException("No reply");
        await Assert.ThrowsAsync<TimeoutException>(() => new AgentCreationService(() => client).CreateAsync("Research", "~/research"));
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task PropagatesGatewayRejectionWithoutRetry()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => throw new InvalidOperationException("unknown method: agents.create");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AgentCreationService(() => client).CreateAsync("Research", "~/research"));
        Assert.Contains("unknown method", error.Message);
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task DoesNotApplySuccessToReplacementGateway()
    {
        var (client, proxy) = Gateway();
        var response = new TaskCompletionSource<JsonElement>();
        proxy.Response = () => response.Task;
        IOperatorGatewayClient? current = client;
        var request = new AgentCreationService(() => current).CreateAsync("Research", "~/research");
        current = Gateway().Client;
        response.SetResult(JsonSerializer.SerializeToElement(new { ok = true, agentId = "research" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        Assert.Single(proxy.Requests);
    }
}
