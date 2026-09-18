using OpenClaw.Shared.Browser;

namespace OpenClaw.Shared.Tests;

public class ChromeExtensionInstallRequestTests
{
    private static readonly string Executable = Path.GetFullPath("OpenClaw.BrowserNativeHost.exe");
    private static Dictionary<string, object?> Owned => new()
    {
        [ChromeExtensionInstallRequest.OwnerValue] = Executable,
        ["update_url"] = ChromeExtensionInstallRequest.UpdateUrl
    };

    [Theory]
    [InlineData(null, true)]
    [InlineData("{}", true)]
    [InlineData("{\"NodeBrowserProxyEnabled\":true}", true)]
    [InlineData("{\"NodeBrowserProxyEnabled\":false}", false)]
    [InlineData("{\"NodeBrowserProxyEnabled\":false,\"NodeBrowserProxyEnabled\":true}", false)]
    [InlineData("{\"NodeBrowserProxyEnabled\":\"true\"}", false)]
    [InlineData("invalid", false)]
    public void SavedBrowserControlOptOut_IsPreserved(string? json, bool expected) =>
        Assert.Equal(expected, ChromeExtensionInstallRequest.SettingsAllowRequest(json));

    [Fact]
    public void ExactOwnedStoreRequest_IsRecognized() =>
        Assert.True(ChromeExtensionInstallRequest.IsOwned(Owned, Executable));

    [Fact]
    public void MatchingStoreUrlWithoutOwner_IsNotAdopted()
    {
        var values = Owned;
        values.Remove(ChromeExtensionInstallRequest.OwnerValue);
        Assert.False(ChromeExtensionInstallRequest.IsOwned(values, Executable));
    }

    [Fact]
    public void ForeignOwnerOrUrl_IsPreserved()
    {
        Assert.False(ChromeExtensionInstallRequest.IsOwned(Owned, Executable + ".other"));
        var values = Owned;
        values["update_url"] = "https://example.invalid/crx";
        Assert.False(ChromeExtensionInstallRequest.IsOwned(values, Executable));
        Assert.False(ChromeExtensionInstallRequest.IsOwned(values, Executable, allowIncomplete: true));
    }

    [Fact]
    public void AdditionalValues_AreNotOwned()
    {
        var values = Owned;
        values["path"] = "foreign.crx";
        Assert.False(ChromeExtensionInstallRequest.IsOwned(values, Executable));
    }

    [Fact]
    public void PartialOwnCreation_CanBeCleanedButIsNotAnInstalledRequest()
    {
        var values = Owned;
        values.Remove("update_url");
        Assert.False(ChromeExtensionInstallRequest.IsOwned(values, Executable));
        Assert.True(ChromeExtensionInstallRequest.IsOwned(values, Executable, allowIncomplete: true));
        Assert.False(ChromeExtensionInstallRequest.IsOwned(new Dictionary<string, object?>(), Executable, allowIncomplete: true));
    }
}
