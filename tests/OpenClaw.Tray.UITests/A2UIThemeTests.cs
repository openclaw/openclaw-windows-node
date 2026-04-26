using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static OpenClaw.Tray.UITests.A2UI;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Tests for <c>beginRendering.styles</c> theme application. Three concerns:
///   1. <c>spacing</c> reaches the container renderers (Row/Column/List Spacing).
///   2. <c>radius</c> reaches the Card renderer (Border CornerRadius).
///   3. Color/font overrides land in the surface root's local resource scope as
///      <c>A2UIAccentBrush</c> / <c>A2UIForegroundBrush</c> / <c>A2UIFontFamily</c>
///      so renderers (or downstream styles) can pick them up via DynamicResource.
/// </summary>
[Collection(UICollection.Name)]
public sealed class A2UIThemeTests
{
    private readonly UIThreadFixture _ui;
    public A2UIThemeTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task Theme_SpacingOverride_AppliesToColumnStackPanel()
    {
        await _ui.PauseAsync("theme spacing → StackPanel.Spacing");
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            var harness = BuildHarness(_ui);
            harness.Router.Push(Surface(
                "s", "col",
                new[]
                {
                    Component("col", "Column", new() { ["children"] = Children("a", "b") }),
                    Component("a", "Text", new() { ["text"] = Lit("first") }),
                    Component("b", "Text", new() { ["text"] = Lit("second") }),
                },
                styles: Styles(spacing: 32)));


            var sp = FindLogical<StackPanel>(harness.LastSurface!.RootElement).Single();
            Assert.Equal(32, sp.Spacing);
        });
        await _ui.PauseAsync();
    }

    [Fact]
    public async Task Theme_RadiusOverride_AppliesToCardBorderCornerRadius()
    {
        await _ui.PauseAsync("theme radius → Border.CornerRadius");
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            var harness = BuildHarness(_ui);
            harness.Router.Push(Surface(
                "s", "card",
                new[]
                {
                    Component("card", "Card", new() { ["child"] = "tx" }),
                    Component("tx", "Text", new() { ["text"] = Lit("rounded") }),
                },
                styles: Styles(radius: 24)));


            var border = FindLogical<Border>(harness.LastSurface!.RootElement).First();
            Assert.Equal(24, border.CornerRadius.TopLeft);
            Assert.Equal(24, border.CornerRadius.TopRight);
            Assert.Equal(24, border.CornerRadius.BottomLeft);
            Assert.Equal(24, border.CornerRadius.BottomRight);
        });
        await _ui.PauseAsync();
    }

    [Fact]
    public async Task Theme_AccentColor_RegistersInSurfaceRootResources()
    {
        await _ui.PauseAsync("theme primaryColor → resource scope");
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            var harness = BuildHarness(_ui);
            harness.Router.Push(Surface(
                "s", "t",
                new[]
                {
                    Component("t", "Text", new() { ["text"] = Lit("themed") }),
                },
                styles: Styles(primaryColor: "#FF8800")));


            var root = harness.LastSurface!.RootElement;
            Assert.True(root.Resources.ContainsKey("A2UIAccentBrush"));
            var brush = root.Resources["A2UIAccentBrush"] as SolidColorBrush;
            Assert.NotNull(brush);
            Assert.Equal(0xFF, brush!.Color.R);
            Assert.Equal(0x88, brush.Color.G);
            Assert.Equal(0x00, brush.Color.B);
        });
        await _ui.PauseAsync();
    }

    [Fact]
    public async Task Theme_FontFamily_RegistersInSurfaceRootResources()
    {
        await _ui.PauseAsync("theme font → resource scope");
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            var harness = BuildHarness(_ui);
            harness.Router.Push(Surface(
                "s", "t",
                new[]
                {
                    Component("t", "Text", new() { ["text"] = Lit("typeset") }),
                },
                styles: Styles(font: "Cascadia Code")));


            var root = harness.LastSurface!.RootElement;
            Assert.True(root.Resources.ContainsKey("A2UIFontFamily"));
            var ff = root.Resources["A2UIFontFamily"] as FontFamily;
            Assert.NotNull(ff);
            Assert.Equal("Cascadia Code", ff!.Source);
        });
        await _ui.PauseAsync();
    }

    [Fact]
    public async Task Theme_NoStyles_LeavesSurfaceResourcesUntouched()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            var harness = BuildHarness(_ui);
            harness.Router.Push(Surface(
                "s", "t",
                new[] { Component("t", "Text", new() { ["text"] = Lit("plain") }) }));


            var root = harness.LastSurface!.RootElement;
            Assert.False(root.Resources.ContainsKey("A2UIAccentBrush"));
            Assert.False(root.Resources.ContainsKey("A2UIFontFamily"));
        });
    }
}
