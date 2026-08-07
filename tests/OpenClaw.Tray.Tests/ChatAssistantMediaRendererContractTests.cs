namespace OpenClaw.Tray.Tests;

public sealed class ChatAssistantMediaRendererContractTests
{
    [Fact]
    public void ImageViewer_UsesRegisteredContentDialogFactory()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ChatAssistantMediaRenderer.cs"));

        Assert.Contains("var viewer = ContentDialog(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ContentDialogElement(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageLoader_DoesNotRenderStateFromPreviousReference()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ChatAssistantMediaRenderer.cs"));

        Assert.Contains(
            "ReferenceEquals(state.Reference, props.Media.Reference)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "props.SessionKey, props.Media.Reference, attempt",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssistantMediaResolutionStatus Status",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AssistantMediaResolutionResult Result",
            source,
            StringComparison.Ordinal);
    }
}
