using System;
using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class SessionCatalogPageSetTests
{
    private static SessionCatalogRefresh Refresh(string json, int admitted)
    {
        using var doc = JsonDocument.Parse(json);
        return SessionCatalogAuthority.Read(doc.RootElement, admitted);
    }

    private static string[] Keys(params string[] keys) => keys;

    [Fact]
    public void Single_complete_page_allows_deletion()
    {
        var set = new SessionCatalogPageSet("main", 1, 0);
        var decision = set.Accept(
            Refresh("""{"sessions":[{"key":"agent:main:main"}],"count":1,"totalCount":1,"hasMore":false,"nextOffset":null}""", 1),
            Keys("agent:main:main"), requestedOffset: 0, rawBytes: 100, nowMs: 10);
        Assert.Equal(PageSetDecision.Complete, decision);
        Assert.True(set.AllowsDeletion);
    }

    [Fact]
    public void Multi_page_success_never_allows_deletion_from_tail()
    {
        var set = new SessionCatalogPageSet("main", 1, 0);
        var d1 = set.Accept(
            Refresh("""{"sessions":[{"key":"a"},{"key":"b"}],"count":2,"totalCount":4,"hasMore":true,"nextOffset":2}""", 2),
            Keys("a", "b"), requestedOffset: 0, rawBytes: 100, nowMs: 10);
        Assert.Equal(PageSetDecision.Continue, d1);
        Assert.Equal(2, set.NextOffset);

        var d2 = set.Accept(
            Refresh("""{"sessions":[{"key":"c"},{"key":"d"}],"count":2,"totalCount":4,"hasMore":false,"nextOffset":null}""", 2),
            Keys("c", "d"), requestedOffset: 2, rawBytes: 100, nowMs: 20);
        Assert.Equal(PageSetDecision.Complete, d2);
        Assert.Equal(2, set.Pages);
        Assert.False(set.AllowsDeletion); // tail pages must not prune
    }

    [Fact]
    public void Deadline_exceeded_fails()
    {
        var set = new SessionCatalogPageSet("main", 1, 0, deadline: TimeSpan.FromMilliseconds(50));
        var d = set.Accept(
            Refresh("""{"sessions":[],"count":0,"totalCount":0,"hasMore":false,"nextOffset":null}""", 0),
            Keys(), requestedOffset: 0, rawBytes: 1, nowMs: 1000);
        Assert.Equal(PageSetDecision.Failed, d);
    }

    [Fact]
    public void Max_pages_bounds_the_acquisition()
    {
        var set = new SessionCatalogPageSet("main", 1, 0, maxPages: 1);
        set.Accept(Refresh("""{"sessions":[{"key":"a"}],"count":1,"totalCount":9,"hasMore":true,"nextOffset":1}""", 1),
            Keys("a"), 0, 10, 10);
        var d = set.Accept(Refresh("""{"sessions":[{"key":"b"}],"count":1,"totalCount":9,"hasMore":true,"nextOffset":2}""", 1),
            Keys("b"), 1, 10, 20);
        Assert.Equal(PageSetDecision.Failed, d);
    }

    [Fact]
    public void Repeated_offset_fails()
    {
        var set = new SessionCatalogPageSet("main", 1, 0);
        set.Accept(Refresh("""{"sessions":[{"key":"a"}],"count":1,"totalCount":9,"hasMore":true,"nextOffset":1}""", 1),
            Keys("a"), 0, 10, 10);
        // Page two claims offset 0 again instead of the requested 1.
        var d = set.Accept(Refresh("""{"sessions":[{"key":"b"}],"count":1,"totalCount":9,"hasMore":true,"nextOffset":1}""", 1),
            Keys("b"), 1, 10, 20);
        Assert.Equal(PageSetDecision.Failed, d);
    }

    [Fact]
    public void Changing_totalCount_fails()
    {
        var set = new SessionCatalogPageSet("main", 1, 0);
        set.Accept(Refresh("""{"sessions":[{"key":"a"}],"count":1,"totalCount":4,"hasMore":true,"nextOffset":1}""", 1),
            Keys("a"), 0, 10, 10);
        var d = set.Accept(Refresh("""{"sessions":[{"key":"b"}],"count":1,"totalCount":5,"hasMore":false,"nextOffset":null}""", 1),
            Keys("b"), 1, 10, 20);
        Assert.Equal(PageSetDecision.Failed, d);
    }

    [Fact]
    public void Duplicate_key_across_pages_fails()
    {
        var set = new SessionCatalogPageSet("main", 1, 0);
        set.Accept(Refresh("""{"sessions":[{"key":"a"}],"count":1,"totalCount":4,"hasMore":true,"nextOffset":1}""", 1),
            Keys("a"), 0, 10, 10);
        var d = set.Accept(Refresh("""{"sessions":[{"key":"a"}],"count":1,"totalCount":4,"hasMore":false,"nextOffset":null}""", 1),
            Keys("a"), 1, 10, 20);
        Assert.Equal(PageSetDecision.Failed, d);
    }

    [Fact]
    public void Malformed_continuation_fails()
    {
        var set = new SessionCatalogPageSet("main", 1, 0);
        // hasMore true but no nextOffset -> cannot continue safely.
        var d = set.Accept(Refresh("""{"sessions":[{"key":"a"}],"count":1,"totalCount":4,"hasMore":true}""", 1),
            Keys("a"), 0, 10, 10);
        Assert.Equal(PageSetDecision.Failed, d);
    }

    [Fact]
    public void Invalid_paging_metadata_fails()
    {
        var set = new SessionCatalogPageSet("main", 1, 0);
        var d = set.Accept(Refresh("""{"sessions":[],"count":-1}""", 0), Keys(), 0, 10, 10);
        Assert.Equal(PageSetDecision.Failed, d);
    }

    [Fact]
    public void Unqualified_page_fails()
    {
        var set = new SessionCatalogPageSet("main", 1, 0);
        var d = set.Accept(Refresh("""{"sessions":[]}""", 0), Keys(), 0, 10, 10);
        Assert.Equal(PageSetDecision.Failed, d);
    }

    [Fact]
    public void Failed_decision_is_sticky()
    {
        var set = new SessionCatalogPageSet("main", 1, 0);
        set.Accept(Refresh("""{"sessions":[],"count":-1}""", 0), Keys(), 0, 10, 10);
        var d = set.Accept(Refresh("""{"sessions":[],"count":0,"totalCount":0,"hasMore":false,"nextOffset":null}""", 0), Keys(), 0, 10, 20);
        Assert.Equal(PageSetDecision.Failed, d);
    }
}
