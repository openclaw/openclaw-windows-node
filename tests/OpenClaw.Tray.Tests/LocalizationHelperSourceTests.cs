using System.IO;

namespace OpenClaw.Tray.Tests;

public sealed class LocalizationHelperSourceTests
{
    [Fact]
    public void GetString_ResolvesXamlPropertyResourceKeys()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Helpers", "LocalizationHelper.cs");

        Assert.Contains("TryGetXamlPropertyResourcePath(resourceKey, out var propertyResourcePath)", source);
        Assert.Contains("TryGetValueAsString(propertyResourcePath", source);
        Assert.Contains("LastIndexOf('.')", source);
        Assert.Contains("\"{resourceKey[..propertySeparator]}/{resourceKey[(propertySeparator + 1)..]}\"", source);
    }

    /// <summary>
    /// SetupEngine.UI's own resource helper (it cannot reference LocalizationHelper directly,
    /// see SetupLocalization.cs) must resolve the same "Key.Property" -> "Key/Property" XAML
    /// property resource shape, so code-behind can share one resw entry with an x:Uid binding
    /// instead of duplicating the string under a second key.
    /// </summary>
    [Fact]
    public void SetupLocalization_ResolvesXamlPropertyResourceKeys()
    {
        var source = ReadSource("src", "OpenClaw.SetupEngine.UI", "SetupLocalization.cs");

        Assert.Contains("LastIndexOf('.')", source);
        Assert.Contains(
            "$\"{resourceKey[..propertySeparator]}/{resourceKey[(propertySeparator + 1)..]}\"", source);
    }

    private static string ReadSource(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
