using System.Security.Cryptography;
using System.Xml.Linq;

namespace OpenClaw.SetupEngine.Tests;

public sealed class OnboardingMascotAssetTests
{
    public static TheoryData<string, string> UpstreamAssets => new()
    {
        { "ProviderIcon-claude.svg", "497A88A780831512317B6DD061285E2EC2608ABF09A94A5F24EE9019A4D3EF8B" },
        { "ProviderIcon-codex.svg", "5C999792F6D26C9D14A197AA6E6C20F5AF3DEF433473F7B66BD0D8C8A41CD8CF" },
        { "ProviderIcon-gemini.svg", "DFC7A86AD243030737BF66D316996743D8C4209FADEEE5A782CCAF4F84E3C7E9" },
        { "ProviderIcon-kimi.svg", "B99F95983147C4CA1A72F61E1E22E172027CCC30E72E88C48DC7280A555C3E9A" },
        { "ProviderIcon-lmstudio.svg", "02DC67068DAA4865D2836D06464DBD853320556812F7B64465F69AFA98EF7DFE" },
        { "ProviderIcon-ollama.svg", "167D65056565611212CBA72F70E582032F92F3B6A1BD35FFC26C2B5014F90794" },
        { "ProviderIcon-opencode.svg", "3A20ABB3D62D9A1FF7AE1395A17CC2D1A7105597AC028E968A6A3ED73EE1D82F" },
        { "ProviderIcon-pi.svg", "10F6335CD4F9B5E8CFAA5408C2AA9C75362D1AD5BCF62B11CBD00B4EF9B23AE5" },
        { "ProviderIcon-xai.svg", "FF019BBAA756CCB2F3F31D9B86BE17ED9EC2FD43D24B0EF56EE42AE7AB691985" },
        { "ATTRIBUTION.md", "08F98F79BE36D70B334E2361A4105AA0E2F0A95635FF8C93CEBBFE745F87B473" },
        { "NOTICE.md", "359646742269AB493AF0B2D62F3F5420D4320143E9F23DCD4A4CA89CF5799D91" },
    };

    [Theory]
    [MemberData(nameof(UpstreamAssets))]
    public void ProviderArtworkAndNotices_AreExactPinnedUpstreamBytes(string name, string hash)
    {
        var path = OnboardingMascotSourceContractTests.RepoPath(
            "src", "OpenClaw.Tray.WinUI", "Assets", "Setup", "ProviderIcons", name);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        if (!name.EndsWith(".svg", StringComparison.Ordinal))
            return;

        var document = XDocument.Load(path);
        XNamespace svg = "http://www.w3.org/2000/svg";
        Assert.Equal(svg + "svg", document.Root!.Name);
        Assert.False(string.IsNullOrWhiteSpace(document.Root.Attribute("viewBox")?.Value));
        Assert.NotEmpty(document.Descendants(svg + "path"));
        // These path-only assets avoid unsupported SVG filters, embedded fonts, scripts, and external resources.
        Assert.All(document.Descendants(), element => Assert.Contains(element.Name.LocalName, new[] { "svg", "path" }));
        Assert.DoesNotContain(document.Descendants().Attributes(),
            attribute => attribute.Name.LocalName is "href" or "style" || attribute.Value.Contains("url(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProviderArtwork_CheckoutFilterPreservesPinnedBytesWithWindowsAutoCrlf()
    {
        var root = Path.GetDirectoryName(OnboardingMascotSourceContractTests.RepoPath(".gitattributes"))!;
        foreach (var asset in UpstreamAssets)
        {
            var path = $"src/OpenClaw.Tray.WinUI/Assets/Setup/ProviderIcons/{asset[0]}";
            var start = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[] { "-c", "core.autocrlf=true", "cat-file", "--filters", $"HEAD:{path}" })
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            using var bytes = new MemoryStream();
            var errors = process.StandardError.ReadToEndAsync();
            await process.StandardOutput.BaseStream.CopyToAsync(bytes);
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await errors);
            Assert.Equal(asset[1], Convert.ToHexString(SHA256.HashData(bytes.ToArray())));
        }
    }

    [Fact]
    public void MascotNotice_PreservesLicenseAndPinnedSourceProvenance()
    {
        var source = File.ReadAllText(OnboardingMascotSourceContractTests.RepoPath(
            "src", "OpenClaw.Tray.WinUI", "Assets", "Setup", "Mascot-NOTICE.txt"));
        Assert.Contains("0fd603a6fece58d60c010e565df9e26c6601db8b", source);
        Assert.Contains("Copyright (c) 2026 OpenClaw Foundation", source);
        Assert.Contains("Permission is hereby granted, free of charge", source);
        Assert.Contains("THE SOFTWARE IS PROVIDED \"AS IS\"", source);
    }
}
