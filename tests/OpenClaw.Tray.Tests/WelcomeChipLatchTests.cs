using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public class WelcomeChipLatchTests
{
    [Fact]
    public void InFlight_BlocksAnotherChip()
    {
        Assert.True(WelcomeChipLatch.BlocksAnotherChip(true));
        Assert.False(WelcomeChipLatch.BlocksAnotherChip(false));
    }

    [Fact]
    public void SendReturn_ClearsTheLatch()
    {
        Assert.True(WelcomeChipLatch.ClearsBecauseSendReturned(true));
        Assert.False(WelcomeChipLatch.ClearsBecauseSendReturned(false));
    }

    [Fact]
    public void UserRowOnTheSentThread_ClearsTheLatch()
    {
        Assert.True(WelcomeChipLatch.ClearsBecauseSentThreadHasUserRow(
            "thread-sent",
            "thread-sent",
            hasUserRow: true));
    }

    [Fact]
    public void UserRowOnADifferentThread_DoesNotClear()
    {
        Assert.False(WelcomeChipLatch.ClearsBecauseSentThreadHasUserRow(
            "thread-sent",
            "compose-target",
            hasUserRow: true));
        Assert.False(WelcomeChipLatch.ClearsBecauseSentThreadHasUserRow(
            "thread-sent",
            "thread-sent",
            hasUserRow: false));
    }

    [Fact]
    public void ChatRoot_ClearsTheLatchWhenTheSendReturns()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs"));

        Assert.Contains("WelcomeChipLatch.BlocksAnotherChip", source, StringComparison.Ordinal);
        Assert.Contains("WelcomeChipLatch.ClearsBecauseSendReturned", source, StringComparison.Ordinal);
        Assert.Contains("WelcomeChipLatch.ClearsBecauseSentThreadHasUserRow", source, StringComparison.Ordinal);
        Assert.Contains("sentWelcomeThread.Current", source, StringComparison.Ordinal);
        Assert.Contains("FailedWhileHostLive", source, StringComparison.Ordinal);
        Assert.Contains("RestoreFailedWelcomeSend", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
