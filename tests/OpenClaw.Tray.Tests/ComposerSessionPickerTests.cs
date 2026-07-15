namespace OpenClaw.Tray.Tests;

/// <summary>
/// Source-contract guards for the composer pickers. These assert the redesigned composer uses
/// declarative FunctionalUI flyouts rather than hand-rolling a native <c>ComboBox</c> inside a
/// setter — the imperative escape hatch that caused the #970 "dropdown slams shut on status
/// render" regression.
/// </summary>
public sealed class ComposerSessionPickerTests
{
    private static string ComposerSource() => File.ReadAllText(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(),
        "src",
        "OpenClaw.Tray.WinUI",
        "Chat",
        "OpenClawComposer.cs"));

    [Fact]
    public void SessionPicker_UsesDeclarativeContentFlyout()
    {
        var composer = ComposerSource();

        Assert.Contains("var sessionRows = new List<Element?>();", composer);
        Assert.Contains("var channelFlyout = ContentFlyout(", composer);
        Assert.Contains("var channelPicker = PickerButton(", composer);
    }

    [Fact]
    public void ModelPicker_UsesDeclarativeMenuFlyout()
    {
        var composer = ComposerSource();

        Assert.Contains("var modelMenu = MenuItems(", composer);
        Assert.Contains("var modelPicker = PickerButton(", composer);
        Assert.Contains("ToggleMenuItem(", composer);
    }

    [Fact]
    public void Composer_DoesNotHandRollNativePickersOrSnapshots()
    {
        var composer = ComposerSource();

        // The escape-hatch patterns that produced #970 must not return.
        Assert.DoesNotContain("border.Child = cb;", composer);
        Assert.DoesNotContain("SessionPickerSnapshot", composer);
        Assert.DoesNotContain("Native(", composer);
        Assert.DoesNotContain("ComboBox(sessionItems", composer);
    }
}
