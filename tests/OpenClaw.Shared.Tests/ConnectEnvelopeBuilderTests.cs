using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class ConnectEnvelopeBuilderTests
{
    private const string RequestId = "request-123";
    private const string Nonce = "challenge-456";
    private const long ChallengeTimestamp = 1_716_480_000_000;
    private const string Signature = "fixed-signature";

    public static IEnumerable<object[]> EnvelopeCases =>
    [
        [Operator(
            "operator paired default scopes",
            new DeviceTokenConnectCredential("paired-device"),
            "operator",
            ["operator.admin", "operator.pairing"])],
        [Operator(
            "operator paired stored scopes",
            new DeviceTokenConnectCredential("paired-device"),
            "operator",
            ["operator.approvals", "operator.read", "operator.talk.secrets", "operator.write"])],
        [Operator(
            "operator shared",
            new TokenConnectCredential("shared-token"),
            "operator",
            ["operator.admin", "operator.pairing"])],
        [Operator(
            "bootstrap operator",
            new BootstrapTokenConnectCredential("bootstrap-token"),
            "operator",
            ["operator.approvals", "operator.read", "operator.talk.secrets", "operator.write"])],
        [Operator(
            "bootstrap pair as node",
            new BootstrapTokenConnectCredential("handoff-token"),
            "node",
            [])],
        [Node("node paired over other credentials", new DeviceTokenConnectCredential("node-device"))],
        [Node("node bootstrap", new BootstrapTokenConnectCredential("node-bootstrap"))],
        [Node("node shared", new TokenConnectCredential("node-shared"))],
    ];

    [Theory]
    [MemberData(nameof(EnvelopeCases))]
    public void Build_CompleteProfileMatrix_PreservesWireShapeAndSigningArguments(object value)
    {
        var testCase = Assert.IsType<EnvelopeCase>(value);
        foreach (var useV2 in new[] { false, true })
        {
            var signer = new CaptureSigner();
            var envelope = Prepare(testCase, signer, useV2);

            var signature = envelope.Sign();
            var json = envelope.Serialize(signature);

            Assert.Equal(Signature, signature);
            AssertSigningCall(testCase, useV2, signer);
            AssertEnvelopeJson(testCase, json);
        }
    }

    [Fact]
    public void CredentialFormatting_IdentifiesKindWithoutTokenValue()
    {
        var cases = new (ConnectCredential Credential, string Kind, string SensitiveValue)[]
        {
            (new TokenConnectCredential("shared-sensitive-value"), "token", "shared-sensitive-value"),
            (new BootstrapTokenConnectCredential("bootstrap-sensitive-value"), "bootstrapToken", "bootstrap-sensitive-value"),
            (new DeviceTokenConnectCredential("device-sensitive-value"), "deviceToken", "device-sensitive-value"),
        };

        foreach (var (credential, kind, sensitiveValue) in cases)
        {
            var diagnostic = credential.ToString();

            Assert.Equal($"ConnectCredential {{ Kind = {kind} }}", diagnostic);
            Assert.DoesNotContain(sensitiveValue, diagnostic, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, "v3")]
    [InlineData(true, "v2")]
    public void SigningArgumentsFormatting_IdentifiesVersionWithoutAuthToken(
        bool useV2Signature,
        string expectedVersion)
    {
        const string sensitiveValue = "signature-sensitive-value";
        var arguments = new ConnectEnvelopeSigningArguments(
            useV2Signature,
            Nonce,
            ChallengeTimestamp,
            "cli",
            "cli",
            "operator",
            ["operator.admin"],
            sensitiveValue,
            "windows",
            "Windows");

        var diagnostic = arguments.ToString();

        Assert.Equal(
            $"ConnectEnvelopeSigningArguments {{ Version = {expectedVersion} }}",
            diagnostic);
        Assert.DoesNotContain(sensitiveValue, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvelopeOptionFormatting_UsesRedactedCredentialFormatting()
    {
        const string sensitiveValue = "option-sensitive-value";
        var credential = new TokenConnectCredential(sensitiveValue);
        var operatorOptions = new OperatorConnectEnvelopeOptions(
            RequestId,
            "9.8.7",
            "operator",
            ["operator.admin"],
            credential,
            Nonce,
            ChallengeTimestamp,
            UseV2Signature: false);
        var nodeOptions = NodeOptions(credential);

        Assert.DoesNotContain(sensitiveValue, operatorOptions.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveValue, nodeOptions.ToString(), StringComparison.Ordinal);
        Assert.Contains("Kind = token", operatorOptions.ToString(), StringComparison.Ordinal);
        Assert.Contains("Kind = token", nodeOptions.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_ValidChallengeTimestamp_UsesChallengeTime()
    {
        var signer = new CaptureSigner();
        var envelope = ConnectEnvelopeBuilder.PrepareOperator(
            new OperatorConnectEnvelopeOptions(
                RequestId,
                "9.8.7",
                "operator",
                ["operator.admin"],
                new TokenConnectCredential("token"),
                Nonce,
                ChallengeTimestamp,
                UseV2Signature: false),
            signer,
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(42)));

        _ = envelope.Sign();

        Assert.Equal(ChallengeTimestamp, envelope.SignedAt);
        Assert.Equal(ChallengeTimestamp, Assert.Single(signer.Calls).SignedAtMs);
    }

    [Fact]
    public void Prepare_MissingChallengeTimestamp_UsesInjectedHostTime()
    {
        const long hostTimestamp = 1_800_000_000_123;
        var signer = new CaptureSigner();
        var envelope = ConnectEnvelopeBuilder.PrepareNode(
            NodeOptions(new TokenConnectCredential("token"), challengeTimestampMs: null),
            signer,
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(hostTimestamp)));

        _ = envelope.Sign();

        Assert.Equal(hostTimestamp, envelope.SignedAt);
        Assert.Equal(hostTimestamp, Assert.Single(signer.Calls).SignedAtMs);
    }

    [Fact]
    public void OperatorMissingNonce_ConvertsToEmptyAndSigns()
    {
        var signer = new CaptureSigner();
        var envelope = ConnectEnvelopeBuilder.PrepareOperator(
            new OperatorConnectEnvelopeOptions(
                RequestId,
                "9.8.7",
                "operator",
                ["operator.admin"],
                new TokenConnectCredential("token"),
                Nonce: null,
                ChallengeTimestamp,
                UseV2Signature: false),
            signer);

        var json = envelope.Serialize(envelope.Sign());
        using var document = JsonDocument.Parse(json);
        var device = document.RootElement.GetProperty("params").GetProperty("device");

        Assert.Equal(string.Empty, Assert.Single(signer.Calls).Nonce);
        Assert.Equal(string.Empty, device.GetProperty("nonce").GetString());
        Assert.Equal(Signature, device.GetProperty("signature").GetString());
    }

    [Fact]
    public void NodeMissingNonce_OmitsNonceAndSignatureWithoutSigning()
    {
        var signer = new CaptureSigner();
        var envelope = ConnectEnvelopeBuilder.PrepareNode(
            NodeOptions(new TokenConnectCredential("token"), nonce: null),
            signer);

        var json = envelope.Serialize(signature: null);
        using var document = JsonDocument.Parse(json);
        var device = document.RootElement.GetProperty("params").GetProperty("device");

        Assert.Empty(signer.Calls);
        Assert.False(envelope.CanSign);
        Assert.False(device.TryGetProperty("nonce", out _));
        Assert.False(device.TryGetProperty("signature", out _));
        Assert.Equal(ChallengeTimestamp, device.GetProperty("signedAt").GetInt64());
    }

    [Fact]
    public async Task OperatorClient_SignerThrow_ReachesExistingSafeHandler()
    {
        var identityPath = CreateDataPath();
        var logger = new CaptureLogger();

        try
        {
            using var client = new OpenClawGatewayClient(
                "ws://localhost:18789",
                "shared-token",
                logger,
                identityPath: identityPath);
            client.ConnectEnvelopeSigner = new ThrowingSigner();

            var unsafeTask = InvokePrivateTask(
                client,
                "SendConnectMessageAsync",
                Nonce,
                0L,
                CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unsafeTask);

            BeginCurrentHandshake(client, 0L);
            await InvokePrivateTask(client, "SendConnectSafeAsync", Nonce, 0L);
            Assert.Contains(
                logger.Errors,
                message => message.Contains("SendConnectMessageAsync threw", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(identityPath, recursive: true);
        }
    }

    [Fact]
    public void NodeClient_SignerThrow_LogsAndOmitsSignature()
    {
        var identityPath = CreateDataPath();
        var logger = new CaptureLogger();

        try
        {
            using var client = new WindowsNodeClient(
                "ws://localhost:18789",
                "shared-token",
                identityPath,
                logger);
            client.ConnectEnvelopeSigner = new ThrowingSigner();

            var method = typeof(WindowsNodeClient).GetMethod(
                "BuildNodeConnectMessage",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var json = (string)method!.Invoke(
                client,
                [Nonce, ChallengeTimestamp, "node-connect-request"])!;
            using var document = JsonDocument.Parse(json);
            Assert.Equal(
                "node-connect-request",
                document.RootElement.GetProperty("id").GetString());
            var device = document.RootElement.GetProperty("params").GetProperty("device");

            Assert.False(device.TryGetProperty("signature", out _));
            Assert.Equal(Nonce, device.GetProperty("nonce").GetString());
            Assert.Contains(
                logger.Errors,
                message => message.Contains("Failed to sign payload", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(identityPath, recursive: true);
        }
    }

    private static PreparedConnectEnvelope Prepare(
        EnvelopeCase testCase,
        IConnectEnvelopeSigner signer,
        bool useV2)
    {
        if (testCase.Profile == ConnectEnvelopeProfile.Operator)
        {
            return ConnectEnvelopeBuilder.PrepareOperator(
                new OperatorConnectEnvelopeOptions(
                    RequestId,
                    "9.8.7",
                    testCase.Role,
                    testCase.Scopes,
                    testCase.Credential,
                    Nonce,
                    ChallengeTimestamp,
                    useV2),
                signer);
        }

        return ConnectEnvelopeBuilder.PrepareNode(
            NodeOptions(testCase.Credential, useV2Signature: useV2),
            signer);
    }

    private static NodeConnectEnvelopeOptions NodeOptions(
        ConnectCredential credential,
        string? nonce = Nonce,
        long? challengeTimestampMs = ChallengeTimestamp,
        bool useV2Signature = false) =>
        new(
            RequestId,
            "2.4.6",
            "WinDows",
            "DeskTop",
            "Registered Windows Node",
            ["screen", "system"],
            ["screen.capture", "system.run"],
            new Dictionary<string, bool>
            {
                ["screen"] = true,
                ["system"] = false,
            },
            credential,
            nonce,
            challengeTimestampMs,
            useV2Signature);

    private static EnvelopeCase Operator(
        string name,
        ConnectCredential credential,
        string role,
        IReadOnlyList<string> scopes) =>
        new(name, ConnectEnvelopeProfile.Operator, credential, role, scopes);

    private static EnvelopeCase Node(string name, ConnectCredential credential) =>
        new(name, ConnectEnvelopeProfile.Node, credential, "node", []);

    private static void AssertSigningCall(
        EnvelopeCase testCase,
        bool useV2,
        CaptureSigner signer)
    {
        var call = Assert.Single(signer.Calls);
        Assert.Equal(useV2 ? 2 : 3, call.Version);
        Assert.Equal(Nonce, call.Nonce);
        Assert.Equal(ChallengeTimestamp, call.SignedAtMs);
        Assert.Equal(testCase.Profile == ConnectEnvelopeProfile.Operator ? "cli" : "node-host", call.ClientId);
        Assert.Equal(testCase.Profile == ConnectEnvelopeProfile.Operator ? "cli" : "node", call.ClientMode);
        Assert.Equal(testCase.Role, call.Role);
        Assert.Equal(testCase.Scopes, call.Scopes);
        Assert.Equal(testCase.Credential.Value, call.AuthToken);
        Assert.Equal(
            useV2 ? null : testCase.Profile == ConnectEnvelopeProfile.Operator ? "windows" : "WinDows",
            call.Platform);
        Assert.Equal(
            useV2 ? null : testCase.Profile == ConnectEnvelopeProfile.Operator ? "Windows" : "DeskTop",
            call.DeviceFamily);
    }

    private static void AssertEnvelopeJson(EnvelopeCase testCase, string json)
    {
        var expected = BuildExpectedJson(testCase);
        var actual = JsonNode.Parse(json);

        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(["type", "id", "method", "params"], PropertyNames(root));
        var parameters = root.GetProperty("params");
        Assert.Equal(3, parameters.GetProperty("minProtocol").GetInt32());
        Assert.Equal(4, parameters.GetProperty("maxProtocol").GetInt32());
        Assert.Equal(
            [
                "minProtocol", "maxProtocol", "client", "role", "scopes", "caps",
                "commands", "permissions", "auth", "locale", "userAgent", "device"
            ],
            PropertyNames(parameters));
        Assert.Equal(["id", "version", "platform", "deviceFamily", "mode", "displayName"],
            PropertyNames(parameters.GetProperty("client")));
        Assert.Equal(["id", "publicKey", "signature", "signedAt", "nonce"],
            PropertyNames(parameters.GetProperty("device")));
        Assert.Equal(testCase.Scopes, StringValues(parameters.GetProperty("scopes")));
        Assert.Equal(
            testCase.Profile == ConnectEnvelopeProfile.Operator ? [] : ["screen", "system"],
            StringValues(parameters.GetProperty("caps")));
        Assert.Equal(
            testCase.Profile == ConnectEnvelopeProfile.Operator ? [] : ["screen.capture", "system.run"],
            StringValues(parameters.GetProperty("commands")));
        Assert.Equal(
            testCase.Profile == ConnectEnvelopeProfile.Operator ? [] : ["screen", "system"],
            PropertyNames(parameters.GetProperty("permissions")));
        var authProperty = Assert.Single(parameters.GetProperty("auth").EnumerateObject());
        Assert.Equal(testCase.Credential.FieldName, authProperty.Name);
        Assert.Equal(testCase.Credential.Value, authProperty.Value.GetString());
    }

    private static JsonObject BuildExpectedJson(EnvelopeCase testCase)
    {
        var isOperator = testCase.Profile == ConnectEnvelopeProfile.Operator;
        var auth = new JsonObject
        {
            [testCase.Credential.FieldName] = testCase.Credential.Value,
        };
        var permissions = new JsonObject();
        if (!isOperator)
        {
            permissions["screen"] = true;
            permissions["system"] = false;
        }

        return new JsonObject
        {
            ["type"] = "req",
            ["id"] = RequestId,
            ["method"] = "connect",
            ["params"] = new JsonObject
            {
                ["minProtocol"] = 3,
                ["maxProtocol"] = 4,
                ["client"] = new JsonObject
                {
                    ["id"] = isOperator ? "cli" : "node-host",
                    ["version"] = isOperator ? "9.8.7" : "2.4.6",
                    ["platform"] = isOperator ? "windows" : "WinDows",
                    ["deviceFamily"] = isOperator ? "Windows" : "DeskTop",
                    ["mode"] = isOperator ? "cli" : "node",
                    ["displayName"] = isOperator ? "OpenClaw Windows Tray" : "Registered Windows Node",
                },
                ["role"] = testCase.Role,
                ["scopes"] = ToJsonArray(testCase.Scopes),
                ["caps"] = ToJsonArray(isOperator ? [] : ["screen", "system"]),
                ["commands"] = ToJsonArray(isOperator ? [] : ["screen.capture", "system.run"]),
                ["permissions"] = permissions,
                ["auth"] = auth,
                ["locale"] = "en-US",
                ["userAgent"] = isOperator
                    ? "openclaw-windows-tray/9.8.7"
                    : "openclaw-windows-node/2.4.6",
                ["device"] = new JsonObject
                {
                    ["id"] = CaptureSigner.FixedDeviceId,
                    ["publicKey"] = CaptureSigner.FixedPublicKey,
                    ["signature"] = Signature,
                    ["signedAt"] = ChallengeTimestamp,
                    ["nonce"] = Nonce,
                },
            },
        };
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToArray();

    private static string[] StringValues(JsonElement element) =>
        element.EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static Task InvokePrivateTask(object instance, string methodName, params object?[] arguments)
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task)method!.Invoke(instance, arguments)!;
    }

    private static void BeginCurrentHandshake(
        OpenClawGatewayClient client,
        long connectionGeneration)
    {
        var gateField = typeof(OpenClawGatewayClient).GetField(
            "_handshakeChallengeGate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(gateField);
        var gate = gateField!.GetValue(client);
        Assert.NotNull(gate);
        var gateType = gate!.GetType();
        gateType.GetMethod("Reset")!.Invoke(gate, [connectionGeneration]);
        Assert.True(
            (bool)gateType.GetMethod("TryBegin")!.Invoke(gate, [connectionGeneration])!);
    }

    private static string CreateDataPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"connect-envelope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record EnvelopeCase(
        string Name,
        ConnectEnvelopeProfile Profile,
        ConnectCredential Credential,
        string Role,
        IReadOnlyList<string> Scopes)
    {
        public override string ToString() => Name;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CaptureSigner : IConnectEnvelopeSigner
    {
        public const string FixedDeviceId = "device-id";
        public const string FixedPublicKey = "public-key";

        public string DeviceId => FixedDeviceId;
        public string PublicKeyBase64Url => FixedPublicKey;
        public List<SigningCall> Calls { get; } = [];

        public string SignConnectPayloadV2(
            string nonce,
            long signedAtMs,
            string clientId,
            string clientMode,
            string role,
            IReadOnlyList<string> scopes,
            string authToken)
        {
            Calls.Add(new SigningCall(
                2, nonce, signedAtMs, clientId, clientMode, role,
                scopes.ToArray(), authToken, null, null));
            return Signature;
        }

        public string SignConnectPayloadV3(
            string nonce,
            long signedAtMs,
            string clientId,
            string clientMode,
            string role,
            IReadOnlyList<string> scopes,
            string authToken,
            string platform,
            string deviceFamily)
        {
            Calls.Add(new SigningCall(
                3, nonce, signedAtMs, clientId, clientMode, role,
                scopes.ToArray(), authToken, platform, deviceFamily));
            return Signature;
        }
    }

    private sealed class ThrowingSigner : IConnectEnvelopeSigner
    {
        public string DeviceId => "device-id";
        public string PublicKeyBase64Url => "public-key";

        public string SignConnectPayloadV2(
            string nonce,
            long signedAtMs,
            string clientId,
            string clientMode,
            string role,
            IReadOnlyList<string> scopes,
            string authToken) =>
            throw new InvalidOperationException("simulated signer failure");

        public string SignConnectPayloadV3(
            string nonce,
            long signedAtMs,
            string clientId,
            string clientMode,
            string role,
            IReadOnlyList<string> scopes,
            string authToken,
            string platform,
            string deviceFamily) =>
            throw new InvalidOperationException("simulated signer failure");
    }

    private sealed class CaptureLogger : IOpenClawLogger
    {
        public List<string> Errors { get; } = [];

        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) => Errors.Add(message);
    }

    private sealed record SigningCall(
        int Version,
        string Nonce,
        long SignedAtMs,
        string ClientId,
        string ClientMode,
        string Role,
        IReadOnlyList<string> Scopes,
        string AuthToken,
        string? Platform,
        string? DeviceFamily);
}
