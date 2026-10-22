using OpenClaw.Shared;
using OpenClaw.TestSupport;
using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupSettingsPersistenceTests
{
    [Fact]
    public async Task StandaloneSetupWritersShareThePathLeaseAndPreserveUnrelatedFields()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("settings.json");
        File.WriteAllText(path, """{"Unrelated":"keep","AutoStart":false}""");
        using var started = new CountdownEvent(2);
        var lease = PersistenceFileLease.Acquire(path);
        var settings = Task.Run(() =>
        {
            started.Signal();
            new TraySettingsConfig { NodeCameraEnabled = false }.MergeIntoSettingsFile(path, includeAutoStart: false);
        });
        var startup = Task.Run(() =>
        {
            started.Signal();
            TraySettingsConfig.UpdateAutoStartInSettingsFile(path, true);
        });
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(settings.IsCompleted);
            Assert.False(startup.IsCompleted);
        }
        finally { lease.Dispose(); }
        await Task.WhenAll(settings, startup).WaitAsync(TimeSpan.FromSeconds(5));
        using var actual = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("keep", actual.RootElement.GetProperty("Unrelated").GetString());
        Assert.True(actual.RootElement.GetProperty("AutoStart").GetBoolean());
        Assert.False(actual.RootElement.GetProperty("NodeCameraEnabled").GetBoolean());
    }
}
