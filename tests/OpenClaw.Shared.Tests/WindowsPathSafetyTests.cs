using OpenClaw.Shared.IO;

namespace OpenClaw.Shared.Tests;

public sealed class WindowsPathSafetyTests
{
    [Theory]
    [InlineData("model.gguf")]
    [InlineData("Qwen3.8-27B_GGUF")]
    [InlineData("folder name")]
    public void IsSafeSegment_AcceptsOrdinarySegments(string value)
    {
        Assert.True(WindowsPathSafety.IsSafeSegment(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" model")]
    [InlineData("model ")]
    [InlineData("model.")]
    [InlineData("dir/model")]
    [InlineData(@"dir\model")]
    [InlineData("model:stream")]
    [InlineData("CON")]
    [InlineData("com1.gguf")]
    [InlineData("COM\u00b9")]
    [InlineData("com\u00b2.gguf")]
    [InlineData("LPT9")]
    [InlineData("LPT\u00b3.txt")]
    public void IsSafeSegment_RejectsUnsafeWindowsSegments(string value)
    {
        Assert.False(WindowsPathSafety.IsSafeSegment(value));
    }

    [Fact]
    public void IsSafeSegment_RejectsOverlongComponent()
    {
        Assert.True(WindowsPathSafety.IsSafeSegment(
            new string('a', WindowsPathSafety.MaximumComponentLength)));
        Assert.False(WindowsPathSafety.IsSafeSegment(
            new string('a', WindowsPathSafety.MaximumComponentLength + 1)));
    }

    [Fact]
    public void Containment_DoesNotTreatSiblingPrefixAsDescendant()
    {
        string root = WindowsPathSafety.NormalizePath(@"C:\cache\models");
        string child = WindowsPathSafety.NormalizePath(@"C:\cache\models\file.gguf");
        string siblingPrefix = WindowsPathSafety.NormalizePath(@"C:\cache\models-other\file.gguf");

        Assert.True(WindowsPathSafety.IsStrictDescendant(child, root));
        Assert.False(WindowsPathSafety.IsStrictDescendant(siblingPrefix, root));
    }
}
