using OpenClaw.SetupEngine.UI;

namespace OpenClaw.Tray.Tests;

public sealed class NativeGatewayEmphasisRangesTests
{
    [Fact]
    public void TryGet_SortsNonOverlappingRanges()
    {
        Assert.True(NativeGatewayEmphasisRanges.TryGet(
            "first middle last",
            ["last", "first", "middle"],
            out var ranges));

        Assert.Equal([(0, 5), (6, 6), (13, 4)], ranges);
    }

    [Theory]
    [MemberData(nameof(InvalidEmphases))]
    public void TryGet_RejectsInvalidEmphases(string text, string[] emphases)
    {
        Assert.False(NativeGatewayEmphasisRanges.TryGet(text, emphases, out _));
    }

    public static TheoryData<string, string[]> InvalidEmphases => new()
    {
        { "complete guidance", [""] },
        { "complete guidance", ["missing"] },
        { "complete guidance", ["complete", "complete guidance"] },
        { "complete guidance", ["complete", "complete"] },
    };
}
