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
}
