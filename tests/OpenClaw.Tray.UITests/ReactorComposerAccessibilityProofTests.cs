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
    public async Task ComposerAutomationVisibility_PreventsHitTestingBeforeLayoutIsUsable()
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

            });
        }
        finally
        {
            if (control is not null)
            {
                await _ui.RunOnUIAsync(() =>
                {
                    ComposerAutomationVisibility.Detach(control);
                });
            }
        }
    }

}
