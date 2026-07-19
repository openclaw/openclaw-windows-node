using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class ExecApprovalContextDisplaySanitizerTests
{
    [Fact]
    public void PreservesIntentionalMultilineContext()
    {
        var input = "Purpose: inspect state.\nRisk: low.\nRecommendation: allow once.";

        Assert.Equal(input, ExecApprovalContextDisplaySanitizer.Sanitize(input));
    }

    [Fact]
    public void EscapesBidirectionalAndZeroWidthFormatting()
    {
        var input = "Read only\u202Espoof\u200Bcontext";

        Assert.Equal(
            @"Read only\u{202E}spoof\u{200B}context",
            ExecApprovalContextDisplaySanitizer.Sanitize(input));
    }

    [Fact]
    public void BoundsRenderedContext()
    {
        var sanitized = ExecApprovalContextDisplaySanitizer.Sanitize(
            new string('a', 1_500),
            maxLength: 100);

        Assert.Equal(100, sanitized.Length);
        Assert.EndsWith("…", sanitized);
    }
}
