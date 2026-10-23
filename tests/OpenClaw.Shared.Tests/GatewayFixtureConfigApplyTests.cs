using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task ConfigApply_ReplacesWholeDocumentAndRejectsStaleAndInvalidWrites()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateConfigEditing(new ConfigEditingFixture()), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var before = await client.SendWizardRequestAsync("config.get");
        var document = JsonNode.Parse(before.GetProperty("parsed").GetRawText())!;
        document["models"]!["providers"]!["fixture"]!["models"] =
            JsonNode.Parse("""[{"id":"beta","name":"Edited Beta"}]""");
        document["messages"]!["responsePrefix"] = "Edited";
        var config = JsonSerializer.SerializeToElement(document);
        var hash = before.GetProperty("hash").GetString()!;
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        server.HoldConfigApplies();
        var apply = client.ApplyConfigDetailedAsync(config, hash, epoch);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("config.apply", cancellationToken: deadline.Token);
        await client.SendWizardRequestAsync("health");
        Assert.False(apply.IsCompleted);
        server.ReleaseConfigApplies();
        var result = await apply.WaitAsync(Deadline);
        Assert.True(result.Ok, result.Error);

        var after = await client.SendWizardRequestAsync("config.get");
        var models = after.GetProperty("parsed").GetProperty("models").GetProperty("providers")
            .GetProperty("fixture").GetProperty("models").EnumerateArray().ToArray();
        Assert.Single(models);
        Assert.Equal("beta", models[0].GetProperty("id").GetString());
        Assert.Equal("Edited Beta", models[0].GetProperty("name").GetString());
        Assert.Equal(2, after.GetProperty("parsed").GetProperty("agents").GetProperty("list").GetArrayLength());
        Assert.Equal("Edited", after.GetProperty("parsed").GetProperty("messages").GetProperty("responsePrefix").GetString());
        Assert.NotEqual(hash, after.GetProperty("hash").GetString());

        var stale = await client.ApplyConfigDetailedAsync(config, hash, epoch);
        Assert.False(stale.Ok);
        Assert.True(stale.LooksLikeStaleBaseHash);
        var invalid = await client.ApplyConfigDetailedAsync(JsonSerializer.SerializeToElement(new { }),
            after.GetProperty("hash").GetString()!, epoch);
        Assert.False(invalid.Ok);
        Assert.Contains("retain the main agent", invalid.Error);
        var unchanged = await client.SendWizardRequestAsync("config.get");
        Assert.Equal(after.GetProperty("hash").GetString(), unchanged.GetProperty("hash").GetString());
        Assert.Equal(3, server.Requests.Count(request => request.Method == "config.apply"));
        Assert.DoesNotContain(server.Requests, request => request.Method == "config.patch");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigApply_RequiresAdministratorBeforeDispatch(bool writeScope)
    {
        var token = CreateToken();
        var scenario = writeScope ? GatewayScenario.CreateContextUsage(new ContextUsageFixture()) : GatewayScenario.CreateBrowse();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var result = await client.ApplyConfigDetailedAsync(JsonSerializer.SerializeToElement(new { }),
            "revision", client.SessionMutationConnectionEpoch!.Value);
        Assert.False(result.Ok);
        Assert.Contains("operator.admin", result.Error);
        Assert.DoesNotContain(server.Requests, request => request.Method == "config.apply");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task ConfigApply_RejectsMissingRevisionOrNonObjectBeforeDispatch(string raw)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateConfigEditing(new ConfigEditingFixture()), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var document = JsonDocument.Parse(raw);
        var result = await connected.Client.ApplyConfigDetailedAsync(document.RootElement,
            raw == "{}" ? "" : "revision", connected.Client.SessionMutationConnectionEpoch!.Value);
        Assert.False(result.Ok);
        Assert.Contains("valid configuration revision", result.Error);
        Assert.DoesNotContain(server.Requests, request => request.Method == "config.apply");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigApply_LostReplyIsNotReplayedAndReconnectRequiresFreshEpoch(bool disconnect)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateConfigEditing(new ConfigEditingFixture()), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var before = await client.SendWizardRequestAsync("config.get");
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        var config = JsonNode.Parse(before.GetProperty("parsed").GetRawText())!;
        config["messages"]!["responsePrefix"] = "Persisted without acknowledgement";
        server.HoldConfigApplies();
        var apply = client.ApplyConfigDetailedAsync(JsonSerializer.SerializeToElement(config),
            before.GetProperty("hash").GetString()!, epoch, timeoutMs: disconnect ? 15000 : 1000);
        using var deadline = new CancellationTokenSource(Deadline);
        await server.WaitForRequestAsync("config.apply", cancellationToken: deadline.Token);
        // The fixture has committed in memory but has not acknowledged the mutation.
        var persisted = await client.SendWizardRequestAsync("config.get");
        Assert.Equal("Persisted without acknowledgement",
            persisted.GetProperty("parsed").GetProperty("messages").GetProperty("responsePrefix").GetString());
        if (disconnect)
            await client.DisconnectAsync();
        var uncertain = await apply.WaitAsync(Deadline);
        Assert.False(uncertain.Ok);
        Assert.Contains("not automatically replayed", uncertain.Error);
        server.ReleaseConfigApplies();
        if (disconnect)
        {
            var handshake = Signal<bool>();
            client.HandshakeSucceeded += (_, _) => handshake.TrySetResult(true);
            await client.ConnectAsync();
            await handshake.Task.WaitAsync(Deadline);
            var rejected = await client.ApplyConfigDetailedAsync(JsonSerializer.SerializeToElement(config),
                persisted.GetProperty("hash").GetString()!, epoch);
            Assert.False(rejected.Ok);
            Assert.Contains("connection changed", rejected.Error, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            await server.WaitForCompletedRequestAsync("config.apply", cancellationToken: deadline.Token);
        }
        var reconciled = await client.SendWizardRequestAsync("config.get");
        Assert.Equal(persisted.GetProperty("hash").GetString(), reconciled.GetProperty("hash").GetString());
        Assert.Single(server.Requests, request => request.Method == "config.apply");
        Assert.Empty(server.UnexpectedRequests);
    }
}
