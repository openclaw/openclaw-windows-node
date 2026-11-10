using System.Text.Json;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class NativeToolProjectorTests
{
    [Fact]
    public void InputDetails_CommandPreviewUsesOneLineWithoutChangingTheDisclosedCommand()
    {
        using var input = JsonDocument.Parse("""{"command":"first\nsecond\tthird"}""");
        var details = NativeToolProjector.ProjectInputDetails(input.RootElement, "exec")!;
        Assert.Equal("first second\\u{9}third", details.Preview);
        Assert.Equal("first\nsecond\\u{9}third", details.Command);
    }

    [Fact]
    public void InputDetails_PrefersSuppliedTitleOverCommandForTheCollapsedPreview()
    {
        using var input = JsonDocument.Parse(
            """{"title":"Build identical reviewer input","command":"dotnet test .\\tests\\OpenClaw.Tray.Tests"}""");

        var details = NativeToolProjector.ProjectInputDetails(input.RootElement, "exec")!;

        Assert.Equal("Build identical reviewer input", details.Preview);
        Assert.Equal("dotnet test .\\tests\\OpenClaw.Tray.Tests", details.Command);
    }

    [Fact]
    public void InputDetails_RetainMultilineCommandAndGenericValuesWithoutExpandingTheSummaryCache()
    {
        var command = "Write-Output 'first'\n" + new string('x', 350) + "\nCOMMAND TAIL";
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            args = new { command, workdir = "C:\\fixture", timeout = 30, options = new { includeDocs = true } },
        }));
        var summary = NativeToolProjector.ExtractSafeToolDisplayArgs(payload.RootElement)!;
        var details = NativeToolProjector.ExtractInputDetails(payload.RootElement, "exec")!;
        Assert.DoesNotContain("COMMAND TAIL", summary["command"]!.GetValue<string>());
        Assert.Equal(command, details.Command);
        Assert.Contains("includeDocs", details.Text);
        Assert.Contains("workdir", details.Text);
        Assert.Contains("30", details.Text);
        Assert.False(summary.ContainsKey("options"));
    }

    [Fact]
    public void InputDetails_RedactCredentialsButRetainOperationalParameters()
    {
        using var input = JsonDocument.Parse("""
            {"message":"Inspect files","node":"fixture-node","sessionKey":"agent:main:main","max_tokens":256,
             "enabled":true,"tags":["one","two"],"password":1234,"credentials":{"value":"private"},
             "headers":{"Authorization":"Bearer private-value"},"api_key":"do-not-show"}
            """);
        var details = NativeToolProjector.ProjectInputDetails(input.RootElement, "custom_task")!;
        Assert.Equal("Inspect files", details.Preview);
        Assert.Contains("fixture-node", details.Text);
        Assert.Contains("agent:main:main", details.Text);
        Assert.Contains("256", details.Text);
        Assert.Contains("true", details.Text);
        Assert.Contains("one", details.Text);
        Assert.DoesNotContain("1234", details.Text);
        Assert.DoesNotContain("private", details.Text);
        Assert.DoesNotContain("do-not-show", details.Text);
        Assert.Contains("[redacted]", details.Text);
    }

    [Fact]
    public void InputDetails_KeepFreeFormInputAndExplicitlyRejectOversizeAndPrivateYieldInput()
    {
        var patch = JsonSerializer.SerializeToElement("*** Begin Patch\n*** Add File: sample.txt\n+hello\n*** End Patch");
        Assert.Contains("*** End Patch", NativeToolProjector.ProjectInputDetails(patch, "apply_patch")!.Text);
        Assert.Null(NativeToolProjector.ProjectInputDetails(patch, "sessions_yield"));
        var oversized = JsonSerializer.SerializeToElement(new { text = new string('x', 70 * 1024) });
        var result = NativeToolProjector.ProjectInputDetails(oversized, "custom")!;
        Assert.True(result.Unavailable);
        Assert.Empty(result.Text);
    }

    [Fact]
    public void ToolOutput_PreservesTheFormerPreviewTailAndRetainsTheEntrySafetyBudget()
    {
        var text = new string('x', 6000) + " OUTPUT TAIL";
        Assert.Equal(text, NativeToolProjector.TruncateToolOutput(text));
        var bounded = NativeToolProjector.TruncateToolOutput(new string('x', 300 * 1024));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(bounded) <= OpenClawChatDataProvider.MaxEntryTextBytes);
        Assert.Contains("truncated", bounded, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("command", "dotnet test")]
    [InlineData("path", "src\\main.cs")]
    [InlineData("file_path", "src\\main.cs")]
    [InlineData("filePath", "src\\main.cs")]
    [InlineData("notebook_path", "analysis.ipynb")]
    [InlineData("query", "streaming tools")]
    [InlineData("url", "https://example.test")]
    public void CollapsedPreview_UsesTheSameSafeSuppliedInputForLiveAndHistory(string key, string value)
    {
        var args = new Dictionary<string, string> { [key] = value, ["content"] = "not displayed" };
        var live = NativeToolProjector.ExtractSafeToolDisplayArgs(
            JsonSerializer.SerializeToElement(new { args }));
        var history = NativeToolProjector.ExtractSafePersistedToolDisplayArgs(
            JsonSerializer.SerializeToElement(JsonSerializer.Serialize(args)));

        Assert.Equal(value, NativeToolProjector.FirstToolDisplayValue(live));
        Assert.Equal(value, NativeToolProjector.FirstToolDisplayValue(history));
        Assert.False(live!.ContainsKey("content"));
        Assert.False(history!.ContainsKey("content"));
    }

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
    public void ExtractSafePersistedToolDisplayArgs_DecodesAllowlistedRedactedInput()
    {
        var value = JsonSerializer.SerializeToElement(
            """{"command":"curl https://example.test --token abcdef1234567890ghij","workdir":"C:\\private","content":"must-not-render"}""");

        var args = NativeToolProjector.ExtractSafePersistedToolDisplayArgs(value)!;

        Assert.DoesNotContain(
            "abcdef1234567890ghij",
            args["command"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.False(args.ContainsKey("workdir"));
        Assert.False(args.ContainsKey("content"));
        Assert.DoesNotContain("must-not-render", args.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void ExtractSafePersistedToolDisplayArgs_RejectsMalformedOrNonObjectInput(
        string encoded)
    {
        var value = JsonSerializer.SerializeToElement(encoded);

        Assert.Null(NativeToolProjector.ExtractSafePersistedToolDisplayArgs(value));
    }

    [Fact]
    public void ExtractSafePersistedToolDisplayArgs_RejectsOversizedInputBeforeParsing()
    {
        var value = JsonSerializer.SerializeToElement(
            new string('x', NativeToolProjector.MaxPersistedToolArgumentsChars + 1));

        Assert.Null(NativeToolProjector.ExtractSafePersistedToolDisplayArgs(value));
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
