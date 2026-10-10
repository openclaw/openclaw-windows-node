using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class SessionCatalogRetentionTests
{
    [Fact]
    public void Preserves_catalog_on_empty_refresh_without_authoritative_count()
        => Assert.True(SessionCatalogRetention.ShouldPreserveOnEmpty(0, 3, null));

    [Fact]
    public void Does_not_preserve_when_payload_declares_authoritative_count()
        => Assert.False(SessionCatalogRetention.ShouldPreserveOnEmpty(0, 3, 0));

    [Fact]
    public void Preserves_catalog_when_positive_count_contradicts_empty_rows()
        => Assert.True(SessionCatalogRetention.ShouldPreserveOnEmpty(0, 3, 3));

    [Fact]
    public void Does_not_preserve_when_no_sessions_held()
        => Assert.False(SessionCatalogRetention.ShouldPreserveOnEmpty(0, 0, null));

    [Fact]
    public void Does_not_preserve_when_incoming_has_rows()
        => Assert.False(SessionCatalogRetention.ShouldPreserveOnEmpty(2, 3, null));
}
