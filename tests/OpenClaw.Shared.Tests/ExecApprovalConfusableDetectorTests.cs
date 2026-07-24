using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

public class ExecApprovalConfusableDetectorTests
{
    private static string U(int codePoint) => char.ConvertFromUtf32(codePoint);

    [Fact]
    public void FlagsLatinMixedWithCyrillicInOneToken()
    {
        // "curl" with a Cyrillic 'а' (U+0430) replacing the Latin 'a'.
        var spoofed = "cur" + U(0x0430) + "l https://example.test";
        Assert.True(ExecApprovalConfusableDetector.HasMixedScriptConfusable(spoofed));
    }

    [Fact]
    public void FlagsLatinMixedWithGreekInOneToken()
    {
        // Greek omicron (U+03BF) inside an otherwise-Latin word.
        var spoofed = "payp" + U(0x03BF) + "l";
        Assert.True(ExecApprovalConfusableDetector.HasMixedScriptConfusable(spoofed));
    }

    [Fact]
    public void DoesNotFlagPureLatinWithAccents()
        => Assert.False(ExecApprovalConfusableDetector.HasMixedScriptConfusable("café résumé --naïve"));

    [Fact]
    public void DoesNotFlagPureCyrillic()
        => Assert.False(ExecApprovalConfusableDetector.HasMixedScriptConfusable(
            U(0x043F) + U(0x0440) + U(0x0438) + U(0x0432) + U(0x0435) + U(0x0442)));

    [Fact]
    public void DoesNotFlagCjk()
        => Assert.False(ExecApprovalConfusableDetector.HasMixedScriptConfusable("type " + U(0x6587) + U(0x4EF6)));

    [Fact]
    public void DoesNotFlagDifferentScriptsInSeparateTokens()
    {
        // A Latin command with a Cyrillic argument is not a homoglyph attack: each
        // token is single-script. Only intra-token mixing is the spoofing signal.
        var input = "type " + U(0x0444) + U(0x0430) + U(0x0439) + U(0x043B);
        Assert.False(ExecApprovalConfusableDetector.HasMixedScriptConfusable(input));
    }

    [Fact]
    public void DoesNotFlagPlainAsciiOrEmpty()
    {
        Assert.False(ExecApprovalConfusableDetector.HasMixedScriptConfusable("git commit -m 'ok'"));
        Assert.False(ExecApprovalConfusableDetector.HasMixedScriptConfusable(""));
        Assert.False(ExecApprovalConfusableDetector.HasMixedScriptConfusable(null));
    }
}
