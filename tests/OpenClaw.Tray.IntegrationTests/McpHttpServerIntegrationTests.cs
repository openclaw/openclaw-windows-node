using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.Tray.IntegrationTests;

/// <summary>
/// Black-box HTTP integration tests against a spawned tray instance. Each test
/// drives one (or two related) MCP commands. Tests that depend on hardware or
/// UI state (canvas eval/snapshot, camera snap) accept either a successful
/// payload or a well-formed tool error — both prove the wiring is intact.
///
/// Runs only with OPENCLAW_RUN_INTEGRATION=1 on Windows. See IntegrationFactAttribute.
/// </summary>
public class McpHttpServerIntegrationTests : IClassFixture<TrayAppFixture>
{
    private readonly TrayAppFixture _fixture;

    public McpHttpServerIntegrationTests(TrayAppFixture fixture)
    {
        _fixture = fixture;
    }

    // ---- Protocol handshake ----

    [IntegrationFact]
    public async Task Initialize_ReturnsProtocolVersionAndServerInfo()
    {
        using var doc = await _fixture.Client.InitializeAsync();
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        var serverInfo = result.GetProperty("serverInfo");
        Assert.Equal("openclaw-tray-mcp", serverInfo.GetProperty("name").GetString());
    }

    [IntegrationFact]
    public async Task ToolsList_AdvertisesEveryRegisteredCommand()
    {
        using var doc = await _fixture.Client.ListToolsAsync();
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");

        var names = new HashSet<string>(
            tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()!));

        // Every command from every registered capability should be advertised.
        var expected = new[]
        {
            "system.notify", "system.run", "system.run.prepare", "system.which",
            "system.execApprovals.get", "system.execApprovals.set",
            "canvas.present", "canvas.hide", "canvas.navigate", "canvas.eval",
            "canvas.snapshot", "canvas.a2ui.push", "canvas.a2ui.reset",
            "screen.snapshot", "screen.record",
            "camera.list", "camera.snap", "camera.clip",
        };
        foreach (var cmd in expected)
        {
            Assert.True(names.Contains(cmd),
                $"tools/list missing command: {cmd}. Got: {string.Join(", ", names)}");
        }

