using System.Reflection;
using System.Text.Json;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests;

public sealed class SetupNativeSkillsTests
{
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
        ((SkillsClient)client).Response = new TaskCompletionSource<JsonElement>().Task;
        using var cancel = new CancellationTokenSource();
        var load = SetupNativeSkills.LoadAsync(Request, () => client, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
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
