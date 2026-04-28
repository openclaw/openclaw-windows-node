using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json.Nodes;
using OpenClawTray.A2UI.Protocol;

namespace OpenClawTray.A2UI.Rendering.Renderers;

public sealed class RowRenderer : IComponentRenderer
{
    public string ComponentName => "Row";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = ctx.Theme.Spacing is { } s ? s : 8,
        };
        ContainerHelpers.ApplyDistribution(sp, c.Properties["distribution"]?.GetValue<string>(), horizontal: true);
        ContainerHelpers.ApplyAlignment(sp, c.Properties["alignment"]?.GetValue<string>(), horizontal: true);

        foreach (var childId in ctx.GetExplicitChildren(c))
        {
            var built = ctx.BuildChild(childId);
            if (built != null) sp.Children.Add(built);
        }
        return sp;
    }
}

public sealed class ColumnRenderer : IComponentRenderer
{
    public string ComponentName => "Column";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = ctx.Theme.Spacing is { } s ? s : 8,
            MinWidth = 0,
        };
        ContainerHelpers.ApplyDistribution(sp, c.Properties["distribution"]?.GetValue<string>(), horizontal: false);
        ContainerHelpers.ApplyAlignment(sp, c.Properties["alignment"]?.GetValue<string>(), horizontal: false);

        foreach (var childId in ctx.GetExplicitChildren(c))
        {
            var built = ctx.BuildChild(childId);
            if (built != null) sp.Children.Add(built);
        }
        return sp;
    }
}

public sealed class ListRenderer : IComponentRenderer
{
    public string ComponentName => "List";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var direction = c.Properties["direction"]?.GetValue<string>() ?? "vertical";
        var orientation = direction.Equals("horizontal", StringComparison.OrdinalIgnoreCase)
            ? Orientation.Horizontal
            : Orientation.Vertical;

        // Virtualize via ItemsRepeater + ItemsRepeaterScrollHost. Building 10k
        // FrameworkElements upfront — the previous StackPanel behavior — was
        // fine for tiny lists but pinned the UI thread on large ones. The
        // repeater realizes only the items in the viewport. (Spec §5 calls
        // this out as the expected mapping for v1.)
        var children = ctx.GetExplicitChildren(c);
        var alignmentValue = c.Properties["alignment"]?.GetValue<string>();
        var spacing = ctx.Theme.Spacing is { } sp ? sp : 6.0;

        var layout = new StackLayout
        {
            Orientation = orientation,
            Spacing = spacing,
        };

        var repeater = new ItemsRepeater
        {
            Layout = layout,
            ItemsSource = children,
            ItemTemplate = new ChildIdTemplate(ctx),
        };

        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollMode = orientation == Orientation.Horizontal ? ScrollMode.Auto : ScrollMode.Disabled,
            HorizontalScrollBarVisibility = orientation == Orientation.Horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollMode = orientation == Orientation.Vertical ? ScrollMode.Auto : ScrollMode.Disabled,
            VerticalScrollBarVisibility = orientation == Orientation.Vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Content = repeater,
        };
        // Cross-axis alignment lives on the scrollviewer; the repeater's layout
        // governs main-axis flow.
        if (alignmentValue != null)
        {
            if (orientation == Orientation.Horizontal)
            {
                scrollViewer.VerticalAlignment = alignmentValue switch
                {
                    "start" => VerticalAlignment.Top,
                    "center" => VerticalAlignment.Center,
                    "end" => VerticalAlignment.Bottom,
                    "stretch" => VerticalAlignment.Stretch,
                    _ => scrollViewer.VerticalAlignment,
                };
            }
            else
            {
                scrollViewer.HorizontalAlignment = alignmentValue switch
                {
                    "start" => HorizontalAlignment.Left,
                    "center" => HorizontalAlignment.Center,
                    "end" => HorizontalAlignment.Right,
                    "stretch" => HorizontalAlignment.Stretch,
                    _ => scrollViewer.HorizontalAlignment,
                };
            }
        }
        return scrollViewer;
    }

    /// <summary>
    /// Resolves each <c>children[i]</c> string-id back to its A2UI component on
    /// realization. Caches by id so scrolling back to a previously-realized item
    /// reuses the same instance and its bindings stay subscribed.
    /// </summary>
    private sealed class ChildIdTemplate : Microsoft.UI.Xaml.IElementFactory
    {
        private readonly RenderContext _ctx;
        private readonly System.Collections.Generic.Dictionary<string, UIElement> _cache = new(StringComparer.Ordinal);
        public ChildIdTemplate(RenderContext ctx) { _ctx = ctx; }

        public UIElement GetElement(Microsoft.UI.Xaml.ElementFactoryGetArgs args)
        {
            if (args.Data is string id)
            {
                if (_cache.TryGetValue(id, out var existing))
                    return existing;
                var built = _ctx.BuildChild(id) ?? (UIElement)new ContentPresenter();
                _cache[id] = built;
                return built;
            }
            return new ContentPresenter();
        }

        public void RecycleElement(Microsoft.UI.Xaml.ElementFactoryRecycleArgs args)
        {
            // Keep the realized element in the cache so re-realization reuses it
            // and its DataModel subscriptions stay live. Removing here would
            // double-build on every scroll.
        }
    }
}

