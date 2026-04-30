using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using static OpenClaw.Tray.UITests.A2UI;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// One realistic, ~60-component dashboard surface that exercises every
/// catalog component, four levels of nesting, theming, and a live
/// dataModelUpdate after the initial render. The point of this test is
/// breadth, not depth: if it passes, the renderer pipeline produces *something
/// reasonable* for every supported wire input shape.
/// </summary>
[Collection(UICollection.Name)]
public sealed class A2UIDashboardScaleTest
{
    private readonly UIThreadFixture _ui;
    public A2UIDashboardScaleTest(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task Dashboard_AllControlTypes_RendersExpectedTreeWithoutUnknownPlaceholders()
    {
        var jsonl = BuildDashboardJsonl();

        await _ui.PauseAsync("Dashboard at scale (~60 components)");
        await _ui.ResetContainerAsync();
        var harness = await _ui.RunOnUIAsync(() =>
        {
            var h = BuildHarness(_ui);
            h.Router.Push(jsonl);
            Assert.NotNull(h.LastSurface);
            return Task.FromResult(h);
        });

        // Tree walking + assertions all happen on the UI thread (WinUI types
        // are thread-affine; cross-thread access throws COMException).
        await _ui.RunOnUIAsync(() =>
        {
            var root = harness.LastSurface!.RootElement;

            var grid = root as Grid;
            Assert.NotNull(grid);
            Assert.True(grid!.Children.Count > 0, "surface root Grid is empty — BeginRendering didn't produce a tree");

            // ── Structure: many Columns, multiple Rows ─
            var allStackPanels = FindLogical<StackPanel>(root).ToList();
            var allRows    = allStackPanels.Where(s => s.Orientation == Orientation.Horizontal).ToList();
            var allColumns = allStackPanels.Where(s => s.Orientation == Orientation.Vertical).ToList();
            Assert.True(allColumns.Count >= 5, $"expected ≥5 columns, got {allColumns.Count}");
            Assert.True(allRows.Count    >= 3, $"expected ≥3 rows, got {allRows.Count}");

            // ── Tabs: one TabView, three tab items with the right headers ─
            var tabView = FindLogical<TabView>(root).Single();
            var headers = tabView.TabItems.OfType<TabViewItem>().Select(t => t.Header as string).ToArray();
            Assert.Equal(new[] { "Overview", "Settings", "Help" }, headers);

            // ── Each catalog component appears at least once ─
            Assert.NotEmpty(FindLogical<FontIcon>(root));                  // Icon
            Assert.NotEmpty(FindLogical<TextBlock>(root));                 // Text
            Assert.NotEmpty(FindLogical<Border>(root));                    // Card
            Assert.NotEmpty(FindLogical<ScrollViewer>(root));              // List
            // Modal renders as a trigger Button hosting the entry-point child;
            // clicking it shows a ContentDialog. We don't assert a Modal-specific
            // type here — its presence is reflected in the Button count below.
            Assert.NotEmpty(FindLogical<Rectangle>(root));                 // Divider
            Assert.NotEmpty(FindLogical<Button>(root));                    // Button
            Assert.NotEmpty(FindLogical<CheckBox>(root));                  // CheckBox
            Assert.NotEmpty(FindLogical<TextBox>(root));                   // TextField shortText / longText
            Assert.NotEmpty(FindLogical<PasswordBox>(root));               // TextField obscured
            Assert.NotEmpty(FindLogical<CalendarDatePicker>(root));        // DateTimeInput (date)
            Assert.NotEmpty(FindLogical<TimePicker>(root));                // DateTimeInput (time)
            Assert.NotEmpty(FindLogical<ComboBox>(root));                  // MultipleChoice (max=1)
            Assert.NotEmpty(FindLogical<ListView>(root));                  // MultipleChoice (max>1)
            Assert.NotEmpty(FindLogical<Slider>(root));                    // Slider
            Assert.NotEmpty(FindLogical<MediaPlayerElement>(root));        // Video / AudioPlayer

            // ── Specific counts on the items we know we declared ─
            var sliders = FindLogical<Slider>(root).ToList();
            Assert.Equal(3, sliders.Count); // 2 in Overview card + 1 in Settings (Volume)

            var buttons = FindLogical<Button>(root).ToList();
            // Header refresh (1) + 3 in Help row (3) + Modal trigger Button (1)
            // wrapping its entry-point Button (1) = 6. The Modal now wraps its
            // entry-point in a uniform click target, so the entry Button shows
            // up nested inside the trigger.
            Assert.Equal(6, buttons.Count);

            var textBoxes = FindLogical<TextBox>(root).ToList();
            Assert.Equal(2, textBoxes.Count); // shortText "Display name" + longText "Bio"
            Assert.Single(FindLogical<PasswordBox>(root));

            // ── No UnknownRenderer placeholders ─
            var unsupported = FindLogical<TextBlock>(root)
                .Where(t => t.Text.StartsWith("Unsupported component:")).ToList();
            Assert.Empty(unsupported);

            // ── Theme reached the renderers ─
            // The top-level Column (root.Children[0]) is built by ColumnRenderer
            // and reads its Spacing from theme.Spacing. Some inner StackPanels
            // (AudioPlayer's, etc.) intentionally use their own hard-coded
            // spacing — we don't assert those.
            var rootColumn = (StackPanel)grid.Children[0];
            Assert.Equal(Orientation.Vertical, rootColumn.Orientation);
            Assert.Equal(12, rootColumn.Spacing);

            // Radius=10 → Card border has CornerRadius=10. Card sets Padding=16
            // (Border + Padding > 0 distinguishes it from ContentPresenter borders
            // inside templated controls).
            var cardBorder = FindLogical<Border>(root)
                .First(b => b.CornerRadius.TopLeft > 0 && b.Padding.Left > 0);
            Assert.Equal(10, cardBorder.CornerRadius.TopLeft);

            // Accent registered as a surface-scoped resource.
            Assert.True(root.Resources.ContainsKey("A2UIAccentBrush"));
        });

        // ── Live update propagates: change /name and /volume after first render ─
        await _ui.PauseAsync("data update → bound text + slider refresh");
        await _ui.RunOnUIAsync(() =>
        {
            var root = harness.LastSurface!.RootElement;

            harness.Router.Push(DataUpdate("dash",
                ("name",   JsonValue.Create("Grace Hopper")),
                ("volume", JsonValue.Create(88.0))));

            var nameField = FindLogical<TextBox>(root).Single(t => t.Header as string == "Display name");
            Assert.Equal("Grace Hopper", nameField.Text);

            // Settings/Volume is the last Slider declared in source order. We use
            // the last one rather than re-finding by some identifier because the
            // wire format doesn't expose component IDs on the resulting controls.
            var volSlider = FindLogical<Slider>(root).Last();
            Assert.Equal(88, volSlider.Value);
        });
        await _ui.PauseAsync();
    }

    /// <summary>
    /// The dashboard fixture itself. Returned as a JSONL string ready to push
    /// to <see cref="OpenClawTray.A2UI.Hosting.A2UIRouter.Push"/>.
    /// </summary>
    private static string BuildDashboardJsonl()
    {
        var components = new List<JsonObject>();

        // ── Top-level layout ──────────────────────────────────────────────
        components.Add(Component("root", "Column", new()
        {
            ["children"] = Children("hdr", "tabs", "ftr"),
        }));

        // ── Header row ────────────────────────────────────────────────────
        components.Add(Component("hdr", "Row", new()
        {
            ["children"] = Children("hdrIcon", "hdrTitle", "hdrDiv", "hdrBtn"),
            ["alignment"] = "center",
        }));
        components.Add(Component("hdrIcon", "Icon", new() { ["name"] = Lit("home") }));
        components.Add(Component("hdrTitle", "Text", new()
        {
            ["text"] = Lit("OpenClaw Dashboard"),
            ["usageHint"] = "h1",
        }));
        components.Add(Component("hdrDiv", "Divider", new() { ["axis"] = "vertical" }));
        components.Add(Component("hdrBtn", "Button", new()
        {
            ["primary"] = true,
            ["child"] = "hdrBtnLbl",
            ["action"] = new JsonObject { ["name"] = "refresh" },
        }));
        components.Add(Component("hdrBtnLbl", "Text", new() { ["text"] = Lit("Refresh") }));

        // ── Tabs ──────────────────────────────────────────────────────────
        components.Add(Component("tabs", "Tabs", new()
        {
            ["tabItems"] = new JsonArray
            {
                Tab("Overview", "ovRoot"),
                Tab("Settings", "stRoot"),
                Tab("Help",     "hpRoot"),
            },
        }));

        // ── Tab 1: Overview ─────────────────────────────────────────
        components.Add(Component("ovRoot", "Column", new()
        {
            ["children"] = Children("ovCard", "ovDiv", "ovList", "ovMedia"),
        }));
        components.Add(Component("ovCard", "Card", new() { ["child"] = "ovCardCol" }));
        components.Add(Component("ovCardCol", "Column", new()
        {
            ["children"] = Children("ovH2", "ovCap", "ovSlide1", "ovSlide2"),
        }));
        components.Add(Component("ovH2",  "Text", new() { ["text"] = Lit("System Status"), ["usageHint"] = "h2" }));
        components.Add(Component("ovCap", "Text", new() { ["text"] = Lit("Last updated: just now"), ["usageHint"] = "caption" }));
        components.Add(Component("ovSlide1", "Slider", new() { ["minValue"] = 0.0, ["maxValue"] = 100.0, ["value"] = Lit(42.0) }));
        components.Add(Component("ovSlide2", "Slider", new() { ["minValue"] = 0.0, ["maxValue"] = 100.0, ["value"] = Lit(75.0) }));

        components.Add(Component("ovDiv", "Divider", new() { ["axis"] = "horizontal" }));

        components.Add(Component("ovList", "List", new()
        {
            ["direction"] = "vertical",
            ["children"] = Children("svcA", "svcB", "svcC", "svcD", "svcE"),
        }));
        components.Add(Component("svcA", "Text", new() { ["text"] = Lit("service-a: running") }));
        components.Add(Component("svcB", "Text", new() { ["text"] = Lit("service-b: running") }));
        components.Add(Component("svcC", "Text", new() { ["text"] = Lit("service-c: running") }));
        components.Add(Component("svcD", "Text", new() { ["text"] = Lit("service-d: stopped") }));
        components.Add(Component("svcE", "Text", new() { ["text"] = Lit("service-e: running") }));

        components.Add(Component("ovMedia", "Row", new() { ["children"] = Children("ovImg", "ovAudio") }));
        components.Add(Component("ovImg",   "Image", new() { ["url"] = Lit("data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=") }));
        components.Add(Component("ovAudio", "AudioPlayer", new()
        {
            ["url"]         = Lit("https://example.com/clip.mp3"),
            ["description"] = Lit("Notification chime"),
        }));

        // ── Tab 2: Settings ─────────────────────────────────────────
        components.Add(Component("stRoot", "Column", new()
        {
            ["children"] = Children("stTitle", "stName", "stBio", "stToken", "stNotify", "stTheme", "stTags", "stSched", "stVol"),
        }));
        components.Add(Component("stTitle", "Text", new() { ["text"] = Lit("User Preferences"), ["usageHint"] = "h3" }));
        components.Add(Component("stName",  "TextField", new()
        {
            ["textFieldType"] = "shortText",
            ["label"] = Lit("Display name"),
            ["text"] = Path("/name"),
        }));
        components.Add(Component("stBio", "TextField", new()
        {
            ["textFieldType"] = "longText",
            ["label"] = Lit("Bio"),
            ["text"] = Path("/bio"),
        }));
        components.Add(Component("stToken", "TextField", new()
        {
            ["textFieldType"] = "obscured",
            ["label"] = Lit("API token"),
            ["text"] = Path("/token"),
        }));
        components.Add(Component("stNotify", "CheckBox", new()
        {
            ["label"] = Lit("Enable notifications"),
            ["value"] = Path("/notify"),
        }));
        components.Add(Component("stTheme", "MultipleChoice", new()
        {
            ["maxAllowedSelections"] = 1,
            ["selections"] = Path("/theme"),
            ["options"] = new JsonArray
            {
                Option("Light", "light"),
                Option("Dark",  "dark"),
                Option("Auto",  "auto"),
            },
        }));
        components.Add(Component("stTags", "MultipleChoice", new()
        {
            ["maxAllowedSelections"] = 3,
            ["selections"] = Path("/tags"),
            ["options"] = new JsonArray
            {
                Option("alpha", "a"),
                Option("beta",  "b"),
                Option("gamma", "g"),
                Option("delta", "d"),
            },
        }));
        components.Add(Component("stSched", "DateTimeInput", new()
        {
            ["enableDate"] = true,
            ["enableTime"] = true,
            ["value"] = Path("/scheduled"),
        }));
        components.Add(Component("stVol", "Slider", new()
        {
            ["minValue"] = 0.0,
            ["maxValue"] = 100.0,
            ["value"] = Path("/volume"),
        }));

        // ── Tab 3: Help ─────────────────────────────────────────────
        components.Add(Component("hpRoot", "Column", new()
        {
            ["children"] = Children("hpH2", "hpBody", "hpModal", "hpRow"),
        }));
        components.Add(Component("hpH2",   "Text", new() { ["text"] = Lit("Need help?"), ["usageHint"] = "h2" }));
        components.Add(Component("hpBody", "Text", new() { ["text"] = Lit("Check the documentation or contact support."), ["usageHint"] = "body" }));
        components.Add(Component("hpModal", "Modal", new()
        {
            ["entryPointChild"] = "hpModalEntry",
            ["contentChild"]    = "hpModalContent",
        }));
        components.Add(Component("hpModalEntry",   "Button", new()
        {
            ["child"]  = "hpModalEntryLbl",
            ["action"] = new JsonObject { ["name"] = "showDetails" },
        }));
        components.Add(Component("hpModalEntryLbl", "Text", new() { ["text"] = Lit("Show details") }));
        components.Add(Component("hpModalContent", "Text", new() { ["text"] = Lit("Detailed help text would go here.") }));

        components.Add(Component("hpRow", "Row", new() { ["children"] = Children("hpDocs", "hpContact", "hpAbout") }));
        components.Add(Component("hpDocs",    "Button", new() { ["child"] = "hpDocsLbl",    ["primary"] = true, ["action"] = new JsonObject { ["name"] = "openDocs" } }));
        components.Add(Component("hpContact", "Button", new() { ["child"] = "hpContactLbl",                      ["action"] = new JsonObject { ["name"] = "contact" } }));
        components.Add(Component("hpAbout",   "Button", new() { ["child"] = "hpAboutLbl",                        ["action"] = new JsonObject { ["name"] = "about" } }));
        components.Add(Component("hpDocsLbl",    "Text", new() { ["text"] = Lit("Open Docs") }));
        components.Add(Component("hpContactLbl", "Text", new() { ["text"] = Lit("Contact") }));
        components.Add(Component("hpAboutLbl",   "Text", new() { ["text"] = Lit("About") }));

        // ── Footer row ────────────────────────────────────────────────────
        components.Add(Component("ftr", "Row", new() { ["children"] = Children("ftrVer", "ftrDiv", "ftrConn") }));
        components.Add(Component("ftrVer",  "Text", new() { ["text"] = Lit("v0.4.4"),   ["usageHint"] = "caption" }));
        components.Add(Component("ftrDiv",  "Divider", new() { ["axis"] = "vertical" }));
        components.Add(Component("ftrConn", "Text", new() { ["text"] = Lit("Connected"), ["usageHint"] = "caption" }));

        return Surface(
            "dash",
            "root",
            components,
            styles: Styles(spacing: 12, radius: 10, primaryColor: "#0078D4"));
    }
}
