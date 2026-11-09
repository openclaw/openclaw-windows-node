// Shared review-layout contract, independent of WinUI and without changing legacy inline rendering.
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class ExecApprovalReviewTextTests
{
    [Fact]
    public void ReviewText_PreservesWhitespaceAndRevealsControlsWithoutDecodingLiterals()
    {
        const string input = "  echo\tA\r\nnext\rhidden\u2028\uE000\u00A0\\u{000D}";
        var status = ExecApprovalCommandDisplaySanitizer.SanitizeCommandForReviewWithStatus(input);
        Assert.Equal("  echo\tA\nnext\\u{000D}hidden\\u{2028}\\u{E000}\\u{00A0}\\u{000D}", status.Text);
        Assert.False(status.Truncated);
        Assert.False(status.Redacted);
        // Existing chat/inline callers keep their original escape contract.
        Assert.Equal(@"echo\u{A}\u{9}x", ExecApprovalCommandDisplaySanitizer.Sanitize("echo\n\tx"));
    }

    [Theory]
    [InlineData("Authorization: Bearer s3cret42")]
    [InlineData("Autho\u200Brization: Bearer s3cret42")]
    [InlineData("Autho\uE000rization: Bearer s3cret42")]
    public void ReviewText_RedactsSecretsBeforeEscapingIncludingObfuscatedKeys(string input)
    {
        var status = ExecApprovalCommandDisplaySanitizer.SanitizeCommandForReviewWithStatus(input);
        Assert.DoesNotContain("s3cret42", status.Text);
        Assert.Contains("***", status.Text);
        Assert.True(status.Redacted);
        Assert.True(status.UnsafeConcealment);
    }

    [Fact]
    public void ReviewText_PreservesReviewSafeUrlRedactionSemantics()
    {
        var status = ExecApprovalCommandDisplaySanitizer.SanitizeCommandForReviewWithStatus("curl https://example.test/run?token=s3cret42");
        Assert.Equal("curl https://example.test/run?token=***", status.Text);
        Assert.True(status.Redacted);
        Assert.False(status.UnsafeConcealment);
    }

    [Fact]
    public void ReviewText_EnforcesOriginalInputAndRenderedOutputBudgets()
    {
        var oversized = ExecApprovalCommandDisplaySanitizer.SanitizeCommandForReviewWithStatus(string.Concat(Enumerable.Repeat("\r\n", 131073)));
        Assert.True(oversized.Oversized); // CRLF normalization must not bypass the input budget.
        Assert.Equal("[exec approval command exceeds display size limit; full text suppressed]", oversized.Text);
        var truncated = ExecApprovalCommandDisplaySanitizer.SanitizeCommandForReviewWithStatus(new string('x', 16383) + "😀");
        Assert.True(truncated.Truncated);
        Assert.Equal(new string('x', 16383) + "…[truncated]", truncated.Text);
        Assert.True(ExecApprovalCommandDisplaySanitizer.SanitizeCommandForReviewWithStatus(new string('\uE000', 3000)).Truncated);
    }
    [Fact]
    public void LegacyTextModesAlsoRedactPrivateUseObfuscatedCredentialKeys()
    {
        const string text = "Autho\uE000rization: Bearer s3cret42";
        Assert.DoesNotContain("s3cret42", ExecApprovalCommandDisplaySanitizer.Sanitize(text));
        Assert.DoesNotContain("s3cret42", ExecApprovalCommandDisplaySanitizer.SanitizeWarningText(text));
        Assert.True(ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(text).UnsafeConcealment);
    }
}
