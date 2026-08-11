namespace OpenClawTray.Chat;

internal readonly record struct ChatComposerContextMenuState(
    bool ShowUndo,
    bool ShowRedo,
    bool ShowCut,
    bool ShowCopy,
    bool ShowPaste,
    bool ShowSelectAll,
    bool ShowEditSeparator,
    bool ShowSelectAllSeparator)
{
    public static ChatComposerContextMenuState Project(
        bool canUndo,
        bool canRedo,
        bool hasSelection,
        bool canPaste,
        bool hasText)
    {
        var showEditSeparator = (canUndo || canRedo) && (hasSelection || canPaste);
        var showSelectAllSeparator =
            hasText && (canUndo || canRedo || hasSelection || canPaste);

        return new ChatComposerContextMenuState(
            ShowUndo: canUndo,
            ShowRedo: canRedo,
            ShowCut: hasSelection,
            ShowCopy: hasSelection,
            ShowPaste: canPaste,
            ShowSelectAll: hasText,
            ShowEditSeparator: showEditSeparator,
            ShowSelectAllSeparator: showSelectAllSeparator);
    }
}
