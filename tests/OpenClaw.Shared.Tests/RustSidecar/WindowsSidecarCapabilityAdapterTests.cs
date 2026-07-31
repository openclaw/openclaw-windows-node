using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared.RustSidecar;

namespace OpenClaw.Shared.Tests.RustSidecar;

public sealed class WindowsSidecarCapabilityAdapterTests
{
    [Fact]
    public void ProtocolCodec_ReproducesRustFrameVectorExactly()
    {
        using var fixture = ReadFixture("node-sidecar-protocol-v1.json");
        var session = fixture.RootElement.GetProperty("session");
        var probe = fixture.RootElement.GetProperty("supervisorProbe");
        var key = Convert.FromBase64String(session.GetProperty("sessionKeyBase64").GetString()!);
        using var supervisor = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Supervisor,
            session.GetProperty("id").GetString()!,
            session.GetProperty("generation").GetUInt64(),
            key,
            4096);
        var payload = JsonSerializer.SerializeToUtf8Bytes(probe.GetProperty("payload"));

        var frame = supervisor.Seal(payload);

        Assert.Equal(probe.GetProperty("frameBase64").GetString(), Convert.ToBase64String(frame));
        using var runtime = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Runtime,
            session.GetProperty("id").GetString()!,
            session.GetProperty("generation").GetUInt64(),
            key,
            4096);
        Assert.Equal(payload, runtime.Open(frame));
    }

    [Fact]
    public void ProtocolCodec_AuthenticationFailurePermanentlyRetiresChannel()
    {
        var key = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        using var supervisor = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Supervisor, "session-7", 7, key, 4096);
        using var runtime = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Runtime, "session-7", 7, key, 4096);
        var frame = supervisor.Seal("{}"u8);
        frame[^1] ^= 1;

        Assert.Throws<SidecarProtocolException>(() => runtime.Open(frame));
        Assert.True(runtime.IsRetired);
        Assert.Throws<SidecarProtocolException>(() => runtime.Open(frame));
        Assert.Throws<SidecarProtocolException>(() => runtime.Seal("{}"u8));
    }

    [Fact]
    public void Handshake_ReproducesRustOfferAndAcceptVectorsExactly()
    {
        using var fixture = ReadFixture("node-sidecar-handshake-v1.json");
        var root = fixture.RootElement;
        var session = root.GetProperty("session");
        using var channel = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Supervisor,
            session.GetProperty("id").GetString()!,
            session.GetProperty("generation").GetUInt64(),
            Convert.FromBase64String(session.GetProperty("keyBase64").GetString()!),
            4096);
        var handshake = new SidecarSupervisorHandshake(
            channel,
            ParseOffer(root.GetProperty("supervisorOffer")));

        Assert.Equal(root.GetProperty("offerFrameBase64").GetString(), Convert.ToBase64String(handshake.Start()));
        handshake.Accept(Convert.FromBase64String(root.GetProperty("acceptFrameBase64").GetString()!));

        Assert.True(handshake.IsAuthenticated);
        Assert.Equal(2048u, channel.MaxFrameBytes);
        Assert.Equal(3ul, handshake.Selection!.FeatureBits);
    }

    [Fact]
    public void Handshake_RejectsUnsupportedLocalOfferBeforeSending()
    {
        using var fixture = ReadFixture("node-sidecar-handshake-v1.json");
        var root = fixture.RootElement;
        var session = root.GetProperty("session");
        using var channel = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Supervisor,
            session.GetProperty("id").GetString()!,
            session.GetProperty("generation").GetUInt64(),
            Convert.FromBase64String(session.GetProperty("keyBase64").GetString()!),
            4096);
        var offer = ParseOffer(root.GetProperty("supervisorOffer")) with { ProtocolMajor = 2 };

        Assert.Throws<SidecarProtocolException>(() => new SidecarSupervisorHandshake(channel, offer));
        Assert.True(channel.IsRetired);
    }

    [Fact]
    public void Handshake_RejectsRuntimeChannelBeforeSending()
    {
        using var fixture = ReadFixture("node-sidecar-handshake-v1.json");
        var root = fixture.RootElement;
        var session = root.GetProperty("session");
        using var channel = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Runtime,
            session.GetProperty("id").GetString()!,
            session.GetProperty("generation").GetUInt64(),
            Convert.FromBase64String(session.GetProperty("keyBase64").GetString()!),
            4096);

        Assert.Throws<SidecarProtocolException>(() =>
            new SidecarSupervisorHandshake(channel, ParseOffer(root.GetProperty("supervisorOffer"))));
        Assert.True(channel.IsRetired);
    }

    [Fact]
    public void Handshake_RejectsUnknownFieldsAtEveryAcceptanceLevel()
    {
        using var fixture = ReadFixture("node-sidecar-handshake-v1.json");
        var root = fixture.RootElement;
        var session = root.GetProperty("session");
        var key = Convert.FromBase64String(session.GetProperty("keyBase64").GetString()!);
        foreach (var target in new[]
        {
            "message", "offer", "peer", "offerLimits", "selection", "selectionLimits"
        })
        {
            using var supervisorChannel = new AuthenticatedSidecarChannel(
                SidecarPeerRole.Supervisor,
                session.GetProperty("id").GetString()!,
                session.GetProperty("generation").GetUInt64(),
                key,
                4096);
            using var runtimeChannel = new AuthenticatedSidecarChannel(
                SidecarPeerRole.Runtime,
                session.GetProperty("id").GetString()!,
                session.GetProperty("generation").GetUInt64(),
                key,
                4096);
            var handshake = new SidecarSupervisorHandshake(
                supervisorChannel,
                ParseOffer(root.GetProperty("supervisorOffer")));
            _ = handshake.Start();
            var acceptance = new JsonObject
            {
                ["type"] = "accept",
                ["offer"] = JsonNode.Parse(root.GetProperty("runtimeOffer").GetRawText()),
                ["selection"] = JsonNode.Parse(root.GetProperty("selection").GetRawText())
            };
            var parent = target switch
            {
                "message" => acceptance,
                "offer" => acceptance["offer"]!.AsObject(),
                "peer" => acceptance["offer"]!["peer"]!.AsObject(),
                "offerLimits" => acceptance["offer"]!["limits"]!.AsObject(),
                "selection" => acceptance["selection"]!.AsObject(),
                _ => acceptance["selection"]!["limits"]!.AsObject()
            };
            parent["unexpected"] = "secret-bearing-extension";

            Assert.Throws<SidecarProtocolException>(() =>
                handshake.Accept(runtimeChannel.Seal(SidecarJson.Serialize(acceptance))));
            Assert.True(supervisorChannel.IsRetired);
        }
    }

    [Fact]
    public void RuntimeCorpus_RoundTripsEveryCanonicalRustMessageExactly()
    {
        using var fixture = ReadFixture("node-sidecar-runtime-v1.json");
        foreach (var canonical in fixture.RootElement.GetProperty("canonicalJson").EnumerateArray())
        {
            var json = canonical.GetString()!;
            var parsed = SidecarJson.Parse(Encoding.UTF8.GetBytes(json));
            Assert.Equal(json, Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(parsed)));
        }
    }

    [Fact]
    public async Task Adapter_UsesExactConfigurationAndRoutesInvocationThroughDispatcher()
    {
        using var fixture = ReadFixture("node-sidecar-runtime-v1.json");
        var canonical = fixture.RootElement.GetProperty("canonicalJson");
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse
            {
                Ok = true,
                Payload = new { ready = true }
            })));
        adapter.RegisterCapability(new TestCapability(
            "native.settings",
            "product.settings",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true })));
        var selection = new SidecarProtocolSelection(
            1, 0, 3, new SidecarLimits(2048, 2, 1000));

        var configure = adapter.BeginConfiguration(
            3,
            selection,
            maxInputBytes: 1024,
            maxOutputBytes: 1024,
            defaultTimeoutMs: 1000,
            maxTimeoutMs: 5000,
            resultGraceMs: 50);
        Assert.Equal(canonical[0].GetString(), Encoding.UTF8.GetString(SidecarJson.Serialize(configure)));
        adapter.ConfirmConfigured(ParseCanonical(canonical[1]));

        var admission = await adapter.HandleRuntimeMessageAsync(
            ParseCanonical(canonical[2]),
            CancellationToken.None);
        Assert.Equal(canonical[3].GetString(), Encoding.UTF8.GetString(SidecarJson.Serialize(admission!)));

        var result = await adapter.HandleRuntimeMessageAsync(
            ParseCanonical(canonical[4]),
            CancellationToken.None);
        Assert.Equal(canonical[5].GetString(), Encoding.UTF8.GetString(SidecarJson.Serialize(result!)));
    }

    [Fact]
    public async Task Supervisor_DrivesAuthenticatedFramesIntoWindowsDispatcher()
    {
        using var fixture = ReadFixture("node-sidecar-handshake-v1.json");
        var root = fixture.RootElement;
        var session = root.GetProperty("session");
        var key = Convert.FromBase64String(session.GetProperty("keyBase64").GetString()!);
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse
            {
                Ok = true,
                Payload = new { ready = true }
            })));
        using var supervisor = new WindowsSidecarSupervisor(
            session.GetProperty("id").GetString()!,
            session.GetProperty("generation").GetUInt64(),
            key,
            4096,
            ParseOffer(root.GetProperty("supervisorOffer")),
            adapter,
            manifestGeneration: 3);
        using var runtime = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Runtime,
            session.GetProperty("id").GetString()!,
            session.GetProperty("generation").GetUInt64(),
            key,
            4096);

        _ = runtime.Open(supervisor.Start());
        var accept = new JsonObject
        {
            ["type"] = "accept",
            ["offer"] = JsonNode.Parse(root.GetProperty("runtimeOffer").GetRawText()),
            ["selection"] = JsonNode.Parse(root.GetProperty("selection").GetRawText())
        };
        var acceptanceFrame = runtime.Seal(SidecarJson.Serialize(accept));
        runtime.LowerFrameLimit(2048);
        var configurationFrame = supervisor.CompleteHandshake(acceptanceFrame);
        var configuration = SidecarJson.Parse(runtime.Open(configurationFrame));
        var configured = new JsonObject
        {
            ["type"] = "configured",
            ["manifest"] = new JsonObject
            {
                ["manifestGeneration"] = 3,
                ["capabilities"] = new JsonArray("native.status"),
                ["commands"] = new JsonArray("product.status")
            }
        };
        Assert.Equal("configure", configuration.GetProperty("type").GetString());
        await supervisor.ReceiveAsync(
            runtime.Seal(SidecarJson.Serialize(configured)),
            CancellationToken.None);

        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-1","nodeId":"node-1","command":"product.status","params":{"verbose":true},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        await supervisor.ReceiveAsync(
            runtime.Seal(JsonSerializer.SerializeToUtf8Bytes(admission)),
            CancellationToken.None);
        var admissionFrame = await supervisor.ReadOutboundAsync(CancellationToken.None);
        var decision = SidecarJson.Parse(runtime.Open(admissionFrame!));
        Assert.Equal("allow", decision.GetProperty("decision").GetProperty("outcome").GetString());

        var invoke = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-1","nodeId":"node-1","command":"product.status","params":{"verbose":true},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        await supervisor.ReceiveAsync(
            runtime.Seal(JsonSerializer.SerializeToUtf8Bytes(invoke)),
            CancellationToken.None);
        var resultFrame = await supervisor.ReadOutboundAsync(CancellationToken.None);
        var result = SidecarJson.Parse(runtime.Open(resultFrame!));
        Assert.True(result.GetProperty("result").GetProperty("payload").GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Adapter_CancelMessageReachesActiveWindowsCapability()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.blocking",
            "product.blocking",
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            }));
        Configure(adapter);
        var invoke = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-block","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-block","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        var decision = await adapter.HandleRuntimeMessageAsync(admission, CancellationToken.None);
        Assert.Equal("allow", decision!["decision"]!["outcome"]!.GetValue<string>());
        var invocation = adapter.HandleRuntimeMessageAsync(invoke, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var response = await adapter.HandleRuntimeMessageAsync(
            ParseJson("""{"type":"cancel","invocationId":"invoke-block"}"""),
            CancellationToken.None);

        Assert.Null(response);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await invocation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("failure", result!["result"]!["outcome"]!.GetValue<string>());
        Assert.Equal("cancelled", result["result"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Supervisor_ReadsCancelWhileInvocationIsBlocked()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.blocking",
            "product.blocking",
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            }));
        var session = await CreateConfiguredSupervisorAsync(adapter);
        using var supervisor = session.Supervisor;
        using var runtime = session.Runtime;
        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-block","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        await supervisor.ReceiveAsync(
            runtime.Seal(JsonSerializer.SerializeToUtf8Bytes(admission)),
            CancellationToken.None);
        _ = runtime.Open(await supervisor.ReadOutboundAsync(CancellationToken.None));
        var invoke = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-block","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);

        await supervisor.ReceiveAsync(
            runtime.Seal(JsonSerializer.SerializeToUtf8Bytes(invoke)),
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await supervisor.ReceiveAsync(
            runtime.Seal("""{"type":"cancel","invocationId":"invoke-block"}"""u8),
            CancellationToken.None);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = SidecarJson.Parse(runtime.Open(
            await supervisor.ReadOutboundAsync(CancellationToken.None)));
        Assert.Equal("failure", result.GetProperty("result").GetProperty("outcome").GetString());
        Assert.Equal("cancelled", result.GetProperty("result").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Adapter_ReturnsFailureAndReleasesSlotWhenResultCannotSerialize()
    {
        var cyclic = new Dictionary<string, object?>();
        cyclic["self"] = cyclic;
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true, Payload = cyclic })));
        Configure(adapter, maxInFlight: 1);

        var first = ParseJson("""
            {"id":"invoke-1","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, first);
        var result = await InvokeAsync(adapter, first);

        Assert.Equal("RESULT_SERIALIZATION", result["result"]!["code"]!.GetValue<string>());
        var second = ParseJson("""
            {"id":"invoke-2","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}
            """);
        var decision = await AdmitAsync(adapter, second);
        Assert.Equal("allow", decision["decision"]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_BoundsCapabilityErrorBeforeBuildingResultEnvelope()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse
            {
                Ok = false,
                Error = new string('e', 4096)
            })));
        Configure(adapter, maxOutputBytes: 128);
        var invocation = ParseJson("""
            {"id":"invoke-error","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation);

        Assert.Equal("OUTPUT_TOO_LARGE", result["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_CountsJsonEscapingAgainstCapabilityErrorLimit()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse
            {
                Ok = false,
                Error = new string('\\', 100)
            })));
        Configure(adapter, maxOutputBytes: 128);
        var invocation = ParseJson("""
            {"id":"invoke-escaped-error","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation);

        Assert.Equal("OUTPUT_TOO_LARGE", result["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_RejectsNonPortableIntegerCapabilityOutput()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse
            {
                Ok = true,
                Payload = new { unsafeValue = long.MaxValue }
            })));
        Configure(adapter, maxOutputBytes: 1024);
        var invocation = ParseJson("""
            {"id":"invoke-unsafe","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation);

        Assert.Equal(
            "SIDECAR_NON_PORTABLE_JSON",
            result["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_RejectsOversizedParametersBeforeWindowsDispatch()
    {
        var invoked = false;
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(new NodeInvokeResponse { Ok = true });
            }));
        Configure(adapter, maxInputBytes: 16);
        var invocation = ParseJson($$"""
            {"id":"invoke-large-input","nodeId":"node-1","command":"product.status","params":{"data":"{{new string('x', 100)}}"},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}
            """);

        var admission = await AdmitAsync(adapter, invocation);

        Assert.Equal(
            "INPUT_TOO_LARGE",
            admission["decision"]!["code"]!.GetValue<string>());
        Assert.False(invoked);
    }

    [Fact]
    public async Task Supervisor_UsesSidecarErrorForOversizedResultEnvelopeWithoutRetiringSession()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse
            {
                Ok = true,
                Payload = new string('x', 1950)
            })));
        var session = await CreateConfiguredSupervisorAsync(adapter);
        using var supervisor = session.Supervisor;
        using var runtime = session.Runtime;
        var invocation = ParseJson("""
            {"id":"invoke-large","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}
            """);
        await supervisor.ReceiveAsync(
            runtime.Seal(SidecarJson.Serialize(new JsonObject
            {
                ["type"] = "admission-request",
                ["invocation"] = JsonNode.Parse(invocation.GetRawText())
            })),
            CancellationToken.None);
        _ = runtime.Open(await supervisor.ReadOutboundAsync(CancellationToken.None));
        await supervisor.ReceiveAsync(
            runtime.Seal(SidecarJson.Serialize(new JsonObject
            {
                ["type"] = "invoke",
                ["invocation"] = JsonNode.Parse(invocation.GetRawText())
            })),
            CancellationToken.None);

        var result = SidecarJson.Parse(runtime.Open(
            await supervisor.ReadOutboundAsync(CancellationToken.None)));
        Assert.Equal(
            "SIDECAR_MESSAGE_TOO_LARGE",
            result.GetProperty("result").GetProperty("code").GetString());
        Assert.False(supervisor.IsRetired);
    }

    [Fact]
    public void Adapter_RejectsInvalidLogicalByteLimitsBeforeConfigurationStarts()
    {
        var selection = new SidecarProtocolSelection(
            1, 0, 0, new SidecarLimits(4096, 8, 1000));
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            adapter.BeginConfiguration(1, selection, maxInputBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            adapter.BeginConfiguration(1, selection, maxOutputBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            adapter.BeginConfiguration(1, selection, maxOutputBytes: 1));
        Assert.False(adapter.IsConfigured);
    }

    [Fact]
    public void Supervisor_RejectsConfigurationWhenWorstCaseStatusCannotFit()
    {
        using var fixture = ReadFixture("node-sidecar-handshake-v1.json");
        var root = fixture.RootElement;
        var session = root.GetProperty("session");
        var key = Convert.FromBase64String(session.GetProperty("keyBase64").GetString()!);
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        using var supervisor = new WindowsSidecarSupervisor(
            session.GetProperty("id").GetString()!,
            session.GetProperty("generation").GetUInt64(),
            key,
            4096,
            ParseOffer(root.GetProperty("supervisorOffer")),
            adapter,
            manifestGeneration: 3);
        using var runtime = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Runtime,
            session.GetProperty("id").GetString()!,
            session.GetProperty("generation").GetUInt64(),
            key,
            4096);
        _ = runtime.Open(supervisor.Start());
        var runtimeOffer = JsonNode.Parse(root.GetProperty("runtimeOffer").GetRawText())!.AsObject();
        runtimeOffer["peer"]!["version"] = new string('v', 900);
        runtimeOffer["limits"]!["maxFrameBytes"] = 1024;
        var selection = JsonNode.Parse(root.GetProperty("selection").GetRawText())!.AsObject();
        selection["limits"]!["maxFrameBytes"] = 1024;
        var acceptance = new JsonObject
        {
            ["type"] = "accept",
            ["offer"] = runtimeOffer,
            ["selection"] = selection
        };

        Assert.Throws<SidecarProtocolException>(() =>
            supervisor.CompleteHandshake(runtime.Seal(SidecarJson.Serialize(acceptance))));
        Assert.True(supervisor.IsRetired);
    }

    [Fact]
    public void Supervisor_RejectsConfigurationWhenStableResultFailureCannotFit()
    {
        using var fixture = ReadFixture("node-sidecar-handshake-v1.json");
        var root = fixture.RootElement;
        var session = root.GetProperty("session");
        var sessionId = session.GetProperty("id").GetString()!;
        var key = Convert.FromBase64String(session.GetProperty("keyBase64").GetString()!);
        var runtimeVersion = root.GetProperty("runtimeOffer").GetProperty("peer")
            .GetProperty("version").GetString()!;
        var statusPayload = SidecarJson.Serialize(new JsonObject
        {
            ["type"] = "status",
            ["status"] = new JsonObject
            {
                ["state"] = "backing-off",
                ["manifestGeneration"] = 3,
                ["runtimeVersion"] = runtimeVersion,
                ["attempt"] = SidecarJson.MaxPortableInteger,
                ["reason"] = "delivery-saturated"
            }
        });
        var stableFailure = SidecarJson.Serialize(
            WindowsSidecarCapabilityAdapter.MessageTooLargeFailure(string.Empty));
        Assert.True(stableFailure.Length > statusPayload.Length);
        var negotiatedFrameBytes = checked((uint)(31 + Encoding.UTF8.GetByteCount(sessionId) + 32 + statusPayload.Length));
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        using var supervisor = new WindowsSidecarSupervisor(
            sessionId,
            session.GetProperty("generation").GetUInt64(),
            key,
            4096,
            ParseOffer(root.GetProperty("supervisorOffer")),
            adapter,
            manifestGeneration: 3);
        using var runtime = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Runtime,
            sessionId,
            session.GetProperty("generation").GetUInt64(),
            key,
            4096);
        _ = runtime.Open(supervisor.Start());
        var runtimeOffer = JsonNode.Parse(root.GetProperty("runtimeOffer").GetRawText())!.AsObject();
        runtimeOffer["limits"]!["maxFrameBytes"] = negotiatedFrameBytes;
        var selection = JsonNode.Parse(root.GetProperty("selection").GetRawText())!.AsObject();
        selection["limits"]!["maxFrameBytes"] = negotiatedFrameBytes;
        var acceptance = new JsonObject
        {
            ["type"] = "accept",
            ["offer"] = runtimeOffer,
            ["selection"] = selection
        };

        Assert.Throws<SidecarProtocolException>(() =>
            supervisor.CompleteHandshake(runtime.Seal(SidecarJson.Serialize(acceptance))));
        Assert.True(supervisor.IsRetired);
    }

    [Fact]
    public void Supervisor_RejectsConfigurationWhenAdmissionDecisionCannotFit()
    {
        using var fixture = ReadFixture("node-sidecar-handshake-v1.json");
        var root = fixture.RootElement;
        var session = root.GetProperty("session");
        var sessionId = session.GetProperty("id").GetString()!;
        var key = Convert.FromBase64String(session.GetProperty("keyBase64").GetString()!);
        var stableFailureBytes = SidecarJson.Serialize(
            WindowsSidecarCapabilityAdapter.MessageTooLargeFailure(string.Empty)).Length;
        var admissionBytes = WindowsSidecarCapabilityAdapter.MaximumAdmissionDecisionBytes(string.Empty);
        Assert.True(admissionBytes > stableFailureBytes);
        var negotiatedPayloadBytes = admissionBytes - 1;
        var negotiatedFrameBytes = checked((uint)(
            31 + Encoding.UTF8.GetByteCount(sessionId) + 32 + negotiatedPayloadBytes));
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        using var supervisor = new WindowsSidecarSupervisor(
            sessionId,
            session.GetProperty("generation").GetUInt64(),
            key,
            4096,
            ParseOffer(root.GetProperty("supervisorOffer")),
            adapter,
            manifestGeneration: 3);
        using var runtime = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Runtime,
            sessionId,
            session.GetProperty("generation").GetUInt64(),
            key,
            4096);
        _ = runtime.Open(supervisor.Start());
        var runtimeOffer = JsonNode.Parse(root.GetProperty("runtimeOffer").GetRawText())!.AsObject();
        runtimeOffer["limits"]!["maxFrameBytes"] = negotiatedFrameBytes;
        var selection = JsonNode.Parse(root.GetProperty("selection").GetRawText())!.AsObject();
        selection["limits"]!["maxFrameBytes"] = negotiatedFrameBytes;
        var acceptance = new JsonObject
        {
            ["type"] = "accept",
            ["offer"] = runtimeOffer,
            ["selection"] = selection
        };

        Assert.Throws<SidecarProtocolException>(() =>
            supervisor.CompleteHandshake(runtime.Seal(SidecarJson.Serialize(acceptance))));
        Assert.True(supervisor.IsRetired);
    }

    [Fact]
    public async Task Supervisor_DiscardsBufferedFramesOnTerminalAuthenticationFailure()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true })));
        var session = await CreateConfiguredSupervisorAsync(adapter);
        using var supervisor = session.Supervisor;
        using var runtime = session.Runtime;
        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-buffered","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        await supervisor.ReceiveAsync(
            runtime.Seal(JsonSerializer.SerializeToUtf8Bytes(admission)),
            CancellationToken.None);
        var invalid = runtime.Seal("""{"type":"cancel","invocationId":"invoke-buffered"}"""u8);
        invalid[^1] ^= 1;

        await Assert.ThrowsAsync<SidecarProtocolException>(() =>
            supervisor.ReceiveAsync(invalid, CancellationToken.None));
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(async () =>
            await supervisor.ReadOutboundAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Adapter_RejectsRuntimeTrafficBeforeConfiguration()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        var message = ParseJson("""{"type":"cancel","invocationId":"invoke-1"}""");

        await Assert.ThrowsAsync<SidecarProtocolException>(
            () => adapter.HandleRuntimeMessageAsync(message, CancellationToken.None));
    }

    [Fact]
    public async Task Adapter_RequiresAnUnchangedAdmissionBeforeDispatch()
    {
        var executions = 0;
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) =>
            {
                executions++;
                return Task.FromResult(new NodeInvokeResponse { Ok = true });
            }));
        Configure(adapter);
        var notAdmitted = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-1","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        var missing = await adapter.HandleRuntimeMessageAsync(notAdmitted, CancellationToken.None);
        Assert.Equal("ADMISSION_REQUIRED", missing!["result"]!["code"]!.GetValue<string>());

        var admitted = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-2","nodeId":"node-1","command":"product.status","params":{"value":1},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        await adapter.HandleRuntimeMessageAsync(admitted, CancellationToken.None);
        var changed = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-2","nodeId":"node-1","command":"product.status","params":{"value":2},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        var mismatch = await adapter.HandleRuntimeMessageAsync(changed, CancellationToken.None);

        Assert.Equal("ADMISSION_MISMATCH", mismatch!["result"]!["code"]!.GetValue<string>());
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task Adapter_RejectsNonPortableInvocationTimeout()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        Configure(adapter);
        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-unsafe-timeout","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":9007199254740992,"idempotencyKey":null,"sessionKey":null}}
            """);

        await Assert.ThrowsAsync<SidecarProtocolException>(
            () => adapter.HandleRuntimeMessageAsync(admission, CancellationToken.None));
    }

    [Fact]
    public async Task Adapter_ReleasesAdmissionWhenInvocationChangesToOversizedInput()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true })));
        Configure(adapter, maxInFlight: 1, maxInputBytes: 32);
        var admitted = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-changed","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        var changed = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-changed","nodeId":"node-1","command":"product.status","params":{"data":"__DATA__"},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """.Replace("__DATA__", new string('x', 100), StringComparison.Ordinal));
        var next = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-next","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);

        _ = await adapter.HandleRuntimeMessageAsync(admitted, CancellationToken.None);
        var mismatch = await adapter.HandleRuntimeMessageAsync(changed, CancellationToken.None);
        var nextDecision = await adapter.HandleRuntimeMessageAsync(next, CancellationToken.None);

        Assert.Equal("ADMISSION_MISMATCH", mismatch!["result"]!["code"]!.GetValue<string>());
        Assert.Equal("allow", nextDecision!["decision"]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_HoldsAdmissionSlotAndIdUntilInvocationIsTerminal()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.blocking",
            "product.blocking",
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new NodeInvokeResponse { Ok = true };
            }));
        Configure(adapter, maxInFlight: 1);
        var firstAdmission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-1","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        await adapter.HandleRuntimeMessageAsync(firstAdmission, CancellationToken.None);
        var firstInvoke = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-1","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        var active = adapter.HandleRuntimeMessageAsync(firstInvoke, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondAdmission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-2","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        var saturated = await adapter.HandleRuntimeMessageAsync(secondAdmission, CancellationToken.None);
        var duplicate = await adapter.HandleRuntimeMessageAsync(firstAdmission, CancellationToken.None);
        Assert.Equal("ADMISSION_SATURATED", saturated!["decision"]!["code"]!.GetValue<string>());
        Assert.Equal("ADMISSION_SATURATED", duplicate!["decision"]!["code"]!.GetValue<string>());

        await adapter.HandleRuntimeMessageAsync(
            ParseJson("""{"type":"cancel","invocationId":"invoke-1"}"""),
            CancellationToken.None);
        _ = await active.WaitAsync(TimeSpan.FromSeconds(5));
        var admittedAfterCompletion = await adapter.HandleRuntimeMessageAsync(
            secondAdmission,
            CancellationToken.None);
        Assert.Equal("allow", admittedAfterCompletion!["decision"]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public void Adapter_RecordsCurrentRustSystemNamespaceGapInsteadOfSelectingIt()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "system",
            "system.run",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true })));

        var error = Assert.Throws<SidecarProtocolException>(() => adapter.BeginConfiguration(
            1,
            new SidecarProtocolSelection(1, 0, 0, new SidecarLimits(4096, 8, 1000))));

        Assert.Contains("does not yet admit the system command namespace", error.Message);
        Assert.False(adapter.IsConfigured);
    }

    private static void Configure(
        WindowsSidecarCapabilityAdapter adapter,
        ushort maxInFlight = 8,
        uint maxInputBytes = 1_048_576,
        uint maxOutputBytes = 1_048_576)
    {
        var configure = adapter.BeginConfiguration(
            1,
            new SidecarProtocolSelection(1, 0, 0, new SidecarLimits(4096, maxInFlight, 1000)),
            maxInputBytes: maxInputBytes,
            maxOutputBytes: maxOutputBytes);
        var configuration = configure["configuration"]!.AsObject();
        var manifest = new JsonObject
        {
            ["manifestGeneration"] = configuration["manifestGeneration"]!.DeepClone(),
            ["capabilities"] = configuration["capabilities"]!.DeepClone(),
            ["commands"] = new JsonArray(
                configuration["commands"]!.AsArray()
                    .Select(command => (JsonNode?)command!["name"]!.DeepClone())
                    .ToArray())
        };
        adapter.ConfirmConfigured(ParseJson(new JsonObject
        {
            ["type"] = "configured",
            ["manifest"] = manifest
        }.ToJsonString()));
    }

    private static async Task<JsonObject> AdmitAsync(
        WindowsSidecarCapabilityAdapter adapter,
        JsonElement invocation) =>
        (await adapter.HandleRuntimeMessageAsync(
            ParseJson(new JsonObject
            {
                ["type"] = "admission-request",
                ["invocation"] = JsonNode.Parse(invocation.GetRawText())
            }.ToJsonString()),
            CancellationToken.None))!;

    private static async Task<JsonObject> InvokeAsync(
        WindowsSidecarCapabilityAdapter adapter,
        JsonElement invocation) =>
        (await adapter.HandleRuntimeMessageAsync(
            ParseJson(new JsonObject
            {
                ["type"] = "invoke",
                ["invocation"] = JsonNode.Parse(invocation.GetRawText())
            }.ToJsonString()),
            CancellationToken.None))!;

    private static async Task<(
        WindowsSidecarSupervisor Supervisor,
        AuthenticatedSidecarChannel Runtime)> CreateConfiguredSupervisorAsync(
        WindowsSidecarCapabilityAdapter adapter)
    {
        using var fixture = ReadFixture("node-sidecar-handshake-v1.json");
        var root = fixture.RootElement;
        var session = root.GetProperty("session");
        var key = Convert.FromBase64String(session.GetProperty("keyBase64").GetString()!);
        var supervisor = new WindowsSidecarSupervisor(
            session.GetProperty("id").GetString()!,
            session.GetProperty("generation").GetUInt64(),
            key,
            4096,
            ParseOffer(root.GetProperty("supervisorOffer")),
            adapter,
            manifestGeneration: 3);
        var runtime = new AuthenticatedSidecarChannel(
            SidecarPeerRole.Runtime,
            session.GetProperty("id").GetString()!,
            session.GetProperty("generation").GetUInt64(),
            key,
            4096);
        try
        {
            _ = runtime.Open(supervisor.Start());
            var accept = new JsonObject
            {
                ["type"] = "accept",
                ["offer"] = JsonNode.Parse(root.GetProperty("runtimeOffer").GetRawText()),
                ["selection"] = JsonNode.Parse(root.GetProperty("selection").GetRawText())
            };
            var acceptanceFrame = runtime.Seal(SidecarJson.Serialize(accept));
            runtime.LowerFrameLimit(2048);
            var configuration = SidecarJson.Parse(
                runtime.Open(supervisor.CompleteHandshake(acceptanceFrame)))
                .GetProperty("configuration");
            var configured = new JsonObject
            {
                ["type"] = "configured",
                ["manifest"] = new JsonObject
                {
                    ["manifestGeneration"] = configuration.GetProperty("manifestGeneration").GetUInt64(),
                    ["capabilities"] = JsonNode.Parse(configuration.GetProperty("capabilities").GetRawText()),
                    ["commands"] = new JsonArray(
                        configuration.GetProperty("commands").EnumerateArray()
                            .Select(command => (JsonNode?)command.GetProperty("name").GetString())
                            .ToArray())
                }
            };
            await supervisor.ReceiveAsync(
                runtime.Seal(SidecarJson.Serialize(configured)),
                CancellationToken.None);
            return (supervisor, runtime);
        }
        catch
        {
            supervisor.Dispose();
            runtime.Dispose();
            throw;
        }
    }

    private static JsonElement ParseCanonical(JsonElement canonical) =>
        ParseJson(canonical.GetString()!);

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonDocument ReadFixture(string name) => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RustSidecar", "Fixtures", name)));

    private static SidecarProtocolOffer ParseOffer(JsonElement offer)
    {
        var peer = offer.GetProperty("peer");
        var limits = offer.GetProperty("limits");
        return new SidecarProtocolOffer(
            offer.GetProperty("protocolMajor").GetUInt16(),
            offer.GetProperty("protocolMinor").GetUInt16(),
            new SidecarPeerIdentity(
                peer.GetProperty("role").GetString() == "supervisor"
                    ? SidecarPeerRole.Supervisor
                    : SidecarPeerRole.Runtime,
                peer.GetProperty("name").GetString()!,
                peer.GetProperty("version").GetString()!,
                peer.GetProperty("artifactIdentity").GetString()!),
            offer.GetProperty("featureBits").GetUInt64(),
            new SidecarLimits(
                limits.GetProperty("maxFrameBytes").GetUInt32(),
                limits.GetProperty("maxInFlight").GetUInt16(),
                limits.GetProperty("bootstrapTimeoutMs").GetUInt32()));
    }

    private sealed class TestCapability(
        string category,
        string command,
        Func<NodeInvokeRequest, CancellationToken, Task<NodeInvokeResponse>> execute)
        : INodeCapability
    {
        public string Category => category;
        public IReadOnlyList<string> Commands => [command];
        public bool CanHandle(string value) => string.Equals(value, command, StringComparison.Ordinal);
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            execute(request, CancellationToken.None);
        public Task<NodeInvokeResponse> ExecuteAsync(
            NodeInvokeRequest request,
            CancellationToken cancellationToken) => execute(request, cancellationToken);
    }

    private sealed class TestLogger : IOpenClawLogger
    {
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
