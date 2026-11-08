using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class HubSearchTests
{
    [Fact]
    public void ExactThenPrefixThenContains_RegardlessOfInputOrder()
    {
        var items = new[] { Item("contains", "Manage camera"), Item("prefix", "Camera access"), Item("exact", "Camera") };
        Assert.Equal(["exact", "prefix", "contains"], HubSearch.Find(items, " camera ").Select(match => match.Item.Id));
    }

    [Fact]
    public void TranscriptAdmissionDoesNotRequireLiteralQueryInShortenedSnippet()
    {
        var hit = Item("hit", "Production discussion") with
        {
            Group = HubSearchGroup.Messages, Subtitle = "We deploy the change, but it failed.", ServerMatch = true
        };
        Assert.Single(HubSearch.Find([hit], "deploy fail"));
        Assert.Single(HubSearch.Find([hit with { Subtitle = "Shortened preview" }], "deploy fail"));
        Assert.Empty(HubSearch.Find([hit with { ServerMatch = false, Subtitle = "Shortened preview" }], "deploy fail"));
    }

    [Fact]
    public void MetadataAndMessageHitsProduceOneSessionRow()
    {
        var chat = Item("session:one", "Deployment") with { Group = HubSearchGroup.Chats };
        var message = chat with { Group = HubSearchGroup.Messages, Title = "Other title", ServerMatch = true };
        var match = Assert.Single(HubSearch.Find([message, chat], "deployment"));
        Assert.Equal(HubSearchGroup.Chats, match.Item.Group);
    }

    [Fact]
    public void UnicodeCanonicalEquivalentsMatch()
    {
        Assert.Equal(3, Assert.Single(HubSearch.Find([Item("one", "Caf\u00e9")], "Cafe\u0301")).Rank);
    }

    [Fact]
    public void ExactSettingPrecedesAChatThatOnlyMentionsItsName()
    {
        var setting = Item("camera", "Camera");
        var chat = Item("chat", "Discuss camera options") with { Group = HubSearchGroup.Chats };
        Assert.Equal("camera", HubSearch.Find([chat, setting], "camera")[0].Item.Id);
    }

    [Fact]
    public void MetadataTermsCanBeSeparatedByPastedWhitespace()
    {
        Assert.Single(HubSearch.Find([Item("camera", "Camera") with { Subtitle = "Permissions" }], "camera\tpermissions"));
    }

    [Fact]
    public void CapsApplyPerGroup_NotJustToTheFirstCategory()
    {
        var items = Enumerable.Range(0, 9).Select(i => Item($"page:{i}", $"Find {i}"))
            .Append(Item("chat", "Find chat") with { Group = HubSearchGroup.Chats });
        var matches = HubSearch.Find(items, "find");
        Assert.Equal(6, matches.Count);
        Assert.Equal("chat", matches[0].Item.Id);
    }

    [Fact]
    public void SessionTargetsPreserveIdentityAndExcludeArchivedAndBackgroundRows()
    {
        var items = HubSearch.Sessions([
            new SessionInfo { Key = "agent:main:one", Label = "My chat" },
            new SessionInfo { Key = "agent:main:old", Archived = true },
            new SessionInfo { Key = "agent:main:background", IsBackground = true }
        ]);
        var item = Assert.Single(items);
        Assert.Equal("My chat", item.Title);
        Assert.Equal(new(HubSearchTargetKind.Session, "agent:main:one"), item.Target);
    }

    private static HubSearchItem Item(string id, string title) =>
        new(id, HubSearchGroup.Settings, title, null, "\uE713", new(HubSearchTargetKind.Page, "settings"));
}