public sealed class CardRenderer : IComponentRenderer
{
    public string ComponentName => "Card";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        // Honor theme.Spacing as the card's padding so a tighter / looser
        // surface theme propagates down. Fallback 16 matches Fluent default.
        var pad = ctx.Theme.Spacing is { } sp ? sp * 2 : 16;
        var border = new Border
        {
            CornerRadius = new CornerRadius(ctx.Theme.CornerRadius is { } r ? r : 8),
            Padding = new Thickness(pad),
            BorderThickness = new Thickness(1),
        };
        if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out var bg) && bg is Brush bgBrush)
            border.Background = bgBrush;
        if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var bs) && bs is Brush bsBrush)
            border.BorderBrush = bsBrush;

        var childId = ctx.GetSingleChild(c, "child");
        if (childId != null) border.Child = ctx.BuildChild(childId);
        return border;
    }
}

public sealed class TabsRenderer : IComponentRenderer
{
    public string ComponentName => "Tabs";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var tabs = new TabView
        {
            IsAddTabButtonVisible = false,
            CanReorderTabs = false,
            CanDragTabs = false,
            TabWidthMode = TabViewWidthMode.SizeToContent,
            CloseButtonOverlayMode = TabViewCloseButtonOverlayMode.Auto,
        };

        if (c.Properties["tabItems"] is JsonArray arr)
        {
            int tabIndex = 0;
            foreach (var node in arr)
            {
                if (node is not JsonObject tabObj) { tabIndex++; continue; }
                var titleVal = A2UIValue.From(tabObj["title"]);
                var titleText = ctx.ResolveString(titleVal) ?? OpenClawTray.Helpers.LocalizationHelper.GetString("A2UI_TabFallback");
                var childId = tabObj["child"]?.GetValue<string>();

                var tab = new TabViewItem
                {
                    Header = titleText,
                    IsClosable = false,
                    Content = childId != null ? ctx.BuildChild(childId) : null,
                };

                // Live-bind the title if it's path-bound. Key by tab index — never
                // Guid.NewGuid(), which leaked a subscription on every render and
                // collided when two tabs had null childId.
                if (titleVal?.HasPath == true)
                {
                    var subKey = $"tab::{tabIndex}::title";
                    void Update() => tab.Header = ctx.ResolveString(titleVal) ?? OpenClawTray.Helpers.LocalizationHelper.GetString("A2UI_TabFallback");
                    ctx.WatchValue(c.Id, subKey, titleVal, Update);
                }

                tabs.TabItems.Add(tab);
                tabIndex++;
            }
        }
        return tabs;
    }
}

public sealed class ModalRenderer : IComponentRenderer
{
    public string ComponentName => "Modal";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        // Render as a real ContentDialog launched from the entry-point child.
        // The entry-point is shown inline; clicking it shows the dialog with
        // the content-child as its body. Falls back to Expander when no
        // XamlRoot can be resolved (e.g. surface not yet attached to a window).
        var entryId = ctx.GetSingleChild(c, "entryPointChild");
        var contentId = ctx.GetSingleChild(c, "contentChild");

