using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task AccessibilityScenario_PopulatesReadDataButStillRefusesConfigWrites()
    {
        var scenario = GatewayScenario.CreateAccessibilityBaseline();
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var config = await client.SendWizardRequestAsync("config.get");
        var parsed = config.GetProperty("config");
        Assert.Equal(new[] { "alpha", "bravo" }, parsed.GetProperty("messages").GetProperty("fixtureLabels").EnumerateArray().Select(v => v.GetString()));
        var schema = await client.SendWizardRequestAsync("config.schema");
        Assert.Equal("Fixture labels", schema.GetProperty("schema").GetProperty("properties").GetProperty("messages").GetProperty("properties").GetProperty("fixtureLabels").GetProperty("title").GetString());
        var status = await client.SendWizardRequestAsync("usage.status");
        Assert.Equal("Fixture Provider", status.GetProperty("providers")[0].GetProperty("displayName").GetString());
        foreach (var days in new[] { 7, 30 })
        {
            var cost = await client.SendWizardRequestAsync("usage.cost", new { days });
            Assert.Equal(days, cost.GetProperty("days").GetInt32());
            Assert.Equal(days == 7 ? 0.75 : 1.25, cost.GetProperty("daily")[0].GetProperty("totalCost").GetDouble());
            Assert.Equal(days == 7 ? 1.25 : 1.75, cost.GetProperty("totals").GetProperty("totalCost").GetDouble());
        }
        scenario.AdvanceAccessibilityUsage();
        var changed = await client.SendWizardRequestAsync("usage.status");
        Assert.Equal(53, changed.GetProperty("providers")[0].GetProperty("windows")[0].GetProperty("usedPercent").GetInt32());
        var changedCost = await client.SendWizardRequestAsync("usage.cost", new { days = 30 });
        Assert.Equal(2.50, changedCost.GetProperty("daily")[0].GetProperty("totalCost").GetDouble());
        foreach (var method in new[] { "config.set", "config.patch", "config.apply" })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendWizardRequestAsync(method, new { }));
            Assert.Contains("read-only", error.Message);
        }
        Assert.Empty(server.UnexpectedRequests);
    }
}
