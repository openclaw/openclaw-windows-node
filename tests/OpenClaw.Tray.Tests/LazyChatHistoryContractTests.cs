namespace OpenClaw.Tray.Tests;

public sealed class LazyChatHistoryContractTests
{
    [Fact]
    public void TrayWindow_DoesNotLoadDefaultThreadOutsideSelectedThreadOwner()
    {
        var chatWindow = Read("src", "OpenClaw.Tray.WinUI", "Windows", "ChatWindow.xaml.cs");
        var chatRoot = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatRoot.cs");

        Assert.DoesNotContain("EagerlyLoadChatHistory", chatWindow);
        Assert.DoesNotContain("LoadHistoryAsync", chatWindow);
        Assert.Contains("native.LoadHistoryAsync(threadId, force: false, ct)", chatRoot);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
