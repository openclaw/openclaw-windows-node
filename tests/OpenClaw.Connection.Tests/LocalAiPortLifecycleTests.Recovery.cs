using OpenClaw.Connection.LocalAi;
using OpenClaw.Connection.Tests.FrozenPreReplacement;
using OpenClaw.TestSupport;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Connection.Tests;

public sealed partial class LocalAiPortLifecycleTests
{
    [Theory]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public async Task Manifest_NewWriterBytesLoadThroughFrozenPrePrStrictReader(int schema, bool pending)
    {
        using var temp = new TempDirectory("local-ai-old-reader-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        LocalAiInstallManifest manifest = CompatibilityManifest(temp.Path, schema);
        if (pending)
        {
            manifest = manifest with
            {
                ReplacedManifest = manifest with { Endpoint = "http://127.0.0.1:28764/v1" },
                PreviousEndpoints = ["http://127.0.0.1:28764/v1"],
            };
        }
        await store.SaveAsync(manifest);
        byte[] bytes = await File.ReadAllBytesAsync(paths.ManifestPath);

        // This is the frozen pre-PR parser AND receipt/path validator, not today's reader
        // ignoring unknown fields. It deliberately does not start an older app or Gateway.
        FrozenResolvedInstall legacy = new FrozenManifestReader(paths).Load(bytes);
        Assert.Equal(schema, legacy.Manifest.SchemaVersion);
        Assert.Equal(manifest.ModelAsset.Sha256, legacy.Manifest.ModelAsset.Sha256);
        Assert.Equal(schema == 3 ? Path.Combine(paths.RootDirectory, manifest.ModelPath) : manifest.CachedModelPath,
            legacy.ModelPath);
        Assert.Equal(pending, (await store.LoadAsync())!.Manifest.ReplacedManifest is not null);
        string json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("replacedManifest", json, StringComparison.Ordinal);
        Assert.DoesNotContain("previousEndpoints", json, StringComparison.Ordinal);
        Assert.DoesNotContain("recoveryRoutes", json, StringComparison.Ordinal);

        // A schema-4-era shape still cannot read schema 5. The new optional hints do not
        // widen the set of historical schemas supported by an older binary.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        };
        if (schema == 5)
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SchemaFourTransitionalManifest>(bytes, options));
        else
            Assert.NotNull(JsonSerializer.Deserialize<SchemaFourTransitionalManifest>(bytes, options));

        JsonObject polluted = JsonNode.Parse(bytes)!.AsObject();
        polluted["replacedManifest"] = new JsonObject();
        Assert.Throws<InvalidDataException>(() => FrozenManifestReader.Read(Encoding.UTF8.GetBytes(polluted.ToJsonString())));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("stale-bytes")]
    [InlineData("old-identical-write")]
    [InlineData("invalid-path")]
    [InlineData("other-gpu")]
    [InlineData("null-history")]
    public async Task Manifest_OptionalHintsCannotInvalidateCanonicalInstall(string change)
    {
        using var temp = new TempDirectory("local-ai-hints-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        LocalAiInstallManifest original = ValidManifest();
        LocalAiInstallManifest pending = original with
        {
            ModelCatalogId = "replacement", ModelAlias = "replacement",
            ReplacedManifest = original,
            PreviousEndpoints = ["http://127.0.0.1:28765/v1"],
        };
        await store.SaveAsync(pending);
        string hintsPath = Path.Combine(paths.RootDirectory, "recovery-hints.json");
        switch (change)
        {
            case "missing": File.Delete(hintsPath); break;
            case "corrupt": await File.WriteAllTextAsync(hintsPath, "{"); break;
            case "stale-bytes":
                await File.AppendAllTextAsync(paths.ManifestPath, " ");
                break;
            case "old-identical-write":
                byte[] bytes = await File.ReadAllBytesAsync(paths.ManifestPath);
                DateTime previousWrite = File.GetLastWriteTimeUtc(paths.ManifestPath);
                await File.WriteAllBytesAsync(paths.ManifestPath, bytes);
                File.SetLastWriteTimeUtc(paths.ManifestPath, previousWrite.AddSeconds(2));
                break;
            default:
                JsonObject hints = JsonNode.Parse(await File.ReadAllTextAsync(hintsPath))!.AsObject();
                if (change == "invalid-path") hints["replacedManifest"]!["modelPath"] = "../foreign.gguf";
                if (change == "other-gpu") hints["replacedManifest"]!["selectedGpuId"] = "GPU-OTHER";
                if (change == "null-history") hints["previousEndpoints"] = new JsonArray { null };
                await File.WriteAllTextAsync(hintsPath, hints.ToJsonString());
                break;
        }

        LocalAiResolvedInstall loaded = (await store.LoadAsync())!;
        Assert.Equal("replacement", loaded.Manifest.ModelCatalogId);
        Assert.Null(loaded.Manifest.ReplacedManifest);
        Assert.Null(loaded.Manifest.PreviousEndpoints);
        Assert.Empty(loaded.Manifest.RecoveryRoutes);
        await store.UpdateVerifiedEndpointAsync(loaded.Manifest, new Uri("http://127.0.0.1:28766/v1"));
        Assert.Equal("http://127.0.0.1:28766/v1", (await store.LoadAsync())!.Manifest.Endpoint);
    }

    [Fact]
    public async Task Manifest_UnwritableHintsDoNotFailCanonicalSave()
    {
        using var temp = new TempDirectory("local-ai-hints-");
        var paths = new LocalAiPaths(temp.Path);
        paths.EnsureDirectories();
        Directory.CreateDirectory(Path.Combine(paths.RootDirectory, "recovery-hints.json"));
        var store = new LocalAiManifestStore(paths);
        LocalAiInstallManifest original = ValidManifest();
        await store.SaveAsync(original with { ModelCatalogId = "replacement", ReplacedManifest = original });
        Assert.Equal("replacement", (await store.LoadAsync())!.Manifest.ModelCatalogId);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("hash")]
    [InlineData("runtime")]
    [InlineData("installation")]
    public async Task Manifest_OptionalUpdatesStillFenceDifferentArtifactsOrInstallation(string drift)
    {
        using var temp = new TempDirectory("local-ai-identity-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        LocalAiInstallManifest expected = ValidManifest();
        LocalAiInstallManifest current = drift switch
        {
            "model" => expected with { ModelCatalogId = "other" },
            "hash" => expected with { ModelAsset = expected.ModelAsset with { Sha256 = new string('a', 64) } },
            "runtime" => expected with { ExecutablePath = Path.Combine("engines", "other", "llama-server.exe") },
            _ => expected with { InstalledAtUtc = expected.InstalledAtUtc.AddMinutes(1) },
        };
        await store.SaveAsync(current);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateVerifiedEndpointAsync(expected, new Uri("http://127.0.0.1:28766/v1")));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.FinalizeReplacementAsync(expected));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateGatewayFallbackAsync(expected, "openai/gpt-5", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RestoreRecoveryManifestAsync(expected, expected));
        Assert.True(LocalAiManifestStore.HasSameRuntimeAndModel(current, (await store.LoadAsync())!.Manifest));
    }

    private static LocalAiInstallManifest CompatibilityManifest(string root, int schema)
    {
        LocalAiInstallManifest manifest = ValidManifest() with { SchemaVersion = schema };
        if (schema == 3)
            return manifest;
        string cache = Path.Combine(root, "hf-cache");
        string snapshot = Path.Combine(cache, "models--unsloth--Qwen3.6-35B-A3B-MTP-GGUF", "snapshots",
            "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d");
        manifest = manifest with { ModelCacheRoot = cache, CachedModelPath = Path.Combine(snapshot, manifest.ModelAsset.FileName) };
        if (schema == 5)
        {
            manifest = manifest with
            {
                AdditionalModelAssets = [manifest.ModelAsset with
                {
                    FileName = "draft.gguf",
                    SourceUrl = "https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF/resolve/5bc3e238d916f48a861bac2f8a1990a0e9b7e98d/draft.gguf",
                }],
                AdditionalModelPaths = [Path.Combine(snapshot, "draft.gguf")],
            };
        }
        return manifest;
    }
}
