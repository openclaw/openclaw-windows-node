using System.Text.Json;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class NativeToolProjectorTests
{
    [Theory]
    [InlineData("Bash", "Bash", ChatToolIdentityStrength.Specific)]
    [InlineData("APPLY_PATCH", "Apply Patch", ChatToolIdentityStrength.Specific)]
    [InlineData("Bash delete files", "Tool", ChatToolIdentityStrength.Fallback)]
    [InlineData("Bash\u202Eevil", "Tool", ChatToolIdentityStrength.Fallback)]
    [InlineData("\u0412ash", "Tool", ChatToolIdentityStrength.Fallback)]
    public void ExtractToolIdentity_TitleRequiresExactTrustedAlias(
        string title,
        string expectedName,
        ChatToolIdentityStrength expectedStrength)
    {
        using var payload = JsonDocument.Parse(
            JsonSerializer.Serialize(new { title }));

        var identity = NativeToolProjector.ExtractToolIdentity(payload.RootElement);

        Assert.Equal(expectedName, identity.Name);
        Assert.Equal(expectedStrength, identity.Strength);
    }

    [Fact]
    public void ExtractToolIdentity_NameWinsOverGenericTitle()
    {
        using var payload = JsonDocument.Parse(
            """{"title":"Tool","name":"web_fetch"}""");

        var identity = NativeToolProjector.ExtractToolIdentity(payload.RootElement);

        Assert.Equal("web_fetch", identity.Name);
        Assert.Equal(ChatToolIdentityStrength.Explicit, identity.Strength);
    }

    [Fact]
    public void ExtractSafeToolDisplayArgs_AllowlistsRedactsAndBoundsValues()
    {
        var longPath = new string('p', 500);
        using var payload = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                args = new
                {
                    command = "curl https://example.test --token abcdef1234567890ghij",
                    path = longPath,
                    environment = new { OPENCLAW_TOKEN = "must-not-render" }
                },
                payload = new { arbitrary = "json-must-not-render" }
            }));

        var args = NativeToolProjector.ExtractSafeToolDisplayArgs(payload.RootElement)!;

        Assert.DoesNotContain(
            "abcdef1234567890ghij",
            args["command"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.True(args["path"]!.GetValue<string>().Length <= NativeToolProjector.MaxDisplayValueChars);
        Assert.False(args.ContainsKey("environment"));
        Assert.False(args.ContainsKey("payload"));
        Assert.DoesNotContain("must-not-render", args.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("json-must-not-render", args.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractToolCorrelationId_ExplicitToolCallIdIsOpaqueAndWins()
    {
        using var payload = JsonDocument.Parse(
            """{"toolCallId":"tool:opaque-value","itemId":"tool:fallback"}""");

        Assert.Equal("tool:opaque-value", NativeToolProjector.ExtractToolCorrelationId(payload.RootElement));
    }

    [Theory]
    [InlineData("""{"itemId":"tool:abc"}""", "abc")]
    [InlineData("""{"itemId":"command:abc"}""", "abc")]
    [InlineData("""{"itemId":"patch:abc"}""", "abc")]
    [InlineData("""{"itemId":"tool:command:abc"}""", "command:abc")]
    [InlineData("""{"itemId":"codex-bare-id"}""", "codex-bare-id")]
    [InlineData("""{"itemId":" tool:abc "}""", " tool:abc ")]
    [InlineData("""{"itemId":"prefix-tool:abc"}""", "prefix-tool:abc")]
    [InlineData("""{"itemId":"tool:abc "}""", "abc ")]
    [InlineData("""{"toolCallId":"","itemId":"command:abc"}""", "abc")]
    [InlineData("""{"toolCallId":"   ","itemId":"patch:abc"}""", "abc")]
    [InlineData("""{}""", null)]
    [InlineData("""{"itemId":""}""", null)]
    [InlineData("""{"itemId":"   "}""", null)]
    [InlineData("""{"itemId":"tool:"}""", null)]
    [InlineData("""{"itemId":"command:"}""", null)]
    [InlineData("""{"itemId":"patch:"}""", null)]
    public void ExtractToolCorrelationId_NormalizesFallbackItemId(string json, string? expected)
    {
        using var payload = JsonDocument.Parse(json);

        Assert.Equal(expected, NativeToolProjector.ExtractToolCorrelationId(payload.RootElement));
    }

    [Fact]
    public void ExtractCommandOutputText_DoesNotUseFreeFormTitle()
    {
        using var payload = JsonDocument.Parse(
            """{"phase":"end","title":"not command output"}""");

        Assert.Equal(string.Empty, NativeToolProjector.ExtractCommandOutputText(payload.RootElement));
    }

    [Fact]
    public void Source_DoesNotUseInventedCorrelationField()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var forbidden = "parent" + "ItemId";
        var files = Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repoRoot, "tests"), "*.cs", SearchOption.AllDirectories))
            .Where(file =>
            {
                var segments = Path.GetRelativePath(repoRoot, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
                    && !segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
            });

        var matches = files
            .Where(file => File.ReadAllText(file).Contains(forbidden, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(matches);
    }
}
