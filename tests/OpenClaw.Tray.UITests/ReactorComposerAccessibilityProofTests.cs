using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ReactorComposerAccessibilityProofTests
{
    private readonly UIThreadFixture _ui;

    public ReactorComposerAccessibilityProofTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task ComposerAutomationVisibility_GatesHitTestingUntilLayoutIsUsable()
    {
        await _ui.ResetContainerAsync();
        Border? control = null;

        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                control = new Border
                {
                    Width = 240,
                    Height = 56,
                };
                AutomationProperties.SetAutomationId(control, "ComposerReady");
                ComposerAutomationVisibility.Prepare(control);
                Assert.False(control.IsHitTestVisible);
                Assert.Equal(
                    AccessibilityView.Raw,
                    AutomationProperties.GetAccessibilityView(control));
                Assert.Equal(
                    "ComposerReady",
                    AutomationProperties.GetAutomationId(control));
                _ui.Container.Children.Add(control);
            });
            await _ui.YieldToRenderAsync();

            await _ui.RunOnUIAsync(() =>
            {
                Assert.True(control!.IsLoaded);
                Assert.True(control.ActualWidth > 0);
                Assert.True(control.ActualHeight > 0);
                Assert.True(control.IsHitTestVisible);
                Assert.Equal(
                    AccessibilityView.Control,
                    AutomationProperties.GetAccessibilityView(control));
                Assert.Equal(
                    "ComposerReady",
                    AutomationProperties.GetAutomationId(control));
            });
        }
        finally
        {
            if (control is not null)
            {
                await _ui.RunOnUIAsync(() =>
                {
                    ComposerAutomationVisibility.Detach(control);
                    _ui.Container.Children.Remove(control);
                });
            }
        }
    }

}
