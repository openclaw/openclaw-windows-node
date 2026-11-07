using Microsoft.UI.Xaml.Media;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.UITests;

public sealed class AgentAvatarImageCacheTests
{
    [Fact]
    public async Task RepeatedRowsShareTheInFlightAndCompletedAvatarLoadUntilInvalidated()
    {
        const string source = "data:image/png;base64,AA==";
        var completion = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        using var cache = new AgentAvatarImageCache((_, _) =>
        {
            Interlocked.Increment(ref loadCount);
            return completion.Task;
        });

        var first = cache.LoadImageAsync(source, CancellationToken.None);
        var second = cache.LoadImageAsync(source, CancellationToken.None);
        Assert.Equal(1, loadCount);

        completion.SetResult(null);
        Assert.Null(await first);
        Assert.Null(await second);
        Assert.Null(await cache.LoadImageAsync(source, CancellationToken.None));
        Assert.Equal(1, loadCount);

        var revision = cache.Revision;
        cache.Clear();
        Assert.Equal(revision + 1, cache.Revision);
        Assert.Null(await cache.LoadImageAsync(source, CancellationToken.None));
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public async Task CacheEvictsOldAvatarSourcesAfterItsCapacityIsReached()
    {
        var loadCount = 0;
        using var cache = new AgentAvatarImageCache((_, _) =>
        {
            Interlocked.Increment(ref loadCount);
            return Task.FromResult<ImageSource?>(null);
        });

        for (var index = 0; index < 16; index++)
            Assert.Null(await cache.LoadImageAsync($"data:image/png;base64,{index}", CancellationToken.None));

        Assert.Null(await cache.LoadImageAsync("data:image/png;base64,0", CancellationToken.None));
        Assert.Equal(17, loadCount);
    }
}
