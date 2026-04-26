using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.A2UI.Hosting;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// End-to-end render tests that drive the production
/// <see cref="A2UIRouter"/> + <see cref="SurfaceHost"/> pipeline against a real
/// WinUI3 visual tree. Each test:
///   1. Builds a fresh router on the UI thread.
///   2. Pushes canonical JSONL fixtures (matches the v0.8 wire format the agent emits).
///   3. Mounts the resulting <see cref="SurfaceHost.RootElement"/> in the
///      fixture's hidden Window so the tree gets a XamlRoot.
///   4. Walks the visual tree with <see cref="VisualTreeHelper"/> to assert.
/// </summary>
[Collection(UICollection.Name)]
public sealed class A2UIRenderingTests
{
    private readonly UIThreadFixture _ui;
    public A2UIRenderingTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task TextComponent_RendersTextBlock_WithLiteralText()
    {
        const string jsonl =
            """{"surfaceUpdate":{"surfaceId":"s1","components":[{"id":"r","component":{"Text":{"text":{"literalString":"hello world"}}}}]}}""" + "\n" +
            """{"beginRendering":{"surfaceId":"s1","root":"r"}}""";

        await _ui.PauseAsync("Text → TextBlock");
        await RenderAndAssertAsync(jsonl, root =>
        {
            var blocks = FindDescendants<TextBlock>(root).ToList();
            Assert.Single(blocks);
            Assert.Equal("hello world", blocks[0].Text);
        });
        await _ui.PauseAsync();
    }

    [Fact]
    public async Task ColumnContainer_RendersStackPanel_WithThreeChildren()
    {
        const string jsonl =
            """{"surfaceUpdate":{"surfaceId":"s","components":[""" +
                """{"id":"col","component":{"Column":{"children":{"explicitList":["a","b","c"]}}}},""" +
                """{"id":"a","component":{"Text":{"text":{"literalString":"first"}}}},""" +
                """{"id":"b","component":{"Text":{"text":{"literalString":"second"}}}},""" +
                """{"id":"c","component":{"Text":{"text":{"literalString":"third"}}}}""" +
            """]}}""" + "\n" +
            """{"beginRendering":{"surfaceId":"s","root":"col"}}""";

        await _ui.PauseAsync("Column container w/ 3 children");
        await RenderAndAssertAsync(jsonl, root =>
        {
            var stacks = FindDescendants<StackPanel>(root).ToList();
            Assert.Single(stacks);
            Assert.Equal(Orientation.Vertical, stacks[0].Orientation);

            var blocks = FindDescendants<TextBlock>(root).Select(b => b.Text).ToList();
            Assert.Equal(new[] { "first", "second", "third" }, blocks);
        });
        await _ui.PauseAsync();
    }

    [Fact]
    public async Task DataModelUpdate_AfterBeginRendering_UpdatesBoundTextReactively()
    {
        const string jsonl =
            """{"surfaceUpdate":{"surfaceId":"s","components":[{"id":"r","component":{"Text":{"text":{"path":"/greeting"}}}}]}}""" + "\n" +
            """{"beginRendering":{"surfaceId":"s","root":"r"}}""";

        await _ui.PauseAsync("DataModelUpdate → reactive text");
        await _ui.ResetContainerAsync();
        var harness = await _ui.RunOnUIAsync(() =>
        {
            var h = BuildHarness();
            h.Router.Push(jsonl);
            Assert.NotNull(h.LastSurface);
            _ui.Container.Children.Add(h.LastSurface!.RootElement);
            _ui.Container.UpdateLayout();

            var tb = FindDescendants<TextBlock>(h.LastSurface.RootElement).Single();
            Assert.Equal(string.Empty, tb.Text); // bound path empty until data arrives
            return Task.FromResult(h);
        });

        await _ui.PauseAsync("write /greeting = 'howdy'");
        await _ui.RunOnUIAsync(() =>
        {
            harness.Router.Push("""{"dataModelUpdate":{"surfaceId":"s","contents":[{"key":"greeting","valueString":"howdy"}]}}""");
            _ui.Container.UpdateLayout();
            var tb = FindDescendants<TextBlock>(harness.LastSurface!.RootElement).Single();
            Assert.Equal("howdy", tb.Text);
        });

        await _ui.PauseAsync("write /greeting = 'hi again'");
        await _ui.RunOnUIAsync(() =>
        {
            harness.Router.Push("""{"dataModelUpdate":{"surfaceId":"s","contents":[{"key":"greeting","valueString":"hi again"}]}}""");
            _ui.Container.UpdateLayout();
            var tb = FindDescendants<TextBlock>(harness.LastSurface!.RootElement).Single();
            Assert.Equal("hi again", tb.Text);
        });
        await _ui.PauseAsync();
    }

