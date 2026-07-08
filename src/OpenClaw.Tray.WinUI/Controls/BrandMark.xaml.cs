using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;

namespace OpenClawTray.Controls;

public sealed partial class BrandMark : UserControl
{
    private const double DefaultMarkSize = 20d;

    public static readonly DependencyProperty MarkSizeProperty =
        DependencyProperty.Register(
            nameof(MarkSize),
            typeof(double),
            typeof(BrandMark),
            new PropertyMetadata(DefaultMarkSize, OnMarkSizeChanged));

    public BrandMark()
    {
        InitializeComponent();
        MarkImage.Source = BrandAssets.CreateRedBotMarkSource();
        ApplySize(MarkSize);
    }

    public double MarkSize
    {
        get => (double)GetValue(MarkSizeProperty);
        set => SetValue(MarkSizeProperty, value);
    }

    private static void OnMarkSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BrandMark mark && e.NewValue is double size)
            mark.ApplySize(size);
    }

    private void ApplySize(double size)
    {
        if (!double.IsFinite(size) || size < 0)
            size = DefaultMarkSize;

        Width = size;
        Height = size;
        MarkImage.Width = size;
        MarkImage.Height = size;
    }
}
