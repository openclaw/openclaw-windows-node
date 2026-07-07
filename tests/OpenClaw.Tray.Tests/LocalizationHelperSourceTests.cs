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

    private static string ReadSource(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
