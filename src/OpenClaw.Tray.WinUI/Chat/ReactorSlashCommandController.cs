using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Chat;

internal readonly record struct ReactorSlashMenuState(
    bool Active,
    string Query,
    int Index,
    bool ArgsMode)
{
    public static ReactorSlashMenuState Closed { get; } = new(false, string.Empty, 0, false);
}

internal sealed record ReactorSlashDisplayState(
    bool IsActive,
    bool IsVisible,
    bool IsLoading,
    bool IsArgsMode,
    string Query,
    int DefaultSelectionIndex,
    int SelectedIndex,
    IReadOnlyList<CommandCategoryGroup> Groups,
    IReadOnlyList<GatewayCommand> Commands,
    GatewayCommand? ArgCommand,
    IReadOnlyList<GatewayCommandArgChoice> ArgChoices)
{
    public int SelectableCount => IsArgsMode ? ArgChoices.Count : Commands.Count;
    public bool HasSelection => SelectableCount > 0;
    public bool ShouldRequestCatalog => IsVisible && IsLoading;

    public static ReactorSlashDisplayState Inactive { get; } = new(
        false,
        false,
        false,
        false,
        string.Empty,
        0,
        0,
        Array.Empty<CommandCategoryGroup>(),
        Array.Empty<GatewayCommand>(),
        null,
        Array.Empty<GatewayCommandArgChoice>());
}

internal readonly record struct ReactorSlashCommitResult(
    bool Accepted,
    string Text,
    ReactorSlashMenuState NextState);

internal static class ReactorSlashCommandController
{
    public const int MaxItems = 8;

    public static ReactorSlashMenuState ReconcileState(
        string? text,
        IReadOnlyList<GatewayCommand>? commands,
        ReactorSlashMenuState current)
    {
        var computed = ComputeSlashState(text, commands);
        return computed.Active != current.Active
               || computed.ArgsMode != current.ArgsMode
               || !string.Equals(computed.Query, current.Query, StringComparison.Ordinal)
            ? new ReactorSlashMenuState(computed.Active, computed.Query, -1, computed.ArgsMode)
            : current;
    }

    public static bool ShouldReconcileAfterCatalogRefresh(
        int inputRevision,
        int? dismissedInputRevision) =>
        dismissedInputRevision != inputRevision;

    public static ReactorSlashDisplayState Evaluate(
        string? text,
        ReactorSlashMenuState state,
        bool commandModeEnabled,
        bool commandsSupported,
        IReadOnlyList<GatewayCommand>? commands)
    {
        if (!commandModeEnabled || !commandsSupported || !state.Active)
            return ReactorSlashDisplayState.Inactive;

        if (commands is null)
        {
            return new ReactorSlashDisplayState(
                true,
                true,
                true,
                false,
                state.Query,
                0,
                0,
                Array.Empty<CommandCategoryGroup>(),
                Array.Empty<GatewayCommand>(),
                null,
                Array.Empty<GatewayCommandArgChoice>());
        }

        if (state.ArgsMode)
        {
            var (argName, _, _) = SplitSlashArgText(text);
            var argCommand = commands.FirstOrDefault(command => command.MatchesName(argName));
            if (argCommand is null)
                return ReactorSlashDisplayState.Inactive;

            var argChoices = argCommand.FirstArgChoices()
                .Where(choice => ChoiceMatches(choice, state.Query))
                .Take(MaxItems)
                .ToArray();
            if (argChoices.Length == 0)
                return ReactorSlashDisplayState.Inactive;

            return new ReactorSlashDisplayState(
                true,
                true,
                false,
                true,
                state.Query,
                0,
                ResolveSelectedIndex(state.Index, 0, argChoices.Length),
                Array.Empty<CommandCategoryGroup>(),
                Array.Empty<GatewayCommand>(),
                argCommand,
                argChoices);
        }

        var palette = new ChatCommandCatalogView(commands)
            .GroupForPalette(CommandCategories.Bucket, state.Query, CommandCategories.DisplayOrder);
        if (palette.Flattened.Count == 0)
            return ReactorSlashDisplayState.Inactive;

        return new ReactorSlashDisplayState(
            true,
            true,
            false,
            false,
            state.Query,
            palette.DefaultSelectionIndex,
            ResolveSelectedIndex(state.Index, palette.DefaultSelectionIndex, palette.Flattened.Count),
            palette.Groups,
            palette.Flattened,
            null,
            Array.Empty<GatewayCommandArgChoice>());
    }

