using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ResumableJobCheckpointTests
{
    [Fact] public void Starts_at_ordinal_one()
    {
        var c = new ResumableJobCheckpoint("job-1");
        Assert.Equal(1, c.NextOrdinal);
        Assert.Equal(0, c.LastCompletedOrdinal);
    }

    [Fact] public void Advances_monotonically()
    {
        var c = new ResumableJobCheckpoint("job-1");
        Assert.True(c.TryMarkCompleted("step-a", 1));
        Assert.True(c.TryMarkCompleted("step-b", 2));
        Assert.Equal(3, c.NextOrdinal);
        Assert.Equal(new[] { "step-a", "step-b" }, c.CompletedStepIds);
    }

    [Fact] public void Rejects_skipped_ordinal()
    {
        var c = new ResumableJobCheckpoint("job-1");
        Assert.False(c.TryMarkCompleted("step-b", 2));
        Assert.Equal(1, c.NextOrdinal);
    }

    [Fact] public void Rejects_duplicate_step_replay()
    {
        var c = new ResumableJobCheckpoint("job-1");
        Assert.True(c.TryMarkCompleted("step-a", 1));
        Assert.False(c.TryMarkCompleted("step-a", 2));
    }

    [Fact] public void Resume_validation()
    {
        Assert.True(ResumableJobCheckpoint.IsResumable(3, 10));
        Assert.False(ResumableJobCheckpoint.IsResumable(10, 10));
        Assert.False(ResumableJobCheckpoint.IsResumable(-1, 10));
    }
}
