// Fixture-free coverage for the real E2E artifact admission boundary.
// Run with dotnet test --filter FullyQualifiedName~SetupAndConnectTestsArtifactPolicy.
namespace OpenClaw.E2ETests.Setup;

/// <summary>Protects credential-bearing profile storage without suppressing ordinary diagnostic evidence.</summary>
public sealed class SetupAndConnectTestsArtifactPolicy
{
    [Theory]
    [InlineData(@"data\search-browser-owned\Default\Local Storage\leveldb\000003.log", false)]
    [InlineData("data/SEARCH-BROWSER-owned/Default/Preferences.json", false)]
    [InlineData(@"data\search-browser-owned\Default\Network\state.json", false)]
    [InlineData(@"data\gateways\fixture\diagnostic.log", false)]
    [InlineData(@"data\gateways.json", false)]
    [InlineData(@"data\settings.json", false)]
    [InlineData(@"data\device-key-ed25519.json", false)]
    [InlineData(@"data\openclaw-tray.log", true)]
    [InlineData(@"data\diagnostics\summary.json", true)]
    public void ArtifactCollectionExcludesCredentialsAndBrowserProfiles(string path, bool expected)
    {
        Assert.Equal(expected, E2ESetupFixture.ShouldCopyArtifactFile(path));
    }
}
