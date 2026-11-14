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
        ApplyBranding();
        // The theme can change while the mark is shown. The static event holds the mark only weakly, because a
        // closing dialog window is not guaranteed to raise Unloaded for its title-bar mark.
        _brandingChanged = WeakBrandingHandler(this);
        Loaded += (_, _) =>
        {
            Theming.ThemeBrandingState.Changed -= _brandingChanged;
            Theming.ThemeBrandingState.Changed += _brandingChanged;
            ApplyBranding();
        };
        Unloaded += (_, _) => Theming.ThemeBrandingState.Changed -= _brandingChanged;
    }

    private readonly EventHandler _brandingChanged;

    // Static so the handler's closure holds only the weak reference, never the mark itself; it removes itself once
    // the mark has been collected.
    private static EventHandler WeakBrandingHandler(BrandMark mark)
    {
        var self = new WeakReference<BrandMark>(mark);
        EventHandler? changed = null;
        changed = (_, _) =>
        {
            if (self.TryGetTarget(out var target))
                target.ApplyBranding();
            else
                Theming.ThemeBrandingState.Changed -= changed;
        };
        return changed;
    }

    /// <summary>True when the theme's mark is shown instead of the OpenClaw claw image.</summary>
    internal bool ShowsThemeMark => ThemeMark.Visibility == Visibility.Visible;

    private void ApplyBranding()
    {
        var claw = Theming.ThemeBrandingState.Current.ShowsClaw;
        MarkImage.Visibility = claw ? Visibility.Visible : Visibility.Collapsed;
        ThemeMark.Visibility = claw ? Visibility.Collapsed : Visibility.Visible;
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
        ThemeMark.Width = size;
        ThemeMark.Height = size;
    }
}
