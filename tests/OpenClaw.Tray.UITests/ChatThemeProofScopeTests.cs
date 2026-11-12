using Microsoft.UI.Xaml;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ChatThemeProofScopeTests(UIThreadFixture ui)
{
    [Fact]
    public async Task RepeatedScopesReuseStableProductionResourceDictionaries()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            var original = Application.Current.Resources;
            using (var light = new ChatThemeProofScope("Light"))
            {
                var standard = Application.Current.Resources;
                Assert.Equal(ElementTheme.Light, light.ElementTheme);

                using (var dark = new ChatThemeProofScope("Dark"))
                {
                    Assert.Same(standard, Application.Current.Resources);
                    Assert.Equal(ElementTheme.Dark, dark.ElementTheme);
                }
                Assert.Same(standard, Application.Current.Resources);

                using (var highContrast = new ChatThemeProofScope("HighContrast"))
                {
                    var contrast = Application.Current.Resources;
                    Assert.NotSame(standard, contrast);

                    using var repeatedHighContrast = new ChatThemeProofScope("HighContrast");
                    Assert.Same(contrast, Application.Current.Resources);
                }
                Assert.Same(standard, Application.Current.Resources);
            }
            Assert.Same(original, Application.Current.Resources);
        });
    }
}
