using static OpenClawTray.FunctionalUI.Factories;

namespace OpenClawTray.FunctionalUI.Tests;

public sealed class RadioButtonsTests
{
    [Fact]
    public void RadioButtons_WithSelectedIndexMinusOne_RepresentsNoSelection()
    {
        var element = RadioButtons(["A", "B"], selectedIndex: -1);

        // Verify -1 represents no selection
        Assert.Equal(-1, element.SelectedIndex);
        Assert.Equal(["A", "B"], element.Items);
    }
    
    [Fact]
    public void RadioButtons_WithValidSelection_SetsSelectedIndex()
    {
        var element = RadioButtons(["Option1", "Option2", "Option3"], selectedIndex: 1);
        
        Assert.Equal(1, element.SelectedIndex);
        Assert.Equal(3, element.Items.Length);
        Assert.Equal("Option2", element.Items[1]);
    }
    
    [Fact]
    public void RadioButtons_EmptyItems_AllowsNoSelection()
    {
        var element = RadioButtons([], selectedIndex: -1);
        
        Assert.Equal(-1, element.SelectedIndex);
        Assert.Empty(element.Items);
    }
}
