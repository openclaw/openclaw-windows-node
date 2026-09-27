using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection.Migration;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[Collection("Migration inventory environment")]
public sealed class MigrationInventoryTests : IDisposable
{
    private const string GatewayId = "12345678-1234-1234-1234-123456789abc";
    private readonly TempDirectory _directory = new(Path.Combine(Environment.CurrentDirectory, ".migration-inventory-tests-"));
    private readonly EnvironmentScope _environment = new();
    private string Roaming => _directory.Combine("roaming");
    private string Local => _directory.Combine("local");

    public MigrationInventoryTests() => _environment.Set("OPENCLAW_STATE_DIR", Roaming);

    [Fact]
    public void Capture_MissingOptionalState_IsStableAndCreatesNothing()
    {
        var first = Capture();
        var second = Capture();

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.False(first.AutoStart);
        Assert.All(first.Entries, entry =>
        {
            Assert.False(entry.Exists);
            Assert.Null(entry.Length);
            Assert.Null(entry.Sha256);
            Assert.True(Path.IsPathFullyQualified(entry.CanonicalPath));
        });
        Assert.Contains(first.Entries, entry => entry.RelativeName == "settings.json");
        Assert.Contains(first.Entries, entry => entry.RelativeName == Path.Combine("LocalAI", "state.json"));
        Assert.Contains(first.Entries, entry => entry.RelativeName == "exec-approvals.json");
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory.Path));
    }

    [CollectionDefinition("Migration inventory environment", DisableParallelization = true)]
    public sealed class MigrationInventoryEnvironmentCollection
    {
    }

    [Fact]
    public void Capture_McpOnlySettings_RoundTripsWithoutCredentialsOrWrites()
    {
        var settings = new SettingsData { AutoStart = false, EnableMcpServer = true, EnableNodeMode = false };
        var legacy = JsonNode.Parse(settings.ToJson())!.AsObject();
        legacy["Token"] = "test";
        var content = legacy.ToJsonString();
        var path = Write(Roaming, "settings.json", content);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var inventory = Capture();
        var json = JsonSerializer.Serialize(inventory);
        var restored = JsonSerializer.Deserialize<MigrationInventory>(json)!;

        Assert.False(inventory.AutoStart);
        Assert.Equal(inventory.Fingerprint, restored.Fingerprint);
        Assert.Equal<MigrationInventory.Entry>(inventory.Entries, restored.Entries);
        Assert.DoesNotContain("\"test\"", json);
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        var entry = Assert.Single(inventory.Entries, entry => entry.CanonicalPath == path);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), entry.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), entry.Sha256);
    }

    [Fact]
    public void Capture_SettingsPreservesUnknownFieldsAndExactCaseOwnerSemantics()
    {
        var settings = JsonNode.Parse(new SettingsData { AutoStart = false }.ToJson())!.AsObject();
        settings["Token"] = "test";
        settings["RetiredSetting"] = new JsonObject { ["Work"] = true, ["work"] = false };
        settings["autoStart"] = true;
        var content = settings.ToJsonString();
        var path = Write(Roaming, "settings.json", content);

        var inventory = Capture();

        Assert.False(SettingsData.FromJson(content)!.AutoStart);
        Assert.False(inventory.AutoStart);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Capture_OptionalCreationDeletionAndChangedBytes_ChangeFingerprint()
    {
        var absent = Capture();
        var path = Write(Roaming, "settings.json", """{"AutoStart":true}""");
        var present = Capture();
        File.WriteAllText(path, """{"AutoStart":false}""");
        var changed = Capture();
        File.Delete(path);

        Assert.True(present.AutoStart);
        Assert.NotEqual(absent.Fingerprint, present.Fingerprint);
        Assert.NotEqual(present.Fingerprint, changed.Fingerprint);
        Assert.Equal(absent.Fingerprint, Capture().Fingerprint);
    }

    [Fact]
    public void Capture_RemoteGatewayAndIdentities_UseStrictReadOnlyReader()
    {
        WriteRegistry();
        var identityDirectory = Path.Combine(Roaming, "gateways", GatewayId);
        var identity = new DeviceIdentity(identityDirectory);
        identity.Initialize();
        identity.StoreDeviceToken("test");
        var path = Path.Combine(identityDirectory, "device-key-ed25519.json");
        var original = File.ReadAllBytes(path);

        var inventory = Capture();

        Assert.Contains(inventory.Entries, entry => entry.CanonicalPath == path && entry.Exists);
        Assert.DoesNotContain("\"test\"", JsonSerializer.Serialize(inventory));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Combine(Roaming, "device-key-ed25519.json")));
    }

    [Fact]
    public void Capture_RegistryWithoutIdentity_DoesNotGenerateOne()
    {
        WriteRegistry();
        var inventory = Capture();
        Assert.Contains(inventory.Entries, entry =>
            entry.RelativeName == Path.Combine("gateways", GatewayId, "device-key-ed25519.json") && !entry.Exists);
        Assert.False(Directory.Exists(Path.Combine(Roaming, "gateways")));
    }

    [Fact]
    public void Capture_GatewayOwnerPathSafeId_DoesNotRequireAnInventedGuidSchema()
    {
        const string id = "saved-gateway";
        var registry = new GatewayRegistry(Roaming);
        registry.AddOrUpdate(new GatewayRecord { Id = id, Url = "wss://example.test" });
        registry.SetActive(id);
        registry.Save();

        var inventory = Capture();

        Assert.Contains(inventory.Entries, entry =>
            entry.RelativeName == Path.Combine("gateways", id, "device-key-ed25519.json") && !entry.Exists);
        registry.Load();
        Assert.Equal(id, registry.ActiveGatewayId);
        Assert.Equal(id, Assert.Single(registry.GetAll()).Id);
    }

    [Fact]
    public void Capture_StateDirectoryOverride_IncludesCanonicalAndLegacyApprovals()
    {
        var state = _directory.Combine("external-state");
        _environment.Set("OPENCLAW_STATE_DIR", state);
        var canonical = Write(state, "exec-approvals.json", """{"version":1,"defaults":{"security":"deny"}}""");
        var legacy = Write(Roaming, "exec-approvals.json", """{"version":1}""");

        var inventory = Capture();

        Assert.Contains(inventory.Entries, entry => entry.CanonicalPath == canonical && entry.Root == "exec-state" && entry.Exists);
        Assert.Contains(inventory.Entries, entry => entry.CanonicalPath == legacy && entry.Root == "roaming" && entry.Exists);
    }

    [Fact]
    public async Task Capture_ApprovalsOwnerPreservesCaseDistinctAgents_WithoutRewritingPolicy()
    {
        using var store = new ExecApprovalsStore(Roaming, NullLogger.Instance);
        var initial = await store.GetSnapshotReadOnlyAsync();
        Assert.True(initial.IsSuccess);
        var written = await store.ReplaceAsync(initial.Snapshot!.Hash, new ExecApprovalsFile
        {
            Version = 1,
            Agents = new Dictionary<string, ExecApprovalsAgent>
            {
                ["Work"] = new() { Security = ExecSecurity.Deny },
                ["work"] = new() { Security = ExecSecurity.Allowlist },
            },
        });
        Assert.NotNull(written);
        var path = ExecApprovalsStore.ResolveFilePath(Roaming);
        var before = File.ReadAllBytes(path);
        var timestamp = File.GetLastWriteTimeUtc(path);

        var inventory = Capture();

        var read = await store.GetSnapshotReadOnlyAsync();
        Assert.True(read.IsSuccess);
        var agents = read.Snapshot!.File.Agents!;
        Assert.Equal(ExecSecurity.Deny, agents["Work"].Security);
        Assert.Equal(ExecSecurity.Allowlist, agents["work"].Security);
        Assert.Contains(inventory.Entries, entry => entry.CanonicalPath == path && entry.Exists);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Capture_UnmigratedApprovals_RecordsAbsenceWithoutRunningMigration()
    {
        using var store = new ExecApprovalsStore(Roaming, NullLogger.Instance);
        var initial = await store.GetSnapshotReadOnlyAsync();
        var written = await store.ReplaceAsync(initial.Snapshot!.Hash, new ExecApprovalsFile { Version = 1 });
        Assert.NotNull(written);
        var legacyPath = ExecApprovalsStore.ResolveFilePath(Roaming);
        var before = File.ReadAllBytes(legacyPath);
        var state = _directory.Combine("external-state");
        _environment.Set("OPENCLAW_STATE_DIR", state);
        using var redirectedStore = new ExecApprovalsStore(Roaming, NullLogger.Instance);
        var ownerRead = await redirectedStore.GetSnapshotReadOnlyAsync();
        Assert.Equal(ExecApprovalsSnapshotFailureKind.LegacyMigrationRequired, ownerRead.Failure!.Kind);

        var inventory = Capture();

        Assert.Contains(inventory.Entries, entry => entry.CanonicalPath == legacyPath && entry.Exists);
        Assert.Contains(inventory.Entries, entry =>
            entry.CanonicalPath == Path.Combine(state, "exec-approvals.json") && !entry.Exists);
        Assert.Equal(before, File.ReadAllBytes(legacyPath));
        Assert.False(Directory.Exists(state));
    }

    [Theory]
    [InlineData("settings.json", "{")]
    [InlineData("settings.json", "null")]
    [InlineData("settings.json", "[]")]
    [InlineData("settings.json", """{"AutoStart":"true"}""")]
    [InlineData("settings.json", """{"AutoStart":true,"AutoStart":false}""")]
    [InlineData("gateways.json", "{}")]
    [InlineData("gateways.json", """{"gateways":null}""")]
    [InlineData("gateways.json", """{"gateways":[],"activeId":"missing"}""")]
    [InlineData("gateways.json", """{"gateways":[{"id":"..","url":"ws://localhost"}]}""")]
    [InlineData("gateways.json", """{"gateways":[{"id":"12345678-1234-1234-1234-123456789abc","url":"wss://host","identityDirName":"other"}]}""")]
    [InlineData("device-key-ed25519.json", "{}")]
    [InlineData("device-key-ed25519.json", """{"PrivateKeyBase64":"test"}""")]
    [InlineData("device-key.json", "{}")]
    [InlineData("device.json", "{}")]
    [InlineData("exec-approvals.json", """{"version":2}""")]
    [InlineData("exec-approvals.json", """{"version":1,"defaults":{"security":"test"}}""")]
    [InlineData("exec-approvals.json", """{"version":1,"agents":{"Work":{},"Work":{}}}""")]
    public void Capture_InvalidSource_FailsWithoutModifyingOrDisclosingIt(string name, string content)
    {
        var path = Write(Roaming, name, content);
        var error = Assert.Throws<InvalidDataException>(() => Capture());
        Assert.DoesNotContain("\"test\"", error.ToString());
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("LocalAI\\state.json", "{}")]
    [InlineData("setup-state.json", "{")]
    [InlineData("setup-state.json", "null")]
    [InlineData("setup-managed-distro.json", "[]")]
    [InlineData("windows-node-context.json", """{"Targets":[],"Targets":[]}""")]
    public void Capture_InvalidLocalState_FailsWithoutChangingIt(string name, string content)
    {
        var path = Write(Local, name, content);
        Assert.Throws<InvalidDataException>(() => Capture());
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Capture_SetupOwnershipAndConfiguration_DoesNotInspectPayloads()
    {
        Write(Local, "setup-state.json", """{"DistroName":"OpenClaw","GatewayUrl":"ws://localhost:18789"}""");
        Write(Local, "setup-managed-distro.json", JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            DistroName = "OpenClaw",
            InstallPath = Path.Combine(Local, "wsl", "OpenClaw"),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
        Write(Local, "windows-node-context.json", """{"Targets":[{"DistroName":"OpenClaw","User":"user","WorkspacePath":"/home/user/workspace"}]}""");
        Write(Local, Path.Combine("LocalAI", "llama-server-models.ini"), "[model]\nmodel=models\\model.gguf");
        Write(Local, Path.Combine("wsl", "OpenClaw", "ext4.vhdx"), "not a disk image");
        Write(Local, Path.Combine("LocalAI", "models", "model.gguf"), "not a model");

        var inventory = Capture();

        Assert.Contains(inventory.Entries, entry => entry.RelativeName == "setup-managed-distro.json" && entry.Exists);
        Assert.DoesNotContain(inventory.Entries, entry => entry.RelativeName.EndsWith(".vhdx") || entry.RelativeName.EndsWith(".gguf"));
    }

    [Fact]
    public void Capture_RemovedGateway_PreservesItsOrphanIdentityWithoutReinstatingRecord()
    {
        var registry = WriteRegistry();
        var identityDirectory = registry.GetIdentityDirectory(GatewayId);
        var identity = new DeviceIdentity(identityDirectory);
        identity.Initialize();
        identity.StoreDeviceToken("test");
        registry.Remove(GatewayId);
        registry.Save();
        var registryPath = Path.Combine(Roaming, "gateways.json");
        var identityPath = Path.Combine(identityDirectory, "device-key-ed25519.json");
        var registryBefore = File.ReadAllBytes(registryPath);
        var identityBefore = File.ReadAllBytes(identityPath);

        var inventory = Capture();

        Assert.Contains(inventory.Entries, entry => entry.CanonicalPath == identityPath && entry.Exists);
        Assert.Equal(registryBefore, File.ReadAllBytes(registryPath));
        Assert.Equal(identityBefore, File.ReadAllBytes(identityPath));
        registry.Load();
        Assert.Empty(registry.GetAll());
        Assert.Null(registry.ActiveGatewayId);
        identity.StoreDeviceToken("next");
        Assert.NotEqual(inventory.Fingerprint, Capture().Fingerprint);
    }

    [Fact]
    public void Capture_DuplicateGatewayIds_IsRejected()
    {
        var record = new { id = GatewayId, url = "wss://example.test" };
        Write(Roaming, "gateways.json", JsonSerializer.Serialize(new { gateways = new[] { record, record } }));
        Assert.Throws<InvalidDataException>(() => Capture());
    }

    [Fact]
    public void Capture_DirectoryInPlaceOfFile_IsRejected()
    {
        Directory.CreateDirectory(Path.Combine(Roaming, "settings.json"));
        Assert.Throws<InvalidDataException>(() => Capture());
    }

    [Fact]
    public void Capture_UnreadableLockedFile_IsRejected()
    {
        var path = Write(Roaming, "settings.json", "{}");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAny<IOException>(() => Capture());
    }

    [Fact]
    public void Capture_OversizedFile_IsRejectedBeforeReadingPayload()
    {
        var path = Write(Roaming, "settings.json", "{}");
        using (var stream = File.OpenWrite(path))
            stream.SetLength(4 * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => Capture());
    }

    [Fact]
    public void Capture_JunctionSource_IsRejectedWithoutReadingTarget()
    {
        var target = _directory.Combine("junction-target");
        Write(target, "settings.json", "{}");
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{Roaming}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10_000));
        Assert.True(process.ExitCode == 0,
            $"Junction creation failed: {process.StandardOutput.ReadToEnd()}\n{process.StandardError.ReadToEnd()}");
        Assert.Throws<InvalidDataException>(() => Capture());
        Assert.Equal("{}", File.ReadAllText(Path.Combine(target, "settings.json")));
        Directory.Delete(Roaming);
    }

    [Fact]
    public async Task Capture_ValidLocalAiReceipt_RequiresNoModelOrRuntimePayload()
    {
        var revision = new string('a', 40);
        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = "b1",
            Architecture = "x64",
            RuntimeId = "llama-server-test",
            ModelCatalogId = "test-model",
            SelectedGpuId = "GPU-TEST",
            ExecutablePath = Path.Combine("engines", "llama-server.exe"),
            RuntimeAssets = [new()
            {
                FileName = "runtime.zip",
                SourceUrl = "https://example.invalid/runtime.zip",
                SizeBytes = 1,
                Sha256 = new string('a', 64),
            }],
            ModelPath = Path.Combine("models", "owner", "repository", revision, "model.gguf"),
            ModelId = $"owner/repository@{revision}",
            ModelAlias = "test-model",
            ModelAsset = new()
            {
                FileName = "model.gguf",
                SourceUrl = $"https://huggingface.co/owner/repository/resolve/{revision}/model.gguf?download=true",
                SizeBytes = 1,
                Sha256 = new string('b', 64),
            },
            ContextLength = 4096,
        };
        var paths = new LocalAiPaths(Local);
        await new LocalAiManifestStore(paths).SaveAsync(manifest);
        var before = File.ReadAllBytes(paths.ManifestPath);

        var inventory = Capture();

        Assert.Contains(inventory.Entries, entry => entry.CanonicalPath == paths.ManifestPath && entry.Exists);
        Assert.Equal(before, File.ReadAllBytes(paths.ManifestPath));
        Assert.False(Directory.Exists(paths.ModelsDirectory));
        Assert.False(Directory.Exists(paths.EnginesDirectory));
    }

    private MigrationInventory Capture() => MigrationInventory.Capture(Roaming, Local);

    private GatewayRegistry WriteRegistry()
    {
        var registry = new GatewayRegistry(Roaming);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = GatewayId,
            Url = "wss://example.test",
            SharedGatewayToken = "test",
        });
        registry.SetActive(GatewayId);
        registry.Save();
        return registry;
    }

    private static string Write(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        _environment.Dispose();
        _directory.Dispose();
    }
}
