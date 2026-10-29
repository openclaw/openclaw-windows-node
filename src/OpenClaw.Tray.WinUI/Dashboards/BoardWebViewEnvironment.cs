using Microsoft.Web.WebView2.Core;

namespace OpenClawTray.Dashboards;

/// <summary>
/// WebView2 environment for untrusted board content. It uses its own user
/// data folder, so widgets run in a browser process separate from Canvas and
/// Gateway Chat, and every controller uses an in-private profile so nothing
/// persists between sessions.
/// </summary>
internal static class BoardWebViewEnvironment
{
    public const string WidgetProfile = "openclaw-board-widgets";

    /// <summary>Prefix of the per-widget website profiles; see <see cref="NewWebsiteProfileName"/>.</summary>
    public const string WebsiteProfilePrefix = "openclaw-board-website-";

    /// <summary>
    /// A fresh profile name for one website WebView, so same-origin sites in
    /// different website widgets never share cookies or storage. HTML widgets
    /// share <see cref="WidgetProfile"/> safely: their content runs in an
    /// opaque-origin sandbox frame without <c>allow-same-origin</c>.
    /// </summary>
    public static string NewWebsiteProfileName() => WebsiteProfilePrefix + Guid.NewGuid().ToString("N");

    private static Task<CoreWebView2Environment>? s_environment;

    /// <summary>
    /// Shared environment. WebView2 environments are created and used on the UI
    /// thread. A failed creation is not cached, so a later widget can retry,
    /// for example after a WebView2 runtime install.
    /// </summary>
    public static Task<CoreWebView2Environment> GetAsync()
    {
        if (s_environment is null or { IsFaulted: true } or { IsCanceled: true })
            s_environment = CreateAsync();
        return s_environment;
    }

    public static CoreWebView2ControllerOptions CreateControllerOptions(CoreWebView2Environment environment, string profile)
    {
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = profile;
        options.IsInPrivateModeEnabled = true;
        return options;
    }

    /// <summary>Applies settings shared by every board WebView: no host access, tools, or browser chrome.</summary>
    public static void ApplyLockedDownSettings(CoreWebView2Settings settings)
    {
        settings.IsScriptEnabled = true;
        settings.IsWebMessageEnabled = true;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
    }

    /// <summary>Denies device permissions, downloads, external protocols, and auth prompts.</summary>
    public static void DenyAmbientCapabilities(CoreWebView2 core)
    {
        core.PermissionRequested += (_, args) =>
        {
            args.State = CoreWebView2PermissionState.Deny;
            args.Handled = true;
        };
        core.DownloadStarting += (_, args) =>
        {
            args.Cancel = true;
            args.Handled = true;
        };
        core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
        core.BasicAuthenticationRequested += (_, args) => args.Cancel = true;
    }

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        var userDataFolder = Path.Combine(AppIdentity.ResolveLocalDataDirectory(), "WebView2-Dashboards");
        Directory.CreateDirectory(userDataFolder);
        return await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, new CoreWebView2EnvironmentOptions());
    }
}
