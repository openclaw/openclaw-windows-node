using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Widgets;

public record FeatureRowProps(string Icon, string Title, string Subtitle);

/// <summary>
/// Icon + title + subtitle row for the Ready page.
/// </summary>
public sealed class FeatureRow : Component<FeatureRowProps>
{
    public override Element Render()
    {
        return HStack(12,
            TextBlock(Props.Icon)
                .FontSize(20)
                .Width(28)
                .HAlign(HorizontalAlignment.Center),
            VStack(2,
                TextBlock(Props.Title)
                    .FontSize(14)
                    .FontWeight(new global::Windows.UI.Text.FontWeight(600)),
                TextBlock(Props.Subtitle)
                    .FontSize(12)
                    .Opacity(0.7)
            )
        );
    }
}
