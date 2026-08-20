namespace OpenClaw.MSIXHost.Tests;

public sealed class OpenClawResetterTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public async Task ResetGatewayPreservesOpenClawState()
    {
        string installDirectory = Path.Combine(_testDirectory, ".openclaw-msix", "app");
        string stateDirectory = Path.Combine(_testDirectory, ".openclaw");
        Directory.CreateDirectory(installDirectory);
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(Path.Combine(installDirectory, "payload.txt"), "payload");
        File.WriteAllText(Path.Combine(stateDirectory, "openclaw.json"), "{}");

        await OpenClawResetter.ResetAsync(
            "missing-node.exe",
            installDirectory,
            stateDirectory,
            includeUserState: false,
            _ => { },
            CancellationToken.None);

        Assert.False(Directory.Exists(installDirectory));
        Assert.True(File.Exists(Path.Combine(stateDirectory, "openclaw.json")));
    }

    [Fact]
    public async Task ResetAllRemovesGatewayAndOpenClawState()
    {
        string installDirectory = Path.Combine(_testDirectory, ".openclaw-msix", "app");
        string stateDirectory = Path.Combine(_testDirectory, ".openclaw");
        Directory.CreateDirectory(installDirectory);
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(Path.Combine(installDirectory, "payload.txt"), "payload");
        File.WriteAllText(Path.Combine(stateDirectory, "openclaw.json"), "{}");

        await OpenClawResetter.ResetAsync(
            "missing-node.exe",
            installDirectory,
            stateDirectory,
            includeUserState: true,
            _ => { },
            CancellationToken.None);

        Assert.False(Directory.Exists(installDirectory));
        Assert.False(Directory.Exists(stateDirectory));
    }

    [Fact]
    public async Task ResetRejectsUnexpectedGatewayDirectory()
    {
        string installDirectory = Path.Combine(_testDirectory, "unrelated", "app");

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                OpenClawResetter.ResetAsync(
                    "missing-node.exe",
                    installDirectory,
                    Path.Combine(_testDirectory, ".openclaw"),
                    includeUserState: false,
                    _ => { },
                    CancellationToken.None));

        Assert.Contains(
            "unexpected gateway directory",
            exception.Message,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
