using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayIdentityRemovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedStageIsReportedOnRetryEvenIfStartupRecreatedProfile(bool recreated)
    {
        using var temp = new TempDirectory();
        var registry = new GatewayRegistry(temp.Path);
        var record = LocalGatewaySettingsTests.Native();
        registry.AddOrUpdate(record);
        registry.SetActive(record.Id);
        registry.Save();
        var profile = registry.GetIdentityDirectory(record.Id);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "device-key-ed25519.json"), "original-key");
        var previousStage = temp.Combine($"gateway-removal-{record.Id}-{Guid.NewGuid():N}");
        Directory.Move(profile, previousStage);
        if (recreated)
        {
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "device-key-ed25519.json"), "new-key");
        }

        var reopened = new GatewayRegistry(temp.Path);
        reopened.Load();
        var error = Assert.Throws<IOException>(() => GatewayIdentityRemoval.Remove(reopened, record));

        Assert.Contains(previousStage, error.Message);
        Assert.Contains("retained", error.Message);
        Assert.Equal(record, reopened.GetById(record.Id));
        Assert.Equal("original-key", File.ReadAllText(Path.Combine(previousStage, "device-key-ed25519.json")));
        if (recreated)
            Assert.Equal("new-key", File.ReadAllText(Path.Combine(profile, "device-key-ed25519.json")));
        Assert.Single(Directory.GetDirectories(temp.Path, "gateway-removal-*"));
    }

    [Fact]
    public void OtherGatewayWithOverlappingIdDoesNotBlockRemoval()
    {
        using var temp = new TempDirectory();
        var registry = new GatewayRegistry(temp.Path);
        var record = LocalGatewaySettingsTests.Native();
        registry.AddOrUpdate(record);
        registry.Save();
        var unrelatedStage = temp.Combine($"gateway-removal-{record.Id}-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(unrelatedStage);

        GatewayIdentityRemoval.Remove(registry, record);

        Assert.Null(registry.GetById(record.Id));
        Assert.True(Directory.Exists(unrelatedStage));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedFinalSaveRestoresOriginalProfile(bool concurrentEdit)
    {
        using var temp = new TempDirectory();
        var fs = new CommitFailureFileSystem();
        var registry = new GatewayRegistry(temp.Path, fs);
        var record = LocalGatewaySettingsTests.Native();
        registry.AddOrUpdate(record);
        registry.SetActive(record.Id);
        registry.Save();
        var profile = registry.GetIdentityDirectory(record.Id);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "device-key-ed25519.json"), "original-key");
        if (concurrentEdit)
        {
            fs.BeforeRead = () =>
            {
                if (Directory.Exists(profile)) return;
                var path = temp.Combine("gateways.json");
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                json["gateways"]![0]!["sharedGatewayToken"] = "new-credential";
                File.WriteAllText(path, json.ToJsonString());
                fs.BeforeRead = null;
            };
        }
        else fs.FailCommit = true;

        Assert.ThrowsAny<Exception>(() => GatewayIdentityRemoval.Remove(registry, record));
        Assert.NotNull(registry.GetById(record.Id));
        Assert.Equal("original-key", File.ReadAllText(Path.Combine(profile, "device-key-ed25519.json")));
        Assert.Empty(Directory.GetDirectories(temp.Path, "gateway-removal-*"));
        if (concurrentEdit)
        {
            registry.Load();
            Assert.Equal("new-credential", registry.GetById(record.Id)!.SharedGatewayToken);
        }
    }

    [Fact]
    public void WorkspaceJunctionIsRemovedWithoutDeletingItsTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var registry = new GatewayRegistry(temp.Path);
        var record = LocalGatewaySettingsTests.Native() with { NativeRuntimeContract = null };
        registry.AddOrUpdate(record);
        registry.SetActive(record.Id);
        registry.Save();
        var workspace = Path.Combine(registry.GetIdentityDirectory(record.Id), "native-gateway", "workspace");
        Directory.CreateDirectory(workspace);
        var gitObject = Path.Combine(workspace, "readonly-git-object");
        File.WriteAllText(gitObject, "git-object");
        File.SetAttributes(gitObject, FileAttributes.ReadOnly);
        var external = temp.Combine("external-package");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "keep.txt"), "outside");
        var link = Path.Combine(workspace, "linked-package");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            Arguments = $"/d /c mklink /J \"{link}\" \"{external}\"",
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);

        GatewayIdentityRemoval.Remove(registry, record);

        Assert.Null(registry.GetById(record.Id));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(external, "keep.txt")));
        Assert.False(Directory.Exists(workspace));
    }

    private sealed class CommitFailureFileSystem : IFileSystem
    {
        public bool FailCommit { get; set; }
        public Action? BeforeRead { get; set; }
        public bool FileExists(string path) => File.Exists(path);
        public string ReadAllText(string path) { BeforeRead?.Invoke(); return File.ReadAllText(path); }
        public void WriteAllText(string path, string content) => File.WriteAllText(path, content);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
        public void DeleteFile(string path) => File.Delete(path);
        public void MoveFile(string source, string destination, bool overwrite)
        {
            if (FailCommit) throw new IOException("Injected registry commit failure");
            File.Move(source, destination, overwrite);
        }
    }
}
