using static OpenClawTray.FunctionalUI.Factories;
using OpenClawTray.FunctionalUI;

namespace OpenClawTray.FunctionalUI.Tests;

public sealed class RadioButtonsTests
{
    [Fact]
    public void RadioButtons_WithSelectedIndexMinusOne_RepresentsNoSelection()
    {
        var element = RadioButtons(["A", "B"], selectedIndex: -1);

        Assert.Equal(-1, element.SelectedIndex);
        Assert.Equal(["A", "B"], element.Items);
    }

    [Fact]
    public void ComboBox_WithSelectedIndex_StoresItemsAndSelection()
    {
        var element = ComboBox(["A", "B"], selectedIndex: 1, header: "Model");

        Assert.Equal(1, element.SelectedIndex);
        Assert.Equal("Model", element.Header);
        Assert.Equal(["A", "B"], element.Items);
    }

    [Fact]
    public void Key_AssignsStableElementKey()
    {
        var element = TextBlock("hello").Key("message-1");

        Assert.Equal("message-1", element.Key);
    }
}
