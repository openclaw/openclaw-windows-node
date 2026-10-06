using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>Setup-local composition only. Slots remain mounted across viewport changes.</summary>
public sealed partial class SetupPageShell : UserControl
{
    public static readonly DependencyProperty HeroProperty = Slot(nameof(Hero));
    public static readonly DependencyProperty HeaderProperty = Slot(nameof(Header));
    public static readonly DependencyProperty BodyProperty = Slot(nameof(Body));
    public static readonly DependencyProperty FooterProperty = Slot(nameof(Footer));

    public UIElement? Hero { get => (UIElement?)GetValue(HeroProperty); set => SetValue(HeroProperty, value); }
    public UIElement? Header { get => (UIElement?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public UIElement? Body { get => (UIElement?)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public UIElement? Footer { get => (UIElement?)GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
    public bool IsWideLayout { get; private set; }
    public ScrollViewer Scroller => ContentScroll;

    public SetupPageShell()
    {
        InitializeComponent();
        ApplyLayout(false);
        SizeChanged += (_, args) => ApplyLayout(args.NewSize.Width >= 960 && args.NewSize.Height >= 600);
    }

    private static DependencyProperty Slot(string name) =>
        DependencyProperty.Register(name, typeof(UIElement), typeof(SetupPageShell), new PropertyMetadata(null));

    private void ApplyLayout(bool wide)
    {
        IsWideLayout = wide;
        Composition.ColumnDefinitions[0].Width = wide ? new GridLength(320) : new GridLength(1, GridUnitType.Star);
        Composition.ColumnDefinitions[1].Width = new GridLength(wide ? 40 : 0);
        Composition.ColumnDefinitions[2].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRowSpan(HeroPresenter, wide ? 3 : 1);
        Grid.SetColumn(HeaderPresenter, wide ? 2 : 0);
        Grid.SetRow(HeaderPresenter, wide ? 0 : 1);
        Grid.SetColumn(BodyPresenter, wide ? 2 : 0);
        Grid.SetRow(BodyPresenter, wide ? 1 : 2);
        HeroPresenter.Margin = new Thickness(0, 0, 0, wide ? 0 : 24);
    }
}
