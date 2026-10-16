using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.A2UI.Actions;
using OpenClawTray.A2UI.DataModel;
using OpenClawTray.A2UI.Hosting;
using OpenClawTray.A2UI.Protocol;
using OpenClawTray.A2UI.Rendering;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Shared helpers for A2UI rendering tests. A test typically:
///   1. Builds a <see cref="TestHarness"/> on the UI thread.
///   2. Pushes a JSONL fixture (built with <see cref="A2UI"/>).
///   3. Mounts <see cref="TestHarness.LastSurface"/>'s root in the fixture
///      container so it gets a XamlRoot.
///   4. Walks the visual tree with <see cref="FindDescendants{T}"/>.
/// </summary>
public static class TestSupport
{
    public static Task WaitForSettingsCardReadyAsync(Page page, SettingsCard card) =>
        WaitForRenderedConditionAsync(
            () => card.IsLoaded && card.IsEnabled && card.IsClickEnabled &&
                page.XamlRoot is not null && ReferenceEquals(page.XamlRoot, card.XamlRoot),
            $"{page.GetType().Name}.{card.Name} ready for invocation",
            () => $"IsLoaded={card.IsLoaded}, IsEnabled={card.IsEnabled}, " +
                $"IsClickEnabled={card.IsClickEnabled}, PageHasXamlRoot={page.XamlRoot is not null}, " +
                $"SameXamlRoot={ReferenceEquals(page.XamlRoot, card.XamlRoot)}");

