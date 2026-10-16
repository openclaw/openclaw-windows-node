namespace OpenClaw.Shared.Tests;

public sealed class LoggerWriteBackoffTests
{
    [Fact]
    public void Next_GrowsThenCaps()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(50), LoggerWriteBackoff.Next(0));
        Assert.Equal(TimeSpan.FromMilliseconds(100), LoggerWriteBackoff.Next(1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), LoggerWriteBackoff.Next(2));
        Assert.Equal(TimeSpan.FromMilliseconds(1600), LoggerWriteBackoff.Next(5));
        Assert.Equal(TimeSpan.FromMilliseconds(1600), LoggerWriteBackoff.Next(40));
    }
}
