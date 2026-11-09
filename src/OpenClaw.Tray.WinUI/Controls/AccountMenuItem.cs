using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Controls;

/// <summary>
/// Account-menu <see cref="MenuFlyoutItem"/> with optional secondary text, metadata, pill badge and
/// profile picture. The styles in Themes/AccountMenuResources.xaml supply the optional parts;
/// the default MenuFlyoutItem behavior (keyboard, focus, accelerator text) is unchanged.
/// </summary>
/// <example>
/// <code>
/// var settings = new AccountMenuItem { Text = "Settings", Badge = "Update" };
/// settings.Style = AccountMenuItem.ItemStyle;
/// </code>
/// </example>
internal sealed partial class AccountMenuItem : MenuFlyoutItem
{
    private TextBlock? _description;
    private TextBlock? _metadata;
    private FrameworkElement? _badgeRoot;
    private TextBlock? _badgeText;
    private PersonPicture? _picture;
    private string? _descriptionValue;
    private string? _metadataValue;
    private string? _badgeValue;
    private ImageSource? _pictureValue;
    private string? _pictureName;

    internal static Style ItemStyle => (Style)Application.Current.Resources["AccountMenuItemStyle"];
    internal static Style ProfileStyle => (Style)Application.Current.Resources["AccountMenuProfileItemStyle"];

    /// <summary>Secondary line under <see cref="MenuFlyoutItem.Text"/>; hidden when empty.</summary>
    public string? Description
    {
        get => _descriptionValue;
        set { _descriptionValue = value; Apply(); }
    }

    /// <summary>Right-aligned detail, separate from keyboard shortcuts and their automation metadata.</summary>
    public string? Metadata
    {
        get => _metadataValue;
        set { _metadataValue = value; Apply(); }
    }

    /// <summary>Short pill shown before the accelerator text; hidden when empty.</summary>
    public string? Badge
    {
        get => _badgeValue;
        set { _badgeValue = value; Apply(); }
    }

    /// <summary>Profile picture for the profile style; initials come from <see cref="PictureName"/>.</summary>
    public ImageSource? Picture
    {
        get => _pictureValue;
        set { _pictureValue = value; Apply(); }
    }

    public string? PictureName
    {
        get => _pictureName;
        set { _pictureName = value; Apply(); }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _description = GetTemplateChild("DescriptionTextBlock") as TextBlock;
        _metadata = GetTemplateChild("MetadataTextBlock") as TextBlock;
        _badgeRoot = GetTemplateChild("BadgeRoot") as FrameworkElement;
        _badgeText = GetTemplateChild("BadgeTextBlock") as TextBlock;
        _picture = GetTemplateChild("PicturePart") as PersonPicture;
        Apply();
    }

    private void Apply()
    {
        if (_description is not null)
        {
            _description.Text = _descriptionValue ?? "";
            _description.Visibility = string.IsNullOrEmpty(_descriptionValue) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_metadata is not null)
        {
            _metadata.Text = _metadataValue ?? "";
            _metadata.Visibility = string.IsNullOrEmpty(_metadataValue) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_badgeRoot is not null)
            _badgeRoot.Visibility = string.IsNullOrEmpty(_badgeValue) ? Visibility.Collapsed : Visibility.Visible;
        if (_badgeText is not null)
            _badgeText.Text = _badgeValue ?? "";
        if (_picture is not null)
        {
            _picture.ProfilePicture = _pictureValue;
            _picture.DisplayName = _pictureName ?? "";
        }
    }
}
