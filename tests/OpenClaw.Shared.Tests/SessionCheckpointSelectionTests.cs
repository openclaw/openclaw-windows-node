using OpenClaw.Shared.Sessions;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class SessionCheckpointSelectionTests
{
    private static SessionCompactionCheckpoint Checkpoint(string id, DateTime? createdAt) => new()
    {
        Id = id,
        CreatedAt = createdAt,
    };

    [Fact]
    public void ResolveUnambiguousLatest_ReturnsNull_ForEmptyList()
    {
        Assert.Null(SessionCheckpointSelection.ResolveUnambiguousLatest(Array.Empty<SessionCompactionCheckpoint>()));
    }

    [Fact]
    public void ResolveUnambiguousLatest_ReturnsOnlyCheckpoint_WhenDated()
    {
        var checkpoint = Checkpoint("one", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.Same(checkpoint, SessionCheckpointSelection.ResolveUnambiguousLatest(new[] { checkpoint }));
    }

    [Fact]
    public void ResolveUnambiguousLatest_ReturnsNewest_WhenStrictlyNewer()
    {
        var older = Checkpoint("older", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var newer = Checkpoint("newer", new DateTime(2026, 1, 1, 12, 5, 0, DateTimeKind.Utc));

        var latest = SessionCheckpointSelection.ResolveUnambiguousLatest(new[] { older, newer });

        Assert.Same(newer, latest);
    }

    [Fact]
    public void ResolveUnambiguousLatest_ReturnsNull_WhenAnyCheckpointLacksTimestamp()
    {
        var dated = Checkpoint("dated", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var unknown = Checkpoint("unknown", null);

        Assert.Null(SessionCheckpointSelection.ResolveUnambiguousLatest(new[] { dated, unknown }));
    }

    [Fact]
    public void ResolveUnambiguousLatest_ReturnsNull_WhenNewestTimestampTies()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Null(SessionCheckpointSelection.ResolveUnambiguousLatest(new[]
        {
            Checkpoint("a", t),
            Checkpoint("b", t),
        }));
    }

    [Fact]
    public void ResolveUnambiguousLatest_ReturnsNull_WhenNewestCheckpointLacksId()
    {
        var older = Checkpoint("older", new DateTime(2026, 1, 1, 12, 5, 0, DateTimeKind.Utc));
        var missingId = Checkpoint("", new DateTime(2026, 1, 1, 12, 10, 0, DateTimeKind.Utc));

        Assert.Null(SessionCheckpointSelection.ResolveUnambiguousLatest(new[] { older, missingId }));
    }

    [Fact]
    public void ResolveUnambiguousLatest_ReturnsNewest_WhenOlderCheckpointLacksId()
    {
        var latest = Checkpoint("newer", new DateTime(2026, 1, 1, 12, 10, 0, DateTimeKind.Utc));
        var missingId = Checkpoint("", new DateTime(2026, 1, 1, 12, 5, 0, DateTimeKind.Utc));

        Assert.Same(latest, SessionCheckpointSelection.ResolveUnambiguousLatest(new[] { latest, missingId }));
    }
}
