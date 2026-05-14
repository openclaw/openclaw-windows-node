using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.V2.Widgets;

public sealed record StepDotsProps(int Total, int CurrentIndex, ElementTheme Theme);

/// <summary>
/// Small horizontal sequence of dots indicating wizard progress.
/// Inactive dots are 8px circles in dim grey; the active dot is the
/// design accent (#60C8F8) at 10px. Spacing between dots is 8px.
/// Lives in the bottom-left of the V2 nav bar (see Dialog-1..Dialog-5).
/// Inactive colour switches with the host theme so the dots stay
/// visible against both the dark and light window backgrounds.
/// </summary>
public sealed class StepDots : Component<StepDotsProps>
{
    public override Element Render()
    {
        var dots = new List<Element?>();
        for (int i = 0; i < Props.Total; i++)
        {
            bool active = i == Props.CurrentIndex;
            double size = active ? 10 : 8;
            var brush = active ? V2Theme.AccentCyan() : V2Theme.StepDotInactive(Props.Theme);
            dots.Add(
                new BorderElement(null)
                    .Background(brush)
                    .Width(size)
                    .Height(size)
                    .VAlign(VerticalAlignment.Center)
                    .Set(b => b.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(size / 2.0))
            );
        }
        return HStack(8, dots.ToArray());
    }
}
