using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Widgets;

public record StepIndicatorProps(int TotalSteps, int CurrentStep);

/// <summary>
/// Dot-based step indicator for the onboarding navigation bar.
/// Current step is highlighted in accent blue; others are grey.
/// </summary>
public sealed class StepIndicator : Component<StepIndicatorProps>
{
    public override Element Render()
    {
        var dots = new Element[Props.TotalSteps];
        for (var i = 0; i < Props.TotalSteps; i++)
        {
            var color = i == Props.CurrentStep ? "#0078D4" : "#999999";
            dots[i] = Border(TextBlock(""))
                .Width(10)
                .Height(10)
                .CornerRadius(5)
                .Background(color);
        }

        return HStack(6, dots)
            .HAlign(HorizontalAlignment.Center);
    }
}
