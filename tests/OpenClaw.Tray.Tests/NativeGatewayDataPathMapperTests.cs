using OpenClaw.SetupEngine.UI;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class NativeGatewayDataPathMapperTests
{
    [Theory]
    [InlineData("state")]
    [InlineData("state/openclaw.json")]
    [InlineData("workspace")]
    public void Resolve_PreservesProspectiveNativeSetupPaths(string relative)
    {
        using var temp = new TempDirectory("native-data-path-");
        string local = temp.Combine("Local");
        string roaming = temp.Combine("Roaming");
        string suffix = Path.Combine("OpenClawTray", relative.Replace('/', Path.DirectorySeparatorChar));
        string logical = Path.Combine(roaming, suffix);
        string expected = Path.Combine(local, "Packages", "companion_test", "LocalCache", "Roaming", suffix);

        Assert.False(Path.Exists(logical));
        Assert.Equal(expected, NativeGatewayDataPathMapper.Resolve(logical, "companion_test", local, roaming));
        // Existing saved profiles keep the same mapping, without rewriting their configuration.
        Directory.CreateDirectory(Path.GetDirectoryName(logical)!);
        File.WriteAllText(logical, "fixture");
        Assert.Equal(expected, NativeGatewayDataPathMapper.Resolve(logical, "companion_test", local, roaming));
    }

    [Fact]
    public void Resolve_PreservesLocalMappingAndDoesNotRemapContainerOrUnpackagedPaths()
    {
        using var temp = new TempDirectory("native-local-path-");
        string local = temp.Combine("Local");
        string roaming = temp.Combine("Roaming");
        string logical = Path.Combine(local, "OpenClawTray", "state");
        string physical = Path.Combine(local, "Packages", "companion_test", "LocalCache", "Local", "OpenClawTray", "state");

        Assert.Equal(physical, NativeGatewayDataPathMapper.Resolve(logical, "companion_test", local, roaming));
        Assert.Equal(physical, NativeGatewayDataPathMapper.Resolve(physical, "companion_test", local, roaming));
        Assert.Equal(logical, NativeGatewayDataPathMapper.Resolve(logical, null, local, roaming));
        Assert.Equal(temp.Path, NativeGatewayDataPathMapper.Resolve(temp.Path, "companion_test", local, roaming));
    }
}
