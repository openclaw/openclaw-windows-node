namespace OpenClaw.SetupEngine.Tests;

public class SetupRunLockTests : IDisposable
{
    private readonly string _tempDir;

    public SetupRunLockTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"setup-lock-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void TryAcquire_BlocksSecondConcurrentRun()
    {
        Assert.True(SetupRunLock.TryAcquire(_tempDir, out var first, out var firstMessage));
        Assert.Null(firstMessage);
        using (first)
        {
            Assert.False(SetupRunLock.TryAcquire(_tempDir, out var second, out var secondMessage));
            Assert.Null(second);
            Assert.Contains("Another OpenClaw setup run", secondMessage);
        }
    }

    [Fact]
    public void Dispose_ReleasesLockForNextRun()
    {
        using (SetupRunLock.TryAcquire(_tempDir, out var first, out _) ? first : null)
        {
        }

        Assert.True(SetupRunLock.TryAcquire(_tempDir, out var second, out var secondMessage));
        Assert.Null(secondMessage);
        second?.Dispose();
    }
}
