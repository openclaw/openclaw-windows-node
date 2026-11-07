using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OpenClawTray.Windows;

/// <summary>
/// Owns the workspace sidebar session context menu: one shared <see cref="MenuFlyout"/>
/// repopulated on open (target row read from the item's <c>Tag</c>), plus the rename /
/// confirm dialogs and gateway dispatch for every action. <see cref="WorkspaceWindow"/>
/// supplies the row lookups, fork/create dispatch, navigation, and status surfaces.
/// </summary>
internal sealed class WorkspaceSessionMenuController
{
    private readonly Func<IOperatorGatewayClient?> _client;
    private readonly Func<string, SessionInfo?> _findSession;
    private readonly Func<IReadOnlyList<SessionInfo>> _activeSessions;
    private readonly Func<XamlRoot?> _xamlRoot;
    private readonly Func<IntPtr> _hwnd;
    private readonly Func<SessionCreateRequest, Task> _forkSession;
    private readonly Action<string> _leaveSession;
    private readonly Action<string, InfoBarSeverity> _showMessage;
    private readonly Action<string, Exception> _reportError;
    private bool _dialogOpen;

    public WorkspaceSessionMenuController(
        Func<IOperatorGatewayClient?> client,
        Func<string, SessionInfo?> findSession,
        Func<IReadOnlyList<SessionInfo>> activeSessions,
        Func<XamlRoot?> xamlRoot,
        Func<IntPtr> hwnd,
        Func<SessionCreateRequest, Task> forkSession,
        Action<string> leaveSession,
        Action<string, InfoBarSeverity> showMessage,
        Action<string, Exception> reportError)
    {
        _client = client;
        _findSession = findSession;
        _activeSessions = activeSessions;
        _xamlRoot = xamlRoot;
        _hwnd = hwnd;
        _forkSession = forkSession;
        _leaveSession = leaveSession;
        _showMessage = showMessage;
        _reportError = reportError;
        Flyout.Opening += OnFlyoutOpening;
    }

    public MenuFlyout Flyout { get; } = new();

