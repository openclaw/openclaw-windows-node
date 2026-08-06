using System;
using System.IO;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Source-contract tests for the composer's D2 owners: <c>ChatComposerController</c>
/// (send/lifecycle delegation and command-catalog request), <c>ChatComposerViewModel</c>
/// (slash evaluation/reconciliation), and <c>ReactorChatComposer</c> (the view-only
/// popup cache). Updated in D2 to point at the new owner/view seam; see
/// docs/ARCHITECTURE.md for the chat-composer-* ledger rows.
/// </summary>
public class ReactorSlashCommandSourceContractTests
{
    [Fact]
    public void ReactorRoot_WiresSnapshotCommandCatalogIntoComposerInputs()
    {
        var root = ReadSource("OpenClawReactorChatRoot.cs");

        Assert.Contains("AvailableCommands: snapshot.AvailableCommands", root);
        Assert.Contains("CommandsSupported: snapshot.CommandsSupported", root);
    }

    [Fact]
    public void ChatComposerController_RequestsCatalogThroughTheRuntimePort()
    {
        var controller = ReadSource("ChatComposerController.cs");

        Assert.Contains("++_catalogOperation;", controller);
        Assert.Contains("FireAndForget(_ => _port.EnsureCommandCatalogAsync(_lifetimeToken));", controller);
    }

    [Fact]
    public void ChatComposerViewModel_UsesShouldRequestCatalogOnOpen()
    {
        var viewModel = ReadSource("ChatComposerViewModel.cs");

        Assert.Contains("ReactorSlashCommandController.ShouldRequestCatalogOnOpen(_awaitingCatalog, SlashDisplay)", viewModel);
    }

    [Fact]
    public void ChatComposerController_SendCoreAsync_RetainsLifecycleDispatcherPath()
    {
        var controller = ReadSource("ChatComposerController.cs");

        AssertInOrder(
            controller,
            "ChatLifecycleCommandParser.TryParse(message, attachments.Count > 0, out var command)",
            "ChatLifecycleCommandExecutionPolicy.ShouldQueue(command)",
            "_port.ExecuteLifecycleCommandAsync(threadId, command)",
            "_port.SendMessageAsync(threadId, message, attachments, _lifetimeToken)");
    }

    [Fact]
    public void ChatComposerViewModel_EvaluatesTheStoredSlashStateWithoutReopeningDismissedText()
    {
        var viewModel = ReadSource("ChatComposerViewModel.cs");
        var evaluationStart = viewModel.IndexOf(
            "SlashDisplay = ReactorSlashCommandController.Evaluate(",
            StringComparison.Ordinal);

        Assert.True(evaluationStart >= 0);
        Assert.True(
            viewModel.IndexOf("_slashMenuState,", evaluationStart, StringComparison.Ordinal) >= 0);
        Assert.DoesNotContain("resolvedSlashMenuState", viewModel);
    }

    [Fact]
    public void ReactorComposer_CachesStablePopupContentBeforeApplyingTheme()
    {
        var composer = ReadSource("ReactorChatComposer.cs");

        Assert.Contains("var slashPopupContentRef = UseRef", composer);
        Assert.Contains("slashPopupContentRef.Current.Key == popupStateKey", composer);
    }

    private static string ReadSource(string fileName)
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            fileName));
    }

    private static void AssertInOrder(string source, params string[] fragments)
    {
        var index = 0;
        foreach (var fragment in fragments)
        {
            var found = source.IndexOf(fragment, index, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Did not find '{fragment}' after index {index}.");
            index = found + fragment.Length;
        }
    }
}
