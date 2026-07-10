using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class ChannelsPageDiscordConfigContractTests
{
    private static readonly string[] DiscordResourceKeys =
    [
        "ChannelsPage_HelpDiscord",
        "ChannelsPage_GuideDiscordHeadline",
        "ChannelsPage_GuideDiscordStep1",
        "ChannelsPage_GuideDiscordStep2",
        "ChannelsPage_GuideDiscordStep3",
        "ChannelsPage_GuideDiscordStep4",
        "ChannelsPage_FieldDiscordBotToken",
        "ChannelsPage_PlaceholderDiscordBotToken",
        "ChannelsPage_HelpDiscordBotToken",
    ];

    [Fact]
    public void DiscordInlineForm_UsesStrictSchemaBotTokenField()
    {
        var source = Read("src", "OpenClaw.Tray.WinUI", "Pages", "ChannelsPage.xaml.cs");

        Assert.Contains("\"channels.discord.token\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("channels.discord.webhookUrl", source, StringComparison.Ordinal);
        Assert.Contains("ChannelsPage_FieldDiscordBotToken", source, StringComparison.Ordinal);
        Assert.Contains("ChannelsPage_PlaceholderDiscordBotToken", source, StringComparison.Ordinal);
        Assert.Contains("ChannelsPage_HelpDiscordBotToken", source, StringComparison.Ordinal);

        Assert.Contains("\"channels.googlechat.webhookUrl\"", source, StringComparison.Ordinal);
        Assert.Contains("ChannelsPage_HelpWebhookGoogleChat", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscordSetupGuidance_UsesBotInstructionsInEveryLocale()
    {
        var stringsRoot = Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Strings");

        foreach (var resourcePath in Directory.EnumerateFiles(stringsRoot, "Resources.resw", SearchOption.AllDirectories))
        {
            var resources = XDocument.Load(resourcePath)
                .Root!
                .Elements("data")
                .ToDictionary(
                    element => element.Attribute("name")!.Value,
                    element => element.Element("value")!.Value,
                    StringComparer.Ordinal);

            foreach (var key in DiscordResourceKeys)
            {
                Assert.True(resources.TryGetValue(key, out var value), $"{resourcePath} is missing {key}.");
                Assert.DoesNotContain("webhook", value!, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void DiscordEnglishCopy_IdentifiesBotTokenAndDeveloperPortal()
    {
        var resourcePath = Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Strings",
            "en-us",
            "Resources.resw");
        var resources = XDocument.Load(resourcePath)
            .Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")!.Value,
                StringComparer.Ordinal);

        Assert.Equal("Discord Bot Token", resources["ChannelsPage_FieldDiscordBotToken"]);
        Assert.Equal("Paste Discord bot token", resources["ChannelsPage_PlaceholderDiscordBotToken"]);
        Assert.Contains("Discord Developer Portal", resources["ChannelsPage_HelpDiscordBotToken"], StringComparison.Ordinal);
        Assert.Equal("Connect Discord via a bot", resources["ChannelsPage_GuideDiscordHeadline"]);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
