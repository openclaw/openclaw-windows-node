using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.IntegrationTests;

/// <summary>
/// Opens a profile persisted by the selected stable portable app with the candidate.
/// Run through test-gateway-fixture.ps1 -StableAppPath; this is not installer/Store upgrade proof.
/// </summary>
[Collection("Gateway fixture environment")]
public sealed class GatewayFixtureUpgradeTests
{
    [GatewayFixtureAppFact]
    public async Task ReleasedProfile_PreservesGatewayRecordsIdentitiesAndSettings()
    {
        var stableApp = RequiredEnvironment("OPENCLAW_GATEWAY_FIXTURE_STABLE_APP");
        var candidateApp = RequiredEnvironment("OPENCLAW_GATEWAY_FIXTURE_APP");
        Assert.NotEqual(Path.GetFullPath(stableApp), Path.GetFullPath(candidateApp), StringComparer.OrdinalIgnoreCase);
        var nativeId = Guid.NewGuid().ToString();
        var wslId = Guid.NewGuid().ToString();
        var startedAt = DateTime.UtcNow;
        await using var run = await GatewayFixtureRun.StartAsync(stableApp,
            Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"), configureProfile: profile =>
            {
                var registry = new GatewayRegistry(profile.DataDirectory);
                registry.Load();
                var distro = $"OpenClawFixture-{profile.RunId[..8]}";
                registry.AddOrUpdate(new GatewayRecord
                {
                    Id = nativeId, Url = "ws://127.0.0.1:18789",
                    FriendlyName = $"Local ({distro})", IsLocal = true,
                    NativePackageFamilyName = "OpenClaw.Fixture_native"
                });
                registry.AddOrUpdate(new GatewayRecord
                {
                    Id = wslId, Url = "ws://127.0.0.1:18790",
                    FriendlyName = $"Local ({distro})", IsLocal = true, SetupManagedDistroName = distro
                });
                registry.Save();
            });
        try
        {
            // LastConnected starts null. Its persisted value proves the released app,
            // rather than this harness, serialized the complete seeded registry.
            await run.WaitForAsync(() => Task.FromResult(ReadRecords(run)[run.Profile.GatewayId]["lastConnected"]
                ?.GetValue<DateTime>() >= startedAt), "stable app persisted its successful connection");
            await run.InvokeAsync("app.settings.set", new { name = "NotifyInfo", value = "false" });
            await run.InvokeAsync("app.settings.set", new { name = "NotifyHealth", value = "false" });
            await AssertPreferencesAsync(run);

            var identity = Path.Combine(run.Profile.DataDirectory, "gateways", run.Profile.GatewayId, "device-key-ed25519.json");
            Assert.True(File.Exists(identity), "The released app must create its own real synthetic operator identity.");
            foreach (var id in new[] { nativeId, wslId })
            {
                var directory = Path.Combine(run.Profile.DataDirectory, "gateways", id);
                Directory.CreateDirectory(directory);
                // Reuse released-format identity bytes for inactive synthetic records.
                // No native package, WSL distro or production identity is imported.
                File.Copy(identity, Path.Combine(directory, "device-key-ed25519.json"));
            }
            var before = ReadRecords(run);
            Assert.Equal(3, before.Count);
            Assert.Equal("OpenClaw.Fixture_native", before[nativeId]["nativePackageFamilyName"]!.GetValue<string>());
            Assert.Equal($"OpenClawFixture-{run.Profile.RunId[..8]}", before[wslId]["setupManagedDistroName"]!.GetValue<string>());
            var identityHashes = before.Keys.ToDictionary(id => id, id => IdentityHash(run, id));
            var activeId = ReadActiveId(run);
            Assert.Equal(run.Profile.GatewayId, activeId);
            await run.WriteReportAsync("stable-profile-persisted");
            File.Copy(Path.Combine(run.ArtifactsDirectory, "run.json"), Path.Combine(run.ArtifactsDirectory, "stable-run.json"));

            await run.RestartAppAsync(candidateApp);

            var after = ReadRecords(run);
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            Assert.Equal(activeId, ReadActiveId(run));
            foreach (var (id, record) in before)
            {
                foreach (var (field, value) in record)
                {
                    if (field == "lastConnected") continue;
                    Assert.True(JsonNode.DeepEquals(value, after[id][field]),
                        $"Existing gateway {id} field '{field}' changed across the released-profile boundary.");
                }
                Assert.Equal(identityHashes[id], IdentityHash(run, id));
            }
            await AssertPreferencesAsync(run);
            Assert.Empty(run.Gateway.UnexpectedRequests);
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "profile-compatibility.json"),
                JsonSerializer.Serialize(new
                {
                    outcome = "passed", activeId, gatewayIds = before.Keys.Order().ToArray(), identityHashes,
                    stableAppSha256 = FileHash(stableApp), candidateAppSha256 = FileHash(candidateApp),
                    preservedPreferences = new { NotifyInfo = false, NotifyHealth = false,
                        EnableMcpServer = true, EnableNodeMode = false, AutoStart = false },
                    boundary = "Released portable app persistence -> candidate startup in the same isolated profile. Not installer, Store, native Gateway or WSL runtime proof."
                }, new JsonSerializerOptions { WriteIndented = true }));
            await run.WriteReportAsync("passed");
        }
        catch (Exception ex)
        {
            await run.WriteReportAsync("failed", ex);
            throw;
        }
    }

    private static async Task AssertPreferencesAsync(GatewayFixtureRun run)
    {
        foreach (var name in new[] { "NotifyInfo", "NotifyHealth", "EnableNodeMode", "AutoStart" })
            Assert.False((await run.InvokeAsync("app.settings.get", new { name })).GetBoolean(), name);
        Assert.True((await run.InvokeAsync("app.settings.get", new { name = "EnableMcpServer" })).GetBoolean());
        using var persisted = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Profile.DataDirectory, "settings.json")));
        Assert.False(persisted.RootElement.GetProperty("NotifyInfo").GetBoolean());
        Assert.False(persisted.RootElement.GetProperty("NotifyHealth").GetBoolean());
        Assert.Equal(run.Gateway.Endpoint.AbsoluteUri, persisted.RootElement.GetProperty("GatewayUrl").GetString());
    }

    private static Dictionary<string, JsonObject> ReadRecords(GatewayFixtureRun run) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(run.Profile.DataDirectory, "gateways.json")))!
            ["gateways"]!.AsArray().ToDictionary(record => record!["id"]!.GetValue<string>(), record => record!.AsObject());

    private static string ReadActiveId(GatewayFixtureRun run) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(run.Profile.DataDirectory, "gateways.json")))!
            ["activeId"]!.GetValue<string>();

    private static string IdentityHash(GatewayFixtureRun run, string id) =>
        FileHash(Path.Combine(run.Profile.DataDirectory, "gateways", id, "device-key-ed25519.json"));

    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Set {name} to an explicit app build.");
}
