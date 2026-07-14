using System.Net.Http;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

/// <summary>
/// Self-tests for the shared <c>OpenClaw.TestSupport</c> fixtures. These are the
/// guard tests named by the architecture ledger rows (test-temp-dir,
/// test-env-scope, ...): if a future change breaks a fixture's contract, the
/// ledger's promise fails here.
/// </summary>
public sealed class TestSupportFixtureTests
{
    [Fact]
    public void TempDirectory_CreatesAndDeletes()
    {
        string path;
        using (var temp = new TempDirectory())
        {
            path = temp.Path;
            Assert.True(Directory.Exists(path));

            var file = temp.Combine("sub", "file.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "x");
            Assert.True(File.Exists(file));
        }

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void EnvironmentScope_RestoresOriginal()
    {
        const string name = "OPENCLAW_TESTSUPPORT_PROBE";
        var original = Environment.GetEnvironmentVariable(name);

        using (var scope = new EnvironmentScope())
        {
            scope.Set(name, "scoped-value");
            Assert.Equal("scoped-value", Environment.GetEnvironmentVariable(name));
        }

        Assert.Equal(original, Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void CliHarness_CapturesAndLooksUp()
    {
        using var harness = new CliHarness()
            .WithEnv("OPENCLAW_ENDPOINT", "http://localhost:1234/");

        harness.Out.Write("stdout-line");
        harness.Err.Write("stderr-line");

        Assert.Equal("http://localhost:1234/", harness.EnvLookup("OPENCLAW_ENDPOINT"));
        Assert.Null(harness.EnvLookup("OPENCLAW_MISSING"));
        Assert.Equal("stdout-line", harness.StdOut);
        Assert.Equal("stderr-line", harness.StdErr);
    }

    [Fact]
    public async Task FakeMcpServer_CapturesRequest()
    {
        using var server = new FakeMcpServer();
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, server.Url)
        {
            Content = new StringContent("{\"method\":\"tools/list\"}"),
        };
        request.Headers.Add("Authorization", "Bearer test-token");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("{\"method\":\"tools/list\"}", server.LastRequestBody);
        Assert.Equal("POST", server.LastRequestMethod);
        Assert.Equal("Bearer test-token", server.LastRequestAuthorization);
        Assert.Contains("jsonrpc", body);
    }

    [Fact]
    public void GatewayRecordBuilder_BuildsRecord()
    {
        GatewayRecord record = new GatewayRecordBuilder()
            .WithId("gw-1")
            .WithUrl("wss://example")
            .WithFriendlyName("Home")
            .WithSharedGatewayToken("tok-123")
            .Build();

        Assert.Equal("gw-1", record.Id);
        Assert.Equal("wss://example", record.Url);
        Assert.Equal("Home", record.FriendlyName);
        Assert.Equal("tok-123", record.SharedGatewayToken);

        // Defaults produce a usable, uniquely-identified record with no overrides.
        var defaulted = new GatewayRecordBuilder().Build();
        Assert.False(string.IsNullOrWhiteSpace(defaulted.Id));
        Assert.False(string.IsNullOrWhiteSpace(defaulted.Url));
    }

    [Fact]
    public void SettingsDataBuilder_StartsFromDefaults()
    {
        var defaults = new SettingsData();

        var built = new SettingsDataBuilder()
            .WithGatewayUrl("wss://gw")
            .WithNodeMode(true)
            .Build();

        Assert.Equal("wss://gw", built.GatewayUrl);
        Assert.True(built.EnableNodeMode);
        // Untouched fields keep the production defaults.
        Assert.Equal(defaults.AutoStart, built.AutoStart);
        Assert.Equal(defaults.ShowNotifications, built.ShowNotifications);
    }

    [Fact]
    public void SettingsDataBuilder_BuildsIndependentInstances()
    {
        var builder = new SettingsDataBuilder().WithAutoStart(false);

        var first = builder.Build();
        // Mutating the builder after a build must not change the already-built result.
        builder.WithAutoStart(true);
        var second = builder.Build();

        Assert.NotSame(first, second);
        Assert.False(first.AutoStart);
        Assert.True(second.AutoStart);
    }
}
