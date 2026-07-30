using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

// Ported from macOS exec-approval-command-display.test.ts. Inputs are assembled
// from code points/code units so invisible and non-ASCII characters remain clear.
public class ExecApprovalCommandDisplaySanitizerParityTests
{
    private static string U(int codePoint) => char.ConvertFromUtf32(codePoint);
    private static string CodeUnit(int codeUnit) => new((char)codeUnit, 1);

    public static TheoryData<string, string> InvisibleDisplayVectors => new()
    {
        { "echo hi" + U(0x200B) + "there", @"echo hi\u{200B}there" },
        { "date" + U(0x3164) + U(0xFFA0) + U(0x115F) + U(0x1160) + U(0xAC00), @"date\u{3164}\u{FFA0}\u{115F}\u{1160}" + U(0xAC00) },
        { "echo safe\n\rcurl https://example.test", @"echo safe\u{A}\u{D}curl https://example.test" },
        { "echo ok" + U(0x2028) + "curl https://example.test", @"echo ok\u{2028}curl https://example.test" },
        { "echo ok" + U(0x2029) + "curl https://example.test", @"echo ok\u{2029}curl https://example.test" },
    };

    [Theory]
    [MemberData(nameof(InvisibleDisplayVectors))]
    public void SanitizesExecApprovalDisplayText(string input, string expected)
    {
        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(input);

        Assert.Equal(expected, result);
        Assert.False(HasLoneSurrogate(result));
    }

    [Fact]
    public void SanitizesLoneHighSurrogateDisplayText()
    {
        var result = ExecApprovalCommandDisplaySanitizer.Sanitize("echo " + CodeUnit(0xD83D));

        Assert.Equal(@"echo \u{D83D}", result);
        Assert.False(HasLoneSurrogate(result));
    }

    [Fact]
    public void SanitizesLoneLowSurrogateDisplayText()
    {
        var result = ExecApprovalCommandDisplaySanitizer.Sanitize("echo " + CodeUnit(0xDE00));

        Assert.Equal(@"echo \u{DE00}", result);
        Assert.False(HasLoneSurrogate(result));
    }