    [Fact]
    public async Task ButtonClick_RaisesActionThroughSink_WithSurfaceAndComponentIds()
    {
        const string jsonl =
            """{"surfaceUpdate":{"surfaceId":"sBtn","components":[""" +
                """{"id":"btn","component":{"Button":{"child":"lbl","action":{"name":"submit"}}}},""" +
                """{"id":"lbl","component":{"Text":{"text":{"literalString":"Go"}}}}""" +
            """]}}""" + "\n" +
            """{"beginRendering":{"surfaceId":"sBtn","root":"btn"}}""";

        await _ui.PauseAsync("Button click → action raised");
        await _ui.ResetContainerAsync();
        var harness = await _ui.RunOnUIAsync(() =>
        {
            var h = BuildHarness();
            h.Router.Push(jsonl);
            Assert.NotNull(h.LastSurface);
            _ui.Container.Children.Add(h.LastSurface!.RootElement);
            _ui.Container.UpdateLayout();
            return Task.FromResult(h);
        });

        await _ui.PauseAsync("(button visible)");
        await _ui.RunOnUIAsync(() =>
        {
            var btn = FindDescendants<Button>(harness.LastSurface!.RootElement).Single();
            var labelText = FindDescendants<TextBlock>(btn).Single().Text;
            Assert.Equal("Go", labelText);

            // Synthesize a click via the public IInvokeProvider automation pattern —
            // the documented WinUI test path. (OnClick is sealed in many controls.)
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(btn);
            var invoke = peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)
                as Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider;
            Assert.NotNull(invoke);
            invoke!.Invoke();
        });

        await _ui.PauseAsync("(action arrived at sink)");
        Assert.Single(harness.Actions.Raised);
        Assert.Equal("submit", harness.Actions.Raised[0].Name);
        Assert.Equal("sBtn", harness.Actions.Raised[0].SurfaceId);
        Assert.Equal("btn", harness.Actions.Raised[0].SourceComponentId);
        await _ui.PauseAsync();
    }

    [Fact]
    public async Task UnknownComponent_RendersUnknownPlaceholder_NotNull()
    {
        // Component name "Frobnicate" has no renderer → UnknownRenderer kicks in.
        const string jsonl =
            """{"surfaceUpdate":{"surfaceId":"sU","components":[{"id":"r","component":{"Frobnicate":{"foo":"bar"}}}]}}""" + "\n" +
            """{"beginRendering":{"surfaceId":"sU","root":"r"}}""";

        await _ui.PauseAsync("Unknown component → fallback");
        await RenderAndAssertAsync(jsonl, root =>
        {
            // The unknown renderer produces *something* visible — a non-null FrameworkElement.
            // We don't assert the exact placeholder shape; that's a renderer-internal detail.
            // The important contract: the surface tree didn't collapse to empty.
            var allDescendants = FindDescendants<FrameworkElement>(root).ToList();
            Assert.NotEmpty(allDescendants);
        });
        await _ui.PauseAsync();
    }

    [Fact]
    public async Task DeleteSurface_RemovesSurfaceFromRouter()
    {
        const string jsonl =
            """{"surfaceUpdate":{"surfaceId":"toDelete","components":[{"id":"r","component":{"Text":{"text":{"literalString":"x"}}}}]}}""" + "\n" +
            """{"beginRendering":{"surfaceId":"toDelete","root":"r"}}""";

        await _ui.PauseAsync("DeleteSurface → router cleanup");
        await _ui.ResetContainerAsync();
        var harness = await _ui.RunOnUIAsync(() =>
        {
            var h = BuildHarness();
            h.Router.Push(jsonl);
            _ui.Container.Children.Add(h.LastSurface!.RootElement);
            _ui.Container.UpdateLayout();
            Assert.True(h.Router.Surfaces.ContainsKey("toDelete"));
            return Task.FromResult(h);
        });

        await _ui.PauseAsync("(surface present, about to delete)");
        await _ui.RunOnUIAsync(() =>
        {
            harness.Router.Push("""{"deleteSurface":{"surfaceId":"toDelete"}}""");
            Assert.False(harness.Router.Surfaces.ContainsKey("toDelete"));
        });
        await _ui.PauseAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RenderAndAssertAsync(string jsonl, Action<FrameworkElement> assertOnRoot)
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            var harness = TestSupport.BuildHarness(_ui);
            // Mount the empty surface root before the tree is built so templated
            // controls have a XamlRoot during template application (avoids PRI
            // resource lookup failures in unpackaged WinUI3 tests).
            harness.Router.SurfaceCreated += (_, s) => _ui.Container.Children.Add(s.RootElement);
            harness.Router.Push(jsonl);
            Assert.NotNull(harness.LastSurface);
            _ui.Container.UpdateLayout();
            assertOnRoot(harness.LastSurface!.RootElement);
        });
    }

    private TestHarness BuildHarness() => TestSupport.BuildHarness(_ui);

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
        => TestSupport.FindDescendants<T>(root);
}
