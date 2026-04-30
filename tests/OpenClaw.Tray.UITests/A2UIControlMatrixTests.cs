using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using OpenClawTray.A2UI.Hosting;
using static OpenClaw.Tray.UITests.A2UI;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// One test per A2UI v0.8 catalog component: verify each name in
/// <see cref="OpenClawTray.A2UI.Rendering.ComponentRendererRegistry.BuildDefault"/>
/// produces the expected XAML control type with the expected properties.
///
/// Each test follows the same pattern: build a minimal surface containing just
/// the component under test, render it, and assert against the visual tree.
/// </summary>
[Collection(UICollection.Name)]
public sealed class A2UIControlMatrixTests
{
    private readonly UIThreadFixture _ui;
    public A2UIControlMatrixTests(UIThreadFixture ui) => _ui = ui;

    // ─── Containers ──────────────────────────────────────────────────────────

    [Fact]
    public Task Row_RendersHorizontalStackPanel_WithChildren() => RunAsync(
        "Row → StackPanel(Horizontal)",
        Surface("s", "r", new[]
        {
            Component("r", "Row", new() { ["children"] = Children("a", "b") }),
            Component("a", "Text", new() { ["text"] = Lit("A") }),
            Component("b", "Text", new() { ["text"] = Lit("B") }),
        }),
        root =>
        {
            var sp = FindLogical<StackPanel>(root).Single();
            Assert.Equal(Orientation.Horizontal, sp.Orientation);
            Assert.Equal(new[] { "A", "B" }, FindLogical<TextBlock>(sp).Select(t => t.Text).ToArray());
        });

    [Fact]
    public Task ListVertical_RendersScrollViewer_WrappingItemsRepeater() => RunAsync(
        "List vertical → ScrollViewer(ItemsRepeater + vertical StackLayout)",
        Surface("s", "lst", new[]
        {
            Component("lst", "List", new()
            {
                ["direction"] = "vertical",
                ["children"] = Children("i1", "i2", "i3"),
            }),
            Component("i1", "Text", new() { ["text"] = Lit("one") }),
            Component("i2", "Text", new() { ["text"] = Lit("two") }),
            Component("i3", "Text", new() { ["text"] = Lit("three") }),
        }),
        root =>
        {
            // List virtualizes via ItemsRepeater. Assert the wrapper shape and
            // layout orientation; children realize on layout pass and aren't
            // present in the unmounted visual tree.
            var sv = FindLogical<ScrollViewer>(root).Single();
            Assert.Equal(ScrollMode.Auto, sv.VerticalScrollMode);
            Assert.Equal(ScrollMode.Disabled, sv.HorizontalScrollMode);

            var repeater = (ItemsRepeater)sv.Content;
            var layout = (StackLayout)repeater.Layout;
            Assert.Equal(Orientation.Vertical, layout.Orientation);
            // ItemsSource carries the explicit children list — same source the
            // template realizes from.
            var ids = ((IEnumerable<string>)repeater.ItemsSource!).ToArray();
            Assert.Equal(new[] { "i1", "i2", "i3" }, ids);
        });

    [Fact]
    public Task ListHorizontal_RendersScrollViewer_WrappingItemsRepeater() => RunAsync(
        "List horizontal → ScrollViewer(ItemsRepeater + horizontal StackLayout)",
        Surface("s", "lst", new[]
        {
            Component("lst", "List", new()
            {
                ["direction"] = "horizontal",
                ["children"] = Children("a", "b"),
            }),
            Component("a", "Text", new() { ["text"] = Lit("a") }),
            Component("b", "Text", new() { ["text"] = Lit("b") }),
        }),
        root =>
        {
            var sv = FindLogical<ScrollViewer>(root).Single();
            Assert.Equal(ScrollMode.Auto, sv.HorizontalScrollMode);
            Assert.Equal(ScrollMode.Disabled, sv.VerticalScrollMode);

            var repeater = (ItemsRepeater)sv.Content;
            var layout = (StackLayout)repeater.Layout;
            Assert.Equal(Orientation.Horizontal, layout.Orientation);
            var ids = ((IEnumerable<string>)repeater.ItemsSource!).ToArray();
            Assert.Equal(new[] { "a", "b" }, ids);
        });

