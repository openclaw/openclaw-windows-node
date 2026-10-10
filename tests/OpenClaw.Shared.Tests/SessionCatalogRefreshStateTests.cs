using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class SessionCatalogRefreshStateTests
{
    [Fact] public void Starts_without_retry_required() => Assert.False(new SessionCatalogRefreshState().RetryRequired);

    [Fact] public void Ignored_empty_marks_retry_required()
    {
        var s = new SessionCatalogRefreshState();
        s.RecordIgnoredEmpty(1234);
        Assert.True(s.RetryRequired);
        Assert.Equal(1, s.IgnoredEmptyRefreshes);
        Assert.Equal(1234, s.LastIgnoredUnixMs);
    }

    [Fact] public void Successful_refresh_clears_retry_state()
    {
        var s = new SessionCatalogRefreshState();
        s.RecordIgnoredEmpty(1);
        s.RecordSuccessfulRefresh();
        Assert.False(s.RetryRequired);
        Assert.Equal(0, s.IgnoredEmptyRefreshes);
    }
}
