using System.Security.Cryptography;
using System.Xml.Linq;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LlamaAppArtworkTests
{
    [Fact]
    public void OfficialArtwork_IsExactPinnedDarkTileAssetAndShipsWithLicenseAndCredit()
    {
        var path = OnboardingMascotSourceContractTests.RepoPath(
            "src", "OpenClaw.Tray.WinUI", "Assets", "Setup", "ProviderIcons");
        Assert.Equal("78BBB16CE08F25BAF0BC2C8F0C765E85D933F537BA92625B844528CB02AA249B",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(path, "ProviderIcon-llama-app.svg")))));
        var credit = File.ReadAllText(Path.Combine(path, "Llama-NOTICE.md"));
        Assert.Contains("ggml-org/llama.brand", credit);
        Assert.Contains("0708f2327336589bd4d3eba15a95199c318cd771", credit);
        Assert.Contains("Creative Commons", File.ReadAllText(Path.Combine(path, "Llama-LICENSE.txt")));
        var document = XDocument.Load(Path.Combine(path, "ProviderIcon-llama-app.svg"));
        Assert.All(document.Descendants(), element => Assert.Contains(element.Name.LocalName, new[] { "svg", "path" }));
    }

    [Fact]
    public void LlamaAppBrand_DoesNotClaimCompanionManagedLlamaCpp()
    {
        Assert.Null(GatewayAiSetupPresentation.GetBundledProviderIconFileName("llamacpp"));
        var artwork = GatewayAiSetupPresentation.GetProviderArtwork("llama-app", "llama-app", null, ProviderArtworkFallback.Install);
        Assert.Equal("ProviderIcon-llama-app.svg", artwork.BundledFileName);
        Assert.Null(artwork.RemoteUri);
    }
}
