using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class DiagnosticsExportRedactorTests
{
    [Fact]
    public void Sanitize_RedactsCommonSecretShapes()
    {
        const string privateKey = """
            -----BEGIN PRIVATE KEY-----
            abcdefghijklmnopqrstuvwxyz
            -----END PRIVATE KEY-----
            """;
        var mcpToken = new string('A', 43);
        var hexToken = new string('a', 64);
        var dpapi = "dpapi:abcdefghijklmnopqrstuvwxyz0123456789+/=";

        var input = string.Join(Environment.NewLine,
            "Authorization: Bearer bearer-secret",
            """{"sharedGatewayToken":"shared-secret","password":"pw-secret","setupCode":"setup-secret","apiKey":"api-secret"}""",
            """{"nonce":"nonce-secret","deviceId":"device-secret","requestId":"request-secret","sessionKey":"agent:abc:def","raw_error_response":"raw-id-secret"}""",
            $"token={mcpToken}",
            $"hex={hexToken}",
            $"protected={dpapi}",
            "jwt=eyJaaaaaaaaaa.bbbbbbbbbb.cccccccccc",
            privateKey);

        var sanitized = DiagnosticsExportRedactor.Sanitize(input);

        Assert.DoesNotContain("bearer-secret", sanitized);
        Assert.DoesNotContain("shared-secret", sanitized);
        Assert.DoesNotContain("pw-secret", sanitized);
        Assert.DoesNotContain("setup-secret", sanitized);
        Assert.DoesNotContain("api-secret", sanitized);
        Assert.DoesNotContain("nonce-secret", sanitized);
        Assert.DoesNotContain("device-secret", sanitized);
        Assert.DoesNotContain("request-secret", sanitized);
        Assert.DoesNotContain("agent:abc:def", sanitized);
        Assert.DoesNotContain("raw-id-secret", sanitized);
        Assert.DoesNotContain(mcpToken, sanitized);
        Assert.DoesNotContain(hexToken, sanitized);
        Assert.DoesNotContain(dpapi, sanitized);
        Assert.DoesNotContain("eyJaaaaaaaaaa", sanitized);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
    }

    [Fact]
    public void Sanitize_RedactsUrlsPathsEmailsIps_WhileKeepingFailureContext()
    {
        var input = """
            Failed to connect to wss://alice:secret@gateway.example.com:18789/reset/password?token=secret#frag
            File: C:\Users\christineyan\AppData\Roaming\OpenClawTray\gateways.json
            EscapedFile: C:\\Users\\christineyan\\AppData\\Roaming\\OpenClawTray\\settings.json
            Contact christine@example.com from 192.168.1.44 or user@host:22
            2026-05-27T15:16:34.3474228Z [handshake] signed: v3|token|cli|cli|operator|operator.admin,operator.read|1779894994338|sig|c5cacc40-2732-4008-a4d9-56b6a2c0643a|windows|desktop
            [2026-05-27 12:57:15.658] [INFO] Loaded Ed25519 device identity: 1ecac3b3e936a1e0...
            2026-05-27T13:27:28.8631055-04:00 [node] Node paired - node: 1ecac3b3e936... · dashboard: nodes
            session=agent:abc123:some-session-key
            [DEBUG] [IsMessageAborted] thread='agent:abc123:some-session-key' id='dee79a01' dictHasThread=False setCount=0 match=False
            [DEBUG] [ChatHistory] user msg OpenClawId='2d85dba4' seq=1
            {"type":"event","payload":{"nonce":"abc","ts":1779900898148}}
            Error: pairing request timed out on port 18789
            """;

        var sanitized = DiagnosticsExportRedactor.Sanitize(input);

        Assert.Contains("Failed to connect", sanitized);
        Assert.Contains("pairing request timed out", sanitized);
        Assert.Contains("18789", sanitized);
        Assert.Contains("wss://<host>:18789/reset", sanitized);
        Assert.Contains("<email>", sanitized);
        Assert.Contains("<ip>", sanitized);
        Assert.Contains("<user>@<host>", sanitized);
        Assert.DoesNotContain("christineyan", sanitized);
        Assert.DoesNotContain("gateway.example.com", sanitized);
        Assert.DoesNotContain("token=secret", sanitized);
        Assert.DoesNotContain("1779894994338", sanitized);
        Assert.DoesNotContain("c5cacc40-2732-4008-a4d9-56b6a2c0643a", sanitized);
        Assert.DoesNotContain("agent:abc123:some-session-key", sanitized);
        Assert.DoesNotContain("1ecac3b3e936a1e0", sanitized);
        Assert.DoesNotContain("dee79a01", sanitized);
        Assert.DoesNotContain("2d85dba4", sanitized);
        Assert.Contains("signed: [REDACTED_HANDSHAKE]", sanitized);
        Assert.Contains("[REDACTED_SESSION_KEY]", sanitized);
        Assert.Contains("node: [REDACTED]", sanitized);
        Assert.Contains("id='[REDACTED]'", sanitized);
        Assert.Contains("OpenClawId='[REDACTED]'", sanitized);
        Assert.Contains("\"ts\":1779900898148", sanitized);
    }

    [Fact]
    public void Sanitize_RedactsScaryConnectionAndChannelCredentialShapes()
    {
        var input = string.Join(Environment.NewLine,
            """{"webhookUrl":"https://hooks.slack.com/services/T000/B000/SECRET","signingSecret":"1234567890abcdef1234567890abcdef","botToken":"telegram-secret","clientSecret":"oauth-secret","relayUrls":"wss://relay.example.com/private","browserPassword":"browser-secret"}""",
            "Cookie: sessionid=browser-cookie; csrftoken=csrf-secret",
            "Set-Cookie: auth=secret-cookie; Path=/",
            "X-Api-Key: api-key-secret",
            "Proxy-Authorization: Basic dXNlcjpwYXNz",
            "openclaw connect --token shared-secret --mcp-token mcp-secret --bootstrap-token boot-secret --setup-code setup-secret --password pass-secret --webhook https://discord.com/api/webhooks/123/secret --signing-secret sign-secret --bot-token bot-secret --client-secret client-secret --nsec nsec1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq",
            "Browser auth failed but status context remains useful");

        var sanitized = DiagnosticsExportRedactor.Sanitize(input);

        foreach (var secret in new[]
        {
            "hooks.slack.com",
            "telegram-secret",
            "oauth-secret",
            "relay.example.com",
            "browser-secret",
            "browser-cookie",
            "csrf-secret",
            "secret-cookie",
            "api-key-secret",
            "dXNlcjpwYXNz",
            "shared-secret",
            "mcp-secret",
            "boot-secret",
            "setup-secret",
            "pass-secret",
            "discord.com",
            "sign-secret",
            "bot-secret",
            "nsec1"
        })
        {
            Assert.DoesNotContain(secret, sanitized, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Browser auth failed", sanitized);
        Assert.Contains("--token [REDACTED]", sanitized);
        Assert.Contains("Cookie: [REDACTED]", sanitized);
        Assert.Contains("--nsec [REDACTED]", sanitized);
    }

    [Fact]
    public void Sanitize_IsIdempotent()
    {
        const string input = """
            Authorization: Bearer first-secret
            token=second-secret
            wss://alice:password@gateway.example.com/private?token=third-secret
            """;

        var once = DiagnosticsExportRedactor.Sanitize(input);
        var twice = DiagnosticsExportRedactor.Sanitize(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Sanitize_RedactsJsonStyleNonStringSecretValues()
    {
        const string input = """{"setupCode":123456,"nonce":987654321,"ok":42,"version":"1.2.3"}""";

        var sanitized = DiagnosticsExportRedactor.Sanitize(input);

        Assert.DoesNotContain("123456", sanitized);
        Assert.DoesNotContain("987654321", sanitized);
        Assert.Contains("\"ok\":42", sanitized);
        Assert.Contains("\"version\":\"1.2.3\"", sanitized);
    }

    [Fact]
    public void Sanitize_DoesNotTreatVersionStringsAsIpAddresses()
    {
        const string input = "Gateway version 1.2.3 connected after 250 ms";

        var sanitized = DiagnosticsExportRedactor.Sanitize(input);

        Assert.Contains("1.2.3", sanitized);
        Assert.DoesNotContain("<ip>", sanitized);
    }

    [Fact]
    public void Sanitize_LongMalformedLine_DoesNotTimeoutOrLeakSecret()
    {
        var input = "prefix " + new string('a', 16_000) + " token=secret-value " + new string('b', 16_000);

        var sanitized = DiagnosticsExportRedactor.Sanitize(input);

        Assert.DoesNotContain("secret-value", sanitized);
        Assert.NotEqual(TokenSanitizer.SanitizerTimeoutSentinel, sanitized);
        Assert.Contains("[REDACTED]", sanitized);
    }
}
