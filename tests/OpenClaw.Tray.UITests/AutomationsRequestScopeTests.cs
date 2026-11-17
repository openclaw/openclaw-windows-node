// Guards the full-app fixture's independent global-catalog and scoped-page expectations.
// Run with --filter FullyQualifiedName~AutomationsRequestScopeTests; no desktop is used.
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

/// <summary>A valid Inbox catalog must not conceal a missing or wrongly scoped page read.</summary>
public sealed class AutomationsRequestScopeTests
{
    private static GatewayFixtureRequest Catalog() => new("cron.list", null, "ok", IsUnscopedCronCatalogRead: true);

    [Fact]
    public void PageScope_AcceptsGlobalCatalogAlongsideScopedReads() =>
        GatewayFixtureUiTests.AssertAutomationsPageReadScope(
            [Catalog(), new("cron.list", null, "ok", AgentId: "main"), new("cron.status", null, "ok")], "main");

    [Theory]
    [InlineData(null)]
    [InlineData("ops")]
    public void PageScope_RejectsMissingOrWrongAgentEvenWithValidCatalog(string? agent) =>
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => GatewayFixtureUiTests.AssertAutomationsPageReadScope(
            [Catalog(), new("cron.list", null, "ok", AgentId: agent)], "main"));

    [Fact]
    public void PageScope_RejectsCatalogOnlyOrEmptyEvidence()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            GatewayFixtureUiTests.AssertAutomationsPageReadScope([Catalog()], "main"));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            GatewayFixtureUiTests.AssertAutomationsPageReadScope([], "main"));
    }
}
