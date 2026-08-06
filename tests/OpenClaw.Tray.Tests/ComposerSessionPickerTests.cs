namespace OpenClaw.Tray.Tests;

/// <summary>
/// Source-contract guards for the Reactor composer pickers. These assert the production composer
/// keeps picker identity in the declarative Reactor tree instead of hand-rolling a native
/// <c>ComboBox</c>, the escape hatch that caused the #970 dropdown regression.
/// </summary>
public sealed class ComposerSessionPickerTests
{
    private static string ComposerSource() => File.ReadAllText(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(),
        "src",
        "OpenClaw.Tray.WinUI",
        "Chat",
        "OpenClawReactorChatRoot.cs"));

    [Fact]
    public void SessionPicker_UsesDeclarativeMenuFlyout()
    {
        var composer = ComposerSource();

        Assert.Contains("var sessionPicker = MenuFlyout(", composer);
        Assert.Contains("props.AvailableChannels", composer);
        Assert.Contains(".Select(thread => RadioMenuItem(", composer);
        Assert.Contains("() => props.OnChannelChanged(thread.Id)", composer);
    }

    [Fact]
    public void ModelPicker_UsesDeclarativeMenuFlyout()
    {
        var composer = ComposerSource();

        Assert.Contains("var modelPicker = MenuFlyout(", composer);
        Assert.Contains("modelNames", composer);
        Assert.Contains(".Select((modelName, index) => RadioMenuItem(", composer);
    }

    [Fact]
    public void Composer_DoesNotHandRollNativePickersOrSnapshots()
    {
        var composer = ComposerSource();

        Assert.DoesNotContain("border.Child = cb;", composer);
        Assert.DoesNotContain("SessionPickerSnapshot", composer);
        Assert.DoesNotContain("ComboBox(sessionItems", composer);
    }
}
