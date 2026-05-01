using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Widgets;

/// <summary>
/// Lobster emoji icon rendered at large size, centered.
/// Can be enhanced with glow/pulse animation later.
/// </summary>
public sealed class GlowingIcon : Component
{
    public override Element Render()
    {
        return TextBlock("🦞")
            .FontSize(48)
            .HAlign(HorizontalAlignment.Center);
    }
}
