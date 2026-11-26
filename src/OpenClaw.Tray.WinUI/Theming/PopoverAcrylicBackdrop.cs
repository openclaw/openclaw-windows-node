// <summary>
// System desktop acrylic for a windowed popup that renders in the popup content's light or dark theme. The stock
// DesktopAcrylicBackdrop on a Popup follows the application theme (the Windows app mode), so a Companion set to Dark,
// or a Gateway theme pinned to one mode, showed a light acrylic under white text when Windows apps were light.
// Otherwise it is the stock acrylic: same controller kind, input-active fallback and High Contrast behavior.
// Usage:
//   var acrylic = new PopoverAcrylicBackdrop();
//   popup.SystemBackdrop = acrylic;
//   acrylic.Theme = popupContent.ActualTheme; // again whenever the content's theme changes
// </summary>

using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Theming;

/// <summary>Desktop acrylic whose light or dark theme the owner sets explicitly. UI thread only.</summary>
internal sealed class PopoverAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _default;
    private readonly SystemBackdropConfiguration _configuration = new() { IsInputActive = true };
    private ElementTheme _theme;

    /// <summary>The theme the acrylic renders; <see cref="ElementTheme.Default"/> follows the application theme.</summary>
    public ElementTheme Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            UpdateConfiguration();
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (_controller is not null || !DesktopAcrylicController.IsSupported())
            return;
        _default = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _controller = new DesktopAcrylicController();
        UpdateConfiguration();
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (_controller is null)
            return;
        _controller.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller.Dispose();
        _controller = null;
        _default = null;
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // As in ThemedMicaBackdrop: the framework can call back without a target.
        if (target is not null)
            base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        UpdateConfiguration();
    }

    // The framework keeps the default configuration's activation and High Contrast state current; only the theme
    // comes from the owner.
    private void UpdateConfiguration()
    {
        if (_default is null)
            return;
        _configuration.IsInputActive = _default.IsInputActive;
        _configuration.IsHighContrast = _default.IsHighContrast;
        _configuration.HighContrastBackgroundColor = _default.HighContrastBackgroundColor;
        _configuration.Theme = _theme switch
        {
            ElementTheme.Light => SystemBackdropTheme.Light,
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            _ => _default.Theme,
        };
    }
}
