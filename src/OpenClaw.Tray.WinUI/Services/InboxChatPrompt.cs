// Sends an Inbox "Ask OpenClaw" prompt to the main conversation through the chat
// provider, so the question and OpenClaw's answer appear in the native chat. The
// prompt is user-initiated text built from localized copy and Gateway labels; it
// is never logged.
//
// Usage:
//   if (InboxChatPrompt.TrySend(app.ChatProvider, app.GatewayClient, question, out var sessionKey))
//       SelectSession(sessionKey);
//   else
//       OpenConnectionSettings();

using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClawTray.Services;

internal static class InboxChatPrompt
{
    public static bool TrySend(
        OpenClawChatDataProvider? provider,
        IOperatorGatewayClient? client,
        string question,
        out string sessionKey)
    {
        sessionKey = client?.MainSessionKey ?? "";
        if (provider is null || client is not { IsConnectedToGateway: true } ||
            string.IsNullOrWhiteSpace(sessionKey) || string.IsNullOrWhiteSpace(question))
            return false;

        var target = sessionKey;
        _ = SendAsync(provider, target, question);
        return true;
    }

    private static async Task SendAsync(OpenClawChatDataProvider provider, string sessionKey, string question)
    {
        try
        {
            await provider.SendMessageAsync(sessionKey, question).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Inbox] Ask OpenClaw send failed: {ex.GetType().Name}");
        }
    }
}
