using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>
/// Setup page footer that keeps its actions in one right-aligned row when they fit and stacks
/// them right-aligned, one per row, when the page column is too narrow. Collapsed actions take
/// no space, and document order (and therefore tab order) is the same in both layouts.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:SetupFooterPanel Grid.Row="2" Style="{StaticResource SetupNavPanelStyle}"&gt;
///     &lt;Button Content="Back" Style="{StaticResource SetupSecondaryButtonStyle}" /&gt;
///     &lt;Button Content="Next" Style="{StaticResource SetupPrimaryButtonStyle}" /&gt;
/// &lt;/controls:SetupFooterPanel&gt;
/// </code>
/// </example>
public sealed partial class SetupFooterPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(SetupFooterPanel),
        new PropertyMetadata(0d, (owner, _) => ((SetupFooterPanel)owner).InvalidateMeasure()));

    private bool _stacked;

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Whether the last measure stacked the actions because one row did not fit.</summary>
    public bool IsStacked => _stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        // Measure collapsed children too, so WinUI can propagate later visibility changes.
        foreach (var action in Children)
            action.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var actions = VisibleActions();
        var widths = actions.Select(action => action.DesiredSize.Width).ToArray();
        _stacked = !SetupFooterLayout.FitsOnOneRow(widths, Spacing, availableSize.Width);
        if (!_stacked)
            return new Size(SetupFooterLayout.RowWidth(widths, Spacing),
                actions.Count == 0 ? 0 : actions.Max(action => action.DesiredSize.Height));

        foreach (var action in actions)
            action.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(
            actions.Max(action => action.DesiredSize.Width),
            SetupFooterLayout.StackHeight(actions.Select(action => action.DesiredSize.Height).ToArray(), Spacing));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var actions = VisibleActions();
        if (_stacked)
        {
            var top = 0d;
            foreach (var action in actions)
            {
                var width = Math.Min(action.DesiredSize.Width, finalSize.Width);
                action.Arrange(new Rect(finalSize.Width - width, top, width, action.DesiredSize.Height));
                top += action.DesiredSize.Height + Spacing;
            }
            return finalSize;
        }

        var left = Math.Max(0, finalSize.Width -
            SetupFooterLayout.RowWidth(actions.Select(action => action.DesiredSize.Width).ToArray(), Spacing));
        foreach (var action in actions)
        {
            action.Arrange(new Rect(left, 0, action.DesiredSize.Width, finalSize.Height));
            left += action.DesiredSize.Width + Spacing;
        }
        return finalSize;
    }

    private List<UIElement> VisibleActions() =>
        Children.Where(child => child.Visibility == Visibility.Visible).ToList();
}
