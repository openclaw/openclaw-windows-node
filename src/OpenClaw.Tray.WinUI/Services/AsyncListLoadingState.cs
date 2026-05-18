using System;

namespace OpenClawTray.Services;

internal sealed class AsyncListLoadingState
{
    public bool HasLoaded { get; private set; }
    public bool IsRefreshing { get; private set; }
    public int ItemCount { get; private set; }

    public bool HasItems => HasLoaded && ItemCount > 0;
    public bool CanEdit => !IsRefreshing;
    public bool ShouldShowLoading => IsRefreshing && !HasLoaded;
    public bool ShouldShowContent => HasItems;
    public bool ShouldShowEmpty => HasLoaded && ItemCount == 0;

    public void BeginRefresh()
    {
        IsRefreshing = true;
    }

    public void BeginInitialRefresh()
    {
        HasLoaded = false;
        ItemCount = 0;
        IsRefreshing = true;
    }

    public void Complete(int itemCount)
    {
        ItemCount = Math.Max(0, itemCount);
        HasLoaded = true;
        IsRefreshing = false;
    }

    public void Fail()
    {
        IsRefreshing = false;
    }
}
