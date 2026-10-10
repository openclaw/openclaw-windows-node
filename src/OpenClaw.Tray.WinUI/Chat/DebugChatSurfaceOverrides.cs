using System;

namespace OpenClawTray.Chat;

/// <summary>
/// Diagnostic override of the user's Settings.UseLegacyWebChat toggle.
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
/// Process-wide debug override for Workspace's native or WebView chat renderer.
/// Not persisted. Resets every launch. ChatPage observes Changed to swap renderers.
/// </summary>
public static class DebugChatSurfaceOverrides
{
    private static ChatSurfaceOverride _workspaceChat = ChatSurfaceOverride.NoOverride;

    /// <summary>Override for Workspace chat.</summary>
    public static ChatSurfaceOverride WorkspaceChat
    {
        get => _workspaceChat;
        set { if (_workspaceChat != value) { _workspaceChat = value; Changed?.Invoke(null, EventArgs.Empty); } }
    }

    /// <summary>Fires when the override changes.</summary>
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
