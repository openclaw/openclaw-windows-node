using System.Collections.Generic;
using System.Text.Json.Nodes;
using OpenClawTray.A2UI.Rendering;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Direct unit tests for the secret-name denylist. Substring-on-segment
/// semantics: a path matches if any one of its segments contains a denylisted
/// term (case-insensitive). False positives are intentional — leaking a real
/// secret is the failure mode we are paid to prevent.
/// </summary>
public sealed class SecretRedactorTests
{
    private static readonly IReadOnlySet<string> NoRegistered =
        new HashSet<string>();

    public static IEnumerable<object[]> DenylistTerms() => new[]
    {
        new object[] { "/userPassword" },
        new object[] { "/auth/secret" },
        new object[] { "/sessionToken" },
        new object[] { "/apiKey" },
        new object[] { "/Authorization" },
        new object[] { "/bearerHeader" },
        new object[] { "/user/pin" },
        new object[] { "/twoFactorOtp" },
        new object[] { "/mfaChallenge" },
        new object[] { "/credentialBag" },
        new object[] { "/sessionId" },
        new object[] { "/sessionCookie" },
        new object[] { "/Cookie" },
        new object[] { "/oauth/refresh" },
        new object[] { "/refreshToken" },
        new object[] { "/privateKey" },
        new object[] { "/accessToken" },
    };

    [Theory, MemberData(nameof(DenylistTerms))]
    public void IsSecret_DenylistTerm_ReturnsTrue(string path)
    {
        Assert.True(SecretRedactor.IsSecret(path, NoRegistered),
            $"Expected '{path}' to match denylist");
    }

    [Theory]
    [InlineData("/userName")]
    [InlineData("/profile/displayName")]
    [InlineData("/list/0/title")]
    [InlineData("/icon")]
    [InlineData("/")]
    public void IsSecret_NonSecretPath_ReturnsFalse(string path)
    {
        Assert.False(SecretRedactor.IsSecret(path, NoRegistered));
    }

    [Fact]
    public void Redact_ReplacesDenylistedKeyValuesWithMarker()
    {
        var root = JsonNode.Parse("""
        {
            "userName": "alice",
            "password": "p@ss",
            "session": { "id": "abc", "expires": 9 },
            "auth": { "bearer": "tok", "scopes": ["a"] },
            "apiKey": "key-123"
        }
        """)!;
        var redacted = SecretRedactor.Redact(root, NoRegistered)!;
        Assert.Equal("alice", (string?)redacted["userName"]);
        Assert.Equal("[REDACTED]", (string?)redacted["password"]);
        // "session" is denylisted → entire subtree replaced
        Assert.Equal("[REDACTED]", (string?)redacted["session"]);
        // "auth" is denylisted → entire subtree replaced
        Assert.Equal("[REDACTED]", (string?)redacted["auth"]);
        Assert.Equal("[REDACTED]", (string?)redacted["apiKey"]);
    }

    [Fact]
    public void IsSecret_RegisteredAncestorPath_RedactsDescendants()
    {
        var registered = new HashSet<string> { "/credentials" };
        Assert.True(SecretRedactor.IsSecret("/credentials/value", registered));
        Assert.True(SecretRedactor.IsSecret("/credentials", registered));
        Assert.False(SecretRedactor.IsSecret("/profile", registered));
    }

    [Fact]
    public void IsSecret_RootRegistered_DoesNotMatchEverything()
    {
        // "/" registered must not be treated as wildcard — registered registry
        // is purely path-anchored. Denylist still applies independently.
        var registered = new HashSet<string> { "/" };
        Assert.False(SecretRedactor.IsSecret("/profile/name", registered));
        Assert.True(SecretRedactor.IsSecret("/profile/password", registered));
    }

    [Fact]
    public void RedactInPlace_RootRegistered_RedactsWholeRoot()
    {
        var registered = new HashSet<string> { "/" };
        var root = JsonNode.Parse("""{ "value": "1234", "profile": { "name": "alice" } }""")!;

        var redacted = SecretRedactor.RedactInPlace(root, registered)!;

        Assert.Equal("[REDACTED]", redacted.GetValue<string>());
    }

    [Fact]
    public void Redact_RegisteredSecretInsideArray_IsRedacted()
    {
        // An obscured TextField bound to "/codes/0" registers that element path.
        // After a valueArray seeds "/codes" as ["1234","5678"], the snapshot/dump
        // path must redact the registered element — arrays previously only
        // recursed (leaving scalar elements untouched).
        var registered = new HashSet<string> { "/codes/0" };
        var root = JsonNode.Parse("""{ "codes": ["1234", "5678"] }""")!;

        var redacted = SecretRedactor.Redact(root, registered)!;

        var arr = Assert.IsType<JsonArray>(redacted["codes"]);
        Assert.Equal("[REDACTED]", (string?)arr[0]);
        Assert.Equal("5678", (string?)arr[1]);
    }

    [Theory]
    [InlineData("/codes/01")]
    [InlineData("/codes/+1")]
    [InlineData("/codes/ 1")]
    public void Redact_NonCanonicalRegisteredArrayIndex_RedactsCanonicalElement(string registeredPath)
    {
        var registered = new HashSet<string> { registeredPath };
        var root = JsonNode.Parse("""{ "codes": ["1234", "5678"] }""")!;

        var redacted = SecretRedactor.Redact(root, registered)!;

        var arr = Assert.IsType<JsonArray>(redacted["codes"]);
        Assert.Equal("1234", (string?)arr[0]);
        Assert.Equal("[REDACTED]", (string?)arr[1]);
    }

    [Fact]
    public void Redact_RegisteredArrayParent_RedactsWholeArray()
    {
        // Registering the array's parent path redacts the entire array value.
        var registered = new HashSet<string> { "/codes" };
        var root = JsonNode.Parse("""{ "codes": ["1234", "5678"] }""")!;

        var redacted = SecretRedactor.Redact(root, registered)!;

        Assert.Equal("[REDACTED]", (string?)redacted["codes"]);
    }

    [Fact]
    public void Redact_DenylistedScalarInsideArray_IsRedacted()
    {
        // A denylisted segment on an array-element path is caught too.
        var root = JsonNode.Parse("""{ "items": [ { "token": "abc" }, { "name": "ok" } ] }""")!;

        var redacted = SecretRedactor.Redact(root, NoRegistered)!;

        var arr = Assert.IsType<JsonArray>(redacted["items"]);
        Assert.Equal("[REDACTED]", (string?)arr[0]!["token"]);
        Assert.Equal("ok", (string?)arr[1]!["name"]);
    }
}
