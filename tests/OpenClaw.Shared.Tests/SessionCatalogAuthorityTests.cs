using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class SessionCatalogAuthorityTests
{
    private static SessionCatalogRefresh Read(string json, int admittedKeyCount = -1)
    {
        using var doc = JsonDocument.Parse(json);
        return SessionCatalogAuthority.Read(doc.RootElement, admittedKeyCount);
    }

    [Fact]
    public void Empty_array_is_not_authoritative_for_deletion()
        => Assert.False(Read("[]").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Nonempty_array_is_a_complete_inventory()
        => Assert.True(Read("""[{"key":"agent:main:main"}]""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Explicit_zero_count_is_a_complete_empty_inventory()
        => Assert.True(Read("""{"sessions":[],"count":0}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Zero_rows_with_positive_total_is_partial()
        => Assert.False(Read("""{"sessions":[],"count":0,"totalCount":7}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Nonempty_page_with_hasMore_is_partial()
        => Assert.False(Read("""{"sessions":[{"key":"agent:main:main"}],"count":1,"totalCount":5,"hasMore":true,"nextOffset":1}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Non_first_offset_is_partial()
        => Assert.False(Read("""{"sessions":[{"key":"agent:main:main"}],"count":1,"totalCount":9,"offset":2}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Zero_rows_with_zero_total_is_a_complete_inventory()
        => Assert.True(Read("""{"sessions":[],"count":0,"totalCount":0}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Unqualified_empty_envelope_is_unknown()
    {
        var refresh = Read("""{"sessions":[]}""");
        Assert.Equal(SessionCatalogRefreshKind.Unknown, refresh.Kind);
        Assert.False(refresh.AllowsDeletionOfAbsentKeys);
    }

    [Fact]
    public void Count_is_page_rows_and_total_is_authority()
    {
        var refresh = Read("""{"sessions":[{"key":"agent:main:main"}],"count":1,"totalCount":100,"hasMore":true,"nextOffset":1}""");
        Assert.Equal(SessionCatalogRefreshKind.PartialPage, refresh.Kind);
        Assert.Equal(1, refresh.ObservedRows);
        Assert.Equal(100, refresh.TotalCount);
    }

    // --- consistency gates (Mini-Catalog-1d132032-Review) ---

    [Fact]
    public void Inflated_declared_count_over_few_rows_is_partial()
        => Assert.False(Read("""{"sessions":[{"key":"agent:main:main"}],"count":5,"totalCount":5,"hasMore":false}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Contradictory_hasMore_false_with_positive_nextOffset_is_partial()
        => Assert.False(Read("""{"sessions":[{"key":"agent:main:main"}],"count":1,"hasMore":false,"nextOffset":1}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Negative_present_count_is_invalid_and_partial()
    {
        var refresh = Read("""{"sessions":[],"count":-1}""");
        Assert.True(refresh.HasInvalidPagingMetadata);
        Assert.False(refresh.AllowsDeletionOfAbsentKeys);
    }

    [Fact]
    public void Nonbool_present_hasMore_is_invalid_and_partial()
    {
        var refresh = Read("""{"sessions":[],"count":0,"hasMore":"no"}""");
        Assert.True(refresh.HasInvalidPagingMetadata);
        Assert.False(refresh.AllowsDeletionOfAbsentKeys);
    }

    [Fact]
    public void Noninteger_present_totalCount_is_invalid_and_partial()
        => Assert.False(Read("""{"sessions":[],"totalCount":"2"}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Agreeing_metadata_and_rows_is_a_complete_inventory()
        => Assert.True(Read("""{"sessions":[{"key":"a"},{"key":"b"}],"count":2,"totalCount":2,"hasMore":false}""", admittedKeyCount: 2).AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Fewer_admitted_keys_than_declared_rows_is_partial()
        => Assert.False(Read("""{"sessions":[{"key":"a"},{"key":"b"}],"count":2,"totalCount":2}""", admittedKeyCount: 1).AllowsDeletionOfAbsentKeys);

    // --- real-protocol terminal null (Mini-Catalog-e168d650-Review) ---

    [Fact]
    public void Complete_empty_envelope_with_null_nextOffset_is_complete()
        => Assert.True(Read("""{"sessions":[],"count":0,"totalCount":0,"hasMore":false,"nextOffset":null}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Complete_nonempty_envelope_with_null_nextOffset_is_complete()
        => Assert.True(Read("""{"sessions":[{"key":"a"},{"key":"b"}],"count":2,"totalCount":2,"hasMore":false,"nextOffset":null}""", admittedKeyCount: 2).AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Null_count_is_present_but_invalid()
    {
        var refresh = Read("""{"sessions":[],"count":null}""");
        Assert.True(refresh.HasInvalidPagingMetadata);
        Assert.False(refresh.AllowsDeletionOfAbsentKeys);
    }

    [Fact]
    public void Null_totalCount_is_present_but_invalid()
        => Assert.False(Read("""{"sessions":[],"totalCount":null}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Null_offset_is_present_but_invalid()
        => Assert.False(Read("""{"sessions":[],"count":0,"offset":null}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Nonnull_invalid_nextOffset_is_invalid()
    {
        var refresh = Read("""{"sessions":[],"count":0,"nextOffset":"x"}""");
        Assert.True(refresh.HasInvalidPagingMetadata);
        Assert.False(refresh.AllowsDeletionOfAbsentKeys);
    }

    [Fact]
    public void Null_nextOffset_with_hasMore_true_stays_partial()
        => Assert.False(Read("""{"sessions":[{"key":"a"}],"count":1,"hasMore":true,"nextOffset":null}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Null_nextOffset_with_contradictory_count_stays_partial()
        => Assert.False(Read("""{"sessions":[{"key":"a"}],"count":5,"totalCount":5,"hasMore":false,"nextOffset":null}""").AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Nonempty_array_with_unadmitted_rows_is_partial()
        => Assert.False(Read("""[{"key":"a"},{"key":"b"}]""", admittedKeyCount: 1).AllowsDeletionOfAbsentKeys);

    [Fact]
    public void Nonempty_array_with_all_rows_admitted_is_complete()
        => Assert.True(Read("""[{"key":"a"},{"key":"b"}]""", admittedKeyCount: 2).AllowsDeletionOfAbsentKeys);
}
