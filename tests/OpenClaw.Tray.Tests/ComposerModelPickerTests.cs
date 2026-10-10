namespace OpenClaw.Tray.Tests;

/// <summary>
/// Source-contract guards for the Reactor composer pickers. These assert the production composer
/// keeps picker identity in the declarative Reactor tree instead of hand-rolling a native
/// <c>ComboBox</c>, the escape hatch that caused the #970 dropdown regression.
/// </summary>
public sealed class ComposerModelPickerTests
{
    private static string ComposerSource() => File.ReadAllText(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(),
        "src",
        "OpenClaw.Tray.WinUI",
        "Chat",
        "ReactorChatComposer.cs"));

    [Fact]
    public void ModelPicker_UsesDeclarativeCatalogFlyout()
    {
        var composer = ComposerSource();

        Assert.Contains("Component<ChatModelPicker, ChatModelPickerProps>", composer);
        Assert.DoesNotContain("controller.ClearModel()", composer);
        Assert.Contains("controller.SetModel(choice.SelectionId)", composer);
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
