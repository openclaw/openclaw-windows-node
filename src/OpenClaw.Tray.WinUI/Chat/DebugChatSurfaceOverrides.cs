using System;

namespace OpenClawTray.Chat;

/// <summary>
/// Per-chat-surface override of the user's Settings.UseLegacyWebChat toggle.
/// Picked from the Debug page so engineers can compare the legacy WebView and
/// the native chat side-by-side without flipping the global setting.
/// </summary>
public enum ChatSurfaceOverride
{
    /// <summary>Use the value of <c>Settings.UseLegacyWebChat</c>.</summary>
    NoOverride,
    /// <summary>Force the legacy WebView (gateway HTML chat).</summary>
    ForceLegacy,
    /// <summary>Force the native chat (Companion Chat UI).</summary>
    ForceNative,
}

/// <summary>
/// Process-wide debug overrides for which chat surface (legacy WebView or
/// native chat) renders inside each chat container. Not persisted — these
/// reset every app launch and are intended only for engineering A/B tests.
///
/// Subscribers (<see cref="OpenClawTray.Pages.ChatPage"/>,
/// <see cref="OpenClawTray.Windows.ChatWindow"/>) listen on
/// <see cref="Changed"/> and call their <c>ApplyChatSurface</c> hook to swap
/// in/out the active surface immediately.
/// </summary>
public static class DebugChatSurfaceOverrides
{
    private static ChatSurfaceOverride _hubChat = ChatSurfaceOverride.NoOverride;
    private static ChatSurfaceOverride _trayChat = ChatSurfaceOverride.NoOverride;

    /// <summary>Override for the Hub Chat tab (in-window NavigationView page).</summary>
    public static ChatSurfaceOverride HubChat
    {
        get => _hubChat;
        set { if (_hubChat != value) { _hubChat = value; Changed?.Invoke(null, EventArgs.Empty); } }
    }

    /// <summary>Override for the floating Tray Chat popup (near-tray window).</summary>
    public static ChatSurfaceOverride TrayChat
    {
        get => _trayChat;
        set { if (_trayChat != value) { _trayChat = value; Changed?.Invoke(null, EventArgs.Empty); } }
    }

    /// <summary>Fires when either override is changed by the Debug page.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Resolve the effective "use legacy WebView" flag for a chat surface:
    /// the override wins when set to a forced value; otherwise the user's
    /// <c>Settings.UseLegacyWebChat</c> applies.
    /// </summary>
    public static bool ResolveUseLegacy(ChatSurfaceOverride ovr, bool settingValue) => ovr switch
    {
        ChatSurfaceOverride.ForceLegacy => true,
        ChatSurfaceOverride.ForceNative => false,
        _ => settingValue,
    };
}