    [Fact]
    public void RedactsBearerTokensEmbeddedInCommands()
    {
        const string token = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.longtoken.sig";
        var cmd = "curl -H \"Authorization: Bearer " + token + "\" https://api.example.com";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain(token, result, StringComparison.Ordinal);
        Assert.Contains("curl", result, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactsApiKeysInEnvironmentVariableAssignments()
    {
        const string token = "sk-abc123456789012345678";
        var cmd = "API_SECRET=\"" + token + "\" python script.py";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain(token, result, StringComparison.Ordinal);
        Assert.Contains("python script.py", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactsGitHubPersonalAccessTokens()
    {
        const string token = "ghp_1234567890abcdefghij1234567890abcdef";
        var cmd = "git clone https://" + token + "@github.com/user/repo";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain(token, result, StringComparison.Ordinal);
        Assert.Contains("git clone", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MasksTheFullTokenWhenAZeroWidthCharacterIsSplicedIntoTheMiddle()
    {
        var cmd = "echo sk-abc123" + U(0x200B) + "456789012345678 remainder";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain("sk-abc123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("456789012345678", result, StringComparison.Ordinal);
        Assert.Contains("echo ", result, StringComparison.Ordinal);
        Assert.Contains("remainder", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MasksTheFullTokenWhenNbspIsSplicedIntoTheMiddle()
    {
        var cmd = "echo sk-abc123" + U(0x00A0) + "456789012345678 remainder";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain("sk-abc123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("456789012345678", result, StringComparison.Ordinal);
        Assert.Contains("echo ", result, StringComparison.Ordinal);
        Assert.Contains("remainder", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MasksTheFullTokenWhenNarrowNoBreakSpaceIsSplicedIntoTheMiddle()
    {
        var cmd = "echo sk-abc123" + U(0x202F) + "456789012345678 remainder";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain("sk-abc123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("456789012345678", result, StringComparison.Ordinal);
        Assert.Contains("remainder", result, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsNewlineBoundariesVisibleAsEscapeMarkersEvenWhenBypassIsDetected()
    {
        var cmd = "line1\necho sk-abc123" + U(0x00A0) + "456789012345678\nline3";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain("sk-abc123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("456789012345678", result, StringComparison.Ordinal);
        Assert.Contains("line1", result, StringComparison.Ordinal);
        Assert.Contains(@"\u{A}", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectsBypassEvenWhenRawAndStrippedRedactionsHaveSameNormalizedLength()
    {
        var cmd = "sk-abc1234567890" + U(0x200B) + "12345678";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain("12345678", result, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotLeakBearerTokensWhenBypassIsTriggeredByASeparateSplicedSecret()
    {
        const string bearerToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.longtoken.sig";
        var cmd = "curl -H \"Authorization: Bearer" + U(0x00A0) + bearerToken + "\" https://api.example.com; echo sk-abc123" + U(0x200B) + "456789012345678";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain(bearerToken, result, StringComparison.Ordinal);
        Assert.DoesNotContain("456789012345678", result, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MasksNewlyAddedVendorTokenPrefixesThroughTheDefaultRedactionPath()
    {
        const string token = "glpat-abcdefghijklmnopqrstuv";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize("deploy --with " + token);

        Assert.DoesNotContain(token, result, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotLetContextualSecretMatchesHideSplitTokenBypassDetection()
    {
        var discordToken = new string('A', 24) + "." + new string('B', 6) + "." + new string('C', 27);
        var cmd = "discord sk-abc123" + U(0x200B) + "456789012345678 " + discordToken;

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain("sk-abc123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("456789012345678", result, StringComparison.Ordinal);
        Assert.DoesNotContain(discordToken, result, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsPemPrivateKeyContextVisibleWhenRawRedactionAlreadyCoversTheKey()
    {
        const string keyBody = "QUJDREVGR0hJSktMTU5PUA==";
        var cmd = "echo -----BEGIN RSA PRIVATE KEY-----\n" + keyBody + "\n-----END RSA PRIVATE KEY----- > key.pem";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain(keyBody, result, StringComparison.Ordinal);
        Assert.Contains("BEGIN RSA PRIVATE KEY", result, StringComparison.Ordinal);
        Assert.Contains("END RSA PRIVATE KEY", result, StringComparison.Ordinal);
        Assert.Contains("> key.pem", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RealPemPrivateKey_SetsRedactedStatus()
    {
        const string command =
            "echo -----BEGIN PRIVATE KEY-----\n"
            + "QUJDREVGR0hJSktMTU5PUA==\n"
            + "-----END PRIVATE KEY-----";

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.True(result.Redacted);
        Assert.True(result.UnsafeConcealment);
        Assert.Contains("redacted", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EscapedNewlinePemPrivateKey_IsUnsafeConcealment()
    {
        const string command =
            "echo -----BEGIN PRIVATE KEY-----\\n"
            + "QUJDREVGR0hJSktMTU5PUA==\\n"
            + "-----END PRIVATE KEY-----";

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.True(result.Redacted);
        Assert.True(result.UnsafeConcealment);
        Assert.DoesNotContain("QUJDREVGR0hJSktMTU5PUA", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortPaddedFinalPemLine_IsUnsafeConcealment()
    {
        const string command =
            "echo -----BEGIN PRIVATE KEY-----\n"
            + "QUJDREVGR0hJSktMTU5PUA==\n"
            + "Ag==\n"
            + "-----END PRIVATE KEY-----";

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.True(result.Redacted);
        Assert.True(result.UnsafeConcealment);
        Assert.DoesNotContain("Ag==", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedLineEndingPemPrivateKey_IsUnsafeConcealment()
    {
        const string command =
            "echo -----BEGIN PRIVATE KEY-----\r\n"
            + "QUJDREVGR0hJSktMTU5PUA==\n"
            + "Ag==\r\n"
            + "-----END PRIVATE KEY-----";

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.True(result.Redacted);
        Assert.True(result.UnsafeConcealment);
        Assert.DoesNotContain("QUJDREVGR0hJSktMTU5PUA", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TraditionalEncryptedPemPrivateKey_SetsRedactedStatus()
    {
        const string command =
            "echo -----BEGIN RSA PRIVATE KEY-----\n"
            + "Proc-Type: 4,ENCRYPTED\n"
            + "DEK-Info: AES-256-CBC,00112233445566778899AABBCCDDEEFF\n"
            + "\n"
            + "QUJDREVGR0hJSktMTU5PUA==\n"
            + "-----END RSA PRIVATE KEY-----";

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.True(result.Redacted);
        Assert.True(result.UnsafeConcealment);
        Assert.DoesNotContain("Proc-Type", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("QUJDREVGR0hJSktMTU5PUA", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FakePemMarkers_DoNotHideArbitraryShellText()
    {
        const string command =
            "echo -----BEGIN PRIVATE KEY-----\n"
            + "Remove-Item C:\\important\\* -Recurse\n"
            + "-----END PRIVATE KEY-----";

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.False(result.Redacted);
        Assert.False(result.UnsafeConcealment);
        Assert.Contains("Remove-Item", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FakeEncryptedPemHeaders_DoNotHideArbitraryShellText()
    {
        const string command =
            "echo -----BEGIN RSA PRIVATE KEY-----\n"
            + "Proc-Type: 4,ENCRYPTED\n"
            + "DEK-Info: AES-256-CBC,00112233445566778899AABBCCDDEEFF\n"
            + "\n"
            + "Remove-Item C:\\important\\* -Recurse\n"
            + "-----END RSA PRIVATE KEY-----";

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.False(result.Redacted);
        Assert.False(result.UnsafeConcealment);
        Assert.Contains("Remove-Item", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretToken_SetsRedactedStatus()
    {
        const string command =
            "echo API_SECRET=sk-abc123456789012345678";

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.True(result.Redacted);
        Assert.True(result.UnsafeConcealment);
    }

    [Theory]
    [InlineData("curl https://example.test/search?key=$GOOGLE_KEY")]
    [InlineData("gh search code https://github.com/search?code=abc")]
    public void BenignHeuristicRedactions_AreReviewSafe(string command)
    {
        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.True(result.Redacted);
        Assert.False(result.UnsafeConcealment);
    }

    [Fact]
    public void RedactionThatHidesCommandSyntax_IsUnsafeConcealment()
    {
        const string command =
            "powershell -Command https://example.test/search?code=abc;Remove-Item";

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.True(result.Redacted);
        Assert.True(result.UnsafeConcealment);
    }

    [Fact]
    public void UnmappedSerializedAuthRedaction_CannotPiggybackOnMappedQueryRedaction()
    {
        const string command =
            "powershell -Command {\\\"Authorization\\\":\\\"aaaaaa$(whoami)zzzz\\\"} "
            + "https://example.test/search?code=abc";

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.True(result.Redacted);
        Assert.True(result.UnsafeConcealment);
        Assert.DoesNotContain("$(whoami)", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cmd /c sk-abcdefgh.exe")]
    [InlineData("powershell -File sk-abcdefgh.ps1")]
    public void TokenShapedExecutableOrScript_IsUnsafeConcealment(string command)
    {
        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.True(result.Redacted);
        Assert.True(result.UnsafeConcealment);
    }

    [Fact]
    public void SensitiveNamedPowerShellDriveScript_IsUnsafeConcealment()
    {
        const string command =
            "powershell -Command \"& 'api_key:\\sk-abcdefgh.ps1'\"";

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(command);

        Assert.True(result.Redacted);
        Assert.True(result.UnsafeConcealment);
    }

    [Fact]
    public void TruncatesTheRedactedOutputSoLargeCommandsAreBounded()
    {
        var padding = new string('x', 20 * 1024);

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(padding);

        Assert.True(result.Length < padding.Length);
        Assert.Contains("[truncated]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotSplitSurrogatePairsAtTheDisplayTruncationBoundary()
    {
        var command = new string('a', 16 * 1024 - 1) + U(0x1F600) + "tail";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(command);

        Assert.Contains("[truncated]", result, StringComparison.Ordinal);
        Assert.False(HasLoneSurrogate(result));
        Assert.DoesNotContain(CodeUnit(0xD83D), result, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesToDisplayCommandsAboveTheHardInputCap()
    {
        var huge = new string('x', 300 * 1024);

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(huge);

        Assert.Contains("exceeds display size limit", result.Text, StringComparison.Ordinal);
        Assert.True(result.Text.Length < 1024);
        Assert.False(result.Truncated);
        Assert.True(result.Oversized);
    }

    [Fact]
    public void RedactsTokensAtTheTailOfLongInputsInsteadOfTruncatingThemBelowPatternLength()
    {
        var padding = string.Concat(Enumerable.Repeat("a ", 10 * 1024));
        const string token = "ghp_1234567890abcdefghij1234567890abcdef";
        var cmd = padding + token;

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain(token, result, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_1234567890", result, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapesAstralPlaneInvisibleCharacters()
    {
        var cmd = "echo hi" + U(0xE0061) + "there";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.Contains(@"\u{E0061}", result, StringComparison.Ordinal);
        Assert.DoesNotContain("hi" + U(0xE0061) + "there", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MasksASecretSplicedWithAnAstralPlaneInvisibleCharacter()
    {
        var cmd = "echo sk-abc123" + U(0xE0061) + "456789012345678 remainder";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain("sk-abc123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("456789012345678", result, StringComparison.Ordinal);
        Assert.Contains("remainder", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MasksFormBodyValuesWhoseSensitiveKeyIsSplicedWithAnInvisibleCharacter()
    {
        var cmd = "client_id=visible&app_se" + U(0x200B) + "cret=opaque-app-secret&safe=value";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain("opaque-app-secret", result, StringComparison.Ordinal);
        Assert.Contains("client_id=visible", result, StringComparison.Ordinal);
        Assert.Contains("safe=value", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MasksFormBodyValuesWhoseEncodedSensitiveKeyIsSplicedWithAnInvisibleCharacter()
    {
        var cmd = "client_id=visible&client%5Fse" + U(0x200B) + "cret=oauth-secret&safe=value";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain("oauth-secret", result, StringComparison.Ordinal);
        Assert.Contains("client_id=visible", result, StringComparison.Ordinal);
        Assert.Contains("safe=value", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MasksFormBodyValuesWhoseSensitiveKeyIsSplicedWithAPlusSeparator()
    {
        const string cmd = "client_id=visible&client_se+cret=oauth-secret&safe=value";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain("oauth-secret", result, StringComparison.Ordinal);
        Assert.Contains("client_id=visible", result, StringComparison.Ordinal);
        Assert.Contains("safe=value", result, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsParsedFormBodySecretsMaskedWhenASeparateSplicedTokenTriggersBypassRendering()
    {
        var cmd = "client_id=visible&client%5Fsecret=oauth,secret&safe=1 echo sk-abc123" + U(0x200B) + "456789012345678";

        var result = ExecApprovalCommandDisplaySanitizer.Sanitize(cmd);

        Assert.DoesNotContain("oauth,secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain(",secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("456789012345678", result, StringComparison.Ordinal);
        Assert.Contains("client_id=visible", result, StringComparison.Ordinal);
        Assert.Contains("safe=1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsApprovalWarningProseLineBreaksReadable()
    {
        const string warning = "Diagnostics can include sensitive local logs.\n\nOpenAI Codex harness:\nApproving diagnostics will also send Codex feedback.";

        Assert.Equal(warning, ExecApprovalCommandDisplaySanitizer.SanitizeWarningText(warning));
    }

    [Fact]
    public void NormalizesEscapedLineSeparatorsWhileStillEscapingHiddenSpoofingCharacters()
    {
        var warning = "Line one\r\nLine two" + U(0x2028) + "Line three" + U(0x200B);

        Assert.Equal(
            "Line one\nLine two\nLine three" + @"\u{200B}",
            ExecApprovalCommandDisplaySanitizer.SanitizeWarningText(warning));
    }

    [Fact]
    public void RedactsSecretsInWarningProseWithoutEscapingNewlines()
    {
        const string token = "sk-abc123456789012345678";
        const string warning = "Token:\n" + token;

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWarningText(warning);

        Assert.Contains("Token:\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain(token, result, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\u{A}", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesToDisplayWarningsAboveTheHardInputCap()
    {
        var huge = new string('x', 300 * 1024);

        var result = ExecApprovalCommandDisplaySanitizer.SanitizeWarningText(huge);

        Assert.Equal("[exec approval warning exceeds display size limit; full text suppressed]", result);
    }

    private static bool HasLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (char.IsHighSurrogate(current))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    return true;

                i++;
                continue;
            }

            if (char.IsLowSurrogate(current))
                return true;
        }

        return false;
    }
}