        // Curated descriptions kicked in (not the generic stub).
        var notify = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "system.notify");
        Assert.Contains("toast notification", notify.GetProperty("description").GetString());
    }

    // ---- system.* ----

    [IntegrationFact]
    public async Task SystemNotify_RaisesToastAndReturnsSent()
    {
        using var payload = await _fixture.Client.CallToolExpectSuccessAsync("system.notify", new
        {
            title = "Integration Test",
            body = "system.notify smoke",
            sound = false,
        });
        Assert.True(payload.RootElement.GetProperty("sent").GetBoolean());
    }

    [IntegrationFact]
    public async Task SystemRun_Echo_ReturnsExpectedOutput()
    {
        // Set up an explicit allow rule so this test does not depend on the
        // default policy (which other tests in the class may have replaced —
        // xUnit does not guarantee execution order). execApprovals.set
        // requires the current policy hash as baseHash to prevent stale writes.
        var baseHash = await GetExecApprovalsHashAsync();
        await _fixture.Client.CallToolExpectSuccessAsync("system.execApprovals.set", new
        {
            baseHash,
            defaultAction = "deny",
            rules = new object[]
            {
                new { pattern = "echo *", action = "allow", description = "test", enabled = true },
            },
        });

        using var payload = await _fixture.Client.CallToolExpectSuccessAsync("system.run", new
        {
            command = new[] { "echo", "hello-from-mcp" },
            timeoutMs = 10_000,
        });
        var stdout = payload.RootElement.GetProperty("stdout").GetString() ?? "";
        Assert.Contains("hello-from-mcp", stdout);
        Assert.Equal(0, payload.RootElement.GetProperty("exitCode").GetInt32());
    }

    private async Task<string> GetExecApprovalsHashAsync()
    {
        using var doc = await _fixture.Client.CallToolExpectSuccessAsync("system.execApprovals.get");
        return doc.RootElement.GetProperty("hash").GetString()
            ?? throw new InvalidOperationException("system.execApprovals.get did not return a hash");
    }

    [IntegrationFact]
    public async Task SystemRunPrepare_ReturnsParsedPlanWithoutExecuting()
    {
        using var payload = await _fixture.Client.CallToolExpectSuccessAsync("system.run.prepare", new
        {
            command = new[] { "whoami" },
            cwd = _fixture.DataDir,
            agentId = "it-agent",
        });
        var plan = payload.RootElement.GetProperty("plan");
        var argv = plan.GetProperty("argv");
        Assert.Equal("whoami", argv[0].GetString());
        Assert.Equal(_fixture.DataDir, plan.GetProperty("cwd").GetString());
    }

    [IntegrationFact]
    public async Task SystemWhich_ResolvesCmdExe()
    {
        // cmd.exe is on PATH on every Windows install — the most reliable probe.
        using var payload = await _fixture.Client.CallToolExpectSuccessAsync("system.which", new
        {
            bins = new[] { "cmd" },
        });
        var bins = payload.RootElement.GetProperty("bins");
        Assert.True(bins.TryGetProperty("cmd", out var cmdPath));
        Assert.Contains("cmd.exe", cmdPath.GetString(), System.StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationFact]
    public async Task SystemExecApprovals_GetThenSet_PersistsRule()
    {
        using var beforeDoc = await _fixture.Client.CallToolExpectSuccessAsync("system.execApprovals.get");
        Assert.True(beforeDoc.RootElement.GetProperty("enabled").GetBoolean());
        var baseHash = beforeDoc.RootElement.GetProperty("hash").GetString()!;

        // Set a single deterministic rule and verify it round-trips. The
        // server requires baseHash to match the current policy hash so it
        // can refuse stale writes.
        var marker = "integration-marker-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        using var setDoc = await _fixture.Client.CallToolExpectSuccessAsync("system.execApprovals.set", new
        {
            baseHash,
            defaultAction = "deny",
            rules = new object[]
            {
                new { pattern = marker + "*", action = "allow", description = marker, enabled = true },
            },
        });
        Assert.True(setDoc.RootElement.GetProperty("updated").GetBoolean());

        using var afterDoc = await _fixture.Client.CallToolExpectSuccessAsync("system.execApprovals.get");
        var rules = afterDoc.RootElement.GetProperty("rules");
        Assert.Contains(rules.EnumerateArray(),
            r => r.GetProperty("pattern").GetString() == marker + "*");
    }

    // ---- screen.* ----
    // Canonical OpenClaw protocol: screen.snapshot + screen.record only.
    // No screen.list / screen.capture (those were stale drift from the prior
    // bridge description set).

    [IntegrationFact]
    public async Task ScreenSnapshot_ReturnsImageOrWellFormedError()
    {
        // On a real desktop session this returns a base64 image; on a locked
        // session or some VMs it can fail. Either way we want a well-formed
        // response — no transport errors.
        var (isError, text) = await _fixture.Client.CallToolAcceptingFailureAsync("screen.snapshot", new
        {
            format = "png",
            maxWidth = 320,
            quality = 60,
            screenIndex = 0,
            includePointer = false,
        });
        if (!isError)
        {
            using var doc = JsonDocument.Parse(text);
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("base64").GetString()));
            Assert.True(doc.RootElement.GetProperty("width").GetInt32() > 0);
        }
        // Tool error is acceptable; we got a deterministic response either way.
    }

    // ---- camera.* ----

    [IntegrationFact]
    public async Task CameraList_ReturnsArray()
    {
        // Empty array is a valid result on a machine without cameras.
        using var payload = await _fixture.Client.CallToolExpectSuccessAsync("camera.list");
        Assert.True(payload.RootElement.GetProperty("cameras").ValueKind == JsonValueKind.Array);
    }

    [IntegrationFact]
    public async Task CameraSnap_ReturnsImageOrWellFormedError()
    {
        // No camera, blocked permissions, etc. all surface as a tool error.
        var (isError, text) = await _fixture.Client.CallToolAcceptingFailureAsync("camera.snap", new
        {
            format = "jpeg",
            maxWidth = 320,
            quality = 60,
        });
        if (!isError)
        {
            using var doc = JsonDocument.Parse(text);
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("base64").GetString()));
        }
    }

    // ---- canvas.* ----
    //
    // The Canvas commands fire events that the WinUI dispatcher consumes
    // asynchronously. The MCP bridge returns success as soon as the event is
    // raised, so present/hide/navigate/a2ui.* return success deterministically.
    // eval and snapshot await their handlers, which require a live canvas
    // window — those tests accept either success or "Canvas not available".

    [IntegrationFact]
    public async Task CanvasPresent_ReturnsPresented()
    {
        using var payload = await _fixture.Client.CallToolExpectSuccessAsync("canvas.present", new
        {
            html = "<html><body><h1>integration</h1></body></html>",
            width = 400,
            height = 300,
            title = "IT Canvas",
        });
        Assert.True(payload.RootElement.GetProperty("presented").GetBoolean());
    }

    [IntegrationFact]
    public async Task CanvasNavigate_ReturnsNavigated()
    {
        // HttpUrlValidator only accepts http/https. We don't need the page to
        // resolve or launch — the fixture suppresses external browser launches,
        // and the tool returns success as soon as the navigate event is raised.
        using var payload = await _fixture.Client.CallToolExpectSuccessAsync("canvas.navigate", new
        {
            url = "https://example.com/",
        });
        Assert.True(payload.RootElement.GetProperty("navigated").GetBoolean());
    }

    [IntegrationFact]
    public async Task CanvasEval_ReturnsResultOrWellFormedError()
    {
        // Either the canvas was up and we got an eval result, or the bridge
        // returned a well-formed tool error (canvas not available, WebView2
        // not yet initialized, etc.). Both prove the wiring is intact.
        var (_, text) = await _fixture.Client.CallToolAcceptingFailureAsync("canvas.eval", new
        {
            script = "1+1",
        });
        Assert.False(string.IsNullOrEmpty(text));
    }

    [IntegrationFact]
    public async Task CanvasSnapshot_ReturnsImageOrWellFormedError()
    {
        var (_, text) = await _fixture.Client.CallToolAcceptingFailureAsync("canvas.snapshot", new
        {
            format = "png",
            maxWidth = 320,
        });
        Assert.False(string.IsNullOrEmpty(text));
    }

    [IntegrationFact]
    public async Task CanvasA2UIPush_ReturnsPushed()
    {
        // Minimal valid A2UI v0.8 surfaceUpdate + beginRendering. The bridge
        // returns success after raising the event; the dispatcher applies the
        // jsonl asynchronously inside the canvas window.
        var jsonl = string.Join("\n", new[]
        {
            "{\"surfaceUpdate\":{\"surfaceId\":\"main\",\"components\":[{\"id\":\"root\",\"component\":{\"Text\":{\"text\":{\"literalString\":\"hi\"}}}}]}}",
            "{\"beginRendering\":{\"surfaceId\":\"main\",\"root\":\"root\"}}",
        });
        using var payload = await _fixture.Client.CallToolExpectSuccessAsync("canvas.a2ui.push", new
        {
            jsonl,
        });
        Assert.True(payload.RootElement.GetProperty("pushed").GetBoolean());
    }

    [IntegrationFact]
    public async Task CanvasA2UIReset_ReturnsReset()
    {
        using var payload = await _fixture.Client.CallToolExpectSuccessAsync("canvas.a2ui.reset");
        Assert.True(payload.RootElement.GetProperty("reset").GetBoolean());
    }

    [IntegrationFact]
    public async Task CanvasHide_ReturnsHidden()
    {
        using var payload = await _fixture.Client.CallToolExpectSuccessAsync("canvas.hide");
        Assert.True(payload.RootElement.GetProperty("hidden").GetBoolean());
    }
}
