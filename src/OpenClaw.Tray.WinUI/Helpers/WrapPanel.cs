using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace OpenClawTray.Helpers;

/// <summary>
/// Minimal left-to-right wrapping panel for compact chip rows.
/// </summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; }

    public double VerticalSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var available = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : availableSize.Width;

        double lineWidth = 0, lineHeight = 0, totalHeight = 0, maxWidth = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = child.DesiredSize;

            if (lineWidth > 0 && lineWidth + HorizontalSpacing + size.Width > available)
            {
                totalHeight += lineHeight + VerticalSpacing;
                maxWidth = Math.Max(maxWidth, lineWidth);
                lineWidth = size.Width;
                lineHeight = size.Height;
            }
            else
            {
                lineWidth += (lineWidth > 0 ? HorizontalSpacing : 0) + size.Width;
                lineHeight = Math.Max(lineHeight, size.Height);
            }
        }

        totalHeight += lineHeight;
        maxWidth = Math.Max(maxWidth, lineWidth);

        return new Size(
            double.IsInfinity(available) ? maxWidth : Math.Min(maxWidth, available),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;

            if (x > 0 && x + size.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return finalSize;
    }
}
