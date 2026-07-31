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
    public void SidecarJson_UsesRustCompatibleDepthLimit()
    {
        var supported = Encoding.UTF8.GetBytes(
            new string('[', SidecarJson.MaxDepth) + "0" + new string(']', SidecarJson.MaxDepth));
        var tooDeep = Encoding.UTF8.GetBytes(
            new string('[', SidecarJson.MaxDepth + 1) + "0" + new string(']', SidecarJson.MaxDepth + 1));

        var parsed = SidecarJson.Parse(supported);

        Assert.Equal(
            supported,
            JsonSerializer.SerializeToUtf8Bytes(parsed, SidecarJson.SerializerOptions));
        Assert.ThrowsAny<JsonException>(() => SidecarJson.Parse(tooDeep));
    }

    [Fact]
    public void SidecarJson_EmitsAllValidUnicodeScalarsLikeSerdeJson()
    {
        const string scalars = "\u00a0\u2028\u2029\u3000😀";
        var encoded = Encoding.UTF8.GetString(SidecarJson.Serialize(new JsonObject
        {
            ["value"] = scalars
        }));

        Assert.Equal($$"""{"value":"{{scalars}}"}""", encoded);
    }

    [Fact]
    public void Adapter_AcceptsReorderedConfiguredManifest()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true })));
        _ = adapter.BeginConfiguration(
            1,
            new SidecarProtocolSelection(1, 0, 0, new SidecarLimits(4096, 8, 1000)));
        var configured = ParseJson("""
            {"type":"configured","manifest":{"commands":["product.status"],"manifestGeneration":1,"capabilities":["native.status"]}}
            """);

        adapter.ConfirmConfigured(configured);

        Assert.True(adapter.IsConfigured);
    }

    [Fact]
    public void Adapter_RejectsCaseInsensitiveWindowsCommandCollisions()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.first",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true })));

        Assert.Throws<SidecarProtocolException>(() => adapter.RegisterCapability(new TestCapability(
            "native.second",
            "PRODUCT.STATUS",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true }))));
        Assert.Single(adapter.Capabilities);
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
    public async Task Supervisor_ReturnsStableFailureWhenResultEnvelopeExceedsDepthLimit()
    {
        var nested = new string('[', 126) + "0" + new string(']', 126);
        var payload = SidecarJson.Parse(Encoding.UTF8.GetBytes(nested));
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true, Payload = payload })));
        var session = await CreateConfiguredSupervisorAsync(adapter);
        using var supervisor = session.Supervisor;
        using var runtime = session.Runtime;
        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-deep-envelope","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        await supervisor.ReceiveAsync(
            runtime.Seal(JsonSerializer.SerializeToUtf8Bytes(admission, SidecarJson.SerializerOptions)),
            CancellationToken.None);
        _ = SidecarJson.Parse(runtime.Open(
            await supervisor.ReadOutboundAsync(CancellationToken.None)));
        var invoke = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-deep-envelope","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);

        await supervisor.ReceiveAsync(
            runtime.Seal(JsonSerializer.SerializeToUtf8Bytes(invoke, SidecarJson.SerializerOptions)),
            CancellationToken.None);
        var resultFrame = await supervisor.ReadOutboundAsync(CancellationToken.None);
        var result = SidecarJson.Parse(runtime.Open(resultFrame));

        Assert.Equal(
            "SIDECAR_MESSAGE_TOO_LARGE",
            result.GetProperty("result").GetProperty("code").GetString());
        Assert.False(supervisor.IsRetired);
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
    public async Task Adapter_CountsFullCapabilityFailurePayloadAgainstOutputLimit()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse
            {
                Ok = false,
                Error = new string('e', 100)
            })));
        Configure(adapter, maxOutputBytes: 128);
        var invocation = ParseJson("""
            {"id":"invoke-error-payload","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation);

        Assert.Equal("OUTPUT_TOO_LARGE", result["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_CountsLogicalOutputUsingSerdeCompatibleUtf8()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse
            {
                Ok = false,
                Error = new string('é', 40)
            })));
        Configure(adapter, maxOutputBytes: 128);
        var invocation = ParseJson("""
            {"id":"invoke-utf8-error","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation);

        Assert.Equal("WINDOWS_CAPABILITY", result["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_ReturnsStableFailureForInvalidUtf16CapabilityError()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse
            {
                Ok = false,
                Error = "\ud800"
            })));
        Configure(adapter);
        var invocation = ParseJson("""
            {"id":"invoke-invalid-utf16","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":0,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("RESULT_SERIALIZATION", result["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_RoutesDispatcherErrorsThroughBoundedFailureBuilder()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => throw new InvalidOperationException(new string('e', 4096))));
        Configure(adapter, maxOutputBytes: 128);
        var invocation = ParseJson("""
            {"id":"invoke-dispatch-error","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation);

        Assert.Equal("WINDOWS_CAPABILITY", result["result"]!["code"]!.GetValue<string>());
        Assert.Equal(
            "Command execution failed",
            result["result"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_AllowsSerdeDepthCapabilityOutput()
    {
        var nested = new string('[', 80) + "0" + new string(']', 80);
        var payload = SidecarJson.Parse(Encoding.UTF8.GetBytes(nested));
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true, Payload = payload })));
        Configure(adapter, maxOutputBytes: 1024);
        var invocation = ParseJson("""
            {"id":"invoke-deep-output","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation);

        Assert.Equal("success", result["result"]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_EmitsCapabilityFloatsLikeSerdeJson()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse
            {
                Ok = true,
                Payload = new
                {
                    doubleIntegral = 1.0,
                    negativeZero = -0.0,
                    doubleExponent = 1.25e-7,
                    singleExponent = 1e-7f
                }
            })));
        Configure(adapter, maxOutputBytes: 1024);
        var invocation = ParseJson("""
            {"id":"invoke-floats","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation);
        var encoded = Encoding.UTF8.GetString(SidecarJson.Serialize(result));

        Assert.Equal("success", result["result"]!["outcome"]!.GetValue<string>());
        Assert.Contains("\"doubleIntegral\":1.0", encoded);
        Assert.Contains("\"negativeZero\":-0.0", encoded);
        Assert.Contains("\"doubleExponent\":1.25e-7", encoded);
        Assert.Contains("\"singleExponent\":1.0000000116860974e-7", encoded);
        Assert.Equal(
            "{\"fixedSmall\":0.00001,\"scientificLarge\":1e+16}",
            Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
                new { fixedSmall = 1e-5, scientificLarge = 1e16 },
                SidecarJson.SerializerOptions)));
    }

    [Fact]
    public async Task Adapter_DoesNotChargeResultSerializationToHandlerDeadline()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse
            {
                Ok = true,
                Payload = new SlowSerializationPayload(TimeSpan.FromMilliseconds(125))
            })));
        Configure(
            adapter,
            defaultTimeoutMs: 200,
            maxTimeoutMs: 200,
            resultGraceMs: 100);
        var invocation = ParseJson("""
            {"id":"invoke-slow-serialization","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":200,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("success", result["result"]!["outcome"]!.GetValue<string>());
        Assert.Equal("serialized", result["result"]!["payload"]!["value"]!.GetValue<string>());
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
    public async Task Adapter_UsesSerdeFloatingPointEqualityForAdmission()
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
        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-float","nodeId":"node-1","command":"product.status","params":{"value":1.0},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        var invocation = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-float","nodeId":"node-1","command":"product.status","params":{"value":1.0000000000000001},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);

        _ = await adapter.HandleRuntimeMessageAsync(admission, CancellationToken.None);
        var result = await adapter.HandleRuntimeMessageAsync(invocation, CancellationToken.None);

        Assert.Equal("success", result!["result"]!["outcome"]!.GetValue<string>());
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Adapter_TreatsReorderedInvocationObjectsAsUnchanged()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true })));
        Configure(adapter);
        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-reordered","nodeId":"node-1","command":"product.status","params":{"first":1,"nested":{"left":2,"right":3}},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        var invocation = ParseJson("""
            {"type":"invoke","invocation":{"sessionKey":null,"idempotencyKey":null,"timeoutMs":1000,"params":{"nested":{"right":3,"left":2},"first":1},"command":"product.status","nodeId":"node-1","id":"invoke-reordered"}}
            """);

        _ = await adapter.HandleRuntimeMessageAsync(admission, CancellationToken.None);
        var result = await adapter.HandleRuntimeMessageAsync(invocation, CancellationToken.None);

        Assert.Equal("success", result!["result"]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_NormalizesDuplicateParameterKeysBeforeAdmissionAndDispatch()
    {
        JsonElement? dispatchedParameters = null;
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (request, _) =>
            {
                dispatchedParameters = request.Args;
                return Task.FromResult(new NodeInvokeResponse { Ok = true });
            }));
        Configure(adapter);
        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-duplicates","nodeId":"node-1","command":"product.status","params":{"value":1,"value":2},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        var invocation = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-duplicates","nodeId":"node-1","command":"product.status","params":{"value":999,"value":2},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);

        _ = await adapter.HandleRuntimeMessageAsync(admission, CancellationToken.None);
        var result = await adapter.HandleRuntimeMessageAsync(invocation, CancellationToken.None);

        Assert.Equal("success", result!["result"]!["outcome"]!.GetValue<string>());
        Assert.NotNull(dispatchedParameters);
        Assert.Single(dispatchedParameters.Value.EnumerateObject());
        Assert.Equal(2, dispatchedParameters.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Adapter_RejectsOutOfRangeChangedParameterWithoutRetiringSession()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true })));
        Configure(adapter, maxInFlight: 1);
        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-range","nodeId":"node-1","command":"product.status","params":{"value":1},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        var invocation = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-range","nodeId":"node-1","command":"product.status","params":{"value":18446744073709551616},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);

        _ = await adapter.HandleRuntimeMessageAsync(admission, CancellationToken.None);
        var result = await adapter.HandleRuntimeMessageAsync(invocation, CancellationToken.None);
        var next = await adapter.HandleRuntimeMessageAsync(
            ParseJson("""
                {"type":"admission-request","invocation":{"id":"invoke-next","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
                """),
            CancellationToken.None);

        Assert.Equal("SIDECAR_NON_PORTABLE_JSON", result!["result"]!["code"]!.GetValue<string>());
        Assert.Equal("allow", next!["decision"]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_DistinguishesSerdeNegativeZeroFromIntegerZero()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true })));
        Configure(adapter);
        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-negative-zero","nodeId":"node-1","command":"product.status","params":{"value":-0},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);
        var invocation = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-negative-zero","nodeId":"node-1","command":"product.status","params":{"value":0},"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """);

        _ = await adapter.HandleRuntimeMessageAsync(admission, CancellationToken.None);
        var result = await adapter.HandleRuntimeMessageAsync(invocation, CancellationToken.None);

        Assert.Equal("ADMISSION_MISMATCH", result!["result"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_AllowsSerdeDepthAndUtf8InvocationInput()
    {
        var nested = new string('[', 80) + "\"" + new string('é', 40) + "\"" + new string(']', 80);
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true })));
        Configure(adapter, maxInputBytes: 256);
        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-deep-input","nodeId":"node-1","command":"product.status","params":__PARAMS__,"timeoutMs":1000,"idempotencyKey":null,"sessionKey":null}}
            """.Replace("__PARAMS__", nested, StringComparison.Ordinal));

        var decision = await adapter.HandleRuntimeMessageAsync(admission, CancellationToken.None);

        Assert.Equal("allow", decision!["decision"]!["outcome"]!.GetValue<string>());
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

        Assert.Equal("INPUT_TOO_LARGE", mismatch!["result"]!["code"]!.GetValue<string>());
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
    public async Task Adapter_ClampsAndEnforcesNegotiatedInvocationTimeout()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.blocking",
            "product.blocking",
            async (_, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new NodeInvokeResponse { Ok = true };
                }
                finally
                {
                    cancelled.TrySetResult();
                }
            }));
        Configure(
            adapter,
            defaultTimeoutMs: 100,
            maxTimeoutMs: 100,
            resultGraceMs: 10);
        var invocation = ParseJson("""
            {"id":"invoke-timeout","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":10000,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("HANDLER_TIMEOUT", result["result"]!["code"]!.GetValue<string>());
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Adapter_UsesFullDefaultInvocationTimeoutLikeRustRuntime()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.slow",
            "product.slow",
            async (_, _) =>
            {
                await Task.Delay(175);
                return new NodeInvokeResponse { Ok = true };
            }));
        Configure(
            adapter,
            defaultTimeoutMs: 250,
            maxTimeoutMs: 250,
            resultGraceMs: 150);
        var invocation = ParseJson("""
            {"id":"invoke-default-timeout","nodeId":"node-1","command":"product.slow","params":{},"timeoutMs":null,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("success", result["result"]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_HoldsTimedOutAdmissionUntilNonCooperativeHandlerTerminates()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.blocking",
            "product.blocking",
            async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return new NodeInvokeResponse { Ok = true };
            }));
        Configure(
            adapter,
            maxInFlight: 1,
            defaultTimeoutMs: 50,
            maxTimeoutMs: 50,
            resultGraceMs: 10);
        var invocation = ParseJson("""
            {"id":"invoke-noncooperative","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":50,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);
        var running = InvokeAsync(adapter, invocation);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var timeout = await running.WaitAsync(TimeSpan.FromSeconds(5));
        var whileRunning = await AdmitAsync(
            adapter,
            ParseJson("""
                {"id":"invoke-next","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":50,"idempotencyKey":null,"sessionKey":null}
                """));
        release.TrySetResult();

        Assert.Equal("HANDLER_TIMEOUT", timeout["result"]!["code"]!.GetValue<string>());
        Assert.Equal("ADMISSION_SATURATED", whileRunning["decision"]!["code"]!.GetValue<string>());
        JsonObject? afterTermination = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            afterTermination = await AdmitAsync(
                adapter,
                ParseJson("""
                    {"id":"invoke-after","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":50,"idempotencyKey":null,"sessionKey":null}
                    """));
            if (afterTermination["decision"]!["outcome"]!.GetValue<string>() == "allow")
                break;
            await Task.Delay(10);
        }
        Assert.Equal("allow", afterTermination!["decision"]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_UsesRustMessageForElapsedPreDispatchDeadline()
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "native.status",
            "product.status",
            (_, _) => Task.FromResult(new NodeInvokeResponse { Ok = true })));
        Configure(
            adapter,
            defaultTimeoutMs: 100,
            maxTimeoutMs: 100,
            resultGraceMs: 10);
        var invocation = ParseJson("""
            {"id":"invoke-elapsed","nodeId":"node-1","command":"product.status","params":{},"timeoutMs":5,"idempotencyKey":null,"sessionKey":null}
            """);
        await AdmitAsync(adapter, invocation);

        var result = await InvokeAsync(adapter, invocation);

        Assert.Equal("HANDLER_TIMEOUT", result["result"]!["code"]!.GetValue<string>());
        Assert.Equal(
            "command handler deadline already elapsed",
            result["result"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adapter_MalformedDuplicateInvokeCannotBreakActiveCancellation()
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
                    return new NodeInvokeResponse { Ok = true };
                }
                finally
                {
                    cancelled.TrySetResult();
                }
            }));
        Configure(adapter);
        var admission = ParseJson("""
            {"type":"admission-request","invocation":{"id":"invoke-active","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":0,"idempotencyKey":null,"sessionKey":null}}
            """);
        var invocation = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-active","nodeId":"node-1","command":"product.blocking","params":{},"timeoutMs":0,"idempotencyKey":null,"sessionKey":null}}
            """);
        _ = await adapter.HandleRuntimeMessageAsync(admission, CancellationToken.None);
        var active = adapter.HandleRuntimeMessageAsync(invocation, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var malformedDuplicate = ParseJson("""
            {"type":"invoke","invocation":{"id":"invoke-active","nodeId":"node-1","command":"product.blocking","params":{"value":18446744073709551616},"timeoutMs":0,"idempotencyKey":null,"sessionKey":null}}
            """);

        var rejected = await adapter.HandleRuntimeMessageAsync(malformedDuplicate, CancellationToken.None);
        _ = await adapter.HandleRuntimeMessageAsync(
            ParseJson("""{"type":"cancel","invocationId":"invoke-active"}"""),
            CancellationToken.None);

        Assert.Equal("SIDECAR_NON_PORTABLE_JSON", rejected!["result"]!["code"]!.GetValue<string>());
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = await active.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("system.run")]
    [InlineData("System.Run")]
    public void Adapter_RecordsCurrentRustSystemNamespaceGapInsteadOfSelectingIt(string command)
    {
        var adapter = new WindowsSidecarCapabilityAdapter("node-1", new TestLogger());
        adapter.RegisterCapability(new TestCapability(
            "system",
            command,
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
        uint maxOutputBytes = 1_048_576,
        uint defaultTimeoutMs = 30_000,
        uint maxTimeoutMs = 120_000,
        uint resultGraceMs = 250)
    {
        var configure = adapter.BeginConfiguration(
            1,
            new SidecarProtocolSelection(1, 0, 0, new SidecarLimits(4096, maxInFlight, 1000)),
            maxInputBytes: maxInputBytes,
            maxOutputBytes: maxOutputBytes,
            defaultTimeoutMs: defaultTimeoutMs,
            maxTimeoutMs: maxTimeoutMs,
            resultGraceMs: resultGraceMs);
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

    private static JsonElement ParseJson(string json) =>
        SidecarJson.Parse(Encoding.UTF8.GetBytes(json));

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

    private sealed class SlowSerializationPayload(TimeSpan delay)
    {
        public string Value
        {
            get
            {
                Thread.Sleep(delay);
                return "serialized";
            }
        }
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
