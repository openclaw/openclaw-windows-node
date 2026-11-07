using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class UpdateDownloadDeadlineTests
{
    [Fact]
    public void UnsetValue_UsesTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), UpdateDownloadDeadline.Resolve(null));
        Assert.Equal(TimeSpan.FromMinutes(10), UpdateDownloadDeadline.Resolve("  "));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData(" 30 ", 30)]
    public void PositiveSeconds_AreAccepted(string raw, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), UpdateDownloadDeadline.Resolve(raw));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("soon")]
    [InlineData("1.5")]
    public void InvalidValues_KeepTheDefault(string raw)
    {
        Assert.Equal(UpdateDownloadDeadline.DefaultTimeout, UpdateDownloadDeadline.Resolve(raw));
    }

    [Fact]
    public async Task HeaderTimeoutWithoutTheCallerToken_StaysAFailedDownload()
    {
        using var caller = new CancellationTokenSource();
        var timedOut = await UpdateDownloadCancellation.FinishAsync(
            Task.FromException<bool>(new TaskCanceledException("response headers")),
            caller.Token);
        Assert.False(timedOut.Installed);
        Assert.True(timedOut.IndependentCancellation);

        caller.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            UpdateDownloadCancellation.FinishAsync(
                Task.FromException<bool>(new TaskCanceledException("response headers")),
                caller.Token));
    }

    [Fact]
    public void DownloadPassesTheTokenAndClosingTheWindowCancelsIt()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var backend = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "UpdatumUpdateBackend.cs"));
        var coordinator = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "UpdateCoordinator.cs"));
        var dialog = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Dialogs", "DownloadProgressDialog.cs"));

        var downloadAt = backend.IndexOf("DownloadUpdateAsync(cancellationToken)", StringComparison.Ordinal);
        var cancelAt = backend.IndexOf("cancellationToken.ThrowIfCancellationRequested()", StringComparison.Ordinal);
        var completedAt = backend.IndexOf("downloadCompleted()", StringComparison.Ordinal);
        Assert.True(downloadAt >= 0 && cancelAt > downloadAt && completedAt > cancelAt);
        Assert.DoesNotContain("DownloadUpdateAsync()", backend, StringComparison.Ordinal);
        Assert.Contains("UpdateDownloadDeadline.Resolve()", coordinator, StringComparison.Ordinal);
        Assert.Contains("progressDialog.Closed += CancelBecauseWindowClosed", coordinator, StringComparison.Ordinal);
        Assert.Contains("downloadCancellation.Token", coordinator, StringComparison.Ordinal);
        Assert.Contains("UpdateDownloadCancellation.FinishAsync(", coordinator, StringComparison.Ordinal);
        Assert.Contains(
            "catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)",
            File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "UpdateDownloadCancellation.cs")),
            StringComparison.Ordinal);
        Assert.Contains("Detail = \"download canceled\"", coordinator, StringComparison.Ordinal);
        Assert.Contains("_window.Closed +=", dialog, StringComparison.Ordinal);
        Assert.Equal("OPENCLAW_UPDATE_DOWNLOAD_TIMEOUT", UpdateDownloadDeadline.TimeoutEnvironmentVariable);
    }
}
