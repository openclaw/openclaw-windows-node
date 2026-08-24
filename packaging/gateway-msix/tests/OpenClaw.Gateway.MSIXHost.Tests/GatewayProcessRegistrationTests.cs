using System.Diagnostics;

namespace OpenClaw.MSIXHost.Tests;

public sealed class GatewayProcessRegistrationTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public void CreateRecordsTheKnownPackagedExecutablePath()
    {
        string installDirectory = Path.Combine(_testDirectory, "app");
        string executablePath = Path.Combine(
            _testDirectory,
            "runtime",
            "node.exe");

        using GatewayProcessRegistration registration =
            GatewayProcessRegistration.Create(
                Process.GetCurrentProcess(),
                installDirectory,
                executablePath);

        string[] record = File.ReadAllLines(
            Path.Combine(_testDirectory, "gateway-process.txt"));

        Assert.Equal(3, record.Length);
        Assert.Equal(Path.GetFullPath(executablePath), record[2]);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
