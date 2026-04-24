using OpenClawTray.Onboarding.Widgets;

namespace OpenClaw.Tray.Tests;

public class WizardStepPropsTests
{
    #region WizardStepType enum

    [Theory]
    [InlineData(WizardStepType.Note, 0)]
    [InlineData(WizardStepType.Text, 1)]
    [InlineData(WizardStepType.Confirm, 2)]
    [InlineData(WizardStepType.Select, 3)]
    [InlineData(WizardStepType.MultiSelect, 4)]
    [InlineData(WizardStepType.Progress, 5)]
    [InlineData(WizardStepType.Action, 6)]
    public void WizardStepType_HasExpectedValues(WizardStepType type, int expectedValue)
    {
        Assert.Equal(expectedValue, (int)type);
    }

    [Fact]
    public void WizardStepType_Has7Values()
    {
        var values = Enum.GetValues<WizardStepType>();
        Assert.Equal(7, values.Length);
    }

    #endregion

    #region WizardStepProps defaults

    [Fact]
    public void WizardStepProps_OptionalDefaults()
    {
        var props = new WizardStepProps("id1", "Title", "Message", WizardStepType.Note);

        Assert.Equal("id1", props.Id);
        Assert.Equal("Title", props.Title);
        Assert.Equal("Message", props.Message);
        Assert.Equal(WizardStepType.Note, props.Type);
        Assert.Null(props.Options);
        Assert.Null(props.InitialValue);
        Assert.Null(props.Placeholder);
        Assert.False(props.Sensitive);
        Assert.Null(props.OnSubmit);
    }

    [Fact]
    public void WizardStepProps_WithAllParameters()
    {
        var options = new[] { "A", "B" };
        string? submitted = null;

        var props = new WizardStepProps(
            Id: "step1",
            Title: "Pick one",
            Message: "Choose wisely",
            Type: WizardStepType.Select,
            Options: options,
            InitialValue: "A",
            Placeholder: "Select...",
            Sensitive: true,
            OnSubmit: v => submitted = v);

        Assert.Equal("step1", props.Id);
        Assert.Equal(WizardStepType.Select, props.Type);
        Assert.Same(options, props.Options);
        Assert.Equal("A", props.InitialValue);
        Assert.Equal("Select...", props.Placeholder);
        Assert.True(props.Sensitive);

        props.OnSubmit!("chosen");
        Assert.Equal("chosen", submitted);
    }

    #endregion
}
