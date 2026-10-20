using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Mcp;
using OpenClawTray.Services;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the optional-capability gating that drives both the gateway client
/// path and the MCP-only path inside <c>NodeService.RegisterCapabilities</c>.
///
/// Privacy-sensitive defaults must be **off** even when settings are missing.
/// A regression that flips Stt/Tts to default-on would silently advertise
/// stt.transcribe / tts.speak the moment the tray launches with a fresh
/// settings file, with no user opt-in.
/// </summary>
public sealed class NodeCapabilityGatingTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private SettingsManager NewSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-tray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return new SettingsManager(dir);
    }

    [Fact]
    public void NullSettings_DefaultOnCapabilities_AreEnabled()
    {
        // Defensive default: when settings are not yet loaded, we still
        // advertise the non-privacy-sensitive capabilities so the node is
        // usable immediately.
        Assert.True(NodeCapabilityGating.ShouldRegisterCanvas(null));
        Assert.True(NodeCapabilityGating.ShouldRegisterScreen(null));
        Assert.True(NodeCapabilityGating.ShouldRegisterCamera(null));
        Assert.True(NodeCapabilityGating.ShouldRegisterLocation(null));
        Assert.True(NodeCapabilityGating.ShouldRegisterBrowserProxy(null));
        Assert.True(NodeCapabilityGating.ShouldRegisterSystemRun(null));
    }

    [Fact]
    public void NullSettings_PrivacySensitiveCapabilities_AreDisabled()
    {
        // Privacy invariant: TTS and STT must require an explicit user
        // opt-in. A null/missing settings object must not enable mic capture
        // or speaker output.
        Assert.False(NodeCapabilityGating.ShouldRegisterTts(null));
        Assert.False(NodeCapabilityGating.ShouldRegisterStt(null));
    }

    [Fact]
    public void DefaultSettings_PrivacySensitiveCapabilities_AreDisabled()
    {
        var s = NewSettings();
        Assert.False(NodeCapabilityGating.ShouldRegisterTts(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterStt(s));
    }

    [Fact]
    public void DefaultSettings_OtherCapabilities_AreEnabled()
    {
        var s = NewSettings();
        Assert.True(NodeCapabilityGating.ShouldRegisterCanvas(s));
        Assert.True(NodeCapabilityGating.ShouldRegisterScreen(s));
        Assert.True(NodeCapabilityGating.ShouldRegisterCamera(s));
        Assert.True(NodeCapabilityGating.ShouldRegisterLocation(s));
        Assert.True(NodeCapabilityGating.ShouldRegisterBrowserProxy(s));
        Assert.True(NodeCapabilityGating.ShouldRegisterSystemRun(s));
    }

    [Fact]
    public void BrowserProxyGatewayRegistration_RequiresGatewayClientAndSharedToken()
    {
        var s = NewSettings();

        Assert.False(NodeCapabilityGating.ShouldRegisterBrowserProxy(s, sharedGatewayToken: null, hasGatewayClient: true));
        Assert.False(NodeCapabilityGating.ShouldRegisterBrowserProxy(s, sharedGatewayToken: "   ", hasGatewayClient: true));
        Assert.False(NodeCapabilityGating.ShouldRegisterBrowserProxy(s, sharedGatewayToken: "shared-token", hasGatewayClient: false));
        Assert.True(NodeCapabilityGating.ShouldRegisterBrowserProxy(s, sharedGatewayToken: "shared-token", hasGatewayClient: true));
        Assert.False(NodeCapabilityGating.ShouldRegisterBrowserProxy(
            s,
            sharedGatewayToken: "shared-token",
            hasGatewayClient: true,
            browserEndpointVerified: false));
        Assert.Equal(
            BrowserProxyActivation.RegistrationBlock.UnverifiedBrowserEndpoint,
            NodeCapabilityGating.ResolveBrowserProxyRegistrationBlock(
                s,
                sharedGatewayToken: "shared-token",
                hasGatewayClient: true,
                browserEndpointVerified: false));
    }

    [Fact]
    public void BrowserProxyGatewayRegistration_RespectsUserToggle()
    {
        var s = NewSettings();
        s.NodeBrowserProxyEnabled = false;

        Assert.False(NodeCapabilityGating.ShouldRegisterBrowserProxy(s, sharedGatewayToken: "shared-token", hasGatewayClient: true));
    }

    [Fact]
    public void SystemRun_OnlyDisabledWhenExplicitlySetToFalse()
    {
        var s = NewSettings();
        Assert.True(NodeCapabilityGating.ShouldRegisterSystemRun(s));
        s.NodeSystemRunEnabled = false;
        Assert.False(NodeCapabilityGating.ShouldRegisterSystemRun(s));
        s.NodeSystemRunEnabled = true;
        Assert.True(NodeCapabilityGating.ShouldRegisterSystemRun(s));
    }

    [Fact]
    public async Task SystemRun_RevocationRemovesMcpToolsWithoutRebuildingCapability()
    {
        var settings = NewSettings();
        var capability = new SystemCapability(
            NullLogger.Instance,
            includeRunCommands: () => NodeCapabilityGating.ShouldRegisterSystemRun(settings));
        var bridge = new McpToolBridge(() => new INodeCapability[] { capability });
        Assert.Contains("system.run", capability.Commands);

        settings.NodeSystemRunEnabled = false;
        settings.Save();

        var response = await bridge.HandleRequestAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        using var document = JsonDocument.Parse(response!);
        var names = document.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.DoesNotContain("system.run", names);
        Assert.DoesNotContain("system.run.prepare", names);
        Assert.Contains("system.which", names);

        settings.NodeSystemRunEnabled = true;
        settings.Save();
        Assert.Contains("system.run", capability.Commands);
        Assert.Contains("system.run.prepare", capability.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemRun_EnableIsNotGrantedUntilPersistenceSucceeds(bool conflictingWriter)
    {
        var settings = NewSettings();
        settings.UpdateAndSave(() => settings.NodeSystemRunEnabled = false);
        var capability = new SystemCapability(
            NullLogger.Instance,
            includeRunCommands: () => NodeCapabilityGating.ShouldRegisterSystemRun(settings));
        if (conflictingWriter)
        {
            var other = new SettingsManager(settings.SettingsDirectory);
            other.NotificationSound = "another writer";
            other.SaveOrThrow();
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = Task.Run(() => Record.Exception(() => settings.UpdateAndSave(() =>
        {
            settings.NodeSystemRunEnabled = true;
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        })));
        await Task.WhenAny(entered.Task, saving);
        try
        {
            Assert.True(entered.Task.IsCompleted);
            Assert.DoesNotContain("system.run", capability.Commands);
            var response = await capability.ExecuteAsync(new NodeInvokeRequest
            {
                Id = "pending-enable",
                Command = "system.run.prepare",
                Args = JsonSerializer.SerializeToElement(new { command = new[] { "cmd.exe", "/c", "echo canary" } })
            });
            Assert.False(response.Ok);
        }
        finally
        {
            release.SetResult();
            await saving;
        }

        if (conflictingWriter)
        {
            Assert.IsType<SettingsPersistenceConflictException>(await saving);
            Assert.DoesNotContain("system.run", capability.Commands);
        }
        else
        {
            Assert.Null(await saving);
            Assert.Contains("system.run", capability.Commands);
        }
    }

    [Fact]
    public async Task SystemRun_RevocationRejectsCachedCapabilityWithoutRebuilding()
    {
        var settings = NewSettings();
        var capability = new SystemCapability(
            NullLogger.Instance,
            includeRunCommands: () => NodeCapabilityGating.ShouldRegisterSystemRun(settings));
        var request = new NodeInvokeRequest
        {
            Id = "revocation-prepare",
            Command = "system.run.prepare",
            Args = JsonSerializer.SerializeToElement(new { command = new[] { "cmd.exe", "/c", "echo canary" } })
        };
        Assert.True((await capability.ExecuteAsync(request)).Ok);

        settings.NodeSystemRunEnabled = false;
        settings.Save();

        var response = await capability.ExecuteAsync(request);
        Assert.False(response.Ok);
        Assert.Contains("disabled", response.Error, StringComparison.OrdinalIgnoreCase);

        var bridge = new McpToolBridge(() => new INodeCapability[] { capability });
        var mcpResponse = await bridge.HandleRequestAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"system.run.prepare","arguments":{"command":["cmd.exe","/c","echo canary"]}}}""");
        using var document = JsonDocument.Parse(mcpResponse!);
        Assert.True(document.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public void Tts_OnlyAdvertisedWhenExplicitlyEnabled()
    {
        var s = NewSettings();
        Assert.False(NodeCapabilityGating.ShouldRegisterTts(s));
        s.NodeTtsEnabled = true;
        Assert.True(NodeCapabilityGating.ShouldRegisterTts(s));
        s.NodeTtsEnabled = false;
        Assert.False(NodeCapabilityGating.ShouldRegisterTts(s));
    }

    [Fact]
    public void Stt_OnlyAdvertisedWhenExplicitlyEnabled()
    {
        var s = NewSettings();
        Assert.False(NodeCapabilityGating.ShouldRegisterStt(s));
        s.NodeSttEnabled = true;
        Assert.True(NodeCapabilityGating.ShouldRegisterStt(s));
        s.NodeSttEnabled = false;
        Assert.False(NodeCapabilityGating.ShouldRegisterStt(s));
    }

    [Fact]
    public void TtsAndStt_Independent()
    {
        // A user who enables only TTS (output) must not silently enable STT
        // (input), and vice versa. Each capability is its own consent surface.
        var s = NewSettings();
        s.NodeTtsEnabled = true;
        s.NodeSttEnabled = false;
        Assert.True(NodeCapabilityGating.ShouldRegisterTts(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterStt(s));

        s.NodeTtsEnabled = false;
        s.NodeSttEnabled = true;
        Assert.False(NodeCapabilityGating.ShouldRegisterTts(s));
        Assert.True(NodeCapabilityGating.ShouldRegisterStt(s));
    }

    [Fact]
    public void Ollama_OnlyAdvertisedWhenExplicitlyEnabled()
    {
        var s = NewSettings();
        Assert.False(NodeCapabilityGating.ShouldRegisterOllama(s));
        s.NodeOllamaInferenceEnabled = true;
        Assert.True(NodeCapabilityGating.ShouldRegisterOllama(s));
        s.NodeOllamaInferenceEnabled = false;
        Assert.False(NodeCapabilityGating.ShouldRegisterOllama(s));
    }

    [Fact]
    public void Ollama_NullSettings_IsFalse()
    {
        Assert.False(NodeCapabilityGating.ShouldRegisterOllama(null));
    }

    [Fact]
    public void DefaultOnCapabilities_OnlyDisabledWhenExplicitlySetToFalse()
    {
        var s = NewSettings();
        s.NodeCanvasEnabled = false;
        s.NodeScreenEnabled = false;
        s.NodeCameraEnabled = false;
        s.NodeLocationEnabled = false;
        s.NodeBrowserProxyEnabled = false;
        s.NodeSystemRunEnabled = false;

        Assert.False(NodeCapabilityGating.ShouldRegisterCanvas(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterScreen(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterCamera(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterLocation(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterBrowserProxy(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterSystemRun(s));
    }

    // ── CountMcpServedCapabilities ────────────────────────────────────────────

    [Fact]
    public void CountMcpServed_Defaults_AreSixCapabilities()
    {
        var s = NewSettings();
        Assert.Equal(6, NodeCapabilityGating.CountMcpServedCapabilities(s));
    }

    [Fact]
    public void CountMcpServed_NullSettings_AreSix()
    {
        Assert.Equal(6, NodeCapabilityGating.CountMcpServedCapabilities(null));
    }

    [Fact]
    public void CountMcpServed_ExcludesBrowserProxy()
    {
        var s = NewSettings();
        var before = NodeCapabilityGating.CountMcpServedCapabilities(s);
        s.NodeBrowserProxyEnabled = false;
        Assert.Equal(before, NodeCapabilityGating.CountMcpServedCapabilities(s));
    }

    [Fact]
    public void CountMcpServed_SystemAndDeviceAlwaysCounted_EvenWhenSystemRunDisabled()
    {
        var s = NewSettings();
        s.NodeCanvasEnabled = false;
        s.NodeScreenEnabled = false;
        s.NodeCameraEnabled = false;
        s.NodeLocationEnabled = false;
        s.NodeBrowserProxyEnabled = false;
        s.NodeSystemRunEnabled = false;
        s.NodeTtsEnabled = false;
        s.NodeSttEnabled = false;
        Assert.Equal(2, NodeCapabilityGating.CountMcpServedCapabilities(s));
    }

    [Fact]
    public void CountMcpServed_OptInCapabilities_IncrementCount()
    {
        var s = NewSettings();
        var baseline = NodeCapabilityGating.CountMcpServedCapabilities(s);
        s.NodeTtsEnabled = true;
        s.NodeSttEnabled = true;
        Assert.Equal(baseline + 2, NodeCapabilityGating.CountMcpServedCapabilities(s));
    }

    [Fact]
    public void CountMcpServed_OllamaOptIn_IncrementsCount()
    {
        var s = NewSettings();
        var baseline = NodeCapabilityGating.CountMcpServedCapabilities(s);
        s.NodeOllamaInferenceEnabled = true;
        Assert.Equal(baseline + 1, NodeCapabilityGating.CountMcpServedCapabilities(s));
        s.NodeOllamaInferenceEnabled = false;
        Assert.Equal(baseline, NodeCapabilityGating.CountMcpServedCapabilities(s));
    }

    [Fact]
    public void McpOnlyRestart_CameraDisabledWhileStoppedUsesFreshCompleteSet()
    {
        var s = NewSettings();
        Assert.True(NodeCapabilityGating.ShouldRegisterCamera(s));
        Assert.Equal(6, NodeCapabilityGating.CountMcpServedCapabilities(s));

        s.NodeCameraEnabled = false;
        var plan = McpRuntimeStatePolicy.PlanCapabilityEnable(
            hasGatewayClient: false,
            hasCapabilities: true);

        Assert.Equal(McpCapabilityEnablePlan.RebuildFromCurrentSettings, plan);
        Assert.False(NodeCapabilityGating.ShouldRegisterCamera(s));
        Assert.Equal(5, NodeCapabilityGating.CountMcpServedCapabilities(s));
    }

    [Fact]
    public void McpOnlyRestart_CameraRestoredWhileStoppedUsesFreshCompleteSet()
    {
        var s = NewSettings();
        s.NodeCameraEnabled = false;
        Assert.Equal(5, NodeCapabilityGating.CountMcpServedCapabilities(s));

        s.NodeCameraEnabled = true;
        var plan = McpRuntimeStatePolicy.PlanCapabilityEnable(
            hasGatewayClient: false,
            hasCapabilities: true);

        Assert.Equal(McpCapabilityEnablePlan.RebuildFromCurrentSettings, plan);
        Assert.True(NodeCapabilityGating.ShouldRegisterCamera(s));
        Assert.Equal(6, NodeCapabilityGating.CountMcpServedCapabilities(s));
    }

    // ── GetLocalNodeCapabilities ──────────────────────────────────────────────

    [Fact]
    public void GetLocalNodeCapabilities_NullNodes_ReturnsNull()
    {
        Assert.Null(NodeCapabilityGating.GetLocalNodeCapabilities(null, "device-1"));
    }

    [Fact]
    public void GetLocalNodeCapabilities_EmptyNodes_ReturnsNull()
    {
        Assert.Null(NodeCapabilityGating.GetLocalNodeCapabilities([], "device-1"));
    }

    [Fact]
    public void GetLocalNodeCapabilities_NullDeviceId_ReturnsNull()
    {
        var nodes = new[] { new GatewayNodeInfo { NodeId = "device-1" } };
        Assert.Null(NodeCapabilityGating.GetLocalNodeCapabilities(nodes, null));
    }

    [Fact]
    public void GetLocalNodeCapabilities_EmptyDeviceId_ReturnsNull()
    {
        var nodes = new[] { new GatewayNodeInfo { NodeId = "device-1" } };
        Assert.Null(NodeCapabilityGating.GetLocalNodeCapabilities(nodes, ""));
    }

    [Fact]
    public void GetLocalNodeCapabilities_NoMatchingNode_ReturnsNull()
    {
        var nodes = new[]
        {
            new GatewayNodeInfo { NodeId = "device-1", Capabilities = ["canvas"] },
            new GatewayNodeInfo { NodeId = "device-2", Capabilities = ["screen"] },
        };
        Assert.Null(NodeCapabilityGating.GetLocalNodeCapabilities(nodes, "device-99"));
    }

    [Fact]
    public void GetLocalNodeCapabilities_MatchingNode_ReturnsCapabilities()
    {
        var nodes = new[]
        {
            new GatewayNodeInfo { NodeId = "device-1", Capabilities = ["canvas", "screen"] },
            new GatewayNodeInfo { NodeId = "device-2", Capabilities = ["location"] },
        };
        var result = NodeCapabilityGating.GetLocalNodeCapabilities(nodes, "device-1");
        Assert.NotNull(result);
        Assert.Equal(new[] { "canvas", "screen" }, result);
    }

    [Fact]
    public void GetLocalNodeCapabilities_MatchingNodeCaseInsensitive_ReturnsCapabilities()
    {
        var nodes = new[]
        {
            new GatewayNodeInfo { NodeId = "Device-ABC", Capabilities = ["canvas"] },
        };
        var result = NodeCapabilityGating.GetLocalNodeCapabilities(nodes, "device-abc");
        Assert.NotNull(result);
        Assert.Equal(new[] { "canvas" }, result);
    }

    [Fact]
    public void GetLocalNodeCapabilities_NodeWithNoCapabilities_ReturnsEmptyList()
    {
        var nodes = new[]
        {
            new GatewayNodeInfo { NodeId = "device-1", Capabilities = [] },
        };
        var result = NodeCapabilityGating.GetLocalNodeCapabilities(nodes, "device-1");
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetLocalNodeInfo_PreservesEffectiveAndPendingApprovalSurfaces()
    {
        var expected = new GatewayNodeInfo
        {
            NodeId = "device-1",
            ApprovalState = GatewayNodeApprovalState.PendingReapproval,
            PendingRequestId = "request-123",
            Capabilities = ["system"],
            Commands = ["system.notify"],
            PendingDeclaredCapabilities = ["system", "camera"],
            PendingDeclaredCommands = ["system.notify", "camera.snap"]
        };

        var result = NodeCapabilityGating.GetLocalNodeInfo([expected], "DEVICE-1");

        Assert.Same(expected, result);
        Assert.Equal(["system"], result!.Capabilities);
        Assert.Equal(["system", "camera"], result.PendingDeclaredCapabilities);
    }
}
