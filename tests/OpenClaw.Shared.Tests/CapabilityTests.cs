using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Unit tests for each capability: SystemCapability, CanvasCapability,
/// ScreenCapability, CameraCapability.
/// Tests execute logic, arg parsing, event raising, and error paths.
/// No hardware or UI dependencies.
/// </summary>
public class SystemCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void CanHandle_SystemNotify()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("system.notify"));
        Assert.True(cap.CanHandle("system.run"));
        Assert.False(cap.CanHandle("system.unknown"));
        Assert.Equal("system", cap.Category);
    }

    [Fact]
    public async Task Notify_RaisesEvent_WithArgs()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        SystemNotifyArgs? received = null;
        cap.NotifyRequested += (s, a) => received = a;

        var req = new NodeInvokeRequest
        {
            Id = "n1",
            Command = "system.notify",
            Args = Parse("""{"title":"Hello","body":"World","sound":false}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Equal("Hello", received!.Title);
        Assert.Equal("World", received.Body);
        Assert.False(received.PlaySound);
    }

    [Fact]
    public async Task Notify_DefaultsTitle_WhenMissing()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        SystemNotifyArgs? received = null;
        cap.NotifyRequested += (s, a) => received = a;

        var req = new NodeInvokeRequest
        {
            Id = "n2",
            Command = "system.notify",
            Args = Parse("""{"body":"Just body"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("OpenClaw", received!.Title);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsError()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "n3",
            Command = "system.unknown",
            Args = Parse("""{}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Unknown command", res.Error);
    }

    [Fact]
    public async Task Run_AcceptsCommandAsArray()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var runner = new FakeCommandRunner();
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r1",
            Command = "system.run",
            Args = Parse("""{"command":["echo","hello","world"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("echo", runner.LastRequest!.Command);
        Assert.Equal(new[] { "hello", "world" }, runner.LastRequest.Args);
    }

    [Fact]
    public async Task Run_AcceptsCommandAsString()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var runner = new FakeCommandRunner();
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r2",
            Command = "system.run",
            Args = Parse("""{"command":"hostname"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("hostname", runner.LastRequest!.Command);
        Assert.Null(runner.LastRequest.Args);
    }

    [Fact]
    public async Task Run_AcceptsSingleElementArray()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var runner = new FakeCommandRunner();
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r3",
            Command = "system.run",
            Args = Parse("""{"command":["hostname"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("hostname", runner.LastRequest!.Command);
        Assert.Null(runner.LastRequest.Args);
    }

    [Fact]
    public async Task Run_ReturnsError_WhenNoCommand()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(new FakeCommandRunner());

        var req = new NodeInvokeRequest
        {
            Id = "r4",
            Command = "system.run",
            Args = Parse("""{}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Missing command", res.Error);
    }

    [Fact]
    public async Task Run_ReturnsError_WhenNoRunner()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "r5",
            Command = "system.run",
            Args = Parse("""{"command":["echo","test"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error);
    }

    [Fact]
    public async Task Run_ReadsTimeoutMs()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var runner = new FakeCommandRunner();
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r6",
            Command = "system.run",
            Args = Parse("""{"command":["test"],"timeoutMs":60000}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal(60000, runner.LastRequest!.TimeoutMs);
    }

    [Fact]
    public void CanHandle_SystemWhich()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("system.which"));
    }

    [Fact]
    public async Task Which_FindsKnownBins()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        // cmd.exe should always exist on Windows
        var req = new NodeInvokeRequest
        {
            Id = "w1",
            Command = "system.which",
            Args = Parse("""{"bins":["cmd"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);

        var payload = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(res.Payload));
        Assert.True(payload.TryGetProperty("bins", out var binsEl));
        // cmd should resolve on Windows
        if (OperatingSystem.IsWindows())
        {
            Assert.True(binsEl.TryGetProperty("cmd", out var cmdPath));
            Assert.Contains("cmd", cmdPath.GetString()!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Which_OmitsMissingBins()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "w2",
            Command = "system.which",
            Args = Parse("""{"bins":["totally_nonexistent_binary_xyz123"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);

        var payload = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(res.Payload));
        Assert.True(payload.TryGetProperty("bins", out var binsEl));
        Assert.False(binsEl.TryGetProperty("totally_nonexistent_binary_xyz123", out _));
    }

    [Fact]
    public async Task Which_RejectsPathsWithSeparators()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "w3",
            Command = "system.which",
            Args = Parse("""{"bins":["..\\..\\etc\\passwd","../../../bin/sh"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);

        var payload = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(res.Payload));
        Assert.True(payload.TryGetProperty("bins", out var binsEl));
        // Both should be rejected (contain path separators)
        Assert.Empty(binsEl.EnumerateObject());
    }

    [Fact]
    public async Task Which_ReturnsErrorWhenNoBins()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "w4",
            Command = "system.which",
            Args = Parse("""{"bins":[]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
    }

    [Fact]
    public void ResolveExecutable_FindsCmdOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = SystemCapability.ResolveExecutable("cmd");
        Assert.NotNull(path);
        Assert.EndsWith(".exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveExecutable_RejectsPathTraversal()
    {
        Assert.Null(SystemCapability.ResolveExecutable("..\\cmd"));
        Assert.Null(SystemCapability.ResolveExecutable("../bin/sh"));
        Assert.Null(SystemCapability.ResolveExecutable("C:\\Windows\\cmd"));
    }

    [Fact]
    public async Task RunPrepare_ReturnsCommandText_ForArray()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "p1",
            Command = "system.run.prepare",
            Args = Parse("""{"command":["echo","hello world"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(res.Payload));
        Assert.True(payload.TryGetProperty("cmdText", out var cmdText));
        Assert.Contains("echo", cmdText.GetString());
    }

    [Fact]
    public async Task RunPrepare_ReturnsCommandText_ForString()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "p2",
            Command = "system.run.prepare",
            Args = Parse("""{"command":"hostname","rawCommand":"hostname"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(res.Payload));
        Assert.True(payload.TryGetProperty("cmdText", out var cmdText));
        Assert.Equal("hostname", cmdText.GetString());
    }

    [Fact]
    public async Task RunPrepare_ReturnsPlan_WithArgvAndCwd()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "p3",
            Command = "system.run.prepare",
            Args = Parse("""{"command":["ls","-la"],"cwd":"/tmp","agentId":"agent1","sessionKey":"sk1"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(res.Payload));
        Assert.True(payload.TryGetProperty("plan", out var plan));
        Assert.True(plan.TryGetProperty("argv", out var argv));
        Assert.Equal(2, argv.GetArrayLength());
        Assert.True(plan.TryGetProperty("cwd", out var cwd));
        Assert.Equal("/tmp", cwd.GetString());
        Assert.True(plan.TryGetProperty("agentId", out var agentId));
        Assert.Equal("agent1", agentId.GetString());
    }

    [Fact]
    public async Task RunPrepare_ReturnsError_WhenMissingCommand()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "p4",
            Command = "system.run.prepare",
            Args = Parse("""{}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Missing command", res.Error);
    }

    [Fact]
    public async Task ExecApprovalsGet_WhenNoPolicyConfigured_ReturnsDisabled()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "ea1",
            Command = "system.execApprovals.get",
            Args = Parse("""{}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(res.Payload));
        Assert.True(payload.TryGetProperty("enabled", out var enabled));
        Assert.False(enabled.GetBoolean());
    }

    [Fact]
    public async Task ExecApprovalsGet_WhenPolicySet_ReturnsRules()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cap = new SystemCapability(NullLogger.Instance);
            var policy = new ExecApprovalPolicy(tempDir, NullLogger.Instance);
            cap.SetApprovalPolicy(policy);

            var req = new NodeInvokeRequest
            {
                Id = "ea2",
                Command = "system.execApprovals.get",
                Args = Parse("""{}""")
            };

            var res = await cap.ExecuteAsync(req);
            Assert.True(res.Ok);
            var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(res.Payload));
            Assert.True(payload.TryGetProperty("enabled", out var enabled));
            Assert.True(enabled.GetBoolean());
            Assert.True(payload.TryGetProperty("hash", out var hash));
            Assert.StartsWith("sha256:", hash.GetString());
            Assert.True(payload.TryGetProperty("baseHash", out var baseHash));
            Assert.Equal(hash.GetString(), baseHash.GetString());
            Assert.True(payload.TryGetProperty("rules", out _));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecApprovalsSet_WhenNoPolicyConfigured_ReturnsError()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "ea3",
            Command = "system.execApprovals.set",
            Args = Parse("""{"rules":[]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("No exec policy configured", res.Error);
    }

    [Fact]
    public async Task ExecApprovalsSet_UpdatesRules()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cap = new SystemCapability(NullLogger.Instance);
            var policy = new ExecApprovalPolicy(tempDir, NullLogger.Instance);
            cap.SetApprovalPolicy(policy);

            var req = new NodeInvokeRequest
            {
                Id = "ea4",
                Command = "system.execApprovals.set",
                Args = Parse($$"""{"baseHash":"{{policy.GetPolicyHash()}}","rules":[{"pattern":"git *","action":"allow","description":"Allow git","enabled":true}],"defaultAction":"deny"}""")
            };

            var res = await cap.ExecuteAsync(req);
            Assert.True(res.Ok);
            var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(res.Payload));
            Assert.True(payload.TryGetProperty("updated", out var updated));
            Assert.True(updated.GetBoolean());
            Assert.True(payload.TryGetProperty("ruleCount", out var ruleCount));
            Assert.Equal(1, ruleCount.GetInt32());
            Assert.True(payload.TryGetProperty("hash", out var hash));
            Assert.NotEqual(req.Args.GetProperty("baseHash").GetString(), hash.GetString());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecApprovalsGet_ReturnsRemoteMutationConstraints()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cap = new SystemCapability(NullLogger.Instance);
            var policy = new ExecApprovalPolicy(tempDir, NullLogger.Instance);
            cap.SetApprovalPolicy(policy);

            var req = new NodeInvokeRequest
            {
                Id = "ea-constraints",
                Command = "system.execApprovals.get",
                Args = Parse("""{}""")
            };

            var res = await cap.ExecuteAsync(req);

            Assert.True(res.Ok);
            var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(res.Payload));
            Assert.True(payload.TryGetProperty("constraints", out var constraints));
            Assert.True(constraints.GetProperty("baseHashRequired").GetBoolean());
            Assert.False(constraints.GetProperty("defaultAllowAllowed").GetBoolean());
            Assert.False(constraints.GetProperty("broadAllowRulesAllowed").GetBoolean());
            Assert.False(constraints.GetProperty("dangerousAllowRulesAllowed").GetBoolean());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecApprovalsSet_RejectsDefaultAllow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cap = new SystemCapability(NullLogger.Instance);
            var policy = new ExecApprovalPolicy(tempDir, NullLogger.Instance);
            cap.SetApprovalPolicy(policy);

            var req = new NodeInvokeRequest
            {
                Id = "ea-default-allow",
                Command = "system.execApprovals.set",
                Args = Parse($$"""{"baseHash":"{{policy.GetPolicyHash()}}","rules":[],"defaultAction":"allow"}""")
            };

            var res = await cap.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.Contains("Default allow", res.Error);
            Assert.Equal(ExecApprovalAction.Deny, policy.DefaultAction);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("*")]
    [InlineData("**")]
    [InlineData("***")]
    [InlineData("?")]
    [InlineData("? *")]
    [InlineData("* ?")]
    [InlineData("cmd *")]
    [InlineData("Remove-Item *")]
    [InlineData("Invoke-WebRequest *")]
    [InlineData("Start-Process *")]
    public async Task ExecApprovalsSet_RejectsUnsafeAllowRules(string pattern)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cap = new SystemCapability(NullLogger.Instance);
            var policy = new ExecApprovalPolicy(tempDir, NullLogger.Instance);
            cap.SetApprovalPolicy(policy);

            var req = new NodeInvokeRequest
            {
                Id = "ea-unsafe-allow",
                Command = "system.execApprovals.set",
                Args = Parse($$"""{"baseHash":"{{policy.GetPolicyHash()}}","rules":[{"pattern":"{{pattern}}","action":"allow","enabled":true}],"defaultAction":"deny"}""")
            };

            var res = await cap.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.Contains("allow rule", res.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Run_BlockedEnvVar_ReturnsError()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(new FakeCommandRunner());

        var req = new NodeInvokeRequest
        {
            Id = "e1",
            Command = "system.run",
            Args = Parse("""{"command":["echo","test"],"env":{"PATH":"C:\\evil"}}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("PATH", res.Error);
    }

    [Fact]
    public async Task Run_AllowedEnvVar_Passes()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var runner = new FakeCommandRunner();
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "e2",
            Command = "system.run",
            Args = Parse("""{"command":["echo","test"],"env":{"MY_CUSTOM_VAR":"hello"}}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(runner.LastRequest!.Env);
        Assert.True(runner.LastRequest.Env!.ContainsKey("MY_CUSTOM_VAR"));
    }

    [Fact]
    public async Task ExecApprovalsSet_RequiresBaseHash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cap = new SystemCapability(NullLogger.Instance);
            var policy = new ExecApprovalPolicy(tempDir, NullLogger.Instance);
            cap.SetApprovalPolicy(policy);

            var req = new NodeInvokeRequest
            {
                Id = "ea-missing-base-hash",
                Command = "system.execApprovals.set",
                Args = Parse("""{"rules":[],"defaultAction":"deny"}""")
            };

            var res = await cap.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.Contains("baseHash", res.Error);
            Assert.NotEmpty(policy.Rules);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecApprovalsSet_RejectsStaleBaseHash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cap = new SystemCapability(NullLogger.Instance);
            var policy = new ExecApprovalPolicy(tempDir, NullLogger.Instance);
            cap.SetApprovalPolicy(policy);

            var staleHash = policy.GetPolicyHash();
            policy.InsertRule(0, new ExecApprovalRule
            {
                Pattern = "hostname",
                Action = ExecApprovalAction.Allow,
                Description = "Local edit after remote read"
            });

            var req = new NodeInvokeRequest
            {
                Id = "ea-stale-base-hash",
                Command = "system.execApprovals.set",
                Args = Parse($$"""{"baseHash":"{{staleHash}}","rules":[],"defaultAction":"deny"}""")
            };

            var res = await cap.ExecuteAsync(req);

            Assert.False(res.Ok);
            Assert.Contains("Refresh policy", res.Error);
            Assert.Contains(policy.Rules, rule => rule.Description == "Local edit after remote read");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private class FakeCommandRunner : ICommandRunner
    {
        public string Name => "fake";
        public CommandRequest? LastRequest { get; private set; }

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new CommandResult
            {
                Stdout = "ok",
                Stderr = "",
                ExitCode = 0,
                TimedOut = false,
                DurationMs = 1
            });
        }
    }
}

public class BrowserProxyCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task BrowserProxy_ForwardsToLocalControlPortWithBearerAuth()
    {
        var handler = new CapturingHandler("""{"ok":true,"url":"https://example.com"}""");
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "secret-token",
            handler);

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "browser-1",
            Command = "browser.proxy",
            Args = Parse("""{"method":"POST","path":"/snapshot","query":{"format":"aria"},"profile":"openclaw","body":{"limit":1},"timeoutMs":5000}""")
        });

        Assert.True(res.Ok);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://127.0.0.1:18791/snapshot?format=aria&profile=openclaw", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", handler.LastRequest.Headers.Authorization?.Parameter);

        var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(res.Payload));
        Assert.True(payload.TryGetProperty("result", out var result));
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BrowserProxy_RejectsAbsoluteUrlPath()
    {
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "secret-token",
            new CapturingHandler("""{"ok":true}"""));

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "browser-2",
            Command = "browser.proxy",
            Args = Parse("""{"path":"https://example.com"}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("must be a local control path", res.Error);
    }

    [Fact]
    public async Task BrowserProxy_ReturnsUnauthorizedAsAuthError()
    {
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "wrong-token",
            new CapturingHandler("Unauthorized", HttpStatusCode.Unauthorized));

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "browser-3",
            Command = "browser.proxy",
            Args = Parse("""{"path":"/"}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("authentication", res.Error);
        Assert.Contains("Verify the gateway token saved in Settings", res.Error);
    }

    [Fact]
    public async Task BrowserProxy_UnauthorizedWithoutTokenExplainsMissingSharedToken()
    {
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "",
            new CapturingHandler("Unauthorized", HttpStatusCode.Unauthorized));

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "browser-unauthenticated",
            Command = "browser.proxy",
            Args = Parse("""{"path":"/"}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("unauthenticated request", res.Error);
        Assert.Contains("no gateway shared token saved", res.Error);
        Assert.Contains("Settings", res.Error);
    }

    [Fact]
    public async Task BrowserProxy_RetriesUnauthorizedWithPasswordAuth()
    {
        var handler = new BrowserProxyAuthFallbackHandler();
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "browser-secret",
            handler);

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "browser-4",
            Command = "browser.proxy",
            Args = Parse("""{"method":"DELETE","path":"/tabs/1","body":{"reason":"test"}}""")
        });

        Assert.True(res.Ok);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("Basic", handler.Requests[1].Headers.Authorization?.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":browser-secret")),
            handler.Requests[1].Headers.Authorization?.Parameter);
        Assert.True(handler.Requests[1].Headers.TryGetValues("x-openclaw-password", out var passwordValues));
        Assert.Contains("browser-secret", passwordValues);
    }

    [Fact]
    public async Task BrowserProxy_UnreachableHostExplainsGatewayPlusTwoAndSshForward()
    {
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "browser-secret",
            new ThrowingBrowserProxyHandler());

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "browser-5",
            Command = "browser.proxy",
            Args = Parse("""{"method":"GET","path":"/"}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("127.0.0.1:18791", res.Error);
        Assert.Contains("gateway port + 2", res.Error);
        Assert.Contains("ssh -N -L 18791:127.0.0.1:<remote-gateway-port+2>", res.Error);
    }

    [Fact]
    public async Task BrowserProxy_UnreachableHostUsesRemoteGatewayPortInSshGuidance()
    {
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:28789",
            "browser-secret",
            new ThrowingBrowserProxyHandler(),
            sshRemoteGatewayPort: 18789);

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "browser-6",
            Command = "browser.proxy",
            Args = Parse("""{"method":"GET","path":"/"}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("127.0.0.1:28791", res.Error);
        Assert.Contains("ssh -N -L 28791:127.0.0.1:18791", res.Error);
    }

    [Fact]
    public async Task BrowserProxy_EmptyPath_ReturnsError()
    {
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "token",
            new CapturingHandler("""{"ok":true}"""));

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "bp-empty-path",
            Command = "browser.proxy",
            Args = Parse("""{"path":""}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("path required", res.Error);
    }

    [Fact]
    public async Task BrowserProxy_SlashlessPrependedPath_NormalizesWithLeadingSlash()
    {
        var handler = new CapturingHandler("""{"ok":true}""");
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "token",
            handler);

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "bp-no-leading-slash",
            Command = "browser.proxy",
            Args = Parse("""{"path":"snapshot"}""")
        });

        Assert.True(res.Ok);
        Assert.NotNull(handler.LastRequest);
        Assert.StartsWith("/snapshot", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task BrowserProxy_DoubleSlashPath_ReturnsError()
    {
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "token",
            new CapturingHandler("""{"ok":true}"""));

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "bp-double-slash",
            Command = "browser.proxy",
            Args = Parse("""{"path":"//evil.com/inject"}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("local control path", res.Error);
    }

    [Fact]
    public async Task BrowserProxy_GatewayPortAbove65533_ReturnsError()
    {
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:65534",   // control port would be 65536 — out of range
            "token",
            new CapturingHandler("""{"ok":true}"""));

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "bp-port-overflow",
            Command = "browser.proxy",
            Args = Parse("""{"path":"/"}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("port", res.Error);
    }

    [Fact]
    public async Task BrowserProxy_QueryAndProfileAppendedToUri()
    {
        var handler = new CapturingHandler("""{"ok":true}""");
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "token",
            handler);

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "bp-query-profile",
            Command = "browser.proxy",
            Args = Parse("""{"path":"/tabs","query":{"active":"true"},"profile":"work"}""")
        });

        Assert.True(res.Ok);
        var requestUri = handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("active=true", requestUri);
        Assert.Contains("profile=work", requestUri);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _response;
        private readonly HttpStatusCode _statusCode;

        public HttpRequestMessage? LastRequest { get; private set; }

        public CapturingHandler(string response, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _response = response;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_response)
            });
        }
    }

    private sealed class BrowserProxyAuthFallbackHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var hasPasswordHeader =
                request.Headers.TryGetValues("x-openclaw-password", out var passwordValues) &&
                passwordValues.Contains("browser-secret");
            var isBasic = request.Headers.Authorization?.Scheme == "Basic";
            var status = hasPasswordHeader && isBasic ? HttpStatusCode.OK : HttpStatusCode.Unauthorized;
            var response = status == HttpStatusCode.OK ? """{"ok":true}""" : "Unauthorized";

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(response)
            });
        }
    }

    private sealed class ThrowingBrowserProxyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}

public class CanvasCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void CanHandle_AllCanvasCommands()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("canvas.present"));
        Assert.True(cap.CanHandle("canvas.hide"));
        Assert.True(cap.CanHandle("canvas.navigate"));
        Assert.True(cap.CanHandle("canvas.eval"));
        Assert.True(cap.CanHandle("canvas.snapshot"));
        Assert.True(cap.CanHandle("canvas.a2ui.push"));
        Assert.True(cap.CanHandle("canvas.a2ui.pushJSONL"));
        Assert.True(cap.CanHandle("canvas.a2ui.reset"));
        Assert.True(cap.CanHandle("canvas.a2ui.dump"));
        Assert.True(cap.CanHandle("canvas.caps"));
        Assert.False(cap.CanHandle("canvas.unknown"));
        Assert.Equal("canvas", cap.Category);
    }

    [Fact]
    public async Task Present_RaisesEvent_WithArgs()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        CanvasPresentArgs? received = null;
        cap.PresentRequested += (s, a) => received = a;

        var req = new NodeInvokeRequest
        {
            Id = "c1",
            Command = "canvas.present",
            Args = Parse("""{"url":"https://example.com","width":1024,"height":768,"title":"Test","alwaysOnTop":true}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Equal("https://example.com", received!.Url);
        Assert.Equal(1024, received.Width);
        Assert.Equal(768, received.Height);
        Assert.Equal("Test", received.Title);
        Assert.True(received.AlwaysOnTop);
    }

    [Fact]
    public async Task Present_UsesDefaults_WhenArgsMissing()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        CanvasPresentArgs? received = null;
        cap.PresentRequested += (s, a) => received = a;

        var req = new NodeInvokeRequest
        {
            Id = "c2",
            Command = "canvas.present",
            Args = Parse("""{}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal(800, received!.Width);
        Assert.Equal(600, received.Height);
        Assert.Equal("Canvas", received.Title);
        Assert.False(received.AlwaysOnTop);
    }

    [Fact]
    public async Task Hide_RaisesEvent()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        bool hideRaised = false;
        cap.HideRequested += (s, e) => hideRaised = true;

        var req = new NodeInvokeRequest { Id = "c3", Command = "canvas.hide", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.True(hideRaised);
    }

    [Fact]
    public async Task Navigate_ReturnsError_WhenUrlMissing()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "c4", Command = "canvas.navigate", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("url", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Eval_AcceptsJavaScriptParam()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        string? evaledScript = null;
        cap.EvalRequested += (script) =>
        {
            evaledScript = script;
            return Task.FromResult("42");
        };

        var req = new NodeInvokeRequest
        {
            Id = "c5",
            Command = "canvas.eval",
            Args = Parse("""{"javaScript":"document.title"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("document.title", evaledScript);
    }

    [Fact]
    public async Task Eval_ReturnsError_WhenNoScript()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "c6", Command = "canvas.eval", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("script", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Eval_ReturnsError_WhenNoHandler()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "c7",
            Command = "canvas.eval",
            Args = Parse("""{"script":"test"}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Snapshot_ReturnsError_WhenNoHandler()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "c8", Command = "canvas.snapshot", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A2UIPush_ReturnsError_WhenNoJsonl()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "c9", Command = "canvas.a2ui.push", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("jsonl", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A2UIPush_RaisesEvent_WithJsonl()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        CanvasA2UIArgs? received = null;
        cap.A2UIPushRequested += (s, a) => received = a;

        var req = new NodeInvokeRequest
        {
            Id = "c10",
            Command = "canvas.a2ui.push",
            Args = Parse("""{"jsonl":"{\"type\":\"text\"}"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Contains("text", received!.Jsonl);
    }

    [Fact]
    public async Task A2UIPushJSONL_RaisesSameEventAsPush()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        CanvasA2UIArgs? received = null;
        cap.A2UIPushRequested += (s, a) => received = a;

        var req = new NodeInvokeRequest
        {
            Id = "c10b",
            Command = "canvas.a2ui.pushJSONL",
            Args = Parse("""{"jsonl":"{\"type\":\"text\",\"value\":\"legacy\"}"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Contains("legacy", received!.Jsonl);
    }

    [Fact]
    public async Task A2UIReset_RaisesEvent()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        bool resetRaised = false;
        cap.A2UIResetRequested += (s, e) => resetRaised = true;

        var req = new NodeInvokeRequest { Id = "c11", Command = "canvas.a2ui.reset", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.True(resetRaised);
    }

    [Fact]
    public async Task Navigate_InvokesHandler_WithCanonicalUrl()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        string? handlerSawUrl = null;
        cap.NavigateRequested += url =>
        {
            handlerSawUrl = url;
            return Task.FromResult("browser");
        };

        var req = new NodeInvokeRequest
        {
            Id = "c12",
            Command = "canvas.navigate",
            Args = Parse("""{"url":"https://example.com/page"}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("https://example.com/page", handlerSawUrl);
    }

    [Fact]
    public async Task Navigate_ResponseIncludesOpenerAndCanonicalUrl()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        cap.NavigateRequested += _ => Task.FromResult("browser");

        var req = new NodeInvokeRequest
        {
            Id = "c12b",
            Command = "canvas.navigate",
            // Mixed-case scheme/host should be canonicalized to lowercase before
            // the agent sees the response.
            Args = Parse("""{"url":"HTTPS://Example.COM/Path"}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);

        var json = System.Text.Json.JsonSerializer.Serialize(res.Payload);
        Assert.Contains("\"opener\":\"browser\"", json);
        Assert.Contains("\"navigated\":true", json);
        // Scheme and host lowercased; path preserved.
        Assert.Contains("\"url\":\"https://example.com/Path\"", json);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:network")]
    [InlineData("/relative/path")]
    [InlineData("https://attacker@evil.example.com/")]
    public async Task Navigate_RejectsUnsafeUrls_WithoutInvokingHandler(string url)
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        bool handlerCalled = false;
        cap.NavigateRequested += _ =>
        {
            handlerCalled = true;
            return Task.FromResult("browser");
        };

        var req = new NodeInvokeRequest
        {
            Id = "c12c",
            Command = "canvas.navigate",
            Args = Parse($$"""{"url":{{System.Text.Json.JsonSerializer.Serialize(url)}}}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.False(handlerCalled);
        Assert.Contains("Invalid url", res.Error);
    }

    [Fact]
    public async Task Navigate_NoHandler_ReturnsError()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        // No NavigateRequested subscription — agent should be told honestly.

        var req = new NodeInvokeRequest
        {
            Id = "c12d",
            Command = "canvas.navigate",
            Args = Parse("""{"url":"https://example.com/"}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("CANVAS_NOT_AVAILABLE", res.Error);
    }

    [Fact]
    public async Task Navigate_HandlerThrows_SurfacesAsError()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        cap.NavigateRequested += _ => throw new InvalidOperationException("browser refused");

        var req = new NodeInvokeRequest
        {
            Id = "c12e",
            Command = "canvas.navigate",
            Args = Parse("""{"url":"https://example.com/"}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("browser refused", res.Error);
    }

    [Fact]
    public async Task Eval_ReturnsError_WhenHandlerThrows()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        cap.EvalRequested += (script) => throw new InvalidOperationException("WebView2 not ready");

        var req = new NodeInvokeRequest
        {
            Id = "c13",
            Command = "canvas.eval",
            Args = Parse("""{"script":"document.title"}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("WebView2 not ready", res.Error);
    }

    [Fact]
    public async Task Snapshot_CallsHandler_WithArgs()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        CanvasSnapshotArgs? receivedArgs = null;
        cap.SnapshotRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult("base64data");
        };

        var req = new NodeInvokeRequest
        {
            Id = "c14",
            Command = "canvas.snapshot",
            Args = Parse("""{"format":"jpeg","maxWidth":800,"quality":70}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(receivedArgs);
        Assert.Equal("jpeg", receivedArgs!.Format);
        Assert.Equal(800, receivedArgs.MaxWidth);
        Assert.Equal(70, receivedArgs.Quality);
    }

    [Fact]
    public async Task Snapshot_ReturnsError_WhenHandlerThrows()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        cap.SnapshotRequested += (args) => throw new InvalidOperationException("Canvas not visible");

        var req = new NodeInvokeRequest { Id = "c15", Command = "canvas.snapshot", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Canvas not visible", res.Error);
    }

    [Fact]
    public async Task A2UIPush_WithJsonlPath_ReadsFile()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        CanvasA2UIArgs? received = null;
        cap.A2UIPushRequested += (s, a) => received = a;

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, """{"type":"text","value":"hello"}""");
            var req = new NodeInvokeRequest
            {
                Id = "c16",
                Command = "canvas.a2ui.push",
                Args = Parse($$$"""{"jsonlPath":"{{{tmpFile.Replace("\\", "\\\\")}}}"}""")
            };
            var res = await cap.ExecuteAsync(req);
            Assert.True(res.Ok);
            Assert.NotNull(received);
            Assert.Contains("hello", received!.Jsonl);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task A2UIPush_WithMissingJsonlPath_ReturnsError()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        // Use a path within the temp directory so path validation passes
        var missingFile = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.jsonl");
        var req = new NodeInvokeRequest
        {
            Id = "c17",
            Command = "canvas.a2ui.push",
            Args = Parse($"{{\"jsonlPath\":\"{missingFile.Replace("\\", "\\\\")}\"}}") 
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Failed to read jsonlPath", res.Error);
    }
    
    [Fact]
    public async Task A2UIPush_WithJsonlPathOutsideTempDir_ReturnsError()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "c18",
            Command = "canvas.a2ui.push",
            Args = Parse("""{"jsonlPath":"C:\\Windows\\System32\\config\\SAM"}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("temp directory", res.Error);
    }
    
    [Fact]
    public async Task A2UIPush_WithJsonlPathTraversal_ReturnsError()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        // Path traversal attempt to escape temp directory
        var traversalPath = Path.Combine(Path.GetTempPath(), "..", "..", "Windows", "System32", "config", "SAM");
        var req = new NodeInvokeRequest
        {
            Id = "c19",
            Command = "canvas.a2ui.push",
            Args = Parse($"{{\"jsonlPath\":\"{traversalPath.Replace("\\", "\\\\")}\"}}")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("temp directory", res.Error);
    }

    [Fact]
    public async Task A2UIDump_ReturnsError_WhenNoCanvasOpen()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "c20", Command = "canvas.a2ui.dump", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("CANVAS_NOT_OPEN", res.Error);
    }

    [Fact]
    public async Task A2UIDump_CallsHandler_AndReturnsParsedPayload()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        cap.A2UIDumpRequested += () => Task.FromResult("""{"surfaceId":"main","root":"r1","components":[],"dataModel":{}}""");

        var req = new NodeInvokeRequest { Id = "c21", Command = "canvas.a2ui.dump", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        // Payload is a parsed object (JsonElement), not a quoted string — verify by re-serializing.
        var json = JsonSerializer.Serialize(res.Payload);
        Assert.Contains("\"surfaceId\":\"main\"", json);
        Assert.Contains("\"root\":\"r1\"", json);
    }

    [Fact]
    public async Task A2UIDump_ReturnsError_WhenHandlerThrows()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        cap.A2UIDumpRequested += () => throw new InvalidOperationException("dispatcher gone");

        var req = new NodeInvokeRequest { Id = "c22", Command = "canvas.a2ui.dump", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("CANVAS_DUMP_FAILED", res.Error);
        Assert.Contains("dispatcher gone", res.Error);
    }

    [Fact]
    public async Task Caps_ReturnsDefault_WhenNoHandler()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "c23", Command = "canvas.caps", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        var json = JsonSerializer.Serialize(res.Payload);
        Assert.Contains("\"renderer\":\"none\"", json);
        Assert.Contains("\"eval\":false", json);
        Assert.Contains("\"snapshot\":false", json);
    }

    [Fact]
    public async Task Caps_CallsHandler_AndReturnsParsedPayload()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        cap.CapsRequested += () => Task.FromResult("""{"renderer":"native","eval":false,"snapshot":true,"a2ui":{"version":"0.8","introspect":true}}""");

        var req = new NodeInvokeRequest { Id = "c24", Command = "canvas.caps", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        var json = JsonSerializer.Serialize(res.Payload);
        Assert.Contains("\"renderer\":\"native\"", json);
        Assert.Contains("\"introspect\":true", json);
    }
}

public class DeviceCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private class FakeDeviceStatusProvider : IDeviceStatusProvider
    {
        public bool ThrowOnBattery { get; set; }

        public object GetOsInfo() => new
        {
            version = "10.0.99999",
            architecture = "X64",
            machineName = "TEST-PC",
            uptimeSeconds = 3600L
        };

        public Task<object> GetCpuInfoAsync() => Task.FromResult<object>(new
        {
            name = "Test CPU",
            logicalProcessors = 8,
            usagePercent = (double?)42.0
        });

        public object GetMemoryInfo() => new
        {
            totalBytes = 16L * 1024 * 1024 * 1024,
            availableBytes = 8L * 1024 * 1024 * 1024,
            usagePercent = 50.0
        };

        public object GetDiskInfo() => new
        {
            drives = new[]
            {
                new
                {
                    name = "C:\\",
                    label = "OS",
                    totalBytes = 500L * 1024 * 1024 * 1024,
                    freeBytes = 250L * 1024 * 1024 * 1024,
                    usagePercent = 50.0,
                    format = "NTFS"
                }
            }
        };

        public object GetBatteryInfo() => ThrowOnBattery
            ? throw new Exception("No battery hardware")
            : new
            {
                present = true,
                chargePercent = (int?)85,
                isCharging = false,
                estimatedMinutesRemaining = (int?)120
            };

        public void Dispose() { }
    }

    private static DeviceCapability CreateCapability(FakeDeviceStatusProvider? provider = null)
    {
        return new DeviceCapability(NullLogger.Instance, provider ?? new FakeDeviceStatusProvider());
    }

    private static JsonElement GetPayload(NodeInvokeResponse res)
    {
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(res.Payload));
    }

    [Fact]
    public void CanHandle_DeviceStatus()
    {
        var cap = CreateCapability();
        Assert.True(cap.CanHandle("device.status"));
    }

    [Fact]
    public void CanHandle_UnknownCommand()
    {
        var cap = CreateCapability();
        Assert.False(cap.CanHandle("device.unknown"));
    }

    [Fact]
    public void Category_IsDevice()
    {
        var cap = CreateCapability();
        Assert.Equal("device", cap.Category);
    }

    [Fact]
    public async Task Status_ReturnsAllSections_WhenNoFilter()
    {
        var cap = CreateCapability();
        var req = new NodeInvokeRequest { Id = "t1", Command = "device.status", Args = Parse("""{}""") };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var payload = GetPayload(res);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("collectedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("os").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("cpu").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("memory").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("disk").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("battery").ValueKind);
    }

    [Fact]
    public async Task Status_ReturnsOnlyRequested_WhenFiltered()
    {
        var cap = CreateCapability();
        var req = new NodeInvokeRequest
        {
            Id = "t2",
            Command = "device.status",
            Args = Parse("""{"sections":["os","disk"]}""")
        };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var payload = GetPayload(res);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("collectedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("os").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("disk").ValueKind);
        // cpu, memory should NOT be present
        Assert.False(payload.TryGetProperty("cpu", out _));
        Assert.False(payload.TryGetProperty("memory", out _));
        // battery always present for legacy compat (fallback stub when not requested)
        var battery = payload.GetProperty("battery");
        Assert.Equal("unknown", battery.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Status_RejectsUnknownSections()
    {
        var cap = CreateCapability();
        var req = new NodeInvokeRequest
        {
            Id = "t3",
            Command = "device.status",
            Args = Parse("""{"sections":["os","bogus"]}""")
        };

        var res = await cap.ExecuteAsync(req);

        Assert.False(res.Ok);
        Assert.Contains("bogus", res.Error);
        Assert.Contains("Valid:", res.Error);
    }

    [Fact]
    public async Task Status_OsSection_HasExpectedFields()
    {
        var cap = CreateCapability();
        var req = new NodeInvokeRequest
        {
            Id = "t4",
            Command = "device.status",
            Args = Parse("""{"sections":["os"]}""")
        };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var os = GetPayload(res).GetProperty("os");
        Assert.Equal("10.0.99999", os.GetProperty("version").GetString());
        Assert.Equal("X64", os.GetProperty("architecture").GetString());
        Assert.Equal("TEST-PC", os.GetProperty("machineName").GetString());
        Assert.Equal(3600, os.GetProperty("uptimeSeconds").GetInt64());
    }

    [Fact]
    public async Task Status_DiskSection_HasDrives()
    {
        var cap = CreateCapability();
        var req = new NodeInvokeRequest
        {
            Id = "t5",
            Command = "device.status",
            Args = Parse("""{"sections":["disk"]}""")
        };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var disk = GetPayload(res).GetProperty("disk");
        var drives = disk.GetProperty("drives");
        Assert.Equal(JsonValueKind.Array, drives.ValueKind);
        Assert.True(drives.GetArrayLength() > 0);
        var firstDrive = drives[0];
        Assert.True(firstDrive.GetProperty("totalBytes").GetInt64() > 0);
        Assert.Equal("NTFS", firstDrive.GetProperty("format").GetString());
    }

    [Fact]
    public async Task Status_MemorySection_HasUsage()
    {
        var cap = CreateCapability();
        var req = new NodeInvokeRequest
        {
            Id = "t6",
            Command = "device.status",
            Args = Parse("""{"sections":["memory"]}""")
        };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var mem = GetPayload(res).GetProperty("memory");
        Assert.True(mem.GetProperty("totalBytes").GetInt64() > 0);
        var usage = mem.GetProperty("usagePercent").GetDouble();
        Assert.InRange(usage, 0, 100);
    }

    [Fact]
    public async Task Status_BatterySection_GracefulOnFailure()
    {
        var provider = new FakeDeviceStatusProvider { ThrowOnBattery = true };
        var cap = CreateCapability(provider);
        var req = new NodeInvokeRequest
        {
            Id = "t7",
            Command = "device.status",
            Args = Parse("""{"sections":["battery"]}""")
        };

        var res = await cap.ExecuteAsync(req);

        // Command should succeed - battery section has error + legacy fields
        Assert.True(res.Ok);
        var battery = GetPayload(res).GetProperty("battery");
        Assert.NotEqual(JsonValueKind.Undefined, battery.GetProperty("error").ValueKind);
        Assert.Contains("No battery hardware", battery.GetProperty("error").GetString());
        // Legacy fields must still be present for backward compat
        Assert.True(battery.TryGetProperty("level", out _), "battery.level missing on failure path");
        Assert.Equal("unknown", battery.GetProperty("state").GetString());
        Assert.True(battery.TryGetProperty("lowPowerModeEnabled", out _), "battery.lowPowerModeEnabled missing on failure path");
    }

    [Fact]
    public async Task Status_EmptySectionsArray_ReturnsAll()
    {
        var cap = CreateCapability();
        var req = new NodeInvokeRequest
        {
            Id = "t8",
            Command = "device.status",
            Args = Parse("""{"sections":[]}""")
        };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var payload = GetPayload(res);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("os").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("cpu").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("memory").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("disk").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("battery").ValueKind);
    }

    [Fact]
    public async Task Status_HasCollectedAtTimestamp()
    {
        var cap = CreateCapability();
        var req = new NodeInvokeRequest { Id = "t9", Command = "device.status", Args = Parse("""{}""") };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var collectedAt = GetPayload(res).GetProperty("collectedAt").GetString();
        Assert.NotNull(collectedAt);
        // Verify it's valid ISO 8601
        Assert.True(DateTimeOffset.TryParse(collectedAt, out var dto));
        // Should be recent (within last 10 seconds)
        Assert.InRange(dto, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task DeviceInfo_StillWorks()
    {
        var cap = CreateCapability();
        var req = new NodeInvokeRequest { Id = "di", Command = "device.info", Args = Parse("""{}""") };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var payload = GetPayload(res);
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("deviceName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("systemName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("appVersion").GetString()));
    }

    [Fact]
    public async Task DeviceUnknownCommand_ReturnsError()
    {
        var cap = CreateCapability();
        var req = new NodeInvokeRequest { Id = "d3", Command = "device.unknown", Args = Parse("""{}""") };

        var res = await cap.ExecuteAsync(req);

        Assert.False(res.Ok);
        Assert.Contains("Unknown command", res.Error);
    }

    [Fact]
    public async Task Status_Default_ContainsLegacyFields()
    {
        var cap = CreateCapability();
        var req = new NodeInvokeRequest { Id = "leg1", Command = "device.status", Args = Parse("""{}""") };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var payload = GetPayload(res);

        // Legacy battery fields — must be present even when provider returns new shape
        var battery = payload.GetProperty("battery");
        Assert.NotEqual(JsonValueKind.Undefined, battery.ValueKind);
        // Old contract fields: level, state, lowPowerModeEnabled
        Assert.True(battery.TryGetProperty("level", out _), "battery.level missing");
        Assert.True(battery.TryGetProperty("state", out _), "battery.state missing");
        Assert.True(battery.TryGetProperty("lowPowerModeEnabled", out _), "battery.lowPowerModeEnabled missing");
        // New fields also present
        Assert.True(battery.TryGetProperty("present", out _), "battery.present missing");
        Assert.True(battery.TryGetProperty("chargePercent", out _), "battery.chargePercent missing");

        // Legacy thermal
        var thermal = payload.GetProperty("thermal");
        Assert.Equal("nominal", thermal.GetProperty("state").GetString());

        // Legacy storage
        var storage = payload.GetProperty("storage");
        Assert.NotEqual(JsonValueKind.Undefined, storage.ValueKind);

        // Legacy network
        var network = payload.GetProperty("network");
        Assert.NotEqual(JsonValueKind.Undefined, network.ValueKind);

        // Legacy top-level uptimeSeconds
        var uptime = payload.GetProperty("uptimeSeconds").GetDouble();
        Assert.True(uptime >= 0);
    }

    [Fact]
    public async Task Status_Filtered_StillContainsLegacyFields()
    {
        var cap = CreateCapability();
        var req = new NodeInvokeRequest
        {
            Id = "leg2",
            Command = "device.status",
            Args = Parse("""{"sections":["os","disk"]}""")
        };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var payload = GetPayload(res);

        // Requested sections present
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("os").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("disk").ValueKind);

        // Legacy fields always present regardless of sections filter
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("battery").ValueKind);
        Assert.Equal("nominal", payload.GetProperty("thermal").GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("storage").ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("network").ValueKind);
        Assert.True(payload.GetProperty("uptimeSeconds").GetDouble() >= 0);
    }

    [Fact]
    public async Task Status_LazyDiskProvider_DoesNotCrashCommand()
    {
        // A provider whose GetDiskInfo returns a lazy enumerable that throws
        // during enumeration. SafeCollect catches synchronous throws; the lazy
        // throw happens during serialization. This test verifies the command
        // still completes (Success wraps the result; serialization is the
        // caller's concern). The real DeviceStatusProvider materializes with
        // .ToArray() to prevent this scenario.
        var provider = new LazyThrowingDiskProvider();
        var cap = new DeviceCapability(NullLogger.Instance, provider);
        var req = new NodeInvokeRequest
        {
            Id = "lazy1",
            Command = "device.status",
            Args = Parse("""{"sections":["disk"]}""")
        };

        var res = await cap.ExecuteAsync(req);

        // Command itself succeeds — the lazy enumerable hasn't been iterated yet.
        // This confirms SafeCollect doesn't mask the issue; materialization in
        // the provider (.ToArray()) is what actually prevents serialization failures.
        Assert.True(res.Ok);
    }

    [Fact]
    public async Task Status_ProviderDisposal_GetCpuInfoStillSafe()
    {
        var provider = new FakeDeviceStatusProvider();
        var cap = new DeviceCapability(NullLogger.Instance, provider);

        // Dispose the provider before calling
        provider.Dispose();

        // GetCpuInfoAsync should still return data (fake doesn't depend on timer)
        var req = new NodeInvokeRequest { Id = "disp1", Command = "device.status", Args = Parse("""{"sections":["cpu"]}""") };
        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var cpu = GetPayload(res).GetProperty("cpu");
        Assert.Equal(8, cpu.GetProperty("logicalProcessors").GetInt32());
    }

    private class LazyThrowingDiskProvider : IDeviceStatusProvider
    {
        public object GetOsInfo() => new { version = "10.0", architecture = "X64", machineName = "TEST", uptimeSeconds = 0L };
        public Task<object> GetCpuInfoAsync() => Task.FromResult<object>(new { name = "CPU", logicalProcessors = 1, usagePercent = (double?)null });
        public object GetMemoryInfo() => new { totalBytes = 0L, availableBytes = 0L, usagePercent = 0.0 };
        public object GetDiskInfo()
        {
            // Returns a lazy enumerable that throws during enumeration/serialization,
            // not at construction time — simulates a drive that becomes unavailable
            // after the provider method returns but before JSON serialization.
            IEnumerable<object> LazyDrives()
            {
                throw new IOException("Simulated lazy disk enumeration failure");
            }
            return new { drives = LazyDrives() };
        }
        public object GetBatteryInfo() => new { present = false, chargePercent = (int?)null, isCharging = false, estimatedMinutesRemaining = (int?)null };
        public void Dispose() { }
    }
}

public class ScreenCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void CanHandle_ScreenCommands()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("screen.snapshot"));
        Assert.True(cap.CanHandle("screen.record"));
        Assert.False(cap.CanHandle("screen.capture"));
        Assert.False(cap.CanHandle("screen.list"));
        Assert.False(cap.CanHandle("screen.record.start"));
        Assert.False(cap.CanHandle("screen.record.stop"));
        Assert.Equal("screen", cap.Category);
    }

    [Fact]
    public async Task Capture_ReturnsError_WhenNoHandler()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "s1", Command = "screen.snapshot", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capture_CallsHandler_WithArgs()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        ScreenCaptureArgs? receivedArgs = null;
        cap.CaptureRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new ScreenCaptureResult { Format = "png", Width = 1920, Height = 1080, Base64 = "abc" });
        };

        var req = new NodeInvokeRequest
        {
            Id = "s2",
            Command = "screen.snapshot",
            Args = Parse("""{"format":"jpeg","maxWidth":800,"quality":50,"screenIndex":1}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(receivedArgs);
        Assert.Equal("jpeg", receivedArgs!.Format);
        Assert.Equal(800, receivedArgs.MaxWidth);
        Assert.Equal(50, receivedArgs.Quality);
        Assert.Equal(1, receivedArgs.MonitorIndex);
    }

    [Fact]
    public async Task Capture_ReturnsError_WhenHandlerThrows()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        cap.CaptureRequested += (args) => throw new InvalidOperationException("Display access denied");

        var req = new NodeInvokeRequest { Id = "s5", Command = "screen.snapshot", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Display access denied", res.Error);
    }

    [Fact]
    public async Task Capture_ResponseIncludesDataUri()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        cap.CaptureRequested += (args) => Task.FromResult(new ScreenCaptureResult
        {
            Format = "png",
            Width = 1920,
            Height = 1080,
            Base64 = "abc123"
        });

        var req = new NodeInvokeRequest { Id = "s7", Command = "screen.snapshot", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);

        var json = JsonSerializer.Serialize(res.Payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("image", out var imageEl));
        Assert.StartsWith("data:image/png;base64,", imageEl.GetString());
        Assert.Contains("abc123", imageEl.GetString());
    }

    [Fact]
    public async Task Capture_ClampsExtremeValues_ToSafeBounds()
    {
        // CR-007: oversized/negative caller values must be clamped before any
        // downstream allocation (back-buffer sizes, image encoder buffers).
        var cap = new ScreenCapability(NullLogger.Instance);
        ScreenCaptureArgs? received = null;
        cap.CaptureRequested += args =>
        {
            received = args;
            return Task.FromResult(new ScreenCaptureResult { Format = "png", Width = 0, Height = 0, Base64 = "" });
        };

        var req = new NodeInvokeRequest
        {
            Id = "clamp",
            Command = "screen.snapshot",
            Args = Parse("""{"maxWidth":99999,"quality":500,"screenIndex":-3}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.True(received!.MaxWidth <= 7680, $"maxWidth not clamped: {received.MaxWidth}");
        Assert.InRange(received.Quality, 1, 100);
        Assert.True(received.MonitorIndex >= 0, $"screenIndex not clamped: {received.MonitorIndex}");
    }

    [Fact]
    public async Task Capture_UsesMonitorAlias_ForScreenIndex()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        ScreenCaptureArgs? receivedArgs = null;
        cap.CaptureRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new ScreenCaptureResult { Format = "png", Width = 1920, Height = 1080, Base64 = "" });
        };

        // "monitor" is an alias for "screenIndex"
        var req = new NodeInvokeRequest
        {
            Id = "s8",
            Command = "screen.snapshot",
            Args = Parse("""{"monitor":2}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(receivedArgs);
        Assert.Equal(2, receivedArgs!.MonitorIndex);
    }

    [Fact]
    public async Task Record_ReturnsError_WhenNoHandler()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "s9", Command = "screen.record", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Record_CallsHandler_WithMacCompatibleArgs()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        ScreenRecordArgs? receivedArgs = null;
        cap.RecordRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new ScreenRecordResult
            {
                Format = "mp4",
                Base64 = "video",
                DurationMs = args.DurationMs,
                Fps = args.Fps,
                ScreenIndex = args.ScreenIndex,
                HasAudio = false
            });
        };

        var req = new NodeInvokeRequest
        {
            Id = "s10",
            Command = "screen.record",
            Args = Parse("""{"durationMs":1500,"fps":7.5,"screenIndex":1,"format":"mp4","includeAudio":true}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(receivedArgs);
        Assert.Equal(1500, receivedArgs!.DurationMs);
        Assert.Equal(7.5, receivedArgs.Fps);
        Assert.Equal(1, receivedArgs.ScreenIndex);
        Assert.Equal("mp4", receivedArgs.Format);
        Assert.True(receivedArgs.IncludeAudio);
    }

    [Fact]
    public async Task Record_RejectsUnsupportedFormat()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        var handlerCalled = false;
        cap.RecordRequested += (args) =>
        {
            handlerCalled = true;
            return Task.FromResult(new ScreenRecordResult());
        };

        var req = new NodeInvokeRequest
        {
            Id = "s11",
            Command = "screen.record",
            Args = Parse("""{"format":"webm"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.False(handlerCalled);
        Assert.Contains("Only mp4", res.Error);
    }

    [Fact]
    public async Task Record_ReturnsMacCompatiblePayload()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        cap.RecordRequested += (args) => Task.FromResult(new ScreenRecordResult
        {
            Format = "mp4",
            Base64 = "abc123",
            DurationMs = 2000,
            Fps = 10,
            ScreenIndex = 2,
            Width = 1920,
            Height = 1080,
            HasAudio = false
        });

        var req = new NodeInvokeRequest
        {
            Id = "s12",
            Command = "screen.record",
            Args = Parse("""{"durationMs":2000,"fps":10,"screenIndex":2}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);

        var json = JsonSerializer.Serialize(res.Payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("mp4", root.GetProperty("format").GetString());
        Assert.Equal("abc123", root.GetProperty("base64").GetString());
        Assert.Equal(2000, root.GetProperty("durationMs").GetInt32());
        Assert.Equal(10, root.GetProperty("fps").GetDouble());
        Assert.Equal(2, root.GetProperty("screenIndex").GetInt32());
        Assert.False(root.GetProperty("hasAudio").GetBoolean());
        Assert.False(root.TryGetProperty("filePath", out _));
        Assert.False(root.TryGetProperty("width", out _));
        Assert.False(root.TryGetProperty("height", out _));
    }

    [Fact]
    public async Task Record_UsesDefaultFps_WhenFpsMissing()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        ScreenRecordArgs? received = null;
        cap.RecordRequested += (args) =>
        {
            received = args;
            return Task.FromResult(new ScreenRecordResult { Format = "mp4", Fps = args.Fps });
        };

        var req = new NodeInvokeRequest
        {
            Id = "s13",
            Command = "screen.record",
            Args = Parse("""{"durationMs":5000}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Equal(10.0, received!.Fps);
    }

    [Fact]
    public async Task Record_UsesDefaultFps_WhenFpsIsNonNumeric()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        ScreenRecordArgs? received = null;
        cap.RecordRequested += (args) =>
        {
            received = args;
            return Task.FromResult(new ScreenRecordResult { Format = "mp4", Fps = args.Fps });
        };

        var req = new NodeInvokeRequest
        {
            Id = "s14",
            Command = "screen.record",
            Args = Parse("""{"fps":"fast"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Equal(10.0, received!.Fps);
    }

    [Fact]
    public async Task Record_ReturnsError_WhenHandlerThrows()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        cap.RecordRequested += (_) => throw new InvalidOperationException("Capture permission denied");

        var req = new NodeInvokeRequest { Id = "s15", Command = "screen.record", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Capture permission denied", res.Error);
    }
}

public class CameraCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void CanHandle_CameraCommands()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("camera.list"));
        Assert.True(cap.CanHandle("camera.snap"));
        Assert.True(cap.CanHandle("camera.clip"));
        Assert.False(cap.CanHandle("camera.unknown"));
        Assert.Equal("camera", cap.Category);
    }

    [Fact]
    public async Task List_ReturnsError_WhenNoHandler()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "cam1", Command = "camera.list", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Contains("not available", res.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_ReturnsCameras_WhenHandler()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        cap.ListRequested += () => Task.FromResult(new[]
        {
            new CameraInfo { DeviceId = "cam-1", Name = "Front", IsDefault = true },
            new CameraInfo { DeviceId = "cam-2", Name = "Back", IsDefault = false }
        });

        var req = new NodeInvokeRequest { Id = "cam2", Command = "camera.list", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(res.Payload);
        
        // Verify payload contains expected camera entries
        var json = System.Text.Json.JsonSerializer.Serialize(res.Payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("cameras", out var camerasEl));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, camerasEl.ValueKind);
        Assert.Equal(2, camerasEl.GetArrayLength());
        Assert.Equal("cam-1", camerasEl[0].GetProperty("DeviceId").GetString());
        Assert.Equal("Front", camerasEl[0].GetProperty("Name").GetString());
        Assert.True(camerasEl[0].GetProperty("IsDefault").GetBoolean());
        Assert.Equal("cam-2", camerasEl[1].GetProperty("DeviceId").GetString());
        Assert.False(camerasEl[1].GetProperty("IsDefault").GetBoolean());
    }

    [Fact]
    public async Task Snap_ReturnsError_WhenNoHandler()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "cam3", Command = "camera.snap", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Contains("not available", res.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Snap_CallsHandler_WithArgs()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        CameraSnapArgs? receivedArgs = null;
        cap.SnapRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new CameraSnapResult { Format = "jpeg", Width = 640, Height = 480, Base64 = "img" });
        };

        var req = new NodeInvokeRequest
        {
            Id = "cam4",
            Command = "camera.snap",
            Args = Parse("""{"deviceId":"cam-1","format":"png","maxWidth":320,"quality":50}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(receivedArgs);
        Assert.Equal("cam-1", receivedArgs!.DeviceId);
        Assert.Equal("png", receivedArgs.Format);
        Assert.Equal(320, receivedArgs.MaxWidth);
        Assert.Equal(50, receivedArgs.Quality);
    }

    [Fact]
    public async Task Snap_UsesDefaults_WhenArgsMissing()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        CameraSnapArgs? receivedArgs = null;
        cap.SnapRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new CameraSnapResult { Format = "jpeg", Width = 640, Height = 480, Base64 = "img" });
        };

        var req = new NodeInvokeRequest { Id = "cam5", Command = "camera.snap", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Null(receivedArgs!.DeviceId);
        Assert.Equal("jpeg", receivedArgs.Format);
        Assert.Equal(1280, receivedArgs.MaxWidth);
        Assert.Equal(80, receivedArgs.Quality);
    }

    [Fact]
    public async Task Snap_ReturnsError_WhenHandlerThrows()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        cap.SnapRequested += (args) => throw new InvalidOperationException("Camera access blocked");

        var req = new NodeInvokeRequest { Id = "cam6", Command = "camera.snap", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Camera access blocked", res.Error);
    }

    [Fact]
    public void CameraClipArgs_DefaultValues()
    {
        var args = new CameraClipArgs();
        Assert.Equal(3000, args.DurationMs);
        Assert.True(args.IncludeAudio);
        Assert.Equal("mp4", args.Format);
        Assert.Null(args.DeviceId);
    }

    [Fact]
    public async Task Clip_ClampsDuration_ToMax60000()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        CameraClipArgs? receivedArgs = null;
        cap.ClipRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new CameraClipResult { Format = "mp4", Base64 = "vid", DurationMs = args.DurationMs, HasAudio = true });
        };

        var req = new NodeInvokeRequest
        {
            Id = "clip1",
            Command = "camera.clip",
            Args = Parse("""{"durationMs":120000}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(receivedArgs);
        Assert.Equal(60000, receivedArgs!.DurationMs);
    }

    [Fact]
    public async Task Clip_RoutesToHandler_WithArgs()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        CameraClipArgs? receivedArgs = null;
        cap.ClipRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new CameraClipResult { Format = "mp4", Base64 = "vid", DurationMs = args.DurationMs, HasAudio = args.IncludeAudio });
        };

        var req = new NodeInvokeRequest
        {
            Id = "clip2",
            Command = "camera.clip",
            Args = Parse("""{"deviceId":"cam-1","durationMs":5000,"includeAudio":false,"format":"mp4"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(receivedArgs);
        Assert.Equal("cam-1", receivedArgs!.DeviceId);
        Assert.Equal(5000, receivedArgs.DurationMs);
        Assert.False(receivedArgs.IncludeAudio);
        Assert.Equal("mp4", receivedArgs.Format);
    }

    [Fact]
    public async Task Clip_ReturnsError_WhenNoHandler()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "clip3", Command = "camera.clip", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Contains("not available", res.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clip_ClampsZeroAndNegativeDuration_ToMinimum()
    {
        // CR-007: durationMs <= 0 used to slip through the original
        // `Math.Min(value, 60000)` cap and ask the recorder to capture for
        // zero / negative seconds, which produced a degenerate file.
        var cap = new CameraCapability(NullLogger.Instance);
        CameraClipArgs? received = null;
        cap.ClipRequested += args =>
        {
            received = args;
            return Task.FromResult(new CameraClipResult { Format = "mp4", Base64 = "", DurationMs = args.DurationMs, HasAudio = false });
        };

        var req = new NodeInvokeRequest
        {
            Id = "clip-clamp",
            Command = "camera.clip",
            Args = Parse("""{"durationMs":-500}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.True(received!.DurationMs >= 100, $"duration not floor-clamped: {received.DurationMs}");
        Assert.True(received.DurationMs <= 60000);
    }
}

public class TtsCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void CanHandle_TtsSpeak()
    {
        var cap = new TtsCapability(NullLogger.Instance);

        Assert.True(cap.CanHandle("tts.speak"));
        Assert.False(cap.CanHandle("tts.stop"));
        Assert.Equal("tts", cap.Category);
    }

    [Theory]
    [InlineData("elevenlabs", "windows", "elevenlabs")]
    [InlineData(" ELEVENLABS ", "windows", "elevenlabs")]
    [InlineData(null, "elevenlabs", "elevenlabs")]
    [InlineData("   ", "elevenlabs", "elevenlabs")]
    [InlineData(null, "", "windows")]
    [InlineData(null, "   ", "windows")]
    public void ResolveProvider_NormalizesRequestedAndConfiguredValues(
        string? requestedProvider,
        string? configuredProvider,
        string expected)
    {
        Assert.Equal(expected, TtsCapability.ResolveProvider(requestedProvider, configuredProvider));
    }

    [Fact]
    public async Task Speak_ReturnsError_WhenTextMissing()
    {
        var cap = new TtsCapability(NullLogger.Instance);
        var handlerCalled = false;
        cap.SpeakRequested += (_, _) =>
        {
            handlerCalled = true;
            return Task.FromResult(new TtsSpeakResult());
        };

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "tts-missing",
            Command = "tts.speak",
            Args = Parse("""{"text":"   "}""")
        });

        Assert.False(res.Ok);
        Assert.False(handlerCalled);
        Assert.Contains("text", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Speak_ReturnsError_WhenNoHandler()
    {
        var cap = new TtsCapability(NullLogger.Instance);

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "tts-unavailable",
            Command = "tts.speak",
            Args = Parse("""{"text":"hello"}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Speak_ReturnsError_WhenTextTooLong()
    {
        var cap = new TtsCapability(NullLogger.Instance);
        var handlerCalled = false;
        cap.SpeakRequested += (_, _) =>
        {
            handlerCalled = true;
            return Task.FromResult(new TtsSpeakResult());
        };

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "tts-too-long",
            Command = "tts.speak",
            Args = Parse(JsonSerializer.Serialize(new
            {
                text = new string('x', TtsCapability.MaxTextLength + 1)
            }))
        });

        Assert.False(res.Ok);
        Assert.False(handlerCalled);
        Assert.Contains(TtsCapability.MaxTextLength.ToString(), res.Error);
    }

    [Fact]
    public async Task Speak_RaisesEvent_WithArgs()
    {
        var cap = new TtsCapability(NullLogger.Instance);
        TtsSpeakArgs? received = null;
        cap.SpeakRequested += (args, _) =>
        {
            received = args;
            return Task.FromResult(new TtsSpeakResult
            {
                Provider = TtsCapability.ElevenLabsProvider,
                ContentType = "audio/mpeg",
                DurationMs = 123
            });
        };

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "tts-args",
            Command = "tts.speak",
            Args = Parse("""{"text":" hello world ","provider":"elevenlabs","voiceId":"voice-1","model":"model-1","interrupt":true}""")
        });

        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Equal("hello world", received!.Text);
        Assert.Equal("elevenlabs", received.Provider);
        Assert.Equal("voice-1", received.VoiceId);
        Assert.Equal("model-1", received.Model);
        Assert.True(received.Interrupt);

        var json = JsonSerializer.Serialize(res.Payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("spoken").GetBoolean());
        Assert.Equal("elevenlabs", root.GetProperty("provider").GetString());
        Assert.Equal("audio/mpeg", root.GetProperty("contentType").GetString());
        Assert.Equal(123, root.GetProperty("durationMs").GetInt32());
    }

    [Fact]
    public async Task Speak_ReturnsError_WhenHandlerThrows()
    {
        var cap = new TtsCapability(NullLogger.Instance);
        cap.SpeakRequested += (_, _) => throw new InvalidOperationException("Audio device unavailable");

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "tts-fail",
            Command = "tts.speak",
            Args = Parse("""{"text":"hello"}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("Audio device unavailable", res.Error);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsError()
    {
        var cap = new TtsCapability(NullLogger.Instance);

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "tts-unknown",
            Command = "tts.stop",
            Args = Parse("""{}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("Unknown command", res.Error);
    }
}

public class LocationCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void LocationGetArgs_HasCorrectDefaults()
    {
        var args = new LocationGetArgs();
        Assert.Equal("default", args.Accuracy);
        Assert.Equal(30000, args.MaxAgeMs);
        Assert.Equal(10000, args.TimeoutMs);
    }

    [Fact]
    public void CanHandle_LocationCommands()
    {
        var cap = new LocationCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("location.get"));
        Assert.False(cap.CanHandle("location.watch"));
        Assert.Equal("location", cap.Category);
    }

    [Fact]
    public async Task Get_ReturnsError_WhenNoHandler()
    {
        var cap = new LocationCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "loc1", Command = "location.get", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Contains("not available", res.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_ReturnsLocation_WhenHandler()
    {
        var cap = new LocationCapability(NullLogger.Instance);
        cap.GetRequested += (args) => Task.FromResult(new LocationResult
        {
            Latitude = 47.6062,
            Longitude = -122.3321,
            AccuracyMeters = 15.5,
            TimestampMs = 1700000000000
        });

        var req = new NodeInvokeRequest { Id = "loc2", Command = "location.get", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(res.Payload);

        var json = System.Text.Json.JsonSerializer.Serialize(res.Payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(47.6062, root.GetProperty("latitude").GetDouble(), 4);
        Assert.Equal(-122.3321, root.GetProperty("longitude").GetDouble(), 4);
        Assert.Equal(15.5, root.GetProperty("accuracy").GetDouble(), 1);
        Assert.Equal(1700000000000, root.GetProperty("timestamp").GetInt64());
    }

    [Fact]
    public async Task Get_PassesArgs_ToHandler()
    {
        var cap = new LocationCapability(NullLogger.Instance);
        LocationGetArgs? receivedArgs = null;
        cap.GetRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new LocationResult
            {
                Latitude = 0, Longitude = 0, AccuracyMeters = 0, TimestampMs = 0
            });
        };

        var req = new NodeInvokeRequest
        {
            Id = "loc3",
            Command = "location.get",
            Args = Parse("""{"accuracy":"precise","maxAge":5000,"locationTimeout":3000}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(receivedArgs);
        Assert.Equal("precise", receivedArgs!.Accuracy);
        Assert.Equal(5000, receivedArgs.MaxAgeMs);
        Assert.Equal(3000, receivedArgs.TimeoutMs);
    }

    [Fact]
    public async Task Get_UsesDefaults_WhenArgsMissing()
    {
        var cap = new LocationCapability(NullLogger.Instance);
        LocationGetArgs? receivedArgs = null;
        cap.GetRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new LocationResult
            {
                Latitude = 0, Longitude = 0, AccuracyMeters = 0, TimestampMs = 0
            });
        };

        var req = new NodeInvokeRequest { Id = "loc4", Command = "location.get", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("default", receivedArgs!.Accuracy);
        Assert.Equal(30000, receivedArgs.MaxAgeMs);
        Assert.Equal(10000, receivedArgs.TimeoutMs);
    }

    [Fact]
    public async Task Get_ReturnsPermissionError_WhenUnauthorized()
    {
        var cap = new LocationCapability(NullLogger.Instance);
        cap.GetRequested += (args) => throw new UnauthorizedAccessException("No permission");

        var req = new NodeInvokeRequest { Id = "loc5", Command = "location.get", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Equal("LOCATION_PERMISSION_REQUIRED", res.Error);
    }

    [Fact]
    public async Task Get_ReturnsError_WhenHandlerThrows()
    {
        var cap = new LocationCapability(NullLogger.Instance);
        cap.GetRequested += (args) => throw new InvalidOperationException("GPS unavailable");

        var req = new NodeInvokeRequest { Id = "loc6", Command = "location.get", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("GPS unavailable", res.Error);
    }

    [Fact]
    public void LocationResult_Serialization()
    {
        var result = new LocationResult
        {
            Latitude = 48.8566,
            Longitude = 2.3522,
            AccuracyMeters = 10.0,
            TimestampMs = 1700000000000
        };

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<LocationResult>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(result.Latitude, deserialized!.Latitude);
        Assert.Equal(result.Longitude, deserialized.Longitude);
        Assert.Equal(result.AccuracyMeters, deserialized.AccuracyMeters);
        Assert.Equal(result.TimestampMs, deserialized.TimestampMs);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_ForUnknownCommand()
    {
        var cap = new LocationCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "loc7", Command = "location.watch", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Unknown command", res.Error);
    }
}
