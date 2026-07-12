using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class SessionPickerSnapshotTests
{
    [Fact]
    public void Composer_ReusesTheNativePickerAcrossRenders()
    {
        var composer = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawComposer.cs"));

        Assert.Contains("var channelComboRef = UseRef<ComboBox?>(null);", composer);
        Assert.Contains("channelGroupsRef.Current?.Matches(groups) != true", composer);
        Assert.Contains("channelComboRef.Current = cb;", composer);
    }

    [Fact]
    public void Matches_EquivalentGroupsFromANewRender()
    {
        var snapshot = SessionPickerSnapshot.Capture(Groups());

        Assert.True(snapshot.Matches(Groups()));
    }

    [Fact]
    public void Matches_DetectsContentAndOrderChanges()
    {
        var snapshot = SessionPickerSnapshot.Capture(Groups());

        Assert.False(snapshot.Matches([
            new ChannelGroup("Main", [("two", "Second"), ("one", "First")]),
        ]));
        Assert.False(snapshot.Matches([
            new ChannelGroup("Main", [("one", "Renamed"), ("two", "Second")]),
        ]));
    }

    private static ChannelGroup[] Groups() =>
    [
        new ChannelGroup("Main", [("one", "First"), ("two", "Second")]),
    ];
}
