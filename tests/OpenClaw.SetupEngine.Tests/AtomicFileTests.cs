namespace OpenClaw.SetupEngine.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"atomic-file-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void WriteAllText_CreatesNewFile()
    {
        var path = Path.Combine(_tempDir, "settings.json");

        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_ReplacesExistingFile()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.tmp"));
    }

    [Fact]
    public async Task WriteAllTextAsync_ReplacesExistingFile()
    {
        var path = Path.Combine(_tempDir, "setup-state.json");
        File.WriteAllText(path, "old");

        await AtomicFile.WriteAllTextAsync(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.tmp"));
    }
}
