// Joins related SettingsCards into one framed group with 1px dividers between rows.
//
// Usage: the Border owns the single backplate and frame; its content is any panel tree that
// holds the SettingsCards (nested panels and ContentControls are walked in document order).
//
//   <Border Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
//           BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
//           BorderThickness="1" CornerRadius="8"
//           presentation:SettingsCardGroup.IsEnabled="True">
//       <StackPanel>
//           <toolkit:SettingsCard .../>
//           <toolkit:SettingsCard .../>
//       </StackPanel>
//   </Border>
//
// A non-card row (for example a list of entries) joins the group by setting
// presentation:SettingsCardGroup.IsRow="True" and BorderBrush on its Border.
//
// The first visible row has no divider. Rows hidden by their own Visibility (or a collapsed
// ancestor) are skipped, and the group re-applies when any of those Visibility values change,
// so the dividers and the rounded first and last corners always follow what is on screen.
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.WinUI.Controls;

namespace OpenClawTray.Presentation;

/// <summary>Flattens the SettingsCards under a Border so they read as one connected card.</summary>
public static class SettingsCardGroup
{
    // The group frame is a 1px border with 8px corners, so the inner rows round at 7px.
    private const double InnerCornerRadius = 7;

    // Elements whose Visibility is already watched by a group, so re-applying never stacks callbacks.
    private static readonly ConditionalWeakTable<UIElement, object> Watched = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SettingsCardGroup), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty IsRowProperty = DependencyProperty.RegisterAttached(
        "IsRow", typeof(bool), typeof(SettingsCardGroup), new PropertyMetadata(false));

    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);

    public static bool GetIsRow(DependencyObject target) => (bool)target.GetValue(IsRowProperty);

    public static void SetIsRow(DependencyObject target, bool value) => target.SetValue(IsRowProperty, value);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Border group)
            throw new ArgumentException("A settings card group requires a Border.", nameof(target));
        group.Loaded -= OnLoaded;
        if (!(bool)args.NewValue) return;
        group.Loaded += OnLoaded;
        if (group.IsLoaded) Apply(group);
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => Apply((Border)sender);

    private static void Apply(Border group)
    {
        var rows = new List<FrameworkElement>();
        Collect(group.Child, rows, group);
        for (var i = 0; i < rows.Count; i++)
        {
            var top = i > 0 ? 1 : 0;
            var radius = new CornerRadius(
                i == 0 ? InnerCornerRadius : 0,
                i == 0 ? InnerCornerRadius : 0,
                i == rows.Count - 1 ? InnerCornerRadius : 0,
                i == rows.Count - 1 ? InnerCornerRadius : 0);
            var divider = new Thickness(0, top, 0, 0);
            switch (rows[i])
            {
                case SettingsCard card:
                    card.Background = new SolidColorBrush(Colors.Transparent);
                    card.BorderThickness = divider;
                    card.CornerRadius = radius;
                    break;
                case Border row:
                    row.Background = new SolidColorBrush(Colors.Transparent);
                    row.BorderThickness = divider;
                    row.CornerRadius = radius;
                    break;
            }
        }
    }

    private static void Collect(object? node, List<FrameworkElement> rows, Border group)
    {
        if (node is UIElement element)
        {
            Watch(element, group);
            if (element.Visibility == Visibility.Collapsed) return;
        }

        switch (node)
        {
            case SettingsCard card:
                rows.Add(card);
                break;
            case Border border when GetIsRow(border):
                rows.Add(border);
                break;
            case Border border:
                Collect(border.Child, rows, group);
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                    Collect(child, rows, group);
                break;
            case ContentControl content:
                Collect(content.Content, rows, group);
                break;
        }
    }

    private static void Watch(UIElement element, Border group)
    {
        if (Watched.TryGetValue(element, out _)) return;
        Watched.Add(element, new object());
        // The callback outlives the group only if the element does, so it must not keep the group alive.
        var weakGroup = new WeakReference<Border>(group);
        element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            if (weakGroup.TryGetTarget(out var target)) Apply(target);
        });
    }
}
