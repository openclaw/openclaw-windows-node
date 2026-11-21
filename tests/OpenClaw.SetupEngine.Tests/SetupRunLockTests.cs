namespace OpenClaw.SetupEngine.Tests;

public class SetupRunLockTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dataDir;
    private readonly string _localDir;

    public SetupRunLockTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"setup-lock-test-{Guid.NewGuid():N}");
        _dataDir = Path.Combine(_tempDir, "data");
        _localDir = Path.Combine(_tempDir, "local");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void TryAcquire_BlocksSecondConcurrentRun()
    {
        Assert.True(SetupRunLock.TryAcquire(_dataDir, _localDir, out var first, out var firstMessage));
        Assert.Null(firstMessage);
        using (first)
        {
            Assert.False(SetupRunLock.TryAcquire(_dataDir, _localDir, out var second, out var secondMessage));
            Assert.Null(second);
            Assert.Contains("Another OpenClaw setup run", secondMessage);
        }
    }

    [Fact]
    public void Dispose_ReleasesLockForNextRun()
    {
        using (SetupRunLock.TryAcquire(_dataDir, _localDir, out var first, out _) ? first : null)
        {
        }

        Assert.True(SetupRunLock.TryAcquire(_dataDir, _localDir, out var second, out var secondMessage));
        Assert.Null(secondMessage);
        second?.Dispose();
    }

    [Fact]
    public void TryAcquire_BlocksSecondProfileSharingLocalDataDir()
    {
        var otherProfileDataDir = Path.Combine(_tempDir, "other-data");
        Assert.True(SetupRunLock.TryAcquire(_dataDir, _localDir, out var first, out _));
        using (first)
        {
            Assert.False(SetupRunLock.TryAcquire(otherProfileDataDir, _localDir, out var second, out var message));
            Assert.Null(second);
            Assert.Contains("Another OpenClaw setup run", message);
            Assert.Contains(_localDir, message);

            // The refused admission must not keep the other profile's run lock.
            Assert.True(SetupRunLock.TryAcquire(
                otherProfileDataDir,
                Path.Combine(_tempDir, "other-local"),
                out var unrelatedTarget,
                out var unrelatedMessage), unrelatedMessage);
            unrelatedTarget!.Dispose();
        }
    }
}
