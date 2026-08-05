using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace OpenClawTray.Dialogs;

internal sealed class SessionCheckpointDialogCoordinator
{
    private static App CurrentApp => (App)Application.Current!;
    private static readonly ConditionalWeakTable<XamlRoot, SemaphoreSlim> DialogGates = new();

    private readonly XamlRoot _xamlRoot;
    private readonly Func<bool> _isHostAvailable;
    private readonly Func<string, string, InfoBarSeverity, Task>? _showStatusAsync;

    private SessionCheckpointDialogCoordinator(
        XamlRoot xamlRoot,
        Func<bool> isHostAvailable,
        Func<string, string, InfoBarSeverity, Task>? showStatusAsync)
    {
        _xamlRoot = xamlRoot;
        _isHostAvailable = isHostAvailable;
        _showStatusAsync = showStatusAsync;
    }
    public static async Task ShowAsync(
        XamlRoot? xamlRoot,
        string sessionKey,
        Func<bool>? isHostAvailable = null,
        Func<string, string, InfoBarSeverity, Task>? showStatusAsync = null,
        string? displayName = null,
        bool? rowIsMain = null)
    {
        if (xamlRoot is null || string.IsNullOrWhiteSpace(sessionKey))
            return;

        var gate = DialogGates.GetValue(xamlRoot, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0))
            return;

        try
        {
            var coordinator = new SessionCheckpointDialogCoordinator(
                xamlRoot,
                isHostAvailable ?? (() => true),
                showStatusAsync);
            await coordinator.ShowCoreAsync(sessionKey, displayName, rowIsMain);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ShowCoreAsync(string key, string? displayName, bool? rowIsMain)
    {
        var client = CurrentApp.GatewayClient;
        if (client is null)
        {
            await ShowStatusAsync(
                LocalizationHelper.GetString("SessionsPage_GatewayDisconnected.Title"),
                LocalizationHelper.GetString("SessionsPage_GatewayDisconnected.Message"),
                InfoBarSeverity.Warning);
            return;
        }

        var session = CurrentApp.AppState?.Sessions?
            .FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
        var name = SessionActionPlanner.Describe(key, displayName ?? session?.DisplayName);
        var isMainState = ResolveMainState(key, rowIsMain ?? session?.IsMain);
        var isMain = isMainState == SessionMainState.Main;

        SessionCompactionCheckpointList list;
        try
        {
            list = await client.ListCompactionCheckpointsAsync(key);
        }
        catch (Exception ex)
        {
            await ShowStatusAsync("Couldn't load checkpoints", ex.Message, InfoBarSeverity.Error);
            return;
        }

        if (!list.IsSupported)
        {
            await ShowStatusAsync(
                "Not supported",
                "This gateway doesn't support session compaction checkpoints. Update the gateway to use this.",
                InfoBarSeverity.Informational);
            return;
        }

        if (!_isHostAvailable())
            return;

        var checkpoints = list.Checkpoints
            .OrderByDescending(checkpoint => checkpoint.CreatedAt ?? DateTime.MinValue)
            .ToList();
        var branchTarget = checkpoints.FirstOrDefault(checkpoint => !string.IsNullOrWhiteSpace(checkpoint.Id));
        var restoreTarget = SessionCheckpointSelection.ResolveUnambiguousLatest(checkpoints);
        var canRestore = restoreTarget is not null
            && SessionActionPlanner.IsAllowed(SessionActionKind.Restore, isMainState, out _);

        var body = BuildDialogBody(
            checkpoints,
            branchTarget,
            restoreTarget,
            canRestore,
            isMainState);
        var dialog = new ContentDialog
        {
            Title = $"Checkpoints: {name}",
            Content = body,
            PrimaryButtonText = branchTarget is not null
                ? (restoreTarget is not null ? "Branch from latest" : "Branch from latest targetable")
                : "",
            SecondaryButtonText = canRestore ? "Restore latest" : "",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _xamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && branchTarget is not null)
            await BranchCheckpointAsync(key, branchTarget.Id);
        else if (result == ContentDialogResult.Secondary && restoreTarget is not null)
            await RestoreCheckpointAsync(key, name, isMain, restoreTarget.Id);
    }

    private static StackPanel BuildDialogBody(
        IReadOnlyCollection<SessionCompactionCheckpoint> checkpoints,
        SessionCompactionCheckpoint? branchTarget,
        SessionCompactionCheckpoint? restoreTarget,
        bool canRestore,
        SessionMainState mainState)
    {
        var body = new StackPanel { Spacing = 12 };
        if (checkpoints.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "No compaction checkpoints yet. Compacting this session creates one you can branch from or restore to.",
                TextWrapping = TextWrapping.Wrap,
            });
            return body;
        }

