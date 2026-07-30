using System;
using System.IO;

namespace OpenClaw.Tray.Tests;

public class ReactorSlashCommandSourceContractTests
{
    [Fact]
    public void ReactorComposer_WiresSnapshotCommandCatalogAndLazyRequest()
    {
        var source = ReadReactorRootSource();

        Assert.Contains("snapshot.AvailableCommands", source);
        Assert.Contains("snapshot.CommandsSupported", source);
        Assert.Contains("() => RunFireAndForget(ct => props.Provider.EnsureCommandCatalogAsync(ct))", source);
        Assert.Contains("ReactorSlashCommandController.ShouldRequestCatalogOnOpen", source);
    }

    [Fact]
    public void ReactorRoot_SendAsync_RetainsLifecycleDispatcherPath()
    {
        var source = ReadReactorRootSource();

        AssertInOrder(
            source,
            "ChatLifecycleCommandParser.TryParse(message, attachments.Count > 0, out var command)",
            "ChatLifecycleCommandExecutionPolicy.ShouldQueue(command)",
            "native.ExecuteLifecycleCommandAsync(threadId, command)",
            "provider.SendMessageAsync(threadId, message, CancellationToken.None, attachments)");
    }

    [Fact]
    public void ReactorComposer_EvaluatesTheStoredSlashStateWithoutReopeningDismissedText()
    {
        var source = ReadReactorRootSource();
        var evaluationStart = source.IndexOf(
            "var slashDisplay = ReactorSlashCommandController.Evaluate(",
            StringComparison.Ordinal);

        Assert.True(evaluationStart >= 0);
        Assert.True(
            source.IndexOf("slashMenuState,", evaluationStart, StringComparison.Ordinal) >= 0);
        Assert.DoesNotContain("resolvedSlashMenuState", source);
    }

    [Fact]
    public void ReactorComposer_CachesStablePopupContentBeforeApplyingTheme()
    {
        var source = ReadReactorRootSource();

        Assert.Contains("var slashPopupContentRef = UseRef", source);
        Assert.Contains("slashPopupContentRef.Current.Key == popupStateKey", source);
    }

    private static string ReadReactorRootSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawReactorChatRoot.cs"));
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
