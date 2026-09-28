using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;

namespace OpenClawTray.Helpers;

/// <summary>
/// Helper for WebView2-hosted gateway chat. Used today only by the
/// Onboarding flow's WebView2 overlay (the Hub Chat tab and tray
/// ChatWindow popup use the native Reactor surface with
/// <c>OpenClawTray.Chat.OpenClawReactorChatRoot</c> and <c>OpenClawChatDataProvider</c>).
/// Retire this helper when the onboarding chat surface is migrated too.
/// </summary>
public static class GatewayChatHelper
{
    private static readonly string s_userDataFolder = Path.Combine(
        AppIdentity.ResolveLocalDataDirectory(), "WebView2");

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebView2, PairedChatNavigation> s_navigation = new();
    // Optional one-time host-issued browser handoff. Never persist a shared password.
    private static string? s_browserHandoff = Environment.GetEnvironmentVariable("OPENCLAW_WEBCHAT_HANDOFF_URL");

    public static void NavigatePairedChat(WebView2 webView, string? url, bool force = false)
    {
        if (webView.CoreWebView2 is null || string.IsNullOrEmpty(url)) return;
        var state = s_navigation.GetOrCreateValue(webView);
        if (!state.ShouldNavigate(url, force)) return;
        var target = PairedChatNavigation.ResolveHandoff(url, s_browserHandoff);
        if (target != url)
        {
            s_browserHandoff = null;
            Environment.SetEnvironmentVariable("OPENCLAW_WEBCHAT_HANDOFF_URL", null);
        }
        webView.CoreWebView2.Navigate(target);
        state.RecordNavigation(url);
    }

    public static void ResetPairedChat(WebView2 webView) => s_navigation.Remove(webView);

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
