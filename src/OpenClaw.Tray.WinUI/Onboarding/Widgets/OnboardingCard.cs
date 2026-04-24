using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Core;
using static OpenClawTray.Infrastructure.Factories;

namespace OpenClawTray.Onboarding.Widgets;

/// <summary>
/// Reusable card with rounded corners, white background, and padding.
/// Props: the child <see cref="Element"/> to render inside the card.
/// </summary>
public sealed class OnboardingCard : Component<Element>
{
    public override Element Render()
    {
        return Border(
            Props
        )
        .CornerRadius(12)
        .Background("#FFFFFF")
        .Padding(20, 20, 20, 20);
    }
}
