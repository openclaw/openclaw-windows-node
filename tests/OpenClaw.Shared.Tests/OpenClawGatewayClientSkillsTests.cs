using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class OpenClawGatewayClientSkillsTests
{
    [Theory]
    [InlineData(null, "node")]
    [InlineData("", "node")]
    [InlineData(" ", "node")]
    [InlineData("calendar", null)]
    [InlineData("calendar", "")]
    [InlineData("calendar", " ")]
    public async Task InstallRequiresBothSkillNameAndAdvertisedRecipe(string? name, string? installId)
    {
        using var identity = new TempDirectory("skills-validation-");
        using var client = new OpenClawGatewayClient("ws://localhost:1", "synthetic-token", identityPath: identity.Path);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.InstallSkillAsync(name!, installId!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task UpdateRequiresStableSkillKey(string? key)
    {
        using var identity = new TempDirectory("skills-validation-");
        using var client = new OpenClawGatewayClient("ws://localhost:1", "synthetic-token", identityPath: identity.Path);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.SetSkillEnabledAsync(key!, false));
    }

    [Fact]
    public async Task MutationsWithoutConnectionAreNotSuccessful()
    {
        using var identity = new TempDirectory("skills-disconnected-");
        var logger = new TestLogger();
        using var client = new OpenClawGatewayClient("ws://localhost:1", "synthetic-token", logger, identityPath: identity.Path);

        Assert.False(await client.SetSkillEnabledAsync("calendar-key", false));
        Assert.False(await client.InstallSkillAsync("calendar", "node"));
        Assert.Contains(logger.Logs, message => message.Contains("WARN: skills.update request failed"));
        Assert.Contains(logger.Logs, message => message.Contains("WARN: skills.install request failed"));
    }
}
