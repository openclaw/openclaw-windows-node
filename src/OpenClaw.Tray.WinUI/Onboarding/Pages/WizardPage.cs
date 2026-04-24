using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using static OpenClawTray.Infrastructure.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 3: Configuring Gateway — hybrid wizard page.
/// When a gateway is reachable, RPC-driven wizard steps (wizard.start / wizard.next)
/// will render dynamic content. For now (Phase 3 MVP), shows a native offline fallback
/// with Gateway URL, Token, Node Mode, and a Test Connection button.
/// </summary>
public sealed class WizardPage : Component<OnboardingState>
{
    public override Element Render()
    {
        var (url, setUrl) = UseState(Props.Settings.GatewayUrl);
        var (token, setToken) = UseState(Props.Settings.Token);
        var (nodeMode, setNodeMode) = UseState(Props.Settings.EnableNodeMode);
        var (statusMsg, setStatusMsg) = UseState("");

        // TODO: RPC wizard integration (Phase 3+)
        // When the gateway is reachable, call wizard.start via WebSocket RPC
        // to get dynamic setup steps, then render them here instead of the
        // offline fallback below. Use wizard.next to advance through steps.
        // UseEffect(() => { /* connect & call wizard.start */ }, url);

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

        void OnNodeModeToggled(bool v)
        {
            setNodeMode(v);
            Props.Settings.EnableNodeMode = v;
        }

        void TestConnection()
        {
            Props.Settings.GatewayUrl = url;
            Props.Settings.Token = token;

            if (string.IsNullOrWhiteSpace(url))
            {
                setStatusMsg("⚠️ Gateway URL is required");
                return;
            }

            // TODO: Actually attempt WebSocket connection to validate reachability
            setStatusMsg("✓ Settings saved — gateway connection will be verified on launch");
        }

        return VStack(16,
            // Title
            TextBlock("Configuring Gateway")
                .FontSize(24)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center),

            // Subtitle explaining the wizard concept
            TextBlock("The gateway provides dynamic setup steps to configure your environment. "
                     + "When offline, you can manually configure the connection below.")
                .FontSize(14)
                .Opacity(0.7)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping(),

            // Offline fallback configuration card
            Border(
                VStack(12,
                    TextField(url, OnUrlChanged,
                        placeholder: "ws://host:port",
                        header: "Gateway URL"),

                    TextField(token, OnTokenChanged,
                        placeholder: "Paste token here",
                        header: "Token (optional)"),

                    // Node Mode toggle
                    HStack(8,
                        ToggleButton("", nodeMode, OnNodeModeToggled)
                            .Width(40),
                        TextBlock("Enable Node Mode")
                            .FontSize(13)
                    ),

                    // Test Connection button + status
                    HStack(8,
                        Button("Test Connection", TestConnection),
                        TextBlock(statusMsg)
                            .FontSize(12)
                            .Opacity(0.8)
                            .TextWrapping()
                    )
                ).Padding(16)
            )
            .CornerRadius(8)
            .Background("#F5F5F5")
            .Margin(0, 8, 0, 0)
        )
        .MaxWidth(460)
        .Padding(0, 32, 0, 0);
    }
}
