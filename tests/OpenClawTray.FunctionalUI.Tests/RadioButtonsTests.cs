using static OpenClawTray.FunctionalUI.Factories;

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
}
