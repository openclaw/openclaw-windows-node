using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.FunctionalUI.Hosting;
using Windows.UI;
using static OpenClawTray.FunctionalUI.Factories;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Regression proof that <see cref="Theme.EnsureThemeCallback"/> and the
/// FunctionalUI <c>TrackThemedBrushes</c> mechanism do not accumulate event
/// handlers across repeated render passes. Before the fix, subscribing
/// <c>ActualThemeChanged</c> inside a <c>.Set()</c> lambda added a new handler
/// on every render, causing linear handler growth per keystroke in the chat
/// composer.
/// </summary>
[Collection(UICollection.Name)]
public sealed class ThemeCallbackAccumulationTests
{
    private readonly UIThreadFixture _ui;
    public ThemeCallbackAccumulationTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task EnsureThemeCallback_100Renders_SingleStoredCallback()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var button = new Button();
            var counter = new int[1]; // reference type to avoid closure issues

            // Simulate 100 render passes.
            for (var i = 0; i < 100; i++)
            {
                Theme.EnsureThemeCallback(button, () => counter[0]++);
            }

            // Immediate invocation fires once per call.
            Assert.Equal(100, counter[0]);

            // Verify the ConditionalWeakTable stores exactly one entry.
            Assert.True(Theme.ThemeCallbackTable.TryGetValue(button, out var box),
                "ThemeCallbackTable must contain the control after EnsureThemeCallback.");

            // Manually invoke the stored callback: must fire exactly once.
            counter[0] = 0;
            box!.Value!();
            Assert.Equal(1, counter[0]);
        });
    }

    [Fact]
    public async Task EnsureThemeCallback_LatestState_StoredCorrectly()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var button = new Button();
            var result = new int[] { -1 };

            // Simulate renders with changing state.
            for (var i = 0; i < 50; i++)
            {
                var captured = i;
                Theme.EnsureThemeCallback(button, () => result[0] = captured);
            }

            // Manually fire the stored callback — must use LATEST state (49).
            result[0] = -1;
            Assert.True(Theme.ThemeCallbackTable.TryGetValue(button, out var box));
            box!.Value!();
            Assert.Equal(49, result[0]);
        });
    }

    [Fact]
    public async Task TrackThemedBrushes_ControlBackground_ReRendersWithoutAccumulation()
    {
        await _ui.RunOnUIAsync(() =>
        {
            // Seed a test brush so BackgroundResource resolution succeeds.
            var resources = Application.Current.Resources;
            resources["ThemeCallbackTestBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Red);

            var renderer = new UiRenderer(() => { });
            var effects = new System.Collections.Generic.List<System.Action>();

            // Render the same keyed Button 100 times with a BackgroundResource
            // modifier (simulating repeated re-renders of the same component tree).
            for (var i = 0; i < 100; i++)
            {
                effects.Clear();
                var element = Button(Empty(), () => { })
                    .BackgroundResource("ThemeCallbackTestBrush");
                renderer.Render(element, "root", effects);
                foreach (var fx in effects) fx();
            }

            // The rendered Button should exist and have the correct background.
            effects.Clear();
            var rendered = renderer.Render(
                Button(Empty(), () => { }).BackgroundResource("ThemeCallbackTestBrush"),
                "root",
                effects);

            Assert.IsType<Button>(rendered);
            var btn = (Button)rendered;
            Assert.IsType<SolidColorBrush>(btn.Background);
            Assert.Equal(Microsoft.UI.Colors.Red, ((SolidColorBrush)btn.Background).Color);

            renderer.Dispose();
        });
    }
}
