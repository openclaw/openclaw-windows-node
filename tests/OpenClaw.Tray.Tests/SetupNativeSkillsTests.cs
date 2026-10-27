using System.Reflection;
using System.Text.Json;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests;

public sealed class SetupNativeSkillsTests
{
    [Fact]
    public async Task RealClientReloadsBoundStatusAfterAcknowledgedMutation()
    {
        const string token = "synthetic-skills-owner-token";
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateSkillsEditing(), token);
        using var identity = new TempDirectory("native-skills-socket-");
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, identityPath: identity.Path,
            ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) => ready.TrySetResult();
        await client.ConnectAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var request = new SetupNativeNavigationRequest(new(
            new(SetupCompletionIntent.CustodianOnboarding, "gateway", "binding", "provider/model", "main", 1,
                IdentityBinding: new string('B', 64), SessionKey: "agent:main:main"),
            new(SetupNativeDestination.Skills, "agent:main:main")));
        try
        {
            var before = await SetupNativeSkills.LoadAsync(request, () => client, CancellationToken.None);
            Assert.Equal("main", before.GetProperty("agentId").GetString());
            Assert.False(before.GetProperty("skills")[0].GetProperty("disabled").GetBoolean());
            Assert.True(await client.SetSkillEnabledAsync("fixture-calendar", false));

            var after = await SetupNativeSkills.LoadAsync(request, () => client, CancellationToken.None);

            Assert.True(after.GetProperty("skills")[0].GetProperty("disabled").GetBoolean());
            Assert.Equal("main", after.GetProperty("agentId").GetString());
            Assert.Equal(2, server.Requests.Count(row => row.Method == "skills.status"));
            Assert.Empty(server.UnexpectedRequests);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    private static SetupNativeNavigationRequest Request => new(new(
        new(SetupCompletionIntent.CustodianOnboarding, "gateway", "binding", "provider/model", "primary", 1,
            IdentityBinding: new string('B', 64), SessionKey: "agent:primary:main"),
        new(SetupNativeDestination.Skills, "agent:primary:main")));

    [Fact]
    public async Task LoadsOnlyResponseBoundSkillsForVerifiedAgent()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, SkillsClient>();
        var result = await SetupNativeSkills.LoadAsync(Request, () => client, CancellationToken.None);
        Assert.Empty(result.GetProperty("skills").EnumerateArray());
        Assert.Equal(1, ((SkillsClient)client).Calls);
    }

    [Fact]
    public async Task ClientReplacementDuringLoadCannotPresentData()
    {
        var first = DispatchProxy.Create<IOperatorGatewayClient, SkillsClient>();
        var second = DispatchProxy.Create<IOperatorGatewayClient, SkillsClient>();
        var calls = 0;
        await Assert.ThrowsAsync<SetupNativeOwnershipException>(() =>
            SetupNativeSkills.LoadAsync(Request, () => ++calls == 1 ? first : second, CancellationToken.None));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"skills\":null}")]
    [InlineData("[]")]
    public async Task MalformedResponseIsNotSuccessfulPresentation(string json)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, SkillsClient>();
        ((SkillsClient)client).Response = Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SetupNativeSkills.LoadAsync(Request, () => client, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledReadDoesNotWaitForGatewayOrInstallAnything()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, SkillsClient>();
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((SkillsClient)client).Response = reply.Task;
        using var cancel = new CancellationTokenSource();
        var ownerChecks = 0;
        var load = SetupNativeSkills.LoadAsync(Request, () => { ownerChecks++; return client; }, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        reply.SetResult(JsonDocument.Parse("{\"skills\":[]}").RootElement.Clone());
        Assert.True(load.IsCanceled);
        Assert.Equal(1, ownerChecks);
        Assert.Equal(1, ((SkillsClient)client).Calls);
    }

    public class SkillsClient : DispatchProxy
    {
        public int Calls { get; private set; }
        public Task<JsonElement> Response { get; set; } =
            Task.FromResult(JsonDocument.Parse("{\"skills\":[]}").RootElement.Clone());

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(IOperatorGatewayClient.SendWizardRequestAsync), targetMethod!.Name);
            Assert.Equal("skills.status", args![0]);
            Assert.Equal("primary", JsonSerializer.SerializeToElement(args[1]).GetProperty("agentId").GetString());
            Assert.Equal(12000, args[2]);
            Calls++;
            return Response;
        }
    }
}
