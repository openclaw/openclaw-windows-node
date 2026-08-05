using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

internal enum ConnectEnvelopeProfile
{
    Operator,
    Node,
}

internal abstract class ConnectCredential
{
    protected ConnectCredential(string value)
    {
        Value = value;
    }

    internal string Value { get; }
    internal abstract string FieldName { get; }

    internal Dictionary<string, string> ToAuthPayload() =>
        new() { [FieldName] = Value };

    public sealed override string ToString() =>
        $"ConnectCredential {{ Kind = {FieldName} }}";
}

internal sealed class TokenConnectCredential(string token)
    : ConnectCredential(token)
{
    internal override string FieldName => "token";
}

internal sealed class BootstrapTokenConnectCredential(string bootstrapToken)
    : ConnectCredential(bootstrapToken)
{
    internal override string FieldName => "bootstrapToken";
}

internal sealed class DeviceTokenConnectCredential(string deviceToken)
    : ConnectCredential(deviceToken)
{
    internal override string FieldName => "deviceToken";
}

internal interface IConnectEnvelopeSigner
{
    string DeviceId { get; }
    string PublicKeyBase64Url { get; }

    string SignConnectPayloadV2(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        IReadOnlyList<string> scopes,
        string authToken);

    string SignConnectPayloadV3(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        IReadOnlyList<string> scopes,
        string authToken,
        string platform,
        string deviceFamily);
}

internal sealed class DeviceIdentityConnectEnvelopeSigner(DeviceIdentity identity)
    : IConnectEnvelopeSigner
{
    public string DeviceId => identity.DeviceId;
    public string PublicKeyBase64Url => identity.PublicKeyBase64Url;

    public string SignConnectPayloadV2(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        IReadOnlyList<string> scopes,
        string authToken) =>
        identity.SignConnectPayloadV2(
            nonce,
            signedAtMs,
            clientId,
            clientMode,
            role,
            scopes,
            authToken);

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
        identity.SignConnectPayloadV3(
            nonce,
            signedAtMs,
            clientId,
            clientMode,
            role,
            scopes,
            authToken,
            platform,
            deviceFamily);
}

internal sealed record OperatorConnectEnvelopeOptions(
    string RequestId,
    string Version,
    string Role,
    IReadOnlyList<string> Scopes,
    ConnectCredential Credential,
    string? Nonce,
    long? ChallengeTimestampMs,
    bool UseV2Signature);

internal sealed record NodeConnectEnvelopeOptions(
    string RequestId,
    string Version,
    string Platform,
    string DeviceFamily,
    string DisplayName,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Commands,
    IReadOnlyDictionary<string, bool> Permissions,
    ConnectCredential Credential,
    string? Nonce,
    long? ChallengeTimestampMs,
    bool UseV2Signature);

internal sealed class ConnectEnvelopeSigningArguments
{
    internal ConnectEnvelopeSigningArguments(
        bool useV2Signature,
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
        UseV2Signature = useV2Signature;
        Nonce = nonce;
        SignedAtMs = signedAtMs;
        ClientId = clientId;
        ClientMode = clientMode;
        Role = role;
        Scopes = scopes;
        AuthToken = authToken;
        Platform = platform;
        DeviceFamily = deviceFamily;
    }

    internal bool UseV2Signature { get; }
    internal string Nonce { get; }
    internal long SignedAtMs { get; }
    internal string ClientId { get; }
    internal string ClientMode { get; }
    internal string Role { get; }
    internal IReadOnlyList<string> Scopes { get; }
    internal string AuthToken { get; }
    internal string Platform { get; }
    internal string DeviceFamily { get; }

    internal string BuildPayload(DeviceIdentity identity) =>
        UseV2Signature
            ? identity.BuildConnectPayloadV2(
                Nonce,
                SignedAtMs,
                ClientId,
                ClientMode,
                Role,
                Scopes,
                AuthToken)
            : identity.BuildConnectPayloadV3(
                Nonce,
                SignedAtMs,
                ClientId,
                ClientMode,
                Role,
                Scopes,
                AuthToken,
                Platform,
                DeviceFamily);

    public override string ToString() =>
        $"ConnectEnvelopeSigningArguments {{ Version = {(UseV2Signature ? "v2" : "v3")} }}";
}

internal sealed class PreparedConnectEnvelope
{
    private static readonly JsonSerializerOptions s_ignoreNullOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IConnectEnvelopeSigner _signer;
    private readonly ConnectEnvelopeProfile _profile;
    private readonly string _requestId;
    private readonly string _version;
    private readonly string _platform;
    private readonly string _deviceFamily;
    private readonly string _displayName;
    private readonly IReadOnlyList<string> _capabilities;
    private readonly IReadOnlyList<string> _commands;
    private readonly IReadOnlyDictionary<string, bool> _permissions;
    private readonly ConnectCredential _credential;
    private readonly string? _serializedNonce;

    internal PreparedConnectEnvelope(
        IConnectEnvelopeSigner signer,
        ConnectEnvelopeProfile profile,
        string requestId,
        string version,
        string platform,
        string deviceFamily,
        string displayName,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> commands,
        IReadOnlyDictionary<string, bool> permissions,
        ConnectCredential credential,
        string? serializedNonce,
        ConnectEnvelopeSigningArguments signingArguments)
    {
        _signer = signer;
        _profile = profile;
        _requestId = requestId;
        _version = version;
        _platform = platform;
        _deviceFamily = deviceFamily;
        _displayName = displayName;
        _capabilities = capabilities;
        _commands = commands;
        _permissions = permissions;
        _credential = credential;
        _serializedNonce = serializedNonce;
        SigningArguments = signingArguments;
    }