        body.Children.Add(new TextBlock
        {
            Text = $"{checkpoints.Count} checkpoint{(checkpoints.Count == 1 ? "" : "s")} \u00B7 newest first",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });

        var listPanel = new StackPanel { Spacing = 4 };
        foreach (var checkpoint in checkpoints)
        {
            listPanel.Children.Add(new TextBlock
            {
                Text = "\u2022 " + DescribeCheckpoint(checkpoint),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        body.Children.Add(listPanel);
        body.Children.Add(new TextBlock
        {
            Text = BuildCheckpointActionHint(
                checkpoints.Count,
                branchTarget,
                restoreTarget,
                canRestore,
                mainState),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        return body;
    }

    private static string DescribeCheckpoint(SessionCompactionCheckpoint checkpoint)
    {
        var parts = new List<string>(3);
        if (checkpoint.CreatedAt is { } timestamp)
            parts.Add(timestamp.ToLocalTime().ToString("g"));
        if (!string.IsNullOrWhiteSpace(checkpoint.Reason))
            parts.Add(checkpoint.Reason!);
        if (checkpoint.TokensBefore is { } tokensBefore && checkpoint.TokensAfter is { } tokensAfter)
            parts.Add($"{tokensBefore:n0}\u2192{tokensAfter:n0} tokens");

        var description = parts.Count > 0
            ? string.Join(" \u00B7 ", parts)
            : (string.IsNullOrEmpty(checkpoint.Id) ? "checkpoint" : checkpoint.Id);
        if (!string.IsNullOrWhiteSpace(checkpoint.Summary))
            description += $" - {checkpoint.Summary}";
        return description;
    }

    private static string BuildCheckpointActionHint(
        int checkpointCount,
        SessionCompactionCheckpoint? branchTarget,
        SessionCompactionCheckpoint? restoreTarget,
        bool canRestore,
        SessionMainState mainState)
    {
        if (checkpointCount <= 0)
            return "";

        if (canRestore)
        {
            return "Actions apply to the most recent checkpoint (top of the list). " +
                   "Branch starts a new session from it; Restore rolls this session back to it.";
        }

        var reason = mainState == SessionMainState.Main
            ? "Restore is unavailable for the main session."
            : restoreTarget is null
                ? "Restore is unavailable because the latest checkpoint can't be determined safely."
                : "Restore is unavailable for this session.";
        var branchText = branchTarget is null
            ? "Branch is unavailable because no checkpoint has a checkpoint id."
            : "Branch starts a new session from the latest targetable checkpoint.";
        return branchText + " " + reason;
    }

    private async Task BranchCheckpointAsync(string key, string checkpointId)
    {
        if (string.IsNullOrWhiteSpace(checkpointId))
        {
            await ShowStatusAsync(
                "Action unavailable",
                "This checkpoint can't be branched because it has no checkpoint id.",
                InfoBarSeverity.Informational);
            return;
        }

        var client = CurrentApp.GatewayClient;
        if (client is null)
        {
            await ShowStatusAsync(
                LocalizationHelper.GetString("SessionsPage_GatewayDisconnected.Title"),
                LocalizationHelper.GetString("SessionsPage_GatewayDisconnected.Message"),
                InfoBarSeverity.Warning);
            return;
        }

        SessionCompactionMutationResult result;
        try
        {
            result = await client.BranchCompactionCheckpointAsync(key, checkpointId);
        }
        catch (Exception ex)
        {
            await ShowStatusAsync("Branch failed", ex.Message, InfoBarSeverity.Error);
            return;
        }

        if (!result.IsSupported)
        {
            await ShowStatusAsync(
                "Not supported",
                "This gateway doesn't support branching from a checkpoint. Update the gateway to use this.",
                InfoBarSeverity.Informational);
        }
        else if (result.Ok)
        {
            await ShowStatusAsync(
                "Branched",
                result.ResultSessionKey is { Length: > 0 } newKey
                    ? $"Created session {newKey}."
                    : "Created a new session from the checkpoint.",
                InfoBarSeverity.Success);
            _ = client.RequestSessionsAsync();
        }
        else
        {
            await ShowStatusAsync(
                "Branch failed",
                result.Error ?? "Could not branch from the checkpoint.",
                InfoBarSeverity.Error);
        }
    }

    private async Task RestoreCheckpointAsync(string key, string name, bool wasMain, string checkpointId)
    {
        var mainState = ResolveMainState(key);
        if (wasMain && mainState == SessionMainState.NotMain)
            mainState = SessionMainState.Main;

        if (!SessionActionPlanner.IsAllowed(SessionActionKind.Restore, mainState, out var blockedReason))
        {
            await ShowStatusAsync(
                "Action unavailable",
                blockedReason ?? "Restore isn't available for this session.",
                InfoBarSeverity.Informational);
            return;
        }

        var prompt = SessionActionPlanner.BuildPrompt(
            SessionActionKind.Restore,
            key,
            name,
            mainState == SessionMainState.Main);
        if (prompt is not null && !await ConfirmAsync(prompt))
            return;

        var client = CurrentApp.GatewayClient;
        if (client is null)
        {
            await ShowStatusAsync(
                LocalizationHelper.GetString("SessionsPage_GatewayDisconnected.Title"),
                LocalizationHelper.GetString("SessionsPage_GatewayDisconnected.Message"),
                InfoBarSeverity.Warning);
            return;
        }

        mainState = ResolveMainState(key);
        if (wasMain && mainState == SessionMainState.NotMain)
            mainState = SessionMainState.Main;
        if (!SessionActionPlanner.IsAllowed(SessionActionKind.Restore, mainState, out blockedReason))
        {
            await ShowStatusAsync(
                "Action unavailable",
                blockedReason ?? "Restore isn't available for this session.",
                InfoBarSeverity.Informational);
            return;
        }

        try
        {
            var fresh = await client.ListCompactionCheckpointsAsync(key);
            if (!fresh.IsSupported)
            {
                await ShowStatusAsync(
                    "Not supported",
                    "This gateway doesn't support restoring a checkpoint. Update the gateway to use this.",
                    InfoBarSeverity.Informational);
                return;
            }

            var freshLatest = SessionCheckpointSelection.ResolveUnambiguousLatest(fresh.Checkpoints);
            if (freshLatest is null || !string.Equals(freshLatest.Id, checkpointId, StringComparison.Ordinal))
            {
                await ShowStatusAsync(
                    "Checkpoints changed",
                    "The latest checkpoint changed since you opened this. Reopen Checkpoints and try again.",
                    InfoBarSeverity.Warning);
                return;
            }
        }
        catch (Exception ex)
        {
            await ShowStatusAsync("Restore failed", ex.Message, InfoBarSeverity.Error);
            return;
        }

        SessionCompactionMutationResult result;
        try
        {
            result = await client.RestoreCompactionCheckpointAsync(key, checkpointId);
        }
        catch (Exception ex)
        {
            await ShowStatusAsync("Restore failed", ex.Message, InfoBarSeverity.Error);
            return;
        }

        if (!result.IsSupported)
        {
            await ShowStatusAsync(
                "Not supported",
                "This gateway doesn't support restoring a checkpoint. Update the gateway to use this.",
                InfoBarSeverity.Informational);
        }
        else if (result.Ok)
        {
            if (CurrentApp.ChatProvider is { } chatProvider)
                await chatProvider.ReplaceHistoryAfterCheckpointRestoreAsync(key);

            await ShowStatusAsync(
                "Restored",
                "Rolled the session back to the checkpoint.",
                InfoBarSeverity.Success);
            _ = client.RequestSessionsAsync();
        }
        else
        {
            await ShowStatusAsync(
                "Restore failed",
                result.Error ?? "Could not restore the checkpoint.",
                InfoBarSeverity.Error);
        }
    }

    private SessionMainState ResolveMainState(string key, bool? rowIsMain = null)
        => SessionActionPlanner.ResolveMainState(
            key,
            rowIsMain,
            CurrentApp.GatewayClient?.MainSessionKey,
            CurrentApp.AppState?.Sessions);

    private async Task<bool> ConfirmAsync(SessionActionPrompt prompt)
    {
        if (!_isHostAvailable())
            return false;

        var localizedPrompt = SessionActionPromptLocalizer.Localize(prompt);
        var dialog = new ContentDialog
        {
            Title = localizedPrompt.Title,
            Content = localizedPrompt.Body,
            PrimaryButtonText = localizedPrompt.ConfirmLabel,
            CloseButtonText = LocalizationHelper.GetString("SessionActionPrompt_CancelLabel"),
            DefaultButton = ContentDialogButton.None,
            XamlRoot = _xamlRoot,
        };
        if (localizedPrompt.IsDestructive)
            dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowStatusAsync(string title, string message, InfoBarSeverity severity)
    {
        if (!_isHostAvailable())
            return;

        if (_showStatusAsync is not null)
        {
            await _showStatusAsync(title, message, severity);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _xamlRoot,
        };
        await dialog.ShowAsync();
    }
}
