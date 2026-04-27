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
    public async Task Navigate_RaisesEvent_WhenUrlPresent()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        string? navigatedUrl = null;
        cap.NavigateRequested += (s, url) => navigatedUrl = url;

        var req = new NodeInvokeRequest
        {
            Id = "c12",
            Command = "canvas.navigate",
            Args = Parse("""{"url":"https://example.com/page"}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("https://example.com/page", navigatedUrl);
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
}

public class DeviceCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void CanHandle_DeviceCommands()
    {
        var cap = new DeviceCapability(NullLogger.Instance);

        Assert.True(cap.CanHandle("device.info"));
        Assert.True(cap.CanHandle("device.status"));
        Assert.False(cap.CanHandle("device.unknown"));
        Assert.Equal("device", cap.Category);
    }

    [Fact]
    public async Task DeviceInfo_ReturnsMacCompatiblePayloadShape()
    {
        var cap = new DeviceCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "d1", Command = "device.info", Args = Parse("""{}""") };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(res.Payload));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("deviceName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("modelIdentifier").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("systemName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("systemVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("appVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("appBuild").GetString()));
        Assert.NotEqual(JsonValueKind.Undefined, payload.GetProperty("locale").ValueKind);
    }

    [Fact]
    public async Task DeviceStatus_ReturnsMacCompatiblePayloadShape()
    {
        var cap = new DeviceCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "d2", Command = "device.status", Args = Parse("""{}""") };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(res.Payload));
        var battery = payload.GetProperty("battery");
        Assert.Equal("unknown", battery.GetProperty("state").GetString());
        Assert.False(battery.GetProperty("lowPowerModeEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, battery.GetProperty("level").ValueKind);

        Assert.Equal("nominal", payload.GetProperty("thermal").GetProperty("state").GetString());

        var storage = payload.GetProperty("storage");
        Assert.True(storage.GetProperty("totalBytes").GetInt64() >= 0);
        Assert.True(storage.GetProperty("freeBytes").GetInt64() >= 0);
        Assert.True(storage.GetProperty("usedBytes").GetInt64() >= 0);

        var network = payload.GetProperty("network");
        Assert.Contains(network.GetProperty("status").GetString(), new[] { "satisfied", "unsatisfied" });
        Assert.False(network.GetProperty("isExpensive").GetBoolean());
        Assert.False(network.GetProperty("isConstrained").GetBoolean());
        Assert.Equal(JsonValueKind.Array, network.GetProperty("interfaces").ValueKind);

        Assert.True(payload.GetProperty("uptimeSeconds").GetDouble() >= 0);
    }

    [Fact]
    public async Task DeviceUnknownCommand_ReturnsError()
    {
        var cap = new DeviceCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "d3", Command = "device.unknown", Args = Parse("""{}""") };

        var res = await cap.ExecuteAsync(req);

        Assert.False(res.Ok);
        Assert.Contains("Unknown command", res.Error);
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
