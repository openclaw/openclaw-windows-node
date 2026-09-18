using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public sealed class WizardOptionalSetupHandoffTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static JsonElement Checkpoint => Json("""{"id":"optional","type":"note","title":"Optional apps"}""");

    [Fact]
    public async Task CancelsBeforeCheckingConfigurationAndAuthenticatedHealth()
    {
        var calls = new List<string>();
        await WizardOptionalSetupHandoff.CompleteAsync((method, parameters, timeout) =>
        {
            calls.Add(method);
            Assert.Equal(30_000, timeout);
            if (method == "wizard.cancel")
                Assert.Equal("session-1", JsonSerializer.SerializeToElement(parameters).GetProperty("sessionId").GetString());
            return Task.FromResult(Response(method));
        }, "session-1", Checkpoint, CancellationToken.None);
        Assert.Equal(["wizard.cancel", "config.get", "health"], calls);
    }

    [Theory]
    [InlineData("""{"status":"running"}""")]
    [InlineData("""{"status":"done"}""")]
    [InlineData("""{"status":"error","error":"save failed"}""")]
    [InlineData("""{"status":"cancelled","error":"cleanup failed"}""")]
    [InlineData("{}")]
    public async Task UnconfirmedCancellationNeverProceedsToValidation(string response)
    {
        var calls = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WizardOptionalSetupHandoff.CompleteAsync((method, _, _) =>
            {
                calls.Add(method);
                return Task.FromResult(Json(response));
            }, "session-1", Checkpoint, CancellationToken.None));
        Assert.Equal(["wizard.cancel"], calls);
    }

    [Theory]
    [InlineData("config.get", """{"valid":false}""")]
    [InlineData("config.get", "{}")]
    [InlineData("health", """{"ok":false}""")]
    [InlineData("health", "{}")]
    public async Task FailedOrMissingValidationCannotComplete(string failedMethod, string response)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WizardOptionalSetupHandoff.CompleteAsync((method, _, _) =>
                Task.FromResult(method == failedMethod ? Json(response) : Response(method)),
                "session-1", Checkpoint, CancellationToken.None));
    }

    [Fact]
    public async Task TransportFailurePropagates()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            WizardOptionalSetupHandoff.CompleteAsync((_, _, _) => throw new TimeoutException(),
                "session-1", Checkpoint, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAfterGatewayCancelPreventsFurtherRequests()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WizardOptionalSetupHandoff.CompleteAsync((method, _, _) =>
            {
                calls.Add(method);
                cancellation.Cancel();
                return Task.FromResult(Response(method));
            }, "session-1", Checkpoint, cancellation.Token));
        Assert.Equal(["wizard.cancel"], calls);
    }

    [Fact]
    public async Task EarlierOrArbitraryStepCannotBeUsedAsHandoff()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WizardOptionalSetupHandoff.CompleteAsync((_, _, _) => throw new Xunit.Sdk.XunitException("Unexpected RPC"),
                "session-1", Json("""{"id":"consent","type":"confirm","title":"Optional apps"}"""),
                CancellationToken.None));
    }

    private static JsonElement Response(string method) => method switch
    {
        "wizard.cancel" => Json("""{"status":"cancelled","error":"cancelled"}"""),
        "config.get" => Json("""{"valid":true}"""),
        "health" => Json("""{"ok":true}"""),
        _ => throw new InvalidOperationException($"Unexpected RPC: {method}"),
    };
}
