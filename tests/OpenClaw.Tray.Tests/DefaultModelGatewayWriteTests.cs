using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class DefaultModelGatewayWriteTests
{
    private const string Envelope =
        """{"parsed":{"agents":{"entries":{"main":{"model":"x/y"}},"defaults":{"model":{"primary":"anthropic/claude-sonnet-5"}}}},"hash":"h1"}""";

    public class GatewayProxy : DispatchProxy
    {
        public long Epoch = 7;
        public bool Connected = true;
        public List<string> Calls = [];
        public long? PatchEpoch;
        public Func<Task<JsonElement>> ConfigGet = () => Task.FromResult(Parse(Envelope));

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_SessionMutationConnectionEpoch":
                    return (long?)Epoch;
                case "get_IsConnectedToGateway":
                    return Connected;
                case "SendWizardRequestOnConnectionAsync":
                    Calls.Add((string)args![0]!);
                    return ConfigGet();
                case "PatchConfigOnConnectionAsync":
                    Calls.Add("config.patch");
                    PatchEpoch = (long)args![2]!;
                    return Task.FromResult(new ConfigPatchResult { Ok = true });
                case "RequestConfigAsync":
                    Calls.Add("config.refresh");
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
        private readonly JsonElement _agentsList = Parse("""{"defaultId":"main"}""");

        public Fixture()
        {
            (Client, Proxy) = Gateway();
            Current = Client;
        }

        public DefaultModelGatewayWrite Create() =>
            new(Client, "gw-a", () => Current, () => Active, () => Connected, () => _agentsList);
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

    private static Task Run(DefaultModelGatewayWrite write, string op) =>
        op == "primary" ? write.SetPrimaryModelAsync("openai/gpt-6") : write.ClearDefaultAgentModelAsync();

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

    [Theory]
    [InlineData("primary")]
    [InlineData("clear")]
    public async Task BoundWrite_SendsOnlyOnTheCapturedConnection(string op)
    {
        var fixture = new Fixture();

        await Run(fixture.Create(), op);

        Assert.Equal(["config.get", "config.patch", "config.refresh"], fixture.Proxy.Calls);
        Assert.Equal(7, fixture.Proxy.PatchEpoch);
    }

    [Theory]
    [InlineData("primary", "client")]
    [InlineData("primary", "reconnect")]
    [InlineData("primary", "disconnect")]
    [InlineData("primary", "registry")]
    [InlineData("primary", "connection")]
    [InlineData("clear", "client")]
    [InlineData("clear", "reconnect")]
    [InlineData("clear", "disconnect")]
    [InlineData("clear", "registry")]
    [InlineData("clear", "connection")]
    public async Task ChangeBeforeDispatch_SendsNothing(string op, string change)
    {
        var fixture = new Fixture();
        DefaultModelGatewayWrite write = fixture.Create();

        Change(fixture, change);

        await Assert.ThrowsAsync<DefaultModelGatewayChangedException>(() => Run(write, op));
        Assert.Empty(fixture.Proxy.Calls);
        Assert.Empty(fixture.Replacement.Proxy.Calls);
    }

    [Theory]
    [InlineData("primary", "client")]
    [InlineData("primary", "reconnect")]
    [InlineData("primary", "registry")]
    [InlineData("clear", "client")]
    [InlineData("clear", "reconnect")]
    [InlineData("clear", "registry")]
    public async Task ChangeWhileConfigGetPending_DoesNotSendPatch(string op, string change)
    {
        var fixture = new Fixture();
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.ConfigGet = () => reply.Task;
        Task task = Run(fixture.Create(), op);

        Change(fixture, change);
        reply.SetResult(Parse(Envelope));

        await Assert.ThrowsAsync<DefaultModelGatewayChangedException>(() => task);
        Assert.Equal(["config.get"], fixture.Proxy.Calls);
        Assert.Empty(fixture.Replacement.Proxy.Calls);
    }

    [Fact]
    public void AdmissionForAnotherGateway_IsRefused()
    {
        var fixture = new Fixture { Active = "gw-b" };

        Assert.Throws<DefaultModelGatewayChangedException>(() => fixture.Create());
        Assert.Empty(fixture.Proxy.Calls);
    }
}
