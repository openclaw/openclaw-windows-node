using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class AsyncListLoadingStateTests
{
    [Fact]
    public void FirstRefresh_ShowsLoadingAndDisablesEdits()
    {
        var state = new AsyncListLoadingState();

        state.BeginInitialRefresh();

        Assert.True(state.IsRefreshing);
        Assert.False(state.CanEdit);
        Assert.True(state.ShouldShowLoading);
        Assert.False(state.ShouldShowContent);
        Assert.False(state.ShouldShowEmpty);
    }

    [Fact]
    public void RefreshAfterLoadedItems_KeepsContentVisibleButDisablesEdits()
    {
        var state = new AsyncListLoadingState();
        state.Complete(3);

        state.BeginRefresh();

        Assert.False(state.ShouldShowLoading);
        Assert.True(state.ShouldShowContent);
        Assert.False(state.CanEdit);
    }

    [Fact]
    public void RefreshAfterLoadedEmpty_KeepsEmptyVisibleButDisablesEdits()
    {
        var state = new AsyncListLoadingState();
        state.Complete(0);

        state.BeginRefresh();

        Assert.False(state.ShouldShowLoading);
        Assert.True(state.ShouldShowEmpty);
        Assert.False(state.CanEdit);
    }

    [Fact]
    public void BeginInitialRefresh_ClearsStaleContentForNewQueryScope()
    {
        var state = new AsyncListLoadingState();
        state.Complete(3);

        state.BeginInitialRefresh();

        Assert.False(state.HasLoaded);
        Assert.Equal(0, state.ItemCount);
        Assert.True(state.ShouldShowLoading);
    }

    [Fact]
    public void Fail_StopsRefreshingWithoutChangingLoadedContent()
    {
        var state = new AsyncListLoadingState();
        state.Complete(3);

        state.BeginRefresh();
        state.Fail();

        Assert.False(state.IsRefreshing);
        Assert.True(state.HasItems);
        Assert.True(state.CanEdit);
    }

    [Fact]
    public void Complete_NormalizesNegativeCounts()
    {
        var state = new AsyncListLoadingState();

        state.Complete(-1);

        Assert.True(state.HasLoaded);
        Assert.Equal(0, state.ItemCount);
        Assert.True(state.CanEdit);
        Assert.True(state.ShouldShowEmpty);
    }
}
