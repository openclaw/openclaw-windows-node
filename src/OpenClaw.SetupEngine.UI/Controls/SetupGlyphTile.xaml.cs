using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>
/// 40-DIP rounded glyph tile that leads a setup choice row. It turns accent while its row is the
/// selected choice; <see cref="SyncSelection"/> keeps every tile in a selector in step.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:SetupGlyphTile Glyph="&amp;#xE756;" /&gt;
/// SetupGlyphTile.SyncSelection(GatewayChoiceSelector);
/// </code>
/// </example>
public sealed partial class SetupGlyphTile : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SetupGlyphTile),
        new PropertyMetadata(string.Empty, (sender, args) => ((SetupGlyphTile)sender).Icon.Glyph = (string)args.NewValue));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(SetupGlyphTile),
        new PropertyMetadata(false, (sender, args) => ((SetupGlyphTile)sender).ApplyState()));

    public SetupGlyphTile()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyState();
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Activates the tile inside the selected item and clears the others.</summary>
    public static void SyncSelection(ListViewBase selector)
    {
        foreach (var item in selector.Items.OfType<ContentControl>())
        {
            if (item.Content is FrameworkElement content && FindTile(content) is { } tile)
                tile.IsActive = ReferenceEquals(selector.SelectedItem, item);
        }
    }

    private static SetupGlyphTile? FindTile(DependencyObject root)
    {
        if (root is SetupGlyphTile tile)
            return tile;
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (FindTile(child) is { } found)
                    return found;
            }
        }
        return null;
    }

    private void ApplyState() => VisualStateManager.GoToState(this, IsActive ? "Active" : "Inactive", false);
}
