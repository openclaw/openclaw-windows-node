using System.Security.Cryptography;
using System.Text;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class IdentityCleanupTransactionTests
{
    [Fact]
    public void CreationReceiptKeepsTheExactPublishedBaselineAfterLaterWrites()
    {
        using var temp = new TempDirectory();
        var registry = new GatewayRegistry(temp.Path);
        var id = Guid.NewGuid().ToString();
        using var staged = new GatewayValidationIdentity();
        new DeviceIdentity(staged.DirectoryPath).Initialize();
        var expectedJson = File.ReadAllText(Path.Combine(staged.DirectoryPath, "device-key-ed25519.json"));
        var creation = staged.CopyTo(registry.GetIdentityDirectory(id));
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedJson)));
        Assert.Null(creation.OriginalJson);
        Assert.Equal(expectedHash, creation.AppliedContentHash);
        var later = new DeviceIdentity(registry.GetIdentityDirectory(id));
        later.LoadExisting();
        later.StoreDeviceTokenForRole("operator", "newer-writer");
        var newerJson = File.ReadAllText(creation.IdentityPath);
        Assert.Equal(expectedHash, creation.AppliedContentHash);
        Assert.NotEqual(expectedHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(newerJson))));
        Assert.False(registry.RemoveUnregisteredIdentity(id, creation));
        Assert.Equal(newerJson, File.ReadAllText(creation.IdentityPath));
    }

    [Fact]
    public async Task CleanupWaitsForTheIdentityWriterThenRejectsItsOlderCreationBaseline()
    {
        using var temp = new TempDirectory();
        var registry = new GatewayRegistry(temp.Path);
        var id = Guid.NewGuid().ToString();
        using var staged = new GatewayValidationIdentity();
        new DeviceIdentity(staged.DirectoryPath).Initialize();
        var creation = staged.CopyTo(registry.GetIdentityDirectory(id));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = new DeviceIdentity(registry.GetIdentityDirectory(id), new HoldingLogger(entered, release));
        writer.LoadExisting();
        var updating = Task.Run(() => writer.StoreDeviceTokenForRole("operator", "newer-writer"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleting = Task.Run(() =>
        {
            started.SetResult();
            return registry.RemoveUnregisteredIdentity(id, creation);
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(deleting, await Task.WhenAny(deleting, Task.Delay(150)));
        }
        finally { release.Set(); }
        await updating.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await deleting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("newer-writer", DeviceIdentity.TryReadStoredDeviceToken(registry.GetIdentityDirectory(id)));
    }

    [Fact]
    public async Task CreateIfAbsentWaitsForIdentityWriterAndCannotOverwriteItsFile()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("destination");
        var existing = new DeviceIdentity(destination);
        existing.Initialize();
        using var staged = new GatewayValidationIdentity();
        new DeviceIdentity(staged.DirectoryPath).Initialize();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = new DeviceIdentity(destination, new HoldingLogger(entered, release));
        writer.LoadExisting();
        var updating = Task.Run(() => writer.StoreDeviceTokenForRole("operator", "keep"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copying = Task.Run(() =>
        {
            started.SetResult();
            return Record.Exception(() => staged.CopyTo(destination));
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(copying, await Task.WhenAny(copying, Task.Delay(150)));
        }
        finally { release.Set(); }
        await updating.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(await copying.WaitAsync(TimeSpan.FromSeconds(5)));
        var actual = new DeviceIdentity(destination);
        actual.LoadExisting();
        Assert.Equal(existing.DeviceId, actual.DeviceId);
        Assert.Equal("keep", actual.DeviceToken);
    }

    [Fact]
    public void UnchangedUnregisteredCreationCanBeRemovedButAdoptedAndUnknownRemain()
    {
        using var temp = new TempDirectory();
        var registry = new GatewayRegistry(temp.Path);
        using var staged = new GatewayValidationIdentity();
        new DeviceIdentity(staged.DirectoryPath).Initialize();
        var id = Guid.NewGuid().ToString();
        var creation = staged.CopyTo(registry.GetIdentityDirectory(id));
        File.WriteAllText(temp.Combine("gateways.json"), "{");
        Assert.Throws<System.Text.Json.JsonException>(() => registry.RemoveUnregisteredIdentity(id, creation));
        Assert.True(File.Exists(creation.IdentityPath));
        File.Delete(temp.Combine("gateways.json"));
        var external = new GatewayRegistry(temp.Path);
        external.AddOrUpdate(new() { Id = id, Url = "wss://adopted.example" });
        external.Save();
        Assert.False(registry.RemoveUnregisteredIdentity(id, creation));
        Assert.True(File.Exists(creation.IdentityPath));
        external.Remove(id);
        external.Save();
        Assert.True(registry.RemoveUnregisteredIdentity(id, creation));
        Assert.False(Directory.Exists(registry.GetIdentityDirectory(id)));
    }

    private sealed class HoldingLogger(ManualResetEventSlim entered, ManualResetEventSlim release) : IOpenClawLogger
    {
        public void Info(string message)
        {
            if (message != "Device token stored") return;
            // The production token writer invokes this while holding WithIdentityFileLock.
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Identity-writer barrier timed out.");
        }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? error = null) { }
    }
}