        var entryElement = entryId != null ? ctx.BuildChild(entryId) : null;
        var contentElement = contentId != null ? ctx.BuildChild(contentId) : null;

        // Wrap the entry in a Button so we have a uniform click target.
        // (A2UI entry-point may itself be a Button, but it may also be a Card/Text.)
        var trigger = new Button
        {
            Content = entryElement,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        var titleVal = ctx.GetValue(c, "title");
        var titleText = ctx.ResolveString(titleVal);

        trigger.Click += async (_, _) =>
        {
            if (trigger.XamlRoot is null) return; // not yet in tree
            var dialog = new ContentDialog
            {
                XamlRoot = trigger.XamlRoot,
                Title = titleText ?? string.Empty,
                Content = contentElement,
                CloseButtonText = OpenClawTray.Helpers.LocalizationHelper.GetString("A2UI_ModalClose"),
            };
            try { await dialog.ShowAsync(); }
            catch { /* ContentDialog throws if another dialog is open; swallow */ }
        };
        AutomationHelpers.Apply(trigger, c, ctx);
        return trigger;
    }
}

public sealed class DividerRenderer : IComponentRenderer
{
    public string ComponentName => "Divider";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var axis = c.Properties["axis"]?.GetValue<string>() ?? "horizontal";
        var rect = new Microsoft.UI.Xaml.Shapes.Rectangle();
        if (axis.Equals("vertical", StringComparison.OrdinalIgnoreCase))
        {
            rect.Width = 1;
            rect.HorizontalAlignment = HorizontalAlignment.Center;
            rect.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            rect.Height = 1;
            rect.HorizontalAlignment = HorizontalAlignment.Stretch;
            rect.VerticalAlignment = VerticalAlignment.Center;
        }
        var marginV = ctx.Theme.Spacing is { } sp ? sp / 2 : 4;
        rect.Margin = new Thickness(0, marginV, 0, marginV);
        if (Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out var divBrush) && divBrush is Brush b)
            rect.Fill = b;
        else
            rect.Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray);
        return rect;
    }
}

internal static class ContainerHelpers
{
    public static void ApplyDistribution(StackPanel sp, string? value, bool horizontal)
    {
        if (value == null) return;
        // StackPanel doesn't have a true justify-content; map to alignment of the panel itself.
        if (horizontal)
        {
            sp.HorizontalAlignment = value switch
            {
                "start" => HorizontalAlignment.Left,
                "center" => HorizontalAlignment.Center,
                "end" => HorizontalAlignment.Right,
                "spaceBetween" or "spaceAround" or "spaceEvenly" => HorizontalAlignment.Stretch,
                _ => sp.HorizontalAlignment,
            };
        }
        else
        {
            sp.VerticalAlignment = value switch
            {
                "start" => VerticalAlignment.Top,
                "center" => VerticalAlignment.Center,
                "end" => VerticalAlignment.Bottom,
                "spaceBetween" or "spaceAround" or "spaceEvenly" => VerticalAlignment.Stretch,
                _ => sp.VerticalAlignment,
            };
        }
    }

    public static void ApplyAlignment(StackPanel sp, string? value, bool horizontal)
    {
        if (value == null) return;
        // Cross-axis alignment.
        if (horizontal)
        {
            sp.VerticalAlignment = value switch
            {
                "start" => VerticalAlignment.Top,
                "center" => VerticalAlignment.Center,
                "end" => VerticalAlignment.Bottom,
                "stretch" => VerticalAlignment.Stretch,
                _ => sp.VerticalAlignment,
            };
        }
        else
        {
            sp.HorizontalAlignment = value switch
            {
                "start" => HorizontalAlignment.Left,
                "center" => HorizontalAlignment.Center,
                "end" => HorizontalAlignment.Right,
                "stretch" => HorizontalAlignment.Stretch,
                _ => sp.HorizontalAlignment,
            };
        }
    }
}
