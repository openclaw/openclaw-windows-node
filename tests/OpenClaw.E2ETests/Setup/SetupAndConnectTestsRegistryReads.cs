using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.E2ETests.Setup;

// The prefix includes these fixture-free regressions in the hosted setup/connect filter.
public sealed class SetupAndConnectTestsRegistryReads : IDisposable
{
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"openclaw-registry-read-{Guid.NewGuid():N}");

    public SetupAndConnectTestsRegistryReads() => Directory.CreateDirectory(_dataDir);

    [Fact]
    public async Task Read_WaitsForInFlightRegistrySaveAndObservesCommittedRecord()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var fs = new HeldWriteFileSystem(entered, release);
        var registry = CreateRegistry(fs);
        fs.Hold = true;
        var saving = Task.Run(() =>
            registry.UpdateAndSave("test-gateway", record => record with { SharedGatewayToken = "new-token" }));
        Task<string>? reading = null;
        try
        {
            Assert.True(entered.Wait(BarrierTimeout), "Registry save did not reach the publication barrier.");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reading = Task.Run(() =>
            {
                started.SetResult();
                using var document = GatewayRegistryFile.Read(_dataDir);
                return document.RootElement.GetProperty("gateways")[0].GetProperty("sharedGatewayToken").GetString()!;
            });
            await started.Task.WaitAsync(BarrierTimeout);
            // slopwatch-ignore: SW004 Bounded negative assertion while the writer is held at an explicit barrier.
            Assert.NotSame(reading, await Task.WhenAny(reading, Task.Delay(150)));
        }
        finally
        {
            release.Set();
            await saving.WaitAsync(BarrierTimeout);
            if (reading is not null)
                await reading.WaitAsync(BarrierTimeout);
        }
        Assert.Equal("new-token", await reading!);
    }

    [Fact]
    public async Task Read_ConcurrentRegistrySavesPreserveCompleteSnapshotsAndDoNotBlockReplacement()
    {
        var registry = CreateRegistry();
        using var start = new ManualResetEventSlim();
        const int iterations = 500;
        var saving = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < iterations; i++)
                registry.UpdateAndSave("test-gateway", record => record with { LastConnected = DateTime.UtcNow });
        });
        var reading = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < iterations; i++)
            {
                using var document = GatewayRegistryFile.Read(_dataDir);
                Assert.Equal("test-gateway", document.RootElement.GetProperty("activeId").GetString());
                var gateway = Assert.Single(document.RootElement.GetProperty("gateways").EnumerateArray());
                Assert.Equal("test-gateway", gateway.GetProperty("id").GetString());
                Assert.Equal("wss://test.example", gateway.GetProperty("url").GetString());
                Assert.Equal("old-token", gateway.GetProperty("sharedGatewayToken").GetString());
            }
        });
        start.Set();
        await Task.WhenAll(saving, reading);
    }

    [Fact]
    public void Read_MissingOrMalformedRegistryFailsWithoutFallback()
    {
        Assert.Throws<FileNotFoundException>(() => GatewayRegistryFile.Read(_dataDir));
        File.WriteAllText(Path.Combine(_dataDir, "gateways.json"), "{");
        Assert.ThrowsAny<JsonException>(() => GatewayRegistryFile.Read(_dataDir));
    }

    private GatewayRegistry CreateRegistry(IFileSystem? fs = null)
    {
        var registry = new GatewayRegistry(_dataDir, fs);
        registry.AddOrUpdate(new() { Id = "test-gateway", Url = "wss://test.example", SharedGatewayToken = "old-token" });
        registry.SetActive("test-gateway");
        registry.Save();
        return registry;
    }

    public void Dispose() => Directory.Delete(_dataDir, recursive: true);

    private sealed class HeldWriteFileSystem(ManualResetEventSlim entered, ManualResetEventSlim release) : IFileSystem
    {
        public bool Hold { get; set; }
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public string ReadAllText(string path) => File.ReadAllText(path);
        public void WriteAllText(string path, string content)
        {
            File.WriteAllText(path, content);
            if (!Hold) return;
            entered.Set();
            if (!release.Wait(BarrierTimeout))
                throw new TimeoutException("Registry save publication barrier timed out.");
        }
        public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
        public void DeleteFile(string path) => File.Delete(path);
    }
}
