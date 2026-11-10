// Windows file-sharing regression for the setup fixture's live tray-log reader.
// Run with --filter FullyQualifiedName~TrayLogReadTests; no WSL or app fixture is started.
namespace OpenClaw.E2ETests.Setup;

/// <summary>Reads real bytes while holding the same append/share handle as the production logger.</summary>
public sealed class TrayLogReadTests : IDisposable
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"openclaw-tray-log-read-{Guid.NewGuid():N}");
    private string LogPath => Path.Combine(_directory, "openclaw-tray.log");

    public TrayLogReadTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task LiveReader_PreservesLinesWhileLoggerAppendHandleIsHeld()
    {
        const string started = "[INFO] [WslKeepAlive] Started keepalive for fixture-distro";
        const string existing = "[INFO] [WslKeepAlive] Keepalive already running for fixture-distro";
        await using var writer = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var text = new StreamWriter(writer) { AutoFlush = true };
        await text.WriteLineAsync(started);
        await text.WriteLineAsync("");

        // Negative control: the old API denies the existing writer's write access on Windows.
        var blocked = await Assert.ThrowsAsync<IOException>(() => File.ReadAllLinesAsync(LogPath));
        Assert.Equal(SharingViolation, blocked.HResult);
        Assert.Equal(new[] { started, "" }, await E2ESetupFixture.ReadTrayLogLinesAsync(LogPath));

        await text.WriteLineAsync(existing);
        Assert.Equal(new[] { started, "", existing }, await E2ESetupFixture.ReadTrayLogLinesAsync(LogPath));
    }

    [Fact]
    public async Task LiveReader_DoesNotConvertMissingFileIntoSuccessfulRead() =>
        await Assert.ThrowsAsync<FileNotFoundException>(() => E2ESetupFixture.ReadTrayLogLinesAsync(LogPath));

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
