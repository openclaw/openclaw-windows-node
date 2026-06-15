namespace OpenClaw.Tray.Tests;

public sealed class PiperTextToSpeechClientContractTests
{
    private static string ReadSource() =>
        File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "TextToSpeech",
            "PiperTextToSpeechClient.cs"));

    [Fact]
    public void NativeLibrary_IsLoadedBeforeFinalizableWrapperConstruction()
    {
        var source = ReadSource();

        AssertInOrder(
            source,
            "EnsureNativeLibraryLoaded();",
            "_tts = new OfflineTts(config);");
        Assert.Contains("NativeLibrary.TryLoad(", source);
        Assert.Contains("typeof(OfflineTts).Assembly", source);
        Assert.Contains("DllImportSearchPath.SafeDirectories", source);
        Assert.Contains("out s_nativeLibraryHandle", source);
    }

    [Fact]
    public void Dispose_SuppressesFinalizerBeforeNativeCleanup()
    {
        var source = ReadSource();

        AssertInOrder(
            source,
            "GC.SuppressFinalize(_tts);",
            "_tts.Dispose();");
    }

    private static void AssertInOrder(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);

        Assert.True(firstIndex >= 0, $"Missing expected source fragment: {first}");
        Assert.True(secondIndex > firstIndex, $"Expected '{first}' before '{second}'.");
    }
}
