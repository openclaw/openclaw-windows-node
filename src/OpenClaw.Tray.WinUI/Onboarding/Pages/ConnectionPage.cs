using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using static OpenClawTray.Infrastructure.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 1: Connection / Gateway Selection.
/// Lets users choose Local (this PC), Remote, or Configure Later,
/// then enter gateway URL and token for Local/Remote modes.
/// </summary>
public sealed class ConnectionPage : Component<OnboardingState>
{
    private const string DefaultLocalUrl = "ws://localhost:18789";

    public override Element Render()
    {
        var (mode, setMode) = UseState(Props.Mode);
        var (url, setUrl) = UseState(Props.Settings.GatewayUrl);
        var (token, setToken) = UseState(Props.Settings.Token);
        var (statusMsg, setStatusMsg) = UseState("");

        void SelectMode(ConnectionMode m)
        {
            setMode(m);
            Props.Mode = m;

            if (m == ConnectionMode.Local)
            {
                setUrl(DefaultLocalUrl);
                Props.Settings.GatewayUrl = DefaultLocalUrl;
            }
        }

        void OnUrlChanged(string v)
        {
            setUrl(v);
            Props.Settings.GatewayUrl = v;
            setStatusMsg("");
        }

        void OnTokenChanged(string v)
        {
            setToken(v);
            Props.Settings.Token = v;
            setStatusMsg("");
        }

        void TestConnection()
        {
            Props.Settings.GatewayUrl = url;
            Props.Settings.Token = token;
            setStatusMsg("Settings saved ✓");
        }

        var showFields = mode != ConnectionMode.Later;

        var children = new List<Element>
        {
            // Title
            TextBlock(LocalizationHelper.GetString("Onboarding_Connection_Title"))
                .FontSize(24)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center),

            // Subtitle
            TextBlock(LocalizationHelper.GetString("Onboarding_Connection_Subtitle"))
                .FontSize(14)
                .Opacity(0.7)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping(),

            // Radio-style mode choices
            VStack(8,
                ModeChoice("Onboarding_Connection_Local", "🖥️  Local (This PC)",
                    mode == ConnectionMode.Local, () => SelectMode(ConnectionMode.Local)),
                ModeChoice("Onboarding_Connection_Remote", "🌐  Remote gateway",
                    mode == ConnectionMode.Remote, () => SelectMode(ConnectionMode.Remote)),
                ModeChoice("Onboarding_Connection_Later", "⏭️  Configure later",
                    mode == ConnectionMode.Later, () => SelectMode(ConnectionMode.Later))
            ).Margin(0, 16, 0, 0)
        };

        // Gateway URL + Token fields for Local/Remote
        if (showFields)
        {
            children.Add(
                Border(
                    VStack(12,
                        TextField(url, OnUrlChanged,
                            placeholder: "ws://host:port",
                            header: "Gateway URL"),
                        TextField(token, OnTokenChanged,
                            placeholder: "Paste token here",
                            header: "Token (optional)"),
                        HStack(8,
                            Button("Test Connection", TestConnection),
                            TextBlock(statusMsg)
                                .FontSize(12)
                                .Opacity(0.8)
                        )
                    ).Padding(16)
                )
                .CornerRadius(8)
                .Background("#F5F5F5")
                .Margin(0, 8, 0, 0)
            );
        }

        return VStack(16, children.ToArray())
            .MaxWidth(460)
            .Padding(0, 32, 0, 0);
    }

    private static Element ModeChoice(string labelKey, string fallback, bool selected, Action onSelect)
    {
        var label = LocalizationHelper.GetString(labelKey);
        if (label == labelKey) label = fallback;

        return Button(
            HStack(8,
                TextBlock(selected ? "●" : "○")
                    .FontSize(16)
                    .Opacity(selected ? 1.0 : 0.5),
                TextBlock(label).FontSize(14)
            ), onSelect)
            .HAlign(HorizontalAlignment.Stretch);
    }
}