    [Fact]
    public Task Card_RendersBorder_WithSingleChild() => RunAsync(
        "Card → Border",
        Surface("s", "card", new[]
        {
            Component("card", "Card", new() { ["child"] = "inner" }),
            Component("inner", "Text", new() { ["text"] = Lit("inside the card") }),
        }),
        root =>
        {
            var border = FindLogical<Border>(root).First();
            Assert.True(border.CornerRadius.TopLeft > 0);
            // The Text component is the border's child.
            var tb = FindLogical<TextBlock>(border).Single();
            Assert.Equal("inside the card", tb.Text);
        });

    [Fact]
    public Task Tabs_RendersTabView_WithExpectedHeadersAndContent() => RunAsync(
        "Tabs → TabView",
        Surface("s", "tabs", new[]
        {
            Component("tabs", "Tabs", new()
            {
                ["tabItems"] = new System.Text.Json.Nodes.JsonArray
                {
                    Tab("Overview", "tabA"),
                    Tab("Details",  "tabB"),
                    Tab("Help",     "tabC"),
                },
            }),
            Component("tabA", "Text", new() { ["text"] = Lit("body of overview") }),
            Component("tabB", "Text", new() { ["text"] = Lit("body of details") }),
            Component("tabC", "Text", new() { ["text"] = Lit("body of help") }),
        }),
        root =>
        {
            var tv = FindLogical<TabView>(root).Single();
            var headers = tv.TabItems.OfType<TabViewItem>().Select(t => t.Header as string).ToArray();
            Assert.Equal(new[] { "Overview", "Details", "Help" }, headers);
            Assert.All(tv.TabItems.OfType<TabViewItem>(), tab => Assert.False(tab.IsClosable));
        });

    [Fact]
    public Task Modal_RendersTriggerButton_ContentDeferredUntilClick() => RunAsync(
        "Modal → Button trigger (ContentDialog on click)",
        Surface("s", "modal", new[]
        {
            Component("modal", "Modal", new()
            {
                ["entryPointChild"] = "trigger",
                ["contentChild"] = "body",
            }),
            Component("trigger", "Text", new() { ["text"] = Lit("Click me") }),
            Component("body",    "Text", new() { ["text"] = Lit("hidden body") }),
        }),
        root =>
        {
            // Modal now renders as a Button whose Content is the entry-point
            // child; clicking it opens a ContentDialog hosting the content
            // child. The body Text isn't in the visual tree until the dialog
            // is opened, so we assert the trigger label is realized but the
            // body label is not.
            var btn = FindLogical<Button>(root).Single();
            var labels = FindLogical<TextBlock>(root).Select(t => t.Text).ToArray();
            Assert.Contains("Click me", labels);
            Assert.DoesNotContain("hidden body", labels);
        });

    [Fact]
    public Task DividerHorizontal_RendersThinRectangle_WithHeight1() => RunAsync(
        "Divider horizontal → Rectangle h=1",
        Surface("s", "d", new[]
        {
            Component("d", "Divider", new() { ["axis"] = "horizontal" }),
        }),
        root =>
        {
            var rect = FindLogical<Rectangle>(root).Single();
            Assert.Equal(1, rect.Height);
            Assert.Equal(HorizontalAlignment.Stretch, rect.HorizontalAlignment);
        });

    [Fact]
    public Task DividerVertical_RendersThinRectangle_WithWidth1() => RunAsync(
        "Divider vertical → Rectangle w=1",
        Surface("s", "d", new[]
        {
            Component("d", "Divider", new() { ["axis"] = "vertical" }),
        }),
        root =>
        {
            var rect = FindLogical<Rectangle>(root).Single();
            Assert.Equal(1, rect.Width);
            Assert.Equal(VerticalAlignment.Stretch, rect.VerticalAlignment);
        });

