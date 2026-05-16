using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

public class RetryPolicyTests
{
    // ─── ShouldRetry: non-retryable categories ───

    [Theory]
    [InlineData(ConnectionErrorCategory.AuthFailure)]
    [InlineData(ConnectionErrorCategory.PairingPending)]
    [InlineData(ConnectionErrorCategory.PairingRejected)]
    [InlineData(ConnectionErrorCategory.ProtocolMismatch)]
    [InlineData(ConnectionErrorCategory.Cancelled)]
    [InlineData(ConnectionErrorCategory.Disposed)]
    public void ShouldRetry_NonRetryableCategory_ReturnsFalse(ConnectionErrorCategory category)
    {
        Assert.False(RetryPolicy.ShouldRetry(category));
    }

    // ─── ShouldRetry: retryable categories ───

    [Theory]
    [InlineData(ConnectionErrorCategory.RateLimited)]
    [InlineData(ConnectionErrorCategory.NetworkUnreachable)]
    [InlineData(ConnectionErrorCategory.ServerClose)]
    [InlineData(ConnectionErrorCategory.MalformedMessage)]
    [InlineData(ConnectionErrorCategory.InternalError)]
    [InlineData(ConnectionErrorCategory.SshTunnelFailure)]
    public void ShouldRetry_RetryableCategory_ReturnsTrue(ConnectionErrorCategory category)
    {
        Assert.True(RetryPolicy.ShouldRetry(category));
    }

    // ─── GetBackoffMs: standard backoff ───

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 2000)]
    [InlineData(2, 4000)]
    [InlineData(3, 8000)]
    [InlineData(4, 15000)]
    [InlineData(5, 30000)]
    [InlineData(6, 60000)]
    public void GetBackoffMs_StandardBackoff_ReturnsExpectedValue(int attempt, int expectedMs)
    {
        Assert.Equal(expectedMs, RetryPolicy.GetBackoffMs(ConnectionErrorCategory.NetworkUnreachable, attempt));
    }

    // ─── GetBackoffMs: rate-limited backoff ───

    [Theory]
    [InlineData(0, 30000)]
    [InlineData(1, 60000)]
    [InlineData(2, 120000)]
    [InlineData(3, 300000)]
    public void GetBackoffMs_RateLimited_ReturnsExpectedValue(int attempt, int expectedMs)
    {
        Assert.Equal(expectedMs, RetryPolicy.GetBackoffMs(ConnectionErrorCategory.RateLimited, attempt));
    }

    // ─── GetBackoffMs: server-close backoff ───

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 2000)]
    [InlineData(2, 4000)]
    public void GetBackoffMs_ServerClose_ReturnsExpectedValue(int attempt, int expectedMs)
    {
        Assert.Equal(expectedMs, RetryPolicy.GetBackoffMs(ConnectionErrorCategory.ServerClose, attempt));
    }

    // ─── GetBackoffMs: clamps to max when attempt exceeds array length ───

    [Theory]
    [InlineData(ConnectionErrorCategory.NetworkUnreachable, 7, 60000)]
    [InlineData(ConnectionErrorCategory.NetworkUnreachable, 100, 60000)]
    [InlineData(ConnectionErrorCategory.RateLimited, 4, 300000)]
    [InlineData(ConnectionErrorCategory.RateLimited, 50, 300000)]
    [InlineData(ConnectionErrorCategory.ServerClose, 3, 4000)]
    [InlineData(ConnectionErrorCategory.ServerClose, 20, 4000)]
    public void GetBackoffMs_AttemptExceedsArrayLength_ClampsToMax(
        ConnectionErrorCategory category, int attempt, int expectedMs)
    {
        Assert.Equal(expectedMs, RetryPolicy.GetBackoffMs(category, attempt));
    }

    // ─── GetBackoffMs: unmapped categories use standard backoff ───

    [Theory]
    [InlineData(ConnectionErrorCategory.MalformedMessage)]
    [InlineData(ConnectionErrorCategory.InternalError)]
    [InlineData(ConnectionErrorCategory.SshTunnelFailure)]
    public void GetBackoffMs_UnmappedCategory_UsesStandardBackoff(ConnectionErrorCategory category)
    {
        for (var i = 0; i < RetryPolicy.StandardBackoffMs.Length; i++)
        {
            Assert.Equal(RetryPolicy.StandardBackoffMs[i], RetryPolicy.GetBackoffMs(category, i));
        }
    }
}
