using System.Text.Json;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcExecutionContextTests
{
    [Theory]
    [InlineData("{}", false, false)]
    [InlineData("{\"chatId\":\"chat\"}", true, false)]
    [InlineData("{\"subagent\":true}", false, true)]
    [InlineData("{\"senderId\":\"sender\",\"chatId\":\"chat\",\"subagent\":true}", true, true)]
    public void SandboxContext_ProjectsCleanDefaultsWithoutChangingArgv(string json, bool channel, bool subagent)
    {
        using var args = JsonDocument.Parse("{\"executionContext\":" + json + "}");
        Assert.True(SystemRunExecutionContext.TryRead(args.RootElement, out var context));
        using var command = JsonDocument.Parse("""{"argv":["C:\\Windows\\System32\\hostname.exe","unchanged"]}""");
        var request = new SandboxExecutionRequest("system.run", command.RootElement,
            new SandboxPolicy(MxcPolicyBuilder.SupportedPolicyVersion), 30000);
        var defaults = new Dictionary<string, string?>
        {
            ["SystemRoot"] = @"C:\Windows", ["LOCALAPPDATA"] = @"C:\Synthetic\Local",
            ["CONTROL"] = "value=retained", ["OpenClaw_Channel_Context"] = "stale",
            ["openclaw_subagent_exec"] = "1",
        };
        var buildContext = new MxcConfigBuildContext(PathEnvVar: "",
            DefaultEnvironmentProvider: () => defaults);
        var legacy = MxcConfigBuilder.Build(request, @"C:\Synthetic\Scratch", buildContext);
        var config = MxcConfigBuilder.Build(request with { ExecutionContext = context },
            @"C:\Synthetic\Scratch", buildContext);

        Assert.Equal(legacy.Process.CommandLine, config.Process.CommandLine);
        Assert.Null(legacy.Process.Env);
        Assert.Null(legacy.Process.InheritDefaultEnv);
        Assert.False(config.Process.InheritDefaultEnv);
        Assert.Contains(@"SystemRoot=C:\Windows", config.Process.Env!);
        Assert.Contains(@"LOCALAPPDATA=C:\Synthetic\Local", config.Process.Env!);
        Assert.Contains("CONTROL=value=retained", config.Process.Env!);
        Assert.Equal(channel, config.Process.Env!.Any(entry => entry.StartsWith("OPENCLAW_CHANNEL_CONTEXT=")));
        Assert.Equal(subagent, config.Process.Env!.Contains("OPENCLAW_SUBAGENT_EXEC=1"));
        Assert.DoesNotContain(config.Process.Env!, entry => entry.Contains("stale"));
        Assert.DoesNotContain(config.Process.Env!, entry => entry.Equals("OPENCLAW_CHANNEL_CONTEXT=") || entry.Equals("OPENCLAW_SUBAGENT_EXEC="));
        Assert.Equal("stale", defaults["OpenClaw_Channel_Context"]);
        Assert.Throws<NotSupportedException>(() => MxcConfigBuilder.Build(
            request with { ExecutionContext = context, Env = new Dictionary<string, string> { ["PATH"] = "override" } },
            @"C:\Synthetic\Scratch", buildContext));
    }
}
