using System.Net;
using System.Net.Sockets;
using System.Text;
using Octokit;
using OpenClaw.TestSupport;
using OpenClawTray.Services;
using Updatum;

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

    [Fact(Timeout = 20000)]
    public async Task HeadersThenStall_CancelsAndLeavesNoInstaller()
    {
        var assetName = $"openclaw-update-stall-{Guid.NewGuid():N}.bin";
        var tempFile = Path.Combine(Path.GetTempPath(), assetName);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener, completeBody: false, released.Task);
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var updater = NewUpdater(assetName);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                updater.DownloadUpdateAsync(ReleaseWithAsset(assetName, $"http://127.0.0.1:{port}/stall.bin"), cancel.Token));
            Assert.False(File.Exists(tempFile));
        }
        finally
        {
            released.TrySetResult();
            listener.Stop();
            await server;
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact(Timeout = 20000)]
    public async Task SlowCompleteBody_ReturnsTheFileWithoutInstalling()
    {
        var assetName = $"openclaw-update-slow-{Guid.NewGuid():N}.bin";
        var tempFile = Path.Combine(Path.GetTempPath(), assetName);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener, completeBody: true, Task.CompletedTask);
        try
        {
            var updater = NewUpdater(assetName);
            var downloaded = await updater.DownloadUpdateAsync(
                ReleaseWithAsset(assetName, $"http://127.0.0.1:{port}/slow.bin"),
                CancellationToken.None);
            Assert.NotNull(downloaded);
            Assert.True(File.Exists(downloaded.FilePath));
            Assert.Equal(tempFile, downloaded.FilePath);
        }
        finally
        {
            listener.Stop();
            await server;
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    private static UpdatumManager NewUpdater(string assetName) =>
        new("openclaw", "openclaw-windows-node")
        {
            AssetRegexPattern = assetName,
            AssetExtensionFilter = ".bin",
        };

    private static Release ReleaseWithAsset(string assetName, string downloadUrl)
    {
        var now = DateTimeOffset.UtcNow;
        var asset = new ReleaseAsset(
            downloadUrl,
            1,
            "node",
            assetName,
            "update",
            "uploaded",
            "application/octet-stream",
            32,
            0,
            now,
            now,
            downloadUrl,
            new Author());
        return new Release(
            downloadUrl,
            downloadUrl,
            downloadUrl,
            downloadUrl,
            1,
            "node",
            "v0",
            "main",
            "stall",
            "",
            false,
            false,
            now,
            now,
            new Author(),
            downloadUrl,
            downloadUrl,
            new[] { asset });
    }

    private static async Task ServeAsync(TcpListener listener, bool completeBody, Task release)
    {
        TcpClient? client = null;
        try
        {
            client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var header = new byte[4096];
            var read = 0;
            while (read < 4 || !Encoding.ASCII.GetString(header, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var n = await stream.ReadAsync(header.AsMemory(read));
                if (n == 0)
                    return;
                read += n;
            }

            var payload = completeBody ? new byte[32] : new byte[16];
            var length = completeBody ? payload.Length : 1_000_000;
            var prefix = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(prefix);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
            if (!completeBody)
                await release;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            client?.Dispose();
        }
    }
}