    // ─── Display ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("h1", "TitleLargeTextBlockStyle")]
    [InlineData("h2", "TitleTextBlockStyle")]
    [InlineData("h3", "SubtitleTextBlockStyle")]
    [InlineData("h4", "BodyStrongTextBlockStyle")]
    [InlineData("caption", "CaptionTextBlockStyle")]
    [InlineData("body", "BodyTextBlockStyle")]
    public async Task Text_UsageHint_AppliesMatchingFluentTextStyle(string hint, string expectedStyleKey)
    {
        await _ui.PauseAsync($"Text usageHint {hint}");
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            var harness = BuildHarness(_ui);
            harness.Router.SurfaceCreated += (_, s) => _ui.Container.Children.Add(s.RootElement);
            harness.Router.Push(Surface("s", "t", new[]
            {
                Component("t", "Text", new()
                {
                    ["text"] = Lit($"styled {hint}"),
                    ["usageHint"] = hint,
                }),
            }));
            _ui.Container.UpdateLayout();

            var tb = FindLogical<TextBlock>(harness.LastSurface!.RootElement).Single();
            Assert.NotNull(tb.Style);

            // The renderer prefers the usage-hint-specific key but falls back to
            // BodyTextBlockStyle if that key isn't in this WinUI build's
            // XamlControlsResources. Assert "expected if present, fallback if not".
            if (Application.Current.Resources.TryGetValue(expectedStyleKey, out var expected))
            {
                Assert.Same(expected, tb.Style);
            }
            else
            {
                var fallback = (Style)Application.Current.Resources["BodyTextBlockStyle"];
                Assert.Same(fallback, tb.Style);
            }
        });
    }

    [Fact]
    public Task Icon_RendersFontIcon_WithSegoeFluentGlyph() => RunAsync(
        "Icon → FontIcon",
        Surface("s", "i", new[]
        {
            Component("i", "Icon", new() { ["name"] = Lit("search") }),
        }),
        root =>
        {
            var fi = FindLogical<FontIcon>(root).Single();
            Assert.Equal("Segoe Fluent Icons", fi.FontFamily.Source);
            // "search" maps to the Segoe MDL2 Search glyph (U+E721).
            Assert.Equal("", fi.Glyph);
        });

    [Theory]
    [InlineData("home",          "")] // Home
    [InlineData("settings",      "")] // Settings
    [InlineData("close",         "")] // Cancel
    [InlineData("check",         "")] // CheckMark
    [InlineData("warning",       "")] // Warning
    [InlineData("delete",        "")] // Delete
    [InlineData("notifications", "")] // Ringer
    [InlineData("noSuchIcon",    "")] // unknown name → Help fallback
    public Task Icon_NameMapsToExpectedGlyph(string name, string expectedGlyph) => RunAsync(
        $"Icon \"{name}\"",
        Surface("s", "i", new[]
        {
            Component("i", "Icon", new() { ["name"] = Lit(name) }),
        }),
        root =>
        {
            var fi = FindLogical<FontIcon>(root).Single();
            Assert.Equal(expectedGlyph, fi.Glyph);
        });

    [Fact]
    public Task Icon_EmptyName_FallsBackToHelpGlyph() => RunAsync(
        "Icon empty name",
        Surface("s", "i", new[]
        {
            Component("i", "Icon", new() { ["name"] = Lit("") }),
        }),
        root =>
        {
            var fi = FindLogical<FontIcon>(root).Single();
            Assert.Equal("", fi.Glyph); // Help
        });

    // ─── Interactive ─────────────────────────────────────────────────────────

    [Fact]
    public Task CheckBox_LiteralValueTrue_RendersChecked() => RunAsync(
        "CheckBox literal true",
        Surface("s", "cb", new[]
        {
            Component("cb", "CheckBox", new()
            {
                ["label"] = Lit("Agree"),
                ["value"] = Lit(true),
            }),
        }),
        root =>
        {
            var cb = FindLogical<CheckBox>(root).Single();
            Assert.Equal("Agree", cb.Content as string);
            Assert.True(cb.IsChecked);
        });

    [Fact]
    public async Task CheckBox_PathBound_DataModelWritesPropagateBothWays()
    {
        await _ui.PauseAsync("CheckBox ↔ data model");
        await _ui.ResetContainerAsync();
        var harness = await _ui.RunOnUIAsync(() =>
        {
            var h = BuildHarness(_ui);
            h.Router.Push(Surface("s", "cb", new[]
            {
                Component("cb", "CheckBox", new()
                {
                    ["label"] = Lit("Subscribe"),
                    ["value"] = Path("/subscribed"),
                }),
            }));
            return Task.FromResult(h);
        });

        await _ui.RunOnUIAsync(() =>
        {
            var cb = FindLogical<CheckBox>(harness.LastSurface!.RootElement).Single();
            Assert.False(cb.IsChecked); // no data yet → unchecked

            // Data → UI: set the path, expect IsChecked=true.
            harness.Router.Push(DataUpdate("s", ("subscribed", System.Text.Json.Nodes.JsonValue.Create(true))));
            Assert.True(cb.IsChecked);

            // UI → Data: programmatically uncheck, expect data model to flip back.
            cb.IsChecked = false;
            var node = harness.DataModel.Read("s", "/subscribed") as System.Text.Json.Nodes.JsonValue;
            Assert.NotNull(node);
            Assert.True(node!.TryGetValue<bool>(out var b) && b == false);
        });
        await _ui.PauseAsync();
    }

    [Fact]
    public Task TextField_ShortText_RendersSingleLineTextBox() => RunAsync(
        "TextField shortText → TextBox",
        Surface("s", "tf", new[]
        {
            Component("tf", "TextField", new()
            {
                ["textFieldType"] = "shortText",
                ["label"] = Lit("Name"),
                ["text"] = Lit("Ada"),
            }),
        }),
        root =>
        {
            var tb = FindLogical<TextBox>(root).Single();
            Assert.Equal("Name", tb.Header as string);
            Assert.Equal("Ada", tb.Text);
            Assert.False(tb.AcceptsReturn);
            Assert.Equal(TextWrapping.NoWrap, tb.TextWrapping);
        });

    [Fact]
    public Task TextField_LongText_RendersMultilineTextBox() => RunAsync(
        "TextField longText → multiline TextBox",
        Surface("s", "tf", new[]
        {
            Component("tf", "TextField", new()
            {
                ["textFieldType"] = "longText",
                ["label"] = Lit("Notes"),
            }),
        }),
        root =>
        {
            var tb = FindLogical<TextBox>(root).Single();
            Assert.True(tb.AcceptsReturn);
            Assert.Equal(TextWrapping.Wrap, tb.TextWrapping);
            Assert.True(tb.MinHeight >= 80);
        });

    [Fact]
    public Task TextField_Obscured_RendersPasswordBox() => RunAsync(
        "TextField obscured → PasswordBox",
        Surface("s", "tf", new[]
        {
            Component("tf", "TextField", new()
            {
                ["textFieldType"] = "obscured",
                ["label"] = Lit("PIN"),
                ["text"] = Lit("1234"),
            }),
        }),
        root =>
        {
            var pb = FindLogical<PasswordBox>(root).Single();
            Assert.Equal("PIN", pb.Header as string);
            Assert.Equal("1234", pb.Password);
            // No TextBox should be present when obscured.
            Assert.Empty(FindLogical<TextBox>(root));
        });

    [Fact]
    public Task DateTimeInput_DateAndTime_RendersBothPickers() => RunAsync(
        "DateTimeInput date+time",
        Surface("s", "dt", new[]
        {
            Component("dt", "DateTimeInput", new()
            {
                ["enableDate"] = true,
                ["enableTime"] = true,
            }),
        }),
        root =>
        {
            Assert.Single(FindLogical<CalendarDatePicker>(root));
            Assert.Single(FindLogical<TimePicker>(root));
        });

    [Fact]
    public Task DateTimeInput_DateOnly_RendersOnlyCalendarPicker() => RunAsync(
        "DateTimeInput date-only",
        Surface("s", "dt", new[]
        {
            Component("dt", "DateTimeInput", new()
            {
                ["enableDate"] = true,
                ["enableTime"] = false,
            }),
        }),
        root =>
        {
            Assert.Single(FindLogical<CalendarDatePicker>(root));
            Assert.Empty(FindLogical<TimePicker>(root));
        });

    [Fact]
    public Task MultipleChoice_Single_RendersComboBox_WithOptions() => RunAsync(
        "MultipleChoice (max=1) → ComboBox",
        Surface("s", "mc", new[]
        {
            Component("mc", "MultipleChoice", new()
            {
                ["maxAllowedSelections"] = 1,
                ["options"] = new System.Text.Json.Nodes.JsonArray
                {
                    Option("Light", "light"),
                    Option("Dark", "dark"),
                    Option("Auto", "auto"),
                },
            }),
        }),
        root =>
        {
            var combo = FindLogical<ComboBox>(root).Single();
            var labels = combo.Items.OfType<ComboBoxItem>().Select(i => i.Content as string).ToArray();
            Assert.Equal(new[] { "Light", "Dark", "Auto" }, labels);
        });

    [Fact]
    public Task MultipleChoice_Multi_RendersListView_WithMultipleSelectionMode() => RunAsync(
        "MultipleChoice (max=3) → ListView",
        Surface("s", "mc", new[]
        {
            Component("mc", "MultipleChoice", new()
            {
                ["maxAllowedSelections"] = 3,
                ["options"] = new System.Text.Json.Nodes.JsonArray
                {
                    Option("Red",   "r"),
                    Option("Green", "g"),
                    Option("Blue",  "b"),
                    Option("Alpha", "a"),
                },
            }),
        }),
        root =>
        {
            var lv = FindLogical<ListView>(root).Single();
            Assert.Equal(ListViewSelectionMode.Multiple, lv.SelectionMode);
            Assert.Equal(4, lv.Items.Count);
        });

    [Fact]
    public Task Slider_LiteralValue_AppliesRangeAndValue() => RunAsync(
        "Slider literal value",
        Surface("s", "sl", new[]
        {
            Component("sl", "Slider", new()
            {
                ["minValue"] = 0.0,
                ["maxValue"] = 200.0,
                ["value"] = Lit(42.0),
            }),
        }),
        root =>
        {
            var slider = FindLogical<Slider>(root).Single();
            Assert.Equal(0, slider.Minimum);
            Assert.Equal(200, slider.Maximum);
            Assert.Equal(42, slider.Value);
        });

    [Fact]
    public async Task Slider_PathBound_UserChangeWritesToDataModel()
    {
        await _ui.PauseAsync("Slider ↔ data model");
        await _ui.ResetContainerAsync();
        var harness = await _ui.RunOnUIAsync(() =>
        {
            var h = BuildHarness(_ui);
            h.Router.Push(Surface("s", "sl", new[]
            {
                Component("sl", "Slider", new()
                {
                    ["minValue"] = 0.0,
                    ["maxValue"] = 100.0,
                    ["value"] = Path("/volume"),
                }),
            }));
            h.Router.Push(DataUpdate("s", ("volume", System.Text.Json.Nodes.JsonValue.Create(25.0))));
            return Task.FromResult(h);
        });

        await _ui.RunOnUIAsync(() =>
        {
            var slider = FindLogical<Slider>(harness.LastSurface!.RootElement).Single();
            Assert.Equal(25, slider.Value);

            // Simulate the user dragging the slider — the renderer wires ValueChanged
            // back to DataModel.Write.
            slider.Value = 73;
            var node = harness.DataModel.Read("s", "/volume") as System.Text.Json.Nodes.JsonValue;
            Assert.NotNull(node);
            Assert.True(node!.TryGetValue<double>(out var d) && d == 73);
        });
        await _ui.PauseAsync();
    }

    // ─── helper ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One-shot run for matrix tests: pause label, render the JSONL, run the
    /// caller-supplied assertion against the SurfaceHost's logical tree.
    ///
    /// We deliberately do NOT mount the surface in the fixture's Window
    /// container: templated controls (TextBox, PasswordBox, ListView) hit PRI
    /// "file not found" lookups during template apply in unpackaged WinUI3
    /// test processes. Walking the renderer-produced logical tree
    /// (Panel.Children, Border.Child, ContentControl.Content, etc.) is
    /// sufficient to verify each catalog component's output, and avoids the
    /// hosting limitation entirely.
    /// </summary>
    private async Task RunAsync(string pauseLabel, string jsonl, System.Action<FrameworkElement> assertOnRoot)
    {
        await _ui.PauseAsync(pauseLabel);
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            var harness = BuildHarness(_ui);
            harness.Router.Push(jsonl);
            Assert.NotNull(harness.LastSurface);
            assertOnRoot(harness.LastSurface!.RootElement);
        });
        await _ui.PauseAsync();
    }
}
