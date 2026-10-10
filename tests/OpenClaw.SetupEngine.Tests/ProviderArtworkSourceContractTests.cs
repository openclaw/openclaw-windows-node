namespace OpenClaw.SetupEngine.Tests;

/// <summary>Retire when a mounted WinUI test host covers image decode and recycled-control lifetimes.</summary>
public sealed class ProviderArtworkSourceContractTests
{
    [Fact]
    public void Page_ForwardsAllMetadataAndClosesArtworkBeforeConnectionCleanup()
    {
        var page = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "AiSetupPage.xaml.cs");
        Assert.Contains("candidate.BrandId, candidate.Icon, candidate.Kind", page);
        Assert.Contains("MetadataActionLabel: option.ActionLabel", page);
        Assert.Contains("Icon: option.Icon, ProviderKind: option.Kind", page);
        Assert.Contains("_artworkSession.Dispose();", page);
        Assert.DoesNotContain("new HttpClient", page);
        Assert.DoesNotContain("IconVisibility", page);
        var xaml = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "AiSetupPage.xaml");
        Assert.Contains("Descriptor=\"{Binding Artwork}\" Session=\"{Binding ArtworkSession}\"", xaml);
        Assert.Contains("Text=\"{Binding ActionLabel}\"", xaml);
    }

    [Fact]
    public void Control_UsesWinUiBundledResourceResolutionAndCancelsEveryReplacementAndUnload()
    {
        var source = Read("src", "OpenClaw.SetupEngine.UI", "Controls", "ProviderArtwork.xaml.cs");
        Assert.Contains("ArtworkImage.Source = null;", source);
        Assert.Contains("_generation.IsCurrent(generation)", source);
        Assert.Contains("_generation.Stop();", source);
        Assert.Contains("Unloaded += OnUnloaded;", source);
        Assert.Contains("session.Closed += OnSessionClosed;", source);
        Assert.Contains("_observedSession.Closed -= OnSessionClosed;", source);
        Assert.Contains("await session.Loader.LoadAsync(remote, ct)", source);
        Assert.Contains("source.UriSource = uri", source);
        var loading = source[source.IndexOf("private async Task<DecodedProviderArtwork> LoadBundledAsync", StringComparison.Ordinal)..];
        Assert.True(loading.IndexOf("ArtworkImage.Source = source", StringComparison.Ordinal) <
            loading.IndexOf("source.UriSource = uri", StringComparison.Ordinal));
        Assert.True(loading.IndexOf("ArtworkImage.Visibility = Visibility.Visible", StringComparison.Ordinal) <
            loading.IndexOf("await completion.Task", StringComparison.Ordinal));
        Assert.Contains("_generation.Matches(generation) && ReferenceEquals(ArtworkImage.Source, source)", loading);
        Assert.Contains("source.Opened -= Opened", source);
        Assert.Contains("source.OpenFailed -= Failed", source);
        Assert.DoesNotContain("RandomAccessStreamReference", source);
        Assert.DoesNotContain("new BitmapImage", source);
        Assert.DoesNotContain("Trace.", source);
        Assert.DoesNotContain("ex.Message", source);
        Assert.Contains("AutomationProperties.AccessibilityView=\"Raw\"",
            Read("src", "OpenClaw.SetupEngine.UI", "Controls", "ProviderArtwork.xaml"));
    }

    [Fact]
    public void Decoder_ChecksCodecAndDimensionsBeforeAllocatingPixelsAndRetainsNativeSlot()
    {
        var source = Read("src", "OpenClaw.SetupEngine.UI", "Controls", "ProviderArtworkDecoder.cs");
        Assert.Contains("decoder.DecoderInformation.CodecId != expected", source);
        Assert.Contains("decoder.FrameCount != 1", source);
        Assert.True(source.IndexOf("AreDimensionsAllowed", StringComparison.Ordinal) <
            source.IndexOf("GetPixelDataAsync", StringComparison.Ordinal));
        Assert.Contains("BitmapPixelFormat.Bgra8", source);
        Assert.Contains("ExifOrientationMode.IgnoreExifOrientation", source);
        Assert.Contains("ColorManagementMode.DoNotColorManage", source);
        Assert.Contains("Slots.WaitAsync(0, ct)", source);
        Assert.Contains("await decode.WaitAsync(ct)", source);
        Assert.Contains("finally { Slots.Release(); }", source);
        Assert.DoesNotContain("BitmapImage", source);
    }

    private static string Read(params string[] parts)
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ??
            throw new InvalidOperationException("Set OPENCLAW_REPO_ROOT for linked-worktree source contracts.");
        return File.ReadAllText(Path.Combine([root, .. parts]));
    }
}