    /// <summary>Per-row flyout: captures the row, so no target lookup is needed.</summary>
    public MenuFlyout CreateFlyout(WorkspaceSession session)
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => Populate(flyout, session);
        return flyout;
    }


    /// <summary>
    /// Background read acknowledgement uses the same acceptance contract, but
    /// reports failure to diagnostics rather than producing a user-action toast.
    /// </summary>
    public async Task AcknowledgeReadAsync(SessionInfo session)
    {
        var client = _client();
        if (!session.Unread || client is not { IsConnectedToGateway: true })
            return;
        var operation = new WorkspaceSessionOperation(client, _client);
        try
        {
            await operation.PatchAsync(session.Key, WorkspaceSessionMenu.ReadAcknowledgementPatch(session.MarkedUnreadAt));
            await RefreshAsync(operation);
        }
        catch (Exception ex)
        {
            new AppLogger().Warn($"[Workspace] Read acknowledgement failed ({ex.GetType().Name}). Unread state will be reconciled on refresh.");
        }
    }

    private void OnFlyoutOpening(object? sender, object e)
    {
        if ((Flyout.Target as FrameworkElement)?.Tag is not WorkspaceSession session)
        {
            Flyout.Hide();
            return;
        }

        Populate(Flyout, session);
    }

    private void Populate(MenuFlyout flyout, WorkspaceSession session)
    {
        flyout.Items.Clear();
        var info = _findSession(session.Key);
        var client = _client();
        var mainState = SessionActionPlanner.ResolveMainState(
            session.Key,
            rowIsMain: info?.IsMain,
            mainSessionKey: client?.MainSessionKey,
            sessions: _activeSessions());
        var state = new WorkspaceSessionMenuState(
            session.IsPinned, session.IsUnread, session.IsArchived,
            !string.IsNullOrWhiteSpace(info?.SessionId), mainState,
            client is { IsConnectedToGateway: true });

        if (info?.UpdatedAt is { } updatedAt)
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = LocalizationHelper.Format(
                    "WorkspaceShell_SessionMenu_LastActiveFormat", ModelFormatting.FormatAge(updatedAt)),
                IsEnabled = false,
            });

        foreach (var entry in WorkspaceSessionMenu.Build(state))
            flyout.Items.Add(Render(entry, session, info));
    }

    private MenuFlyoutItemBase Render(
        WorkspaceSessionMenuEntry entry, WorkspaceSession session, SessionInfo? info)
    {
        if (entry.Kind == WorkspaceSessionMenuEntryKind.Separator)
            return new MenuFlyoutSeparator();
        if (entry.Kind == WorkspaceSessionMenuEntryKind.SubMenu)
        {
            var sub = new MenuFlyoutSubItem
            {
                Text = WorkspaceWindow.Text(entry.LabelKey),
                Icon = entry.Icon == WorkspaceSessionMenuIcon.None
                    ? null
                    : FluentIconCatalog.Build(IconGlyph(entry.Icon)),
                AccessKey = entry.AccessKey,
                IsEnabled = entry.IsEnabled,
            };
            AutomationProperties.SetAutomationId(sub, "WorkspaceSessionMenuCopy");
            foreach (var child in entry.Children ?? [])
                sub.Items.Add(Render(child, session, info));
            return sub;
        }

        var item = new MenuFlyoutItem
        {
            Text = WorkspaceWindow.Text(entry.LabelKey),
            Icon = entry.Icon == WorkspaceSessionMenuIcon.None
                ? null
                : FluentIconCatalog.Build(IconGlyph(entry.Icon)),
            AccessKey = entry.AccessKey,
            IsEnabled = entry.IsEnabled,
        };
        AutomationProperties.SetAutomationId(item, $"WorkspaceSessionMenu{entry.Action}");
        if (entry.IsDestructive)
        {
            var critical = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            item.Foreground = critical;
            if (item.Icon is FontIcon icon)
                icon.Foreground = critical;
        }
        item.Click += (_, _) => AsyncEventHandlerGuard.Run(
            () => RunAsync(entry.Action, session, info),
            new AppLogger(),
            nameof(RunAsync));
        return item;
    }

    private static string IconGlyph(WorkspaceSessionMenuIcon icon) => icon switch
    {
        WorkspaceSessionMenuIcon.Pin => FluentIconCatalog.Pin,
        WorkspaceSessionMenuIcon.Unpin => FluentIconCatalog.Unpin,
        WorkspaceSessionMenuIcon.Rename => FluentIconCatalog.Rename,
        WorkspaceSessionMenuIcon.MarkUnread => FluentIconCatalog.MarkUnread,
        WorkspaceSessionMenuIcon.MarkRead => FluentIconCatalog.MarkRead,
        WorkspaceSessionMenuIcon.Archive => FluentIconCatalog.Archive,
        WorkspaceSessionMenuIcon.Unarchive => FluentIconCatalog.Unarchive,
        WorkspaceSessionMenuIcon.Fork => FluentIconCatalog.Fork,
        WorkspaceSessionMenuIcon.Copy => FluentIconCatalog.Copy,
        WorkspaceSessionMenuIcon.Reset => FluentIconCatalog.Reset,
        WorkspaceSessionMenuIcon.Compact => FluentIconCatalog.Compact,
        WorkspaceSessionMenuIcon.Export => FluentIconCatalog.Export,
        WorkspaceSessionMenuIcon.Delete => FluentIconCatalog.Delete,
        _ => FluentIconCatalog.Copy,
    };

    private async Task RunAsync(WorkspaceSessionMenuAction action, WorkspaceSession session, SessionInfo? info)
    {
        try
        {
            await RunActionAsync(action, session, info);
        }
        catch (WorkspaceSessionConnectionChangedException)
        {
            _showMessage(WorkspaceWindow.Text("SessionConnectionChanged"), InfoBarSeverity.Error);
        }
        catch (TimeoutException)
        {
            _showMessage(WorkspaceWindow.Text("SessionActionTimedOut"), InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            _reportError($"session-{action}", ex);
        }
    }

    private async Task RunActionAsync(WorkspaceSessionMenuAction action, WorkspaceSession session, SessionInfo? info)
    {
        var client = _client();
        if (client is not { IsConnectedToGateway: true } &&
            action is not (WorkspaceSessionMenuAction.CopyKey or WorkspaceSessionMenuAction.CopySessionId))
        {
            _showMessage(WorkspaceWindow.Text("ConnectionRequired"), InfoBarSeverity.Error);
            return;
        }

        var operation = client is null ? null : new WorkspaceSessionOperation(client, _client);
        switch (action)
        {
            case WorkspaceSessionMenuAction.TogglePin:
                await PatchAsync(operation!, session.Key, new SessionPatch { Pinned = !session.IsPinned });
                break;
            case WorkspaceSessionMenuAction.ToggleUnread:
                await PatchAsync(operation!, session.Key, new SessionPatch { Unread = !session.IsUnread });
                break;
            case WorkspaceSessionMenuAction.ToggleArchive:
                await ToggleArchiveAsync(operation!, session, info);
                break;
            case WorkspaceSessionMenuAction.Rename:
                await RenameAsync(operation!, session, info);
                break;
            case WorkspaceSessionMenuAction.Fork:
                await _forkSession(WorkspaceSessionMenu.ForkRequest(
                    info ?? new SessionInfo { Key = session.Key }));
                break;
            case WorkspaceSessionMenuAction.CopyKey:
                CopyToClipboard(session.Key, "CopyFailed");
                break;
            case WorkspaceSessionMenuAction.CopySessionId:
                CopyToClipboard(info?.SessionId ?? "", "CopyFailed");
                break;
            case WorkspaceSessionMenuAction.CopyMarkdown:
                await CopyMarkdownAsync(client!, session);
                break;
            case WorkspaceSessionMenuAction.Reset:
                await RunLifecycleAsync(operation!, SessionActionKind.Reset, session, info);
                break;
            case WorkspaceSessionMenuAction.Compact:
                await RunLifecycleAsync(operation!, SessionActionKind.Compact, session, info);
                break;
            case WorkspaceSessionMenuAction.Delete:
                await RunLifecycleAsync(operation!, SessionActionKind.Delete, session, info);
                break;
            case WorkspaceSessionMenuAction.ExportTranscript:
                await ExportTranscriptAsync(client!, session);
                break;
        }
    }

    private async Task ToggleArchiveAsync(WorkspaceSessionOperation operation, WorkspaceSession session, SessionInfo? info)
    {
        operation.RequireCurrent();
        if (!session.IsArchived)
        {
            // Archiving the open conversation must navigate away before the row
            // disappears; the main session can never be archived.
            var mainState = SessionActionPlanner.ResolveMainState(
                session.Key,
                rowIsMain: info?.IsMain,
                mainSessionKey: operation.Client.MainSessionKey,
                sessions: _activeSessions());
            if (!WorkspaceSessionMenu.CanArchiveOrDelete(mainState))
            {
                _showMessage(
                    mainState == SessionMainState.Unknown
                        ? "Session identity is still loading. Try again after sessions refresh."
                        : "The main session can't be archived. Reset it instead to start fresh.",
                    InfoBarSeverity.Informational);
                return;
            }
        }
        await operation.PatchAsync(session.Key, new SessionPatch { Archived = !session.IsArchived });
        if (!session.IsArchived)
            _leaveSession(session.Key);
        await RefreshAsync(operation);
    }

    private async Task PatchAsync(WorkspaceSessionOperation operation, string key, SessionPatch patch)
    {
        await operation.PatchAsync(key, patch);
        await RefreshAsync(operation);
    }

    private async Task RefreshAsync(WorkspaceSessionOperation operation)
    {
        if (!operation.IsCurrent)
            return;
        try
        {
            await operation.Client.RequestSessionsAsync();
        }
        catch (Exception ex)
        {
            // Acceptance already happened; a refresh error must not be reported as a rejected mutation.
            new AppLogger().Warn($"[Workspace] Session mutation accepted, but refresh failed ({ex.GetType().Name}).");
        }
    }

    private async Task RenameAsync(WorkspaceSessionOperation operation, WorkspaceSession session, SessionInfo? info)
    {
        if (_dialogOpen || _xamlRoot() is not { } xamlRoot)
            return;
        _dialogOpen = true;
        try
        {
            var input = new TextBox
            {
                // Seed only with an explicit label; blank clears to the derived title.
                Text = info?.Label ?? string.Empty,
                PlaceholderText = session.Title,
                MaxLength = 512,
                AcceptsReturn = false,
                SelectionStart = 0,
            };
            input.Loaded += (_, _) =>
            {
                input.Focus(FocusState.Programmatic);
                input.SelectAll();
            };

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = WorkspaceWindow.Text("SessionRename_Body"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(input);

            var dialog = new ContentDialog
            {
                Title = WorkspaceWindow.Text("SessionRename_Title"),
                Content = content,
                PrimaryButtonText = WorkspaceWindow.Text("SessionRename_Save"),
                CloseButtonText = LocalizationHelper.GetString("SessionActionPrompt_CancelLabel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await PatchAsync(operation, session.Key, WorkspaceSessionMenu.RenamePatch(input.Text));
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void CopyToClipboard(string text, string failureKey)
    {
        try
        {
            ClipboardHelper.CopyText(text);
        }
        catch (Exception)
        {
            _showMessage(WorkspaceWindow.Text(failureKey), InfoBarSeverity.Error);
        }
    }

    private async Task CopyMarkdownAsync(IOperatorGatewayClient client, WorkspaceSession session)
    {
        ChatHistoryInfo history;
        try
        {
            history = await client.RequestChatHistoryAsync(session.Key);
        }
        catch (NotSupportedException)
        {
            _showMessage(WorkspaceWindow.Text("HistoryUnsupported"), InfoBarSeverity.Informational);
            return;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("unknown method", StringComparison.OrdinalIgnoreCase))
        {
            _showMessage(WorkspaceWindow.Text("HistoryUnsupported"), InfoBarSeverity.Informational);
            return;
        }
        catch (Exception ex)
        {
            _reportError("copy-markdown", ex);
            return;
        }

        var markdown = SessionTranscriptFormatter.FormatMarkdown(history, session.Title);
        if (markdown is null)
        {
            _showMessage(WorkspaceWindow.Text("CopyEmpty"), InfoBarSeverity.Informational);
            return;
        }
        CopyToClipboard(markdown, "CopyFailed");
    }

    private async Task RunLifecycleAsync(
        WorkspaceSessionOperation operation, SessionActionKind kind, WorkspaceSession session, SessionInfo? info)
    {
        operation.RequireCurrent();
        var client = operation.Client;
        var mainState = SessionActionPlanner.ResolveMainState(
            session.Key,
            rowIsMain: info?.IsMain,
            mainSessionKey: client.MainSessionKey,
            sessions: _activeSessions());
        if (!SessionActionPlanner.IsAllowed(kind, mainState, out var blockedReason))
        {
            _showMessage(blockedReason ?? WorkspaceWindow.Text("SessionActionFailed"), InfoBarSeverity.Informational);
            return;
        }

        var prompt = SessionActionPlanner.BuildPrompt(kind, session.Key, session.Title, mainState == SessionMainState.Main);
        if (prompt is not null && !await ConfirmAsync(prompt))
            return;
        operation.RequireCurrent();

        if (kind == SessionActionKind.Delete)
        {
            // Re-resolve: the row may have changed identity while the dialog was open.
            var latestState = SessionActionPlanner.ResolveMainState(
                session.Key,
                rowIsMain: _findSession(session.Key)?.IsMain,
                mainSessionKey: client.MainSessionKey,
                sessions: _activeSessions());
            if (!SessionActionPlanner.IsAllowed(kind, latestState, out blockedReason))
            {
                _showMessage(blockedReason ?? WorkspaceWindow.Text("SessionActionFailed"), InfoBarSeverity.Informational);
                return;
            }
        }

        if (kind == SessionActionKind.Delete)
        {
            await operation.DeleteAsync(session.Key);
            _leaveSession(session.Key);
            await RefreshAsync(operation);
        }
        else
        {
            var sent = kind switch
            {
                SessionActionKind.Reset => await client.ResetSessionAsync(session.Key),
                SessionActionKind.Compact => await client.CompactSessionAsync(session.Key),
                _ => true,
            };
            if (!sent)
            {
                _showMessage(WorkspaceWindow.Text("SessionActionFailed"), InfoBarSeverity.Error);
                return;
            }
        }
    }

    private async Task<bool> ConfirmAsync(SessionActionPrompt prompt)
    {
        if (_xamlRoot() is not { } xamlRoot)
            return false;
        var localizedPrompt = SessionActionPromptLocalizer.Localize(prompt);
        var dialog = new ContentDialog
        {
            Title = localizedPrompt.Title,
            Content = localizedPrompt.Body,
            PrimaryButtonText = localizedPrompt.ConfirmLabel,
            CloseButtonText = LocalizationHelper.GetString("SessionActionPrompt_CancelLabel"),
            DefaultButton = ContentDialogButton.None,
            XamlRoot = xamlRoot,
        };
        if (localizedPrompt.IsDestructive)
            dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ExportTranscriptAsync(IOperatorGatewayClient client, WorkspaceSession session)
    {
        var hwnd = _hwnd();
        if (hwnd == IntPtr.Zero)
        {
            _showMessage(WorkspaceWindow.Text("ExportEmpty"), InfoBarSeverity.Informational);
            return;
        }

        ChatHistoryInfo history;
        try
        {
            history = await client.RequestChatHistoryAsync(session.Key);
        }
        catch (NotSupportedException)
        {
            _showMessage(WorkspaceWindow.Text("HistoryUnsupported"), InfoBarSeverity.Informational);
            return;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("unknown method", StringComparison.OrdinalIgnoreCase))
        {
            _showMessage(WorkspaceWindow.Text("HistoryUnsupported"), InfoBarSeverity.Informational);
            return;
        }
        catch (Exception ex)
        {
            _reportError("export-transcript", ex);
            return;
        }

        if (history.Messages.Count == 0)
        {
            _showMessage(WorkspaceWindow.Text("ExportEmpty"), InfoBarSeverity.Informational);
            return;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
                SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(
                    SessionTranscriptFormatter.SuggestFileName(session.Key)),
            };
            picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file == null) return; // user cancelled

            await FileIO.WriteTextAsync(file, SessionTranscriptFormatter.Format(history));
            _showMessage(
                LocalizationHelper.Format("WorkspaceShell_ExportDone", history.Messages.Count, file.Name),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _reportError("export-transcript", ex);
        }
    }
}
