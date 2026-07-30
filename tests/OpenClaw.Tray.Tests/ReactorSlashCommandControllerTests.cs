using OpenClaw.Shared;
using OpenClawTray.Chat;
using System.Linq;

namespace OpenClaw.Tray.Tests;

public class ReactorSlashCommandControllerTests
{
    [Fact]
    public void CatalogRefresh_DoesNotReopenMenuDismissedForCurrentInput()
    {
        Assert.False(ReactorSlashCommandController.ShouldReconcileAfterCatalogRefresh(
            inputRevision: 4,
            dismissedInputRevision: 4));
        Assert.True(ReactorSlashCommandController.ShouldReconcileAfterCatalogRefresh(
            inputRevision: 5,
            dismissedInputRevision: 4));
        Assert.True(ReactorSlashCommandController.ShouldReconcileAfterCatalogRefresh(
            inputRevision: 4,
            dismissedInputRevision: null));
    }

    [Fact]
    public void Evaluate_RecognizesLeadingSlashAndStaticFirstArgumentMode()
    {
        var commands = SampleCommands();

        var commandState = ReactorSlashCommandController.ReconcileState(
            "/mo",
            commands,
            ReactorSlashMenuState.Closed);
        var commandDisplay = ReactorSlashCommandController.Evaluate(
            "/mo",
            commandState,
            commandModeEnabled: true,
            commandsSupported: true,
            commands);

        Assert.True(commandDisplay.IsVisible);
        Assert.False(commandDisplay.IsArgsMode);
        Assert.Equal("mo", commandDisplay.Query);

        var argState = ReactorSlashCommandController.ReconcileState(
            "/model g",
            commands,
            commandState);
        var argDisplay = ReactorSlashCommandController.Evaluate(
            "/model g",
            argState,
            commandModeEnabled: true,
            commandsSupported: true,
            commands);

        Assert.True(argDisplay.IsVisible);
        Assert.True(argDisplay.IsArgsMode);
        Assert.Equal("g", argDisplay.Query);
        Assert.Equal(["gpt-4.1", "gpt-5"], argDisplay.ArgChoices.Select(choice => choice.Value).ToArray());

        var trailingWhitespaceState = ReactorSlashCommandController.ReconcileState(
            "/model g ",
            commands,
            argState);
        var trailingWhitespaceDisplay = ReactorSlashCommandController.Evaluate(
            "/model g ",
            trailingWhitespaceState,
            commandModeEnabled: true,
            commandsSupported: true,
            commands);

        Assert.False(trailingWhitespaceDisplay.IsVisible);
        Assert.False(trailingWhitespaceDisplay.IsArgsMode);
    }

    [Fact]
    public void ShouldRequestCatalogOnOpen_FiresOnlyOnOpenEdge()
    {
        var loadingState = ReactorSlashCommandController.ReconcileState(
            "/",
            commands: null,
            current: ReactorSlashMenuState.Closed);
        var loadingDisplay = ReactorSlashCommandController.Evaluate(
            "/",
            loadingState,
            commandModeEnabled: true,
            commandsSupported: true,
            commands: null);

        Assert.True(loadingDisplay.ShouldRequestCatalog);
        Assert.True(ReactorSlashCommandController.ShouldRequestCatalogOnOpen(false, loadingDisplay));
        Assert.False(ReactorSlashCommandController.ShouldRequestCatalogOnOpen(true, loadingDisplay));

        var unsupportedDisplay = ReactorSlashCommandController.Evaluate(
            "/",
            loadingState,
            commandModeEnabled: true,
            commandsSupported: false,
            commands: null);
        Assert.False(ReactorSlashCommandController.ShouldRequestCatalogOnOpen(false, unsupportedDisplay));
    }

    [Fact]
    public void Evaluate_GroupsResultsAndDefaultsToGlobalBestMatch()
    {
        GatewayCommand[] commands =
        [
            new() { Name = "reexec", NativeName = "/reexec", Category = "session" },
            new() { Name = "exec", NativeName = "/exec", Category = "options" },
            new() { Name = "usage", NativeName = "/usage", Category = "options" },
        ];

        var state = ReactorSlashCommandController.ReconcileState(
            "/exec",
            commands,
            ReactorSlashMenuState.Closed);
        var display = ReactorSlashCommandController.Evaluate(
            "/exec",
            state,
            commandModeEnabled: true,
            commandsSupported: true,
            commands);

        Assert.Equal(["session", "model"], display.Groups.Select(group => group.Category).ToArray());
        Assert.Equal(["reexec", "exec"], display.Commands.Select(command => command.Name).ToArray());
        Assert.Equal(1, display.DefaultSelectionIndex);
        Assert.Equal(1, display.SelectedIndex);
    }

    [Fact]
    public void CommitSelection_InsertsCommandThenArgumentChoice()
    {
        var commands = SampleCommands();

        var commandState = ReactorSlashCommandController.ReconcileState(
            "/model",
            commands,
            ReactorSlashMenuState.Closed);
        var commandDisplay = ReactorSlashCommandController.Evaluate(
            "/model",
            commandState,
            commandModeEnabled: true,
            commandsSupported: true,
            commands);
        var commandCommit = ReactorSlashCommandController.CommitSelection(commandDisplay);

        Assert.True(commandCommit.Accepted);
        Assert.Equal("/model ", commandCommit.Text);
        Assert.True(commandCommit.NextState.Active);
        Assert.True(commandCommit.NextState.ArgsMode);

        var argDisplay = ReactorSlashCommandController.Evaluate(
            commandCommit.Text,
            commandCommit.NextState,
            commandModeEnabled: true,
            commandsSupported: true,
            commands);
        var movedState = ReactorSlashCommandController.MoveSelection(
            commandCommit.NextState,
            argDisplay,
            2);
        var movedDisplay = ReactorSlashCommandController.Evaluate(
            commandCommit.Text,
            movedState,
            commandModeEnabled: true,
            commandsSupported: true,
            commands);
        var argCommit = ReactorSlashCommandController.CommitSelection(movedDisplay);

        Assert.True(argCommit.Accepted);
        Assert.Equal("/model gpt-5", argCommit.Text);
        Assert.Equal(ReactorSlashMenuState.Closed, argCommit.NextState);
    }

    [Fact]
    public void Evaluate_ClosedState_DoesNotReopenUntilTheInputChanges()
    {
        var display = ReactorSlashCommandController.Evaluate(
            "/stop",
            ReactorSlashMenuState.Closed,
            commandModeEnabled: true,
            commandsSupported: true,
            SampleCommands());

        Assert.False(display.IsVisible);
    }

    private static GatewayCommand[] SampleCommands() =>
    [
        new()
        {
            Name = "model",
            NativeName = "/model",
            Category = "options",
            AcceptsArgs = true,
            Args =
            [
                new GatewayCommandArg
                {
                    Name = "id",
                    Choices =
                    [
                        new GatewayCommandArgChoice { Value = "claude-sonnet-5", Label = "Claude Sonnet 5" },
                        new GatewayCommandArgChoice { Value = "gpt-4.1", Label = "GPT 4.1" },
                        new GatewayCommandArgChoice { Value = "gpt-5", Label = "GPT 5" },
                    ],
                },
            ],
        },
        new()
        {
            Name = "stop",
            NativeName = "/stop",
            Category = "session",
        },
    ];
}