    public static bool ShouldRequestCatalogOnOpen(
        bool wasAwaitingCatalog,
        ReactorSlashDisplayState state) =>
        !wasAwaitingCatalog && state.ShouldRequestCatalog;

    public static ReactorSlashMenuState MoveSelection(
        ReactorSlashMenuState state,
        ReactorSlashDisplayState displayState,
        int delta)
    {
        if (!displayState.HasSelection)
            return state;

        var start = state.Index < 0 ? displayState.DefaultSelectionIndex : state.Index;
        var nextIndex = Math.Clamp(start + delta, 0, displayState.SelectableCount - 1);
        return state with { Index = nextIndex };
    }

    public static ReactorSlashCommitResult CommitSelection(ReactorSlashDisplayState displayState)
    {
        if (!displayState.HasSelection)
            return new(false, string.Empty, ReactorSlashMenuState.Closed);

        if (displayState.IsArgsMode && displayState.ArgCommand is { } argCommand)
        {
            var choice = displayState.ArgChoices[displayState.SelectedIndex];
            return new(
                true,
                argCommand.BuildArgInsertionText(choice.Value),
                ReactorSlashMenuState.Closed);
        }

        var command = displayState.Commands[displayState.SelectedIndex];
        if (command.FirstArgChoices().Count > 0)
        {
            return new(
                true,
                command.DisplayName() + " ",
                new ReactorSlashMenuState(true, string.Empty, 0, true));
        }

        return new(
            true,
            command.BuildInsertionText(),
            ReactorSlashMenuState.Closed);
    }

    private static int ResolveSelectedIndex(int currentIndex, int defaultIndex, int count)
    {
        if (count <= 0)
            return 0;

        var effectiveIndex = currentIndex < 0 ? defaultIndex : currentIndex;
        return Math.Clamp(effectiveIndex, 0, count - 1);
    }

    private static (bool Active, string Query, bool ArgsMode) ComputeSlashState(
        string? text,
        IReadOnlyList<GatewayCommand>? commands)
    {
        var value = text ?? string.Empty;
        if (value.Length == 0 || value[0] != '/')
            return (false, string.Empty, false);

        var (name, rest, hasSpace) = SplitSlashArgText(value);
        if (!hasSpace)
            return (true, value[1..], false);

        if (rest.Any(char.IsWhiteSpace))
            return (false, string.Empty, false);

        // Preserve the loading palette for an argument-capable command typed
        // before commands.list returns. ReconcileState runs again once the
        // catalog arrives and promotes this into argument-choice mode.
        if (commands is null)
            return (true, rest, false);

        var command = commands?.FirstOrDefault(candidate => candidate.MatchesName(name));
        if (command is not null)
        {
            var choices = command.FirstArgChoices();
            if (choices.Count > 0 && choices.Any(choice => ChoiceMatches(choice, rest)))
                return (true, rest, true);
        }

        return (false, string.Empty, false);
    }

    private static (string Name, string Remainder, bool HasSpace) SplitSlashArgText(string? text)
    {
        var value = text ?? string.Empty;
        if (value.Length == 0 || value[0] != '/')
            return (string.Empty, string.Empty, false);

        for (var index = 1; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
                return (value.Substring(1, index - 1), value[(index + 1)..], true);
        }

        return (value[1..], string.Empty, false);
    }

    private static bool ChoiceMatches(GatewayCommandArgChoice choice, string? filter)
    {
        var value = (filter ?? string.Empty).Trim();
        if (value.Length == 0)
            return true;

        return (choice.Value?.StartsWith(value, StringComparison.OrdinalIgnoreCase) ?? false)
               || (choice.Label?.StartsWith(value, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
