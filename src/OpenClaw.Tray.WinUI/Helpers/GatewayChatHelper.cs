using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Helpers;

/// <summary>
/// Shared helper for building gateway chat URLs and initializing WebView2.
/// Used by both the onboarding ChatPage and the standalone WebChatWindow
/// to ensure visual and behavioral consistency.
/// </summary>
public static class GatewayChatHelper
{
    private static readonly string s_userDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenClawTray", "WebView2");

    /// <summary>
    /// Build the HTTP(S) chat URL from a WebSocket gateway URL.
    /// Converts ws:// → http://, wss:// → https://, appends token and optional session key.
    /// </summary>
    /// <remarks>
    /// SECURITY NOTE: Token is passed as a URL query parameter (?token=...). This follows the
    /// existing WebChatWindow pattern in the repo. Tokens in URLs can leak to server access logs,
    /// WebView2 navigation logs, and Referrer headers. The NavigationStarting handler in
    /// OnboardingWindow.cs strips query params before logging to mitigate log exposure.
    /// Future improvement: inject token via Authorization header using CoreWebView2.AddWebResourceRequestedFilter.
    /// </remarks>
    public static bool TryBuildChatUrl(
        string gatewayUrl,
        string token,
        out string url,
        out string errorMessage,
        string? sessionKey = null)
    {
        return GatewayChatUrlBuilder.TryBuildChatUrl(gatewayUrl, token, out url, out errorMessage, sessionKey);
    }

    /// <summary>
    /// Initialize a WebView2 control with standard settings for gateway chat.
    /// Sets up user data folder, configures settings, and returns when ready.
    /// </summary>
    public static async Task InitializeWebView2Async(WebView2 webView)
    {
        Directory.CreateDirectory(s_userDataFolder);
        // Note: WEBVIEW2_USER_DATA_FOLDER is process-scoped (Environment.SetEnvironmentVariable
        // without EnvironmentVariableTarget defaults to Process). This follows the existing
        // WebChatWindow pattern. The env var only affects WebView2 instances in this process.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", s_userDataFolder);

        await webView.EnsureCoreWebView2Async();

        webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
        // SECURITY: Disable features not needed for chat that could aid attacks
        webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false; // Prevents Ctrl+U view-source, etc.
        webView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        webView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
    }

    private static bool IsLocalHost(Uri uri)
    {
        return GatewayChatUrlBuilder.IsLocalHost(uri);
    }
}
