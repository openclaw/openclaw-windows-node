using System.Text;
using OpenClawTray.A2UI.Rendering;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Boundary tests for <see cref="SvgGeometryBudget.Exceeds"/>: the pre-decode guard that
/// keeps pathological SVG geometry away from D2D, whose parse can block the UI thread past
/// the render timeout.
/// </summary>
public sealed class A2UISvgGeometryBudgetTests
{
    private static readonly int Budget = SvgGeometryBudget.MaxAttributeChars;

    private static bool Exceeds(string svg) => SvgGeometryBudget.Exceeds(Encoding.UTF8.GetBytes(svg));

    private static SvgGeometryBudget.SvgBudgetLimit Limit(string svg) =>
        SvgGeometryBudget.FindExceededLimit(Encoding.UTF8.GetBytes(svg));

    private static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));

    private static string Svg(string body) => $"<svg xmlns='http://www.w3.org/2000/svg'>{body}</svg>";

    [Fact]
    public void SmallPath_IsWithinBudget()
    {
        Assert.False(Exceeds(Svg("<path d='M0 0 L1 1'/>")));
    }

    [Fact]
    public void SinglePathData_RejectsOnlyAboveTheBudget()
    {
        Assert.False(Exceeds(Svg($"<path d=\"{new string('1', Budget)}\"/>")));
        Assert.True(Exceeds(Svg($"<path d=\"{new string('1', Budget + 1)}\"/>")));
    }

    [Fact]
    public void PathData_IsSummedAcrossElements()
    {
        var part = new string('1', Budget * 6 / 10);
        Assert.False(Exceeds(Svg($"<path d='{part}'/>")));
        Assert.True(Exceeds(Svg($"<path d='{part}'/><path d='{part}'/>")));
    }

    [Fact]
    public void PolygonPoints_CountTowardTheBudget()
    {
        Assert.True(Exceeds(Svg($"<polygon points='{new string('1', Budget + 1)}'/>")));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("data-d")]
    [InlineData("dx")]
    public void OtherAttributes_DoNotCount(string attribute)
    {
        Assert.False(Exceeds(Svg($"<path {attribute}='{new string('1', Budget + 1)}'/>")));
    }

    [Fact]
    public void WhitespaceAroundEquals_StillCounts()
    {
        Assert.True(Exceeds(Svg($"<path\nd = \"{new string('1', Budget + 1)}\"/>")));
    }

    [Fact]
    public void Exceeds_CommentContainingAttributeLikeText_DoesNotHideRealAttribute()
    {
        Assert.True(Exceeds(Svg($"<!-- d=' --><path d='{new string('1', Budget + 1)}'/>")));
    }

    [Fact]
    public void Exceeds_OversizedAttributeInsideComment_IsIgnored()
    {
        Assert.False(Exceeds(Svg($"<!-- d='{new string('1', Budget + 1)}' --><path d='M0 0'/>")));
    }

    [Fact]
    public void Exceeds_CdataAndProcessingInstruction_AreSkipped()
    {
        Assert.False(Exceeds(Svg($"<![CDATA[ d='{new string('1', Budget + 1)}' ]]><?pi d='{new string('1', Budget + 1)}'?>")));
        Assert.True(Exceeds(Svg($"<![CDATA[ d=' ]]><path d='{new string('1', Budget + 1)}'/>")));
    }

    [Fact]
    public void Exceeds_QuotedValueContainingAttributeText_IsNotRescanned()
    {
        Assert.False(Exceeds(Svg($"<path id=\" d='{new string('1', Budget + 1)}'\" d='M0 0'/>")));
    }

    [Fact]
    public void IsUnsupportedEncoding_RejectsUtf16Utf32AndNul_AcceptsUtf8()
    {
        Assert.True(SvgGeometryBudget.IsUnsupportedEncoding(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<svg/>")).ToArray()));
        Assert.True(SvgGeometryBudget.IsUnsupportedEncoding(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("<svg/>")).ToArray()));
        Assert.True(SvgGeometryBudget.IsUnsupportedEncoding(Encoding.Unicode.GetBytes("<svg/>")));
        Assert.True(SvgGeometryBudget.IsUnsupportedEncoding(new byte[] { 0, 0, 0xFE, 0xFF, 0, 0, 0, (byte)'<' }));
        Assert.False(SvgGeometryBudget.IsUnsupportedEncoding(Encoding.UTF8.GetBytes(Svg(""))));
        Assert.False(SvgGeometryBudget.IsUnsupportedEncoding(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'<', (byte)'s' }));
    }

    [Fact]
    public void TryPrepareSvgForDecode_DoctypeCommentContainingFakePath_StillAcceptsSmallSvg()
    {
        // The raw scan stops a declaration at its first '>', so it counted the fake path inside this
        // DTD comment and rejected an SVG the decoder renders fine. Production strips the DOCTYPE
        // first, so the guard now sees exactly what the decoder parses.
        var fakePath = new string('1', Budget + 1);
        var bytes = Encoding.UTF8.GetBytes(
            $"<!DOCTYPE svg [<!-- > <path d='{fakePath}'/> -->]>" + Svg("<path d='M0 0 L1 1'/>"));

        Assert.True(SvgGeometryBudget.TryPrepareSvgForDecode(bytes, out var sanitized, out var rejection));
        Assert.Empty(rejection);
        Assert.DoesNotContain("<!DOCTYPE", Encoding.UTF8.GetString(sanitized), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryPrepareSvgForDecode_OversizedGeometryAfterDoctype_IsRejected()
    {
        // The raw scan ends the declaration at the '>' in the DTD comment, then treats the '<?' as a
        // processing instruction and skips to end of input because there is no '?>', so it never
        // counts the oversized path the decoder would parse. The strip removes the whole declaration.
        var bytes = Encoding.UTF8.GetBytes(
            "<!DOCTYPE svg [<!-- > <? -->]>" + Svg($"<path d='{new string('1', Budget + 1)}'/>"));

        Assert.False(SvgGeometryBudget.Exceeds(bytes));
        Assert.False(SvgGeometryBudget.TryPrepareSvgForDecode(bytes, out _, out _));
    }

    [Fact]
    public void TryPrepareSvgForDecode_DoctypeSurvivingTheStrip_IsRejected()
    {
        // A decoy <!DOCTYPE inside a comment is the first textual match, so the strip removes only
        // that one and leaves the real declaration, whose DTD hides the oversized path from the scan.
        var bytes = Encoding.UTF8.GetBytes(
            "<!-- <!DOCTYPE x> --><!DOCTYPE svg [<!-- > <![CDATA[ -->]>" +
            Svg($"<path d='{new string('1', Budget + 1)}'/>"));

        Assert.False(SvgGeometryBudget.TryPrepareSvgForDecode(bytes, out _, out _));
    }

    [Fact]
    public void ElementCount_RejectsOnlyAboveTheLimit()
    {
        Assert.Equal(SvgGeometryBudget.SvgBudgetLimit.None,
            Limit(Svg(Repeat("<rect/>", SvgGeometryBudget.MaxElements - 1))));
        Assert.Equal(SvgGeometryBudget.SvgBudgetLimit.ElementCount,
            Limit(Svg(Repeat("<rect/>", SvgGeometryBudget.MaxElements))));
    }

    [Fact]
    public void ElementCount_IgnoresEndTagsCommentsAndCdata()
    {
        var body = "<!--" + Repeat("<rect/>", SvgGeometryBudget.MaxElements * 2) + "-->"
            + "<![CDATA[" + Repeat("<rect/>", SvgGeometryBudget.MaxElements * 2) + "]]>"
            + Repeat("</g>", SvgGeometryBudget.MaxElements);
        Assert.Equal(SvgGeometryBudget.SvgBudgetLimit.None, Limit(Svg(body)));
    }

    [Fact]
    public void TryPrepareSvgForDecode_ManySmallShapes_IsRejectedForElementCount()
    {
        var bytes = Encoding.UTF8.GetBytes(
            Svg(Repeat("<rect width='1' height='1'/>", SvgGeometryBudget.MaxElements)));

        Assert.Equal(SvgGeometryBudget.SvgBudgetLimit.ElementCount, SvgGeometryBudget.FindExceededLimit(bytes));
        Assert.False(SvgGeometryBudget.TryPrepareSvgForDecode(bytes, out _, out _));
    }

    [Fact]
    public void TryPrepareSvgForDecode_Utf16_IsRejected()
    {
        var bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(Svg("<path d='M0 0'/>"))).ToArray();

        Assert.False(SvgGeometryBudget.TryPrepareSvgForDecode(bytes, out _, out _));
    }
}