    /// <summary>Exercise the XAML-wired action boundary, not the toolkit's native input provider.</summary>
    public static void InvokeSettingsCardAction(Page page, SettingsCard card, string handler)
    {
        Assert.True(card.IsLoaded && card.IsEnabled && card.IsClickEnabled,
            $"{page.GetType().Name}.{card.Name}: IsLoaded={card.IsLoaded}, " +
            $"IsEnabled={card.IsEnabled}, IsClickEnabled={card.IsClickEnabled}.");
        Assert.NotNull(page.XamlRoot);
        Assert.Same(page.XamlRoot, card.XamlRoot);
        // The toolkit advertises Invoke but its managed and native providers reject it.
        // Keep actual keyboard/pointer coverage in NativeOnboardingProof, never synthesize OS input here.
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("Set OPENCLAW_REPO_ROOT for framework UI tests.");
        var xaml = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(root,
            "src", "OpenClaw.SetupEngine.UI", "Pages", page.GetType().Name + ".xaml"));
        var declarations = xaml.Descendants().Where(element => element.Name.LocalName == "SettingsCard").ToArray();
        if (!string.IsNullOrEmpty(card.Name))
        {
            System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var declaration = Assert.Single(declarations, element => (string?)element.Attribute(x + "Name") == card.Name);
            Assert.Equal(handler, (string?)declaration.Attribute("Click"));
        }
        else if (page is OpenClaw.SetupEngine.UI.Pages.AdvancedSetupPage)
        {
            // This page has exactly four flat siblings, unlike repeated AI DataTemplate rows.
            var parent = Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(card));
            var siblings = parent.Children.OfType<SettingsCard>().ToArray();
            Assert.Equal(4, siblings.Length);
            Assert.Equal(siblings.Length, declarations.Length);
            Assert.All(declarations, element => Assert.Same(declarations[0].Parent, element.Parent));
            var index = Array.IndexOf(siblings, card);
            Assert.InRange(index, 0, declarations.Length - 1);
            Assert.Equal(handler, (string?)declarations[index].Attribute("Click"));
        }
        else
        {
            Assert.Fail("Unsupported unnamed SettingsCard. Use a template-scoped name or the Advanced Setup sibling contract.");
        }
        var action = page.GetType().GetMethod(handler,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(action);
        action.Invoke(page, [card, new RoutedEventArgs()]);
    }

    public static async Task WaitForRenderedConditionAsync(
        Func<bool> predicate, string operation, Func<string>? diagnostics = null)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, object args)
        {
            if (predicate()) settled.TrySetResult();
        }
        CompositionTarget.Rendering += Check;
        try
        {
            Check(null, EventArgs.Empty);
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Timed out waiting for {operation}." +
                (diagnostics is null ? "" : $" {diagnostics()}"));
        }
        finally
        {
            CompositionTarget.Rendering -= Check;
        }
    }

    public static async Task WaitForSettingsExpanderSettledAsync(
        UIThreadFixture ui, SettingsExpander expander, bool expanded)
    {
        Assert.Equal(expanded, expander.IsExpanded);
        expander.UpdateLayout();
        await ui.YieldToRenderAsync();
        var content = Assert.Single(FindDescendants<Border>(expander), border => border.Name == "ExpanderContent");
        var transform = Assert.IsType<CompositeTransform>(content.RenderTransform);
        // Toolkit changes IsExpanded before its visibility/translation transition finishes.
        await WaitForRenderedConditionAsync(
            () => content.Visibility == (expanded ? Visibility.Visible : Visibility.Collapsed) &&
                (!expanded || transform.TranslateY == 0), "SettingsExpander transition");
        expander.UpdateLayout();
        await ui.YieldToRenderAsync();
    }

    /// <summary>Build a fresh router/registry/datamodel/sink stack for one test.</summary>
    public static TestHarness BuildHarness(UIThreadFixture ui)
    {
        var logger = NullLogger.Instance;
        var media = new MediaResolver(logger);
        var registry = ComponentRendererRegistry.BuildDefault(media);
        var dataModel = new DataModelStore(ui.Dispatcher);
        var actions = new RecordingActionSink();
        var router = new A2UIRouter(ui.Dispatcher, dataModel, registry, actions, logger);
        var harness = new TestHarness(router, dataModel, actions);
        router.SurfaceCreated += (_, s) => harness.LastSurface = s;
        return harness;
    }

    /// <summary>
    /// Yields every <typeparamref name="T"/> in the visual subtree rooted at
    /// <paramref name="root"/>. Requires the tree to be attached to a XamlRoot
    /// (templated controls otherwise fail to apply their template, breaking the walk).
    /// </summary>
    public static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var grand in FindDescendants<T>(child)) yield return grand;
        }
    }

    /// <summary>
    /// Yields every <typeparamref name="T"/> reachable through the logical /
    /// content properties that A2UI renderers populate (Panel.Children,
    /// Border.Child, ContentControl.Content, ScrollViewer.Content,
    /// TabView.TabItems, Expander.Header/Content). Doesn't require the tree
    /// to be mounted, so it works for templated controls (TextBox, PasswordBox,
    /// ListView, ComboBox) whose template apply requires PRI resources that
    /// unpackaged test processes can't always resolve.
    /// </summary>
    public static IEnumerable<T> FindLogical<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T self) yield return self;

        switch (root)
        {
            case Panel panel:
                foreach (var child in panel.Children)
                    foreach (var d in FindLogical<T>(child)) yield return d;
                break;
            case Border border when border.Child is FrameworkElement bc:
                foreach (var d in FindLogical<T>(bc)) yield return d;
                break;
            case ScrollViewer sv when sv.Content is FrameworkElement sc:
                foreach (var d in FindLogical<T>(sc)) yield return d;
                break;
            case TabView tv:
                foreach (var ti in tv.TabItems.OfType<TabViewItem>())
                {
                    if (ti is T tiT) yield return tiT;
                    if (ti.Content is FrameworkElement tc)
                        foreach (var d in FindLogical<T>(tc)) yield return d;
                }
                break;
            case Expander exp:
                if (exp.Header is FrameworkElement eh)
                    foreach (var d in FindLogical<T>(eh)) yield return d;
                if (exp.Content is FrameworkElement ec)
                    foreach (var d in FindLogical<T>(ec)) yield return d;
                break;
            case ContentControl cc when cc.Content is FrameworkElement cf:
                foreach (var d in FindLogical<T>(cf)) yield return d;
                break;
        }
    }
}

/// <summary>One-shot per-test holder: router + the sink that records actions.</summary>
public sealed class TestHarness
{
    public A2UIRouter Router { get; }
    public DataModelStore DataModel { get; }
    public RecordingActionSink Actions { get; }
    public SurfaceHost? LastSurface { get; set; }

    public TestHarness(A2UIRouter router, DataModelStore dataModel, RecordingActionSink actions)
    {
        Router = router;
        DataModel = dataModel;
        Actions = actions;
    }
}

/// <summary>Action sink that captures everything the surface emits, in order.</summary>
public sealed class RecordingActionSink : IActionSink
{
    public List<A2UIAction> Raised { get; } = new();
    public void Raise(A2UIAction action) => Raised.Add(action);
}
