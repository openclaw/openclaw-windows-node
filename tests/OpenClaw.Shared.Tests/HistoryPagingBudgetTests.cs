using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class HistoryPagingBudgetTests
{
    [Fact] public void Next_page_is_capped_by_page_size() => Assert.Equal(200, HistoryPagingBudget.NextPageSize(1000));
    [Fact] public void Next_page_returns_remainder() => Assert.Equal(50, HistoryPagingBudget.NextPageSize(50));
    [Fact] public void Next_page_zero_when_nothing_left() => Assert.Equal(0, HistoryPagingBudget.NextPageSize(0));
    [Fact] public void Tick_items_are_bounded() => Assert.Equal(250, HistoryPagingBudget.ItemsThisTick(10_000));
    [Fact] public void Tick_items_remainder() => Assert.Equal(10, HistoryPagingBudget.ItemsThisTick(10));
    [Fact] public void Continue_only_on_full_page()
    {
        Assert.True(HistoryPagingBudget.ShouldContinuePaging(200, 200));
        Assert.False(HistoryPagingBudget.ShouldContinuePaging(199, 200));
    }
}
