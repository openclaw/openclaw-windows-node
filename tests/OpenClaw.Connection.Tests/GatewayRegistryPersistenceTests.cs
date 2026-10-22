using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayRegistryPersistenceTests
{
    [Theory]
    [InlineData("endpoint")]
    [InlineData("credential")]
    [InlineData("active")]
    [InlineData("addition")]
    public void StaleInstanceCannotOverwriteAnotherWriter(string change)
    {
        using var temp = new TempDirectory();
        var first = new GatewayRegistry(temp.Path);
        first.AddOrUpdate(new() { Id = "a", Url = "wss://a.example" });
        first.AddOrUpdate(new() { Id = "b", Url = "wss://b.example" });
        first.SetActive("a");
        first.Save();
        var stale = new GatewayRegistry(temp.Path);
        stale.Load();
        switch (change)
        {
            case "endpoint": first.Update("a", value => value with { Url = "wss://new.example" }); break;
            case "credential": first.Update("a", value => value with { SharedGatewayToken = "new" }); break;
            case "active": first.SetActive("b"); break;
            case "addition": first.AddOrUpdate(new() { Id = "c", Url = "wss://c.example" }); break;
        }
        first.Save();
        var saved = File.ReadAllBytes(Path.Combine(temp.Path, "gateways.json"));
        var before = stale.GetSnapshot();
        Assert.Throws<InvalidOperationException>(() => stale.UpdateAndSave("a", value => value with { LastConnected = DateTime.UtcNow }));
        Assert.Equal(before.Records, stale.GetAll());
        Assert.Throws<InvalidOperationException>(() => stale.Save());
        Assert.Equal(saved, File.ReadAllBytes(Path.Combine(temp.Path, "gateways.json")));
    }

    [Fact]
    public async Task OtherInstanceCannotWriteBetweenFinalCheckAndReplacement()
    {
        using var temp = new TempDirectory();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var fs = new HeldWriteFileSystem(entered, release);
        var first = new GatewayRegistry(temp.Path, fs);
        first.AddOrUpdate(new() { Id = "a", Url = "wss://a.example" });
        first.SetActive("a");
        first.Save();
        var second = new GatewayRegistry(temp.Path);
        second.Load();
        first.Update("a", value => value with { SharedGatewayToken = "first-writer" });
        fs.Hold = true;
        var saving = Task.Run(() => first.Save());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var competing = Task.Run(() =>
        {
            started.SetResult();
            return Record.Exception(() => second.UpdateAndSave("a", value => value with { Url = "wss://second.example" }));
        });
        try
        {
            await started.Task;
            Assert.NotSame(competing, await Task.WhenAny(competing, Task.Delay(150)));
        }
        finally { release.Set(); }
        await saving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(await competing.WaitAsync(TimeSpan.FromSeconds(5)));
        var actual = new GatewayRegistry(temp.Path);
        actual.Load();
        Assert.Equal("first-writer", actual.GetActive()!.SharedGatewayToken);
        Assert.Equal("wss://a.example", actual.GetActive()!.Url);
    }

    [Fact]
    public void LastConnectedMergesMonotonicallyUnderTheLease()
    {
        using var temp = new TempDirectory();
        var first = new GatewayRegistry(temp.Path);
        first.AddOrUpdate(new() { Id = "a", Url = "wss://a.example" });
        first.Save();
        var second = new GatewayRegistry(temp.Path);
        second.Load();
        var newer = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        first.UpdateAndSave("a", value => value with { LastConnected = newer });
        second.UpdateAndSave("a", value => value with { LastConnected = newer.AddMinutes(-1) });
        Assert.Equal(newer, second.GetById("a")!.LastConnected);
        first.Load();
        Assert.Equal(newer, first.GetById("a")!.LastConnected);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    public void UnchangedInvalidJsonCanBeReplacedButAChangedInvalidFileCannot(string invalidJson)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "gateways.json");
        File.WriteAllText(path, invalidJson);
        var registry = new GatewayRegistry(temp.Path);
        registry.Load();
        registry.AddOrUpdate(new() { Id = "a", Url = "wss://a.example" });
        registry.SetActive("a");
        registry.Save();
        var reloaded = new GatewayRegistry(temp.Path);
        reloaded.Load();
        Assert.Equal("a", reloaded.GetActive()?.Id);

        File.WriteAllText(path, invalidJson);
        registry.Load();
        registry.AddOrUpdate(new() { Id = "b", Url = "wss://b.example" });
        File.WriteAllText(path, "[");
        Assert.Throws<JsonException>(() => registry.Save());
        Assert.Equal("[", File.ReadAllText(path));
    }

    private sealed class HeldWriteFileSystem(ManualResetEventSlim entered, ManualResetEventSlim release) : IFileSystem
    {
        public bool Hold { get; set; }
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public string ReadAllText(string path) => File.ReadAllText(path);
        public void WriteAllText(string path, string contents)
        {
            File.WriteAllText(path, contents);
            if (!Hold) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test barrier timed out.");
        }
        public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
        public void DeleteFile(string path) => File.Delete(path);
    }
}
