using OpenClawTray;

namespace OpenClaw.Tray.Tests;

public sealed class DestructiveCliOperationTests
{
    [Theory]
    [InlineData("--reset-local-ai", "--uninstall")]
    [InlineData("--uninstall", "--reset-local-ai")]
    [InlineData("--UNINSTALL", "--Reset-Local-AI")]
    public void BothDestructiveFlags_AreRejectedBeforeForwarding(string first, string second)
    {
        // Only the selected flag reaches SetupEngine, so its own mutual-exclusion check cannot fire.
        string[] args = ["OpenClaw.Tray.WinUI.exe", first, second, "--confirm-destructive"];

        Assert.True(DestructiveCliOperation.IsRequested(args));
        Assert.False(DestructiveCliOperation.TrySelect(args, out string? operation, out string? error));
        Assert.Null(operation);
        Assert.Contains("cannot be used together", error);
    }

    [Theory]
    [InlineData("--uninstall", DestructiveCliOperation.UninstallFlag)]
    [InlineData("--Reset-Local-AI", DestructiveCliOperation.ResetLocalAiFlag)]
    public void SingleDestructiveFlag_SelectsThatOperation(string flag, string expected)
    {
        string[] args = ["OpenClaw.Tray.WinUI.exe", flag, "--dry-run"];

        Assert.True(DestructiveCliOperation.TrySelect(args, out string? operation, out string? error));
        Assert.Equal(expected, operation);
        Assert.Null(error);
    }

    [Fact]
    public void NoDestructiveFlag_IsNotRequestedAndNotSelected()
    {
        string[] args = ["OpenClaw.Tray.WinUI.exe", "--dry-run"];

        Assert.False(DestructiveCliOperation.IsRequested(args));
        Assert.False(DestructiveCliOperation.TrySelect(args, out _, out string? error));
        Assert.NotNull(error);
    }
}
