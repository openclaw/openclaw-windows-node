using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.SetupEngine.UI.Themes;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>
/// Gives toolkit <see cref="SettingsCard"/> rows the setup row title (15/22 Semibold). The card's
/// description keeps its own size and color, so only the header is replaced; its plain text stays
/// the card's accessible name.
/// </summary>
/// <example>
/// <code>
/// DistroCard.Header = summary.DistroTitle;
/// SetupRowPresentation.StyleHeaders(DistroCard, CliCard);
/// </code>
/// </example>
internal static class SetupRowPresentation
{
    private static SetupResources? s_resources;

    private static Style TitleStyle => (Style)(s_resources ??= new SetupResources())["SetupRowTitleTextStyle"];

    public static void StyleHeaders(params SettingsCard[] cards)
    {
        foreach (var card in cards)
        {
            if (card.Header is not string text)
                continue;
            if (string.IsNullOrEmpty(AutomationProperties.GetName(card)))
                AutomationProperties.SetName(card, text);
            card.Header = new TextBlock { Text = text, Style = TitleStyle };
        }
    }
}
