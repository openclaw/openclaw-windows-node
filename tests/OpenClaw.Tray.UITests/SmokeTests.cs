using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SmokeTests
{
    private readonly UIThreadFixture _ui;
    public SmokeTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task Fixture_BootsApplicationAndWindow()
    {
        await _ui.RunOnUIAsync(() =>
        {
            Assert.NotNull(Application.Current);
            Assert.NotNull(_ui.TestWindow);
            Assert.NotNull(_ui.Container);
        });
    }

    [Fact]
    public async Task Container_AcceptsTextBlockAndLaysOut()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            var tb = new TextBlock { Text = "hello" };
            _ui.Container.Children.Add(tb);

            // Force a layout pass so we can measure that the element is live.
            _ui.Container.UpdateLayout();
            Assert.Equal("hello", tb.Text);
            Assert.NotNull(tb.XamlRoot); // attached to a real XamlRoot
        });
    }

    [Fact]
    public async Task ApplicationResources_HasBodyTextBlockStyle()
    {
        // Sanity: the renderers look up XamlControlsResources keys; if this
        // fails, A2UI tests will silently render unstyled text.
        await _ui.RunOnUIAsync(() =>
        {
            var res = Application.Current.Resources["BodyTextBlockStyle"];
            Assert.IsAssignableFrom<Style>(res);
        });
    }

}
