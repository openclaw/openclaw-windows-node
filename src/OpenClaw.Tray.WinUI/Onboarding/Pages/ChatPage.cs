using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using static OpenClawTray.Infrastructure.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 8: Meet your Agent — placeholder chat UI.
/// Shows a simulated chat area with an agent welcome message,
/// a text input, and a send button. Full WebView2 chat integration
/// will be added in a future iteration.
/// </summary>
public sealed class ChatPage : Component<OnboardingState>
{
    public override Element Render()
    {
        var (userInput, setUserInput) = UseState("");
        var (userMessages, setUserMessages) = UseState(Array.Empty<string>());

        void OnSend()
        {
            if (!string.IsNullOrWhiteSpace(userInput))
            {
                setUserMessages([.. userMessages, userInput]);
                setUserInput("");
            }
        }

        var chatBubbles = new List<Element>
        {
            // Agent welcome message
            ChatBubble(
                "🦞 Agent",
                "Hi! I'm your OpenClaw agent. I can help you with tasks, answer questions, and automate workflows.",
                "#E8F4FD",
                HorizontalAlignment.Left)
        };

        foreach (var msg in userMessages)
        {
            chatBubbles.Add(
                ChatBubble("You", msg, "#F0F0F0", HorizontalAlignment.Right));
        }

        return VStack(16,
            // Title
            TextBlock("Meet your Agent")
                .FontSize(24)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center),

            // Subtitle
            TextBlock("Have a quick chat to get started")
                .FontSize(14)
                .Opacity(0.7)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping(),

            // Chat area
            Border(
                VStack(8, chatBubbles.ToArray())
                    .Padding(12)
            )
            .CornerRadius(8)
            .Background("#FAFAFA")
            .Height(200)
            .Margin(0, 8, 0, 0),

            // Input row
            HStack(8,
                TextField(userInput, v => setUserInput(v),
                    placeholder: "Type a message..."),
                Button("Send", OnSend)
            ).Margin(0, 4, 0, 0),

            // Footer note
            TextBlock("Full chat integration coming soon. You can chat from the tray menu anytime.")
                .FontSize(11)
                .Opacity(0.5)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping()
                .Margin(0, 8, 0, 0)
        )
        .MaxWidth(460)
        .Padding(0, 16, 0, 0);
    }

    private static Element ChatBubble(string sender, string message, string bgColor, HorizontalAlignment align)
    {
        return Border(
            VStack(4,
                TextBlock(sender)
                    .FontSize(11)
                    .FontWeight(new global::Windows.UI.Text.FontWeight(600))
                    .Opacity(0.6),
                TextBlock(message)
                    .FontSize(13)
                    .TextWrapping()
            ).Padding(10)
        )
        .CornerRadius(8)
        .Background(bgColor)
        .HAlign(align);
    }
}
