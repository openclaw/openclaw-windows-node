using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class TokenSanitizerTests
{
    // ── null / empty input ─────────────────────────────────────────────────

    [Fact]
    public void Sanitize_NullInput_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, TokenSanitizer.Sanitize(null));
    }

    [Fact]
    public void Sanitize_EmptyInput_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, TokenSanitizer.Sanitize(""));
    }

    [Fact]
    public void Sanitize_NoSecrets_ReturnsSameString()
    {
        const string harmless = "Hello, world! This log message has no secrets.";
        Assert.Equal(harmless, TokenSanitizer.Sanitize(harmless));
    }

    // ── Authorization: Bearer ──────────────────────────────────────────────

    [Fact]
    public void Sanitize_RedactsAuthorizationBearerHeader()
    {
        var sanitized = TokenSanitizer.Sanitize("Authorization: Bearer abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNO12");

        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", sanitized);
        Assert.Contains("Authorization: Bearer [REDACTED]", sanitized);
    }

    [Theory]
    [InlineData("AUTHORIZATION: BEARER my-token-value", "AUTHORIZATION: BEARER [REDACTED]")]
    [InlineData("authorization: bearer my-token-value", "authorization: bearer [REDACTED]")]
    [InlineData("Authorization:Bearer my-token-value", "Authorization:Bearer [REDACTED]")]
    [InlineData("Authorization :  Bearer   my-token-value", "Authorization :  Bearer   [REDACTED]")]
    public void Sanitize_BearerHeader_CaseAndSpacingVariants(string input, string expectedResult)
    {
        Assert.Equal(expectedResult, TokenSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_BearerToken_StopsAtWhitespace()
    {
        var sanitized = TokenSanitizer.Sanitize("Authorization: Bearer abc123 other text continues here");

        Assert.Contains("[REDACTED]", sanitized);
        Assert.Contains("other text continues here", sanitized);
        Assert.DoesNotContain("abc123", sanitized);
    }

    [Fact]
    public void Sanitize_BearerInLog_RedactsTokenOnly()
    {
        var input = "2024-01-15 Sending request with Authorization: Bearer tok-secret remaining-log-context";
        var sanitized = TokenSanitizer.Sanitize(input);

        Assert.DoesNotContain("tok-secret", sanitized);
        Assert.Contains("2024-01-15", sanitized);
        Assert.Contains("remaining-log-context", sanitized);
    }

    // ── JSON secret fields ─────────────────────────────────────────────────

    [Fact]
    public void Sanitize_RedactsJsonTokenFields()
    {
        var sanitized = TokenSanitizer.Sanitize("""{"authToken":"super-secret-value","other":"visible"}""");

        Assert.Contains(""""authToken":"[REDACTED]"""", sanitized);
        Assert.Contains(""""other":"visible"""", sanitized);
    }

    [Theory]
    [InlineData("token", """{"token":"my-secret"}""")]
    [InlineData("secret", """{"secret":"my-secret"}""")]
    [InlineData("bearer", """{"bearer":"my-secret"}""")]
    [InlineData("authorization", """{"authorization":"my-secret"}""")]
    [InlineData("access_token", """{"access_token":"my-secret"}""")]
    [InlineData("client_secret", """{"client_secret":"my-secret"}""")]
    [InlineData("BEARER_TOKEN", """{"BEARER_TOKEN":"my-secret"}""")]
    public void Sanitize_JsonFieldsContainingKeyword_AreRedacted(string key, string input)
    {
        var sanitized = TokenSanitizer.Sanitize(input);

        Assert.DoesNotContain("my-secret", sanitized);
        Assert.Contains(key, sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", sanitized);
    }

    [Fact]
    public void Sanitize_JsonFieldWithoutSecretKeyword_IsNotRedacted()
    {
        var sanitized = TokenSanitizer.Sanitize("""{"username":"alice","email":"alice@example.com"}""");

        Assert.Contains("alice", sanitized);
        Assert.DoesNotContain("[REDACTED]", sanitized);
    }

    [Fact]
    public void Sanitize_MultipleJsonSecretFields_AllRedacted()
    {
        var input = """{"token":"tok1","secret":"sec1","name":"alice","authorization":"auth1"}""";
        var sanitized = TokenSanitizer.Sanitize(input);

        Assert.DoesNotContain("tok1", sanitized);
        Assert.DoesNotContain("sec1", sanitized);
        Assert.DoesNotContain("auth1", sanitized);
        Assert.Contains("alice", sanitized);
    }

    // ── Long base64-url token shape ────────────────────────────────────────

    [Fact]
    public void Sanitize_RedactsBareMcpTokenShape()
    {
        var token = new string('A', 43);
        var sanitized = TokenSanitizer.Sanitize($"token is {token}");

        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("[REDACTED_TOKEN]", sanitized);
    }

    [Fact]
    public void Sanitize_RedactsBareGatewayHexTokenShape()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var sanitized = TokenSanitizer.Sanitize($"argv: openclaw devices approve --token {token}");

        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("[REDACTED_TOKEN]", sanitized);
    }

    [Fact]
    public void Sanitize_RedactsBareGatewayHexTokenShapeWithUppercaseHex()
    {
        const string token = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

        var sanitized = TokenSanitizer.Sanitize($"argv: openclaw devices approve --token {token}");

        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("[REDACTED_TOKEN]", sanitized);
    }

    [Fact]
    public void Sanitize_DoesNotRedactGatewayHexTokenAdjacentToHexCharacters()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var sanitized = TokenSanitizer.Sanitize($"x{token}f");

        Assert.Equal($"x{token}f", sanitized);
    }

    [Fact]
    public void Sanitize_TokenAtStartOfString_IsRedacted()
    {
        var token = new string('x', 43);
        var sanitized = TokenSanitizer.Sanitize($"{token} suffix");

        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("[REDACTED_TOKEN]", sanitized);
        Assert.Contains("suffix", sanitized);
    }

    [Fact]
    public void Sanitize_TokenAtEndOfString_IsRedacted()
    {
        var token = new string('Z', 43);
        var sanitized = TokenSanitizer.Sanitize($"prefix {token}");

        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("prefix", sanitized);
        Assert.Contains("[REDACTED_TOKEN]", sanitized);
    }

    [Fact]
    public void Sanitize_ShortToken_NotRedacted()
    {
        // The pattern requires exactly 43 chars; 42-char sequences are NOT redacted.
        var shortToken = new string('A', 42);
        var sanitized = TokenSanitizer.Sanitize($"token is {shortToken} here");

        Assert.Contains(shortToken, sanitized);
        Assert.DoesNotContain("[REDACTED_TOKEN]", sanitized);
    }

    [Fact]
    public void Sanitize_LongerToken44Chars_NotRedacted()
    {
        // 44-char sequences are not matched (pattern anchors at exactly 43 within word boundaries).
        var longToken = new string('A', 44);
        var sanitized = TokenSanitizer.Sanitize($"token is {longToken} here");

        Assert.Contains(longToken, sanitized);
    }

    [Fact]
    public void Sanitize_MultipleTokensInSameString_AllRedacted()
    {
        var t1 = new string('A', 43);
        var t2 = new string('B', 43);
        var sanitized = TokenSanitizer.Sanitize($"first={t1} second={t2}");

        Assert.DoesNotContain(t1, sanitized);
        Assert.DoesNotContain(t2, sanitized);
        Assert.Equal(2, CountOccurrences(sanitized, "[REDACTED_TOKEN]"));
    }

    // ── combinations ───────────────────────────────────────────────────────

    [Fact]
    public void Sanitize_BearerAndJsonInSameString_BothRedacted()
    {
        var input = """Authorization: Bearer tok123 {"apiToken":"api-secret"}""";
        var sanitized = TokenSanitizer.Sanitize(input);

        Assert.DoesNotContain("tok123", sanitized);
        Assert.DoesNotContain("api-secret", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
    }

    [Fact]
    public void SanitizeLogMessage_RedactsLocalUserPaths()
    {
        var sanitized = TokenSanitizer.SanitizeLogMessage(@"Failed reading C:\Users\alice\AppData\Local\OpenClawTray\settings.json");

        Assert.DoesNotContain("alice", sanitized);
        Assert.Contains("%USERPROFILE%", sanitized);
    }

    [Fact]
    public void SanitizeLogMessage_RedactsNetworkAndIdentityValues()
    {
        var sanitized = TokenSanitizer.SanitizeLogMessage("Connect to ws://gateway.example.com:19001/ as alice@example.com via 10.1.2.3 and alice@server:22");

        Assert.DoesNotContain("gateway.example.com", sanitized);
        Assert.DoesNotContain("alice@example.com", sanitized);
        Assert.DoesNotContain("10.1.2.3", sanitized);
        Assert.DoesNotContain("alice@server", sanitized);
        Assert.Contains("ws://<host>:19001/", sanitized);
        Assert.Contains("<email>", sanitized);
        Assert.Contains("<ip>", sanitized);
        Assert.Contains("<user>@<host>:22", sanitized);
    }

    [Fact]
    public void SanitizeLogMessage_RedactsUrlUserInfoCredentials()
    {
        var sanitized = TokenSanitizer.SanitizeLogMessage("Connect to http://alice:s3cret@gateway.example.com/path now");

        Assert.DoesNotContain("alice", sanitized);
        Assert.DoesNotContain("s3cret", sanitized);
        Assert.DoesNotContain("gateway.example.com", sanitized);
        Assert.Contains("http://<host>/path", sanitized);
    }

    [Fact]
    public void SanitizeLogMessage_RedactsBracketedIpV6Host()
    {
        var sanitized = TokenSanitizer.SanitizeLogMessage("Listening on ws://[::1]:19001/socket");

        Assert.DoesNotContain("[::1]", sanitized);
        Assert.Contains("ws://<host>:19001/socket", sanitized);
    }

    [Theory]
    [InlineData("fe80::1234")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:db8:0:0:0:0:0:1")]
    public void SanitizeLogMessage_RedactsRawIpV6Literals(string ipv6)
    {
        var sanitized = TokenSanitizer.SanitizeLogMessage($"Peer at {ipv6} disconnected");

        Assert.DoesNotContain(ipv6, sanitized);
        Assert.Contains("<ipv6>", sanitized);
        Assert.Contains("Peer at", sanitized);
        Assert.Contains("disconnected", sanitized);
    }

    [Fact]
    public void SanitizeLogMessage_DoesNotRedactTimestamps()
    {
        // HH:MM:SS timestamps must not be misclassified as IPv6 literals.
        const string input = "2024-01-15 14:58:46 Connection established";
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.Contains("14:58:46", sanitized);
        Assert.DoesNotContain("<ipv6>", sanitized);
    }

    [Fact]
    public void SanitizeLogMessage_DoesNotRedactBracketedTimestampsOrShortHexTokens()
    {
        // Bracketed values that lack IPv6 structure must not be redacted: [HH:MM:SS], [hexword], [hex:hex].
        const string input = "Event [14:58:46] tag=[face] id=[abc] correlation=[dead:beef]";
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.Contains("[14:58:46]", sanitized);
        Assert.Contains("[face]", sanitized);
        Assert.Contains("[abc]", sanitized);
        Assert.Contains("[dead:beef]", sanitized);
        Assert.DoesNotContain("<ipv6>", sanitized);
    }

    [Theory]
    [InlineData("::ffff:192.0.2.1")]
    [InlineData("::192.0.2.1")]
    [InlineData("2001:db8::ffff:192.0.2.1")]
    [InlineData("2001:db8:0:0:0:ffff:192.0.2.1")]
    public void SanitizeLogMessage_RedactsIpV6WithEmbeddedIpV4AsSingleToken(string mixed)
    {
        var input = $"Peer at {mixed} disconnected";
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        // Whole address collapses to a single <ipv6> token — no leftover hex prefix and no split <ipv6>:<ip>.
        Assert.DoesNotContain(mixed, sanitized);
        Assert.DoesNotContain("192.0.2.1", sanitized);
        Assert.DoesNotContain("ffff:", sanitized);
        Assert.DoesNotContain("<ip>", sanitized);
        Assert.Contains("<ipv6>", sanitized);
        Assert.Equal("Peer at <ipv6> disconnected", sanitized);
    }

    [Fact]
    public void SanitizeLogMessage_RedactsUrlUserInfoWithoutPassword()
    {
        // Userinfo with no colon (token-style) must also be dropped.
        const string input = "Connecting to http://[email protected]/path";
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.DoesNotContain("secret-token", sanitized);
        Assert.DoesNotContain("example.com", sanitized);
        Assert.Contains("http://<host>", sanitized);
    }

    [Fact]
    public void SanitizeLogMessage_RedactsUrlUserInfoWithBracketedIpV6Host()
    {
        const string input = "auth url https://user:pw@[2001:db8::1]:8443/api";
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.DoesNotContain("user", sanitized);
        Assert.DoesNotContain(":pw@", sanitized);
        Assert.DoesNotContain("2001:db8::1", sanitized);
        Assert.Contains("https://<host>", sanitized);
    }

    [Fact]
    public void Sanitize_DoesNotRedactNetworkOrIdentityValues()
    {
        // The non-log Sanitize overload must leave URLs, IPs, IPv6 literals and emails intact —
        // it only strips secrets/tokens. Callers using Sanitize for non-log paths rely on this.
        const string input = "see http://user:[email protected]/x ip=192.168.1.1 ipv6=2001:db8::1 [email protected]";
        var sanitized = TokenSanitizer.Sanitize(input);

        Assert.Contains("http://user:[email protected]/x", sanitized);
        Assert.Contains("192.168.1.1", sanitized);
        Assert.Contains("2001:db8::1", sanitized);
        Assert.Contains("[email protected]", sanitized);
    }

    // ── URL tail redaction ──────────────────────────────────────────────────
    //
    // SanitizeLogMessage must drop credential-bearing URL tails — query strings, fragments,
    // and path segments past the first — because disk-backed logs effectively live forever and
    // tokens/codes/signatures/PII routinely appear after the host. Mirrors the UrlLogSanitizer
    // contract.

    [Theory]
    [InlineData("Reset link: https://login.example.com/reset?token=abc123 end", "token=abc123")]
    [InlineData("OAuth callback: https://example.com/oauth/callback?code=xyz789 done", "code=xyz789")]
    [InlineData("Signed url: https://api.example.com/v1/items?sig=deadbeef list", "sig=deadbeef")]
    [InlineData("Magic link: https://app.example.com/m/[email protected] go", "alice")]
    [InlineData("Fragment: https://example.com/a#access_token=secretval done", "access_token=secretval")]
    [InlineData("Deep path: https://example.com/a/b/c/d/secret end", "secret")]
    public void SanitizeLogMessage_DropsCredentialBearingUrlTails(string input, string forbiddenFragment)
    {
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.DoesNotContain(forbiddenFragment, sanitized);
        Assert.Contains("<host>", sanitized);
    }

    [Theory]
    [InlineData("Connect to http://gateway.example.com/path now", "Connect to http://<host>/path now")]
    [InlineData("Reset: https://login.example.com/reset?token=abc123 end", "Reset: https://<host>/reset end")]
    [InlineData("OAuth: https://example.com/oauth/callback?code=xyz789 done", "OAuth: https://<host>/oauth/… done")]
    [InlineData("Deep: https://example.com/a/b/c/d/secret end", "Deep: https://<host>/a/… end")]
    [InlineData("Bare host: https://example.com end", "Bare host: https://<host>/ end")]
    [InlineData("Trailing period: see https://example.com/api.", "Trailing period: see https://<host>/api.")]
    [InlineData("Non-default port: ws://[::1]:19001/socket open", "Non-default port: ws://<host>:19001/socket open")]
    public void SanitizeLogMessage_CollapsesUrlsToFirstSegment(string input, string expected)
    {
        Assert.Equal(expected, TokenSanitizer.SanitizeLogMessage(input));
    }

    [Fact]
    public void SanitizeLogMessage_UnparseableUrl_FallsBackToSchemeAndHostPlaceholder()
    {
        // Inputs that match the URL regex but cannot be parsed by Uri.TryCreate must still
        // be redacted, not echoed back verbatim. (e.g., empty-host pseudo-URLs.)
        const string input = "Saw https://[ malformed";
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.DoesNotContain("https://[", sanitized);
        Assert.Contains("https://<host>", sanitized);
    }

    [Theory]
    [InlineData("Encoded slash: https://example.com/path%2Fsecret%2Ftoken end", "secret")]
    [InlineData("Lowercase: https://example.com/a%2fb%2ftoken123 end", "token123")]
    public void SanitizeLogMessage_PercentEncodedSlashes_DoNotBypassFirstSegmentTruncation(
        string input, string forbiddenFragment)
    {
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.DoesNotContain(forbiddenFragment, sanitized);
        Assert.DoesNotContain("%2F", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<host>", sanitized);
    }

    [Theory]
    [InlineData("Backslash URL: https://example.com\\reset?token=abc123 end", "token=abc123")]
    [InlineData("Mixed: https://host.example\\path\\secret end", "secret")]
    public void SanitizeLogMessage_BackslashInUrl_DoesNotLeakTail(string input, string forbiddenFragment)
    {
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.DoesNotContain(forbiddenFragment, sanitized);
        Assert.Contains("<host>", sanitized);
    }

    // ── URL log-injection defense ───────────────────────────────────────────
    //
    // Uri.UnescapeDataString (used inside RedactUrlMatch to expand %2F into real path
    // boundaries) also decodes %0A/%0D/%00/%09/%1B into raw control bytes. Those bytes,
    // if written into the log line, would forge new log entries, corrupt JSONL framing,
    // or smuggle ANSI escapes into terminal log viewers. RedactUrlMatch must strip
    // C0 control chars and DEL from the unescaped path before composing its output.

    [Theory]
    [InlineData("URL: https://host.example/path%0AFAKE-LINE end")]
    [InlineData("URL: https://host.example/api%0D%0AInjected end")]
    [InlineData("URL: https://host.example/ok%00secret end")]
    [InlineData("URL: https://host.example/log%1B%5B31mRED end")]
    [InlineData("URL: https://host.example/col%09umn end")]
    [InlineData("URL: https://host.example/nel%C2%85injected end")]
    [InlineData("URL: https://host.example/lsep%E2%80%A8injected end")]
    [InlineData("URL: https://host.example/psep%E2%80%A9injected end")]
    public void SanitizeLogMessage_PercentEncodedControlChars_DoNotAppearInOutput(string input)
    {
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.DoesNotMatch(@"[\x00-\x1F\x7F\u0085\u2028\u2029]", sanitized);
        Assert.Contains("<host>", sanitized);
    }

    [Theory]
    [InlineData("[1:2:3:4:5]")]
    [InlineData("[a:b:c:d:e]")]
    [InlineData("[::::]")]
    [InlineData("[1:2:3:4:5:6:7:8:9]")]
    public void SanitizeLogMessage_DoesNotRedactBracketedContentThatIsNotValidIpV6(string token)
    {
        // Bracketed lookahead admits "5+ colons" or "::" but System.Net.IPAddress validates the
        // candidate, so invalid bracketed colon-soup is left intact rather than misleadingly
        // redacted as <ipv6>.
        var input = $"value {token} end";
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.Contains(token, sanitized);
        Assert.DoesNotContain("<ipv6>", sanitized);
    }

    [Fact]
    public void SanitizeLogMessage_DoesNotLeakPartialIpV6OnInvalidSuffix()
    {
        // 'b' suffix makes a::ffff:192.0.2.1b neither a valid IPv6 (alt 2's trailing \b fails
        // between '1' and 'b') nor a valid IPv4 (\b fails at the same boundary). The trailing
        // (?![A-Fa-f0-9:]|\.\d) on alts 3/5 must keep alt 3 from partially matching a::ffff:192
        // and emitting <ipv6>.0.2.1b. Asserts exact output so a future regex change that
        // incorrectly emits any <ipv6> token gets caught.
        const string input = "Peer at a::ffff:192.0.2.1b is wrong";
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.Equal(input, sanitized);
    }

    [Theory]
    [InlineData("Server at fe80::1.", "Server at <ipv6>.")]
    [InlineData("Reached ::1. Done.", "Reached <ipv6>. Done.")]
    [InlineData("Host 2001:db8::1, next step", "Host <ipv6>, next step")]
    [InlineData("Up at fe80::abcd! end", "Up at <ipv6>! end")]
    public void SanitizeLogMessage_RedactsIpV6FollowedBySentencePunctuation(string input, string expected)
    {
        // The negative lookahead `(?![A-Fa-f0-9:]|\.\d)` deliberately permits `.` not followed
        // by a digit so sentence-ending punctuation does not block legitimate IPv6 redaction.
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.Equal(expected, sanitized);
    }

    [Theory]
    [InlineData("Link-local fe80::1%eth0 reachable", "Link-local <ipv6> reachable")]
    [InlineData("Link-local fe80::1%wlan0 reachable", "Link-local <ipv6> reachable")]
    [InlineData("Bracketed [fe80::1%eth0]:8443 end", "Bracketed [<ipv6>]:8443 end")]
    [InlineData("Numeric fe80::1%12 reachable", "Numeric <ipv6> reachable")]
    [InlineData("Bridged fe80::1%br-1234 reachable", "Bridged <ipv6> reachable")]
    [InlineData("Vlan fe80::1%eth0.1 reachable", "Vlan <ipv6> reachable")]
    [InlineData("Wireless fe80::1%wlan_0 reachable", "Wireless <ipv6> reachable")]
    public void SanitizeLogMessage_RedactsIpV6WithZoneIdentifier(string input, string expected)
    {
        // IPAddress.TryParse rejects textual zone-ids (fe80::1%eth0) but accepts numeric ones
        // (fe80::1%12). RedactIfValidIpV6 strips the `%<scope>` suffix before parsing so both
        // forms redact consistently. Zone-id char class covers RFC 6874 unreserved chars
        // (-A-Za-z0-9._~) so real interface names like br-1234, eth0.1, wlan_0 are fully
        // consumed instead of leaking the suffix.
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void SanitizerTimeoutSentinel_IsExposedAsPublicConstant()
    {
        // The fail-closed sentinel must be a stable public contract so log-readers and
        // diagnostics tooling can recognise timed-out sanitization without false-positive
        // string matches against real content.
        Assert.Equal("[REDACTED_SANITIZER_TIMEOUT]", TokenSanitizer.SanitizerTimeoutSentinel);
    }

    [Fact]
    public void SanitizeLogMessage_DoesNotThrowOnAdversarialInput()
    {
        // SanitizeLogMessage wraps every regex pass in a single try/catch that returns the
        // SanitizerTimeoutSentinel on RegexMatchTimeoutException, so the sanitizer never
        // tears down the logging / diagnostics / crash-report pipelines on adversarial input.
        // Even if no pattern actually times out here, we must not throw for any reason.
        var adversarial = new string(':', 5_000) + new string('a', 5_000);

        var exception = Record.Exception(() => TokenSanitizer.SanitizeLogMessage(adversarial));

        Assert.Null(exception);
    }

    [Fact]
    public void SanitizeLogMessage_DoesNotRedactNonHexScopeIdentifiers()
    {
        // C++/Rust scope-resolution identifiers containing non-hex letters never match the
        // IPv6 regex character class and so are left intact (e.g. stack traces in logs).
        const string input = "Error in MyNs::MyClass::DoThing(int) at line 42";
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.Contains("MyNs::MyClass::DoThing(int)", sanitized);
        Assert.DoesNotContain("<ipv6>", sanitized);
    }

    [Theory]
    [InlineData("dead::beef")]
    [InlineData("cafe::face")]
    public void SanitizeLogMessage_RedactsHexOnlyScopeIdentifiersThatParseAsIpV6(string token)
    {
        // Documents an accepted tradeoff: hex-only "scope-id-shaped" strings that System.Net
        // accepts as valid compressed IPv6 (e.g. dead::beef == dead:0:0:0:0:0:0:beef) are
        // redacted. False positives here are rare in real logs and the alternative — tightening
        // the regex to require 2+ groups on one side — would drop legitimate link-local forms
        // like fe80::1. Pin behavior so future regex edits surface this tradeoff explicitly.
        var input = $"context {token} end";
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.Contains("<ipv6>", sanitized);
        Assert.DoesNotContain(token, sanitized);
    }

    [Fact]
    public void SanitizeLogMessage_PreservesExistingIpV6Marker()
    {
        // Pre-redacted text passing through SanitizeLogMessage a second time must keep the
        // <ipv6> marker intact — none of the downstream passes (IPv4, email, host, etc.) should
        // recognise the literal string '<ipv6>' as sensitive. Locks the pipeline invariant
        // against future pattern changes.
        const string input = "Already-redacted host=<ipv6> port=8443 end";
        var sanitized = TokenSanitizer.SanitizeLogMessage(input);

        Assert.Equal(input, sanitized);
    }

    private static int CountOccurrences(string source, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
