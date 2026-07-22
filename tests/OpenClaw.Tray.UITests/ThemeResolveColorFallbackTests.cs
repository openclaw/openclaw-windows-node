using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using Windows.UI;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Regression proof for <see cref="Theme.ResolveColor(string, ElementTheme)"/>.
///
/// WinUI color tokens (e.g. <c>TextFillColorSecondary</c>) are paired with a
/// <c>&lt;key&gt;Brush</c> SolidColorBrush. Minimal hosts - including these UI
/// test surfaces, which seed only the brush form via
/// <see cref="TestApp.EnsureFluentBrushFallbacks"/> - do not register the raw
/// color token. Before the fallback existed, resolving such a token threw mid
/// render and aborted the whole component mount (the chat timeline then rendered
/// no ItemsRepeater), which is why the chat proof tests started failing.
///
/// This locks in the fallback so a token whose only registered form is the
/// paired brush resolves to that brush's color instead of throwing.
/// </summary>
[Collection(UICollection.Name)]
public sealed class ThemeResolveColorFallbackTests
{
    private readonly UIThreadFixture _ui;
    public ThemeResolveColorFallbackTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task ResolveColor_FallsBackToPairedBrushColor_WhenOnlyBrushRegistered()
    {
        await _ui.RunOnUIAsync(() =>
        {
            // Register ONLY the paired brush form (no raw color token) at the
            // application root, mirroring how the minimal test host and other
            // brush-only surfaces are set up.
            var expected = Color.FromArgb(0xFF, 0x12, 0x34, 0x56);
            var resources = Application.Current.Resources;
            resources["ProofOnlyBrushTokenBrush"] = new SolidColorBrush(expected);
            Assert.False(resources.ContainsKey("ProofOnlyBrushToken"));

            // Must not throw, and must resolve to the paired brush's color.
            var resolved = Theme.ResolveColor("ProofOnlyBrushToken", ElementTheme.Default);
            Assert.Equal(expected, resolved);
        });
    }

    [Fact]
    public async Task ResolveColor_Throws_WhenNeitherColorNorPairedBrushRegistered()
    {
        await _ui.RunOnUIAsync(() =>
        {
            // A key with no color token and no "<key>Brush" companion must still
            // surface as a clear error rather than silently returning a wrong color.
            Assert.Throws<System.InvalidOperationException>(
                () => Theme.ResolveColor("NoSuchThemeTokenXyz", ElementTheme.Default));
        });
    }
}
