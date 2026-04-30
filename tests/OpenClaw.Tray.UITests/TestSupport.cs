using System.Collections.Generic;
using System.Linq;
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
