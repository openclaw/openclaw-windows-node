using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;

namespace OpenClawTray.Helpers;

/// <summary>
/// Helper for WebView2-hosted gateway chat. Used today only by the
/// Onboarding flow's WebView2 overlay (the Hub Chat tab and tray
/// ChatWindow popup were migrated to native FunctionalUI controls — see
/// <c>OpenClawTray.Chat.OpenClawChatRoot</c> + <c>OpenClawChatDataProvider</c>).
/// Retire this helper when the onboarding chat surface is migrated too.
/// </summary>
public static class GatewayChatHelper
{
    private static readonly string s_userDataFolder = Path.Combine(
        AppIdentity.ResolveLocalDataDirectory(), "WebView2");

    /// <summary>
    /// Build the HTTP(S) chat URL from a WebSocket gateway URL.
    /// Delegates to <see cref="GatewayChatUrlBuilder"/>; kept here so the
    /// existing onboarding callsite signature is preserved.
    /// </summary>
    public static bool TryBuildChatUrl(
        string gatewayUrl,
        string token,
        out string url,
        out string errorMessage,
        string? sessionKey = null)
        => GatewayChatUrlBuilder.TryBuildChatUrl(gatewayUrl, token, out url, out errorMessage, sessionKey);

    /// <summary>
    /// Initialize a WebView2 control with standard settings for gateway chat.
    /// </summary>
    public static async Task InitializeWebView2Async(WebView2 webView)
    {
        Directory.CreateDirectory(s_userDataFolder);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", s_userDataFolder);

        await webView.EnsureCoreWebView2Async();

        webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
        webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        webView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        webView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
    }
}
