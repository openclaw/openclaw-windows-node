using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

[CollectionDefinition("Exec routing environment", DisableParallelization = true)]
public sealed class ExecRoutingEnvironmentCollection { }

[Collection("Exec routing environment")]
public class SystemRunExecutionContextTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("\"context\"")]
    [InlineData("{\"senderId\":\"\"}")]
    [InlineData("{\"chatId\":null}")]
    [InlineData("{\"chatId\":10}")]
    [InlineData("{\"subagent\":false}")]
    [InlineData("{\"subagent\":\"true\"}")]
    [InlineData("{\"sessionKey\":\"authority\"}")]
    [InlineData("{\"runId\":\"authority\"}")]
    [InlineData("{\"approved\":true}")]
    [InlineData("{\"env\":{\"PATH\":\"injection\"}}")]
    [InlineData("{\"SenderId\":\"wrong-case\"}")]
    [InlineData("{\"senderId\":\"a\",\"senderId\":\"b\"}")]
    public void ClosedContext_RejectsMalformedOrAuthorityBearingValues(string json)
    {
        using var args = JsonDocument.Parse("{\"executionContext\":" + json + "}");
        Assert.False(SystemRunExecutionContext.TryRead(args.RootElement, out _));
    }

    [Theory]
    [InlineData("{}", null, null, false)]
    [InlineData("{\"chatId\":\"chat\"}", null, "chat", false)]
    [InlineData("{\"senderId\":\"sender\"}", "sender", null, false)]
    [InlineData("{\"subagent\":true}", null, null, true)]
    [InlineData("{\"senderId\":\"sender\",\"chatId\":\"chat\",\"subagent\":true}", "sender", "chat", true)]
    public void PresentContext_CompletelyProjectsMarkers(string json, string? sender, string? chat, bool subagent)
    {
        using var args = JsonDocument.Parse("{\"executionContext\":" + json + "}");
        Assert.True(SystemRunExecutionContext.TryRead(args.RootElement, out var context));
        Assert.NotNull(context);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OpenClaw_Channel_Context"] = "stale-channel",
            ["OPENCLAW_CHANNEL_CONTEXT"] = "second-stale-channel",
            ["openclaw_SUBAGENT_exec"] = "1",
            ["SYSTEMROOT"] = @"C:\Windows",
            ["CONTROL"] = "retained",
        };

        context.ApplyTo(environment);

        Assert.DoesNotContain("OpenClaw_Channel_Context", environment.Keys);
        Assert.DoesNotContain("openclaw_SUBAGENT_exec", environment.Keys);
        Assert.Equal(@"C:\Windows", environment["SYSTEMROOT"]);
        Assert.Equal("retained", environment["CONTROL"]);
        Assert.Equal(subagent, environment.ContainsKey(SystemRunExecutionContext.SubagentMarker));
        if (subagent) Assert.Equal("1", environment[SystemRunExecutionContext.SubagentMarker]);
        Assert.Equal(sender is not null || chat is not null, environment.ContainsKey(SystemRunExecutionContext.ChannelMarker));
        if (sender is not null || chat is not null)
        {
            using var channel = JsonDocument.Parse(environment[SystemRunExecutionContext.ChannelMarker]!);
            Assert.Equal(sender is not null, channel.RootElement.TryGetProperty("sender", out var senderValue));
            Assert.Equal(chat is not null, channel.RootElement.TryGetProperty("chat", out var chatValue));
            if (sender is not null) Assert.Equal(sender, senderValue.GetProperty("id").GetString());
            if (chat is not null) Assert.Equal(chat, chatValue.GetProperty("id").GetString());
        }
    }

    [Fact]
    public void AbsentContext_PreservesLegacyInheritance()
    {
        using var args = JsonDocument.Parse("{}");
        Assert.True(SystemRunExecutionContext.TryRead(args.RootElement, out var context));
        Assert.Null(context);
        var environment = new Dictionary<string, string?> { [SystemRunExecutionContext.SubagentMarker] = "legacy" };
        context?.ApplyTo(environment);
        Assert.Equal("legacy", environment[SystemRunExecutionContext.SubagentMarker]);
    }

    [Fact]
    public void MarkerJson_RoundTripsUntrustedStringsWithoutShellEncoding()
    {
        const string sender = "quoted\" & $env:PATH \n \u2603";
        var context = new SystemRunExecutionContext(SenderId: sender, ChatId: " ");
        var environment = new Dictionary<string, string?>();
        context.ApplyTo(environment);
        using var json = JsonDocument.Parse(environment[SystemRunExecutionContext.ChannelMarker]!);
        Assert.Equal(sender, json.RootElement.GetProperty("sender").GetProperty("id").GetString());
        Assert.Equal(" ", json.RootElement.GetProperty("chat").GetProperty("id").GetString());
    }

    [Fact]
    public void NativeDefaults_DoNotInheritParentProcessVariables()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "OPENCLAW_DEFAULT_ENV_PROBE_" + Guid.NewGuid().ToString("N");
        using var environment = new EnvironmentScope(name, "process-only");
        var defaults = WindowsDefaultEnvironment.Read();
        Assert.False(defaults.ContainsKey(name));
        Assert.False(string.IsNullOrEmpty(defaults["SYSTEMROOT"]));
        Assert.False(string.IsNullOrEmpty(defaults["LOCALAPPDATA"]));
    }

    [Theory]
    [InlineData(null, "{\"chat\":{\"id\":\"stale\"}}", "1")]
    [InlineData("{}", null, null)]
    [InlineData("{\"chatId\":\"chat\"}", "{\"chat\":{\"id\":\"chat\"}}", null)]
    [InlineData("{\"subagent\":true}", null, "1")]
    [InlineData("{\"chatId\":\"chat\",\"subagent\":true}", "{\"chat\":{\"id\":\"chat\"}}", "1")]
    public async Task NativeChild_SeesExactProjectionAndLegacyAbsence(string? contextJson, string? channel, string? subagent)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var environment = new EnvironmentScope("OpenClaw_Channel_Context", "{\"chat\":{\"id\":\"stale\"}}")
            .Set("openclaw_SUBAGENT_exec", "1").Set("OPENCLAW_CONTEXT_CONTROL", "retained");
        using var args = JsonDocument.Parse(contextJson is null ? "{}" : "{\"executionContext\":" + contextJson + "}");
        Assert.True(SystemRunExecutionContext.TryRead(args.RootElement, out var context));
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = "@{channel=[Environment]::GetEnvironmentVariable('OPENCLAW_CHANNEL_CONTEXT');" +
            "subagent=[Environment]::GetEnvironmentVariable('OPENCLAW_SUBAGENT_EXEC');" +
            "control=$env:OPENCLAW_CONTEXT_CONTROL} | ConvertTo-Json -Compress";
        var argv = new[] { shell, "-NoProfile", "-NonInteractive", "-Command", script };
        var result = await new LocalCommandRunner(NullLogger.Instance).RunAsync(new CommandRequest
        {
            Argv = argv, ExecutionContext = context, TimeoutMs = 15000,
        });

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.Stdout);
        Assert.Equal(channel, output.RootElement.GetProperty("channel").GetString());
        Assert.Equal(subagent, output.RootElement.GetProperty("subagent").GetString());
        Assert.Equal("retained", output.RootElement.GetProperty("control").GetString());
        Assert.Equal(script, argv[^1]);
    }
}