    internal long SignedAt => SigningArguments.SignedAtMs;
    internal string? Nonce => _serializedNonce;
    internal bool CanSign =>
        _profile == ConnectEnvelopeProfile.Operator ||
        !string.IsNullOrEmpty(_serializedNonce);
    internal ConnectEnvelopeSigningArguments SigningArguments { get; }

    internal string Sign()
    {
        if (!CanSign)
        {
            throw new InvalidOperationException("A node connect nonce is required for signing.");
        }

        var arguments = SigningArguments;
        return arguments.UseV2Signature
            ? _signer.SignConnectPayloadV2(
                arguments.Nonce,
                arguments.SignedAtMs,
                arguments.ClientId,
                arguments.ClientMode,
                arguments.Role,
                arguments.Scopes,
                arguments.AuthToken)
            : _signer.SignConnectPayloadV3(
                arguments.Nonce,
                arguments.SignedAtMs,
                arguments.ClientId,
                arguments.ClientMode,
                arguments.Role,
                arguments.Scopes,
                arguments.AuthToken,
                arguments.Platform,
                arguments.DeviceFamily);
    }

    internal string Serialize(string? signature)
    {
        var isOperator = _profile == ConnectEnvelopeProfile.Operator;
        var message = new
        {
            type = "req",
            id = _requestId,
            method = "connect",
            @params = new
            {
                minProtocol = 3,
                maxProtocol = 4,
                client = new
                {
                    id = SigningArguments.ClientId,
                    version = _version,
                    platform = _platform,
                    deviceFamily = _deviceFamily,
                    mode = SigningArguments.ClientMode,
                    displayName = _displayName,
                },
                role = SigningArguments.Role,
                scopes = SigningArguments.Scopes,
                caps = _capabilities,
                commands = _commands,
                permissions = _permissions,
                auth = _credential.ToAuthPayload(),
                locale = "en-US",
                userAgent = isOperator
                    ? $"openclaw-windows-tray/{_version}"
                    : $"openclaw-windows-node/{_version}",
                device = new
                {
                    id = _signer.DeviceId,
                    publicKey = _signer.PublicKeyBase64Url,
                    signature,
                    signedAt = SignedAt,
                    nonce = _serializedNonce,
                },
            },
        };

        return isOperator
            ? JsonSerializer.Serialize(message)
            : JsonSerializer.Serialize(message, s_ignoreNullOptions);
    }
}

internal static class ConnectEnvelopeBuilder
{
    private const string OperatorClientId = "cli";
    private const string OperatorClientMode = "cli";
    private const string OperatorDisplayName = "OpenClaw Windows Tray";
    private const string NodeClientId = "node-host";
    private const string NodeClientMode = "node";
    private const string NodeRole = "node";

    internal static PreparedConnectEnvelope PrepareOperator(
        OperatorConnectEnvelopeOptions options,
        IConnectEnvelopeSigner signer,
        TimeProvider? timeProvider = null)
    {
        var scopes = options.Scopes.ToArray();
        var nonce = options.Nonce ?? string.Empty;
        var signedAt = ConnectAuthTimestamp.ResolveSignedAt(
            options.ChallengeTimestampMs,
            timeProvider);
        var signingArguments = new ConnectEnvelopeSigningArguments(
            options.UseV2Signature,
            nonce,
            signedAt,
            OperatorClientId,
            OperatorClientMode,
            options.Role,
            scopes,
            options.Credential.Value,
            WindowsClientMetadata.Platform,
            WindowsClientMetadata.DeviceFamily);

        return new PreparedConnectEnvelope(
            signer,
            ConnectEnvelopeProfile.Operator,
            options.RequestId,
            options.Version,
            WindowsClientMetadata.Platform,
            WindowsClientMetadata.DeviceFamily,
            OperatorDisplayName,
            [],
            [],
            new Dictionary<string, bool>(),
            options.Credential,
            nonce,
            signingArguments);
    }

    internal static PreparedConnectEnvelope PrepareNode(
        NodeConnectEnvelopeOptions options,
        IConnectEnvelopeSigner signer,
        TimeProvider? timeProvider = null)
    {
        var signedAt = ConnectAuthTimestamp.ResolveSignedAt(
            options.ChallengeTimestampMs,
            timeProvider);
        var signingArguments = new ConnectEnvelopeSigningArguments(
            options.UseV2Signature,
            options.Nonce ?? string.Empty,
            signedAt,
            NodeClientId,
            NodeClientMode,
            NodeRole,
            [],
            options.Credential.Value,
            options.Platform,
            options.DeviceFamily);

        return new PreparedConnectEnvelope(
            signer,
            ConnectEnvelopeProfile.Node,
            options.RequestId,
            options.Version,
            options.Platform,
            options.DeviceFamily,
            options.DisplayName,
            options.Capabilities.ToArray(),
            options.Commands.ToArray(),
            new Dictionary<string, bool>(options.Permissions),
            options.Credential,
            options.Nonce,
            signingArguments);
    }
}
